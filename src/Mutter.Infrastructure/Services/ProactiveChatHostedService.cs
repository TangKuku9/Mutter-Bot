using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Proactive;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;

namespace Mutter.Infrastructure.Services;

/// <summary>
/// 择机发言评估循环：把观察窗口里的群聊交给 AI 判时机，判定通过才发言。
/// 全局开关关闭时本服务空转，不读库也不调用 AI。
/// </summary>
public class ProactiveChatHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProactiveObserveService _observe;
    private readonly ProactiveSpeaker _speaker;
    private readonly IStructuredAiService _structuredAi;
    private readonly IAiChatService _ai;
    private readonly IContextBuilder _contextBuilder;
    private readonly ILogger<ProactiveChatHostedService> _logger;

    /// <summary>串行闸门：择机评估不能挤占 @bot 回复的 AI 并发槽（MessageEventHandler._aiSemaphore）。</summary>
    private readonly SemaphoreSlim _evalGate = new(1, 1);

    public ProactiveChatHostedService(
        IServiceScopeFactory scopeFactory,
        ProactiveObserveService observe,
        ProactiveSpeaker speaker,
        IStructuredAiService structuredAi,
        IAiChatService ai,
        IContextBuilder contextBuilder,
        ILogger<ProactiveChatHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _observe = observe;
        _speaker = speaker;
        _structuredAi = structuredAi;
        _ai = ai;
        _contextBuilder = contextBuilder;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 不说“默认关闭”：全局开关是面板状态，日志里断言一句状态就会在排查时误导人
        _logger.LogInformation("择机发言评估服务已启动（是否采集与发言取决于面板的全局开关）");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "择机发言评估循环异常");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var config = await LoadConfigAsync(ct);
        if (!config.Enabled) return;

        var now = DateTime.UtcNow;
        foreach (var (groupId, _) in _observe.PendingWindows())
        {
            if (ct.IsCancellationRequested) return;
            if (!_observe.IsDue(groupId, config, now)) continue;

            var window = _observe.Take(groupId, config.WindowMaxMessages);
            if (window is null) continue;

            // 取走即记一次评估：即便这次没说话，也不会立刻拿同一个窗口重复问 AI
            _observe.MarkEvaluated(groupId, now);
            await EvaluateAsync(window, config, ct);
        }
    }

    private async Task EvaluateAsync(ProactiveWindow window, ProactiveChatConfig config, CancellationToken ct)
    {
        var blocked = _observe.BlockReason(window.GroupId, config, DateTime.UtcNow);
        if (blocked is not null)
        {
            await WriteLogAsync(window, new LogNote(blocked), ct);
            return;
        }

        if (!await _evalGate.WaitAsync(TimeSpan.Zero, ct))
        {
            _logger.LogDebug("已有窗口在评估，跳过 group={GroupId}", window.GroupId);
            await WriteLogAsync(window, new LogNote(ProactiveOutcomes.SkippedAiBusy), ct);
            return;
        }

        try
        {
            await SpeakIfAllowedAsync(window, config, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "择机发言评估失败 group={GroupId}", window.GroupId);
            await WriteLogAsync(window, new LogNote(ProactiveOutcomes.FailedApiError, Detail: ex.Message), ct);
        }
        finally
        {
            _evalGate.Release();
        }
    }

    private async Task SpeakIfAllowedAsync(ProactiveWindow window, ProactiveChatConfig config, CancellationToken ct)
    {
        if (!await _structuredAi.IsAiConfiguredAsync(ct))
        {
            await WriteLogAsync(window, new LogNote(ProactiveOutcomes.SkippedAiUnavailable, Detail: "AI 未配置"), ct);
            return;
        }

        var last = window.Messages[^1];
        var nowUtc = DateTime.UtcNow;

        // 只继承人设 / 模型 / 接口地址这类"这个 bot 是谁"的设置；
        // 「上下文条数」与「输出上限」改用择机自己的两个参数。
        // 此前这里直接吃 AI 面板的全局值，于是同一件事被两个面板的旋钮叠乘着控制。
        var context = await _contextBuilder.BuildGroupContextAsync(
            window.GroupId, last.Text, last.SenderName,
            ai => ai with
            {
                MaxContextMessages = config.HistoryMaxMessages,
                MaxTokens = config.EvalMaxTokens
            }, ct);

        // 窗口消息同时也躺在上下文历史里，渲染出来是同一行「[昵称]: 正文」。
        // 不剔除就会在同一份 prompt 里被喂两遍（背景一次、当前窗口一次），
        // 既白烧 token，又让模型把正在聊的话读成"更早的旧事"。
        var history = window.ExcludeFrom(context.Messages);

        // 窗口里的图片直接交给判定这一调用来读，不另起一次"先描述图片再判断"的调用。
        // 先筛再拼提示词：动图和失效链不附，"附了几张"才不会跟实际发出的对不上。
        var images = await _ai.AttachableImagesAsync(
            window.CollectImages(config.MaxAttachedImages), ct);
        var startedAt = nowUtc;

        // 群记忆：全局开关关了连库都不查；群级覆盖 false 表示这个群单独停用注入（摘要仍保留）
        var memory = await LoadGroupMemoryAsync(window.GroupId, config, ct);
        // 记的就是真正喂进去的那段：与 BuildUserPrompt 共用同一个字符串，留痕不会和实际对不上
        var memoryText = memory?.Summary?.Trim();
        var memoryCount = string.IsNullOrWhiteSpace(memoryText) ? 0 : 1;
        var memoryChars = memoryCount == 0 ? 0 : memoryText!.Length;

        // 择机判定是内部决策，不该继承全局联网开关：它只是在判断“要不要接话”，
        // 每条群消息都去检索既慢又费 token。联网只服务于最终话术，不服务于判断。
        var judgeConfig = context.EffectiveConfig with { WebSearchEnabled = false };

        var outcome = await _structuredAi.GetStructuredOutcomeAsync<ProactiveVerdict>(
            BuildJudgePrompt(context, config.Liveliness),
            ProactivePrompts.BuildUserPrompt(window, history, images.Count, memoryText, nowUtc),
            images,
            judgeConfig, ct);

        if (outcome.Result is null)
        {
            var (outcomeCode, detail) = outcome.FailureKind switch
            {
                StructuredAiFailureKind.ApiError => (ProactiveOutcomes.FailedAiCall, Truncate(outcome.ErrorMessage, 160)),
                StructuredAiFailureKind.Truncated => (ProactiveOutcomes.SkippedAiTruncated,
                    $"finish_reason=length；tokens {outcome.PromptTokens}+{outcome.CompletionTokens}"),
                _ => (ProactiveOutcomes.SkippedAiUnparsable, Truncate(outcome.RawContent, 160))
            };
            await WriteLogAsync(window,
                new LogNote(outcomeCode, Detail: detail, Usage: outcome,
                    MemoryCount: memoryCount, MemoryPromptChars: memoryChars), ct);
            return;
        }

        var tookMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogInformation("择机评估完成 group={GroupId} 用时={Ms}ms 消息={Count} 附图={Images}/{Total}",
            window.GroupId, tookMs, window.Messages.Count, images.Count, window.ImageCount);

        var verdict = outcome.Result;
        var data = verdict.Data;
        var line = ProactivePrompts.SanitizeLine(data.Line);

        // 唯一决策口：有话就说，没话就不说。不再有 should_speak 一票否决
        // （它曾把模型已经写好的那句话连同 token 一起丢掉）。
        if (line is null)
        {
            await WriteLogAsync(window,
                new LogNote(ProactiveOutcomes.SkippedAiDeclined, data, Usage: outcome,
                    MemoryCount: memoryCount, MemoryPromptChars: memoryChars), ct);
            return;
        }

        // 第二阶段：只有判定明确给了检索词才走。闲聊、玩梗、情绪接话不会多打一次调用。
        // 触发条件集中在 ProactiveSpeech.ShouldSearch：少任何一条都直接发草稿，行为与联网上线前一致。
        var searchQuery = ProactivePrompts.SanitizeQuery(data.Query);
        var searchUsed = false;
        var speechPromptTokens = 0;
        var speechCompletionTokens = 0;
        string? searchDetail = null;

        if (ProactiveSpeech.ShouldSearch(config, context.EffectiveConfig, line, searchQuery))
        {
            searchUsed = true;
            var speech = await RewriteWithSearchAsync(
                window, context, data, line, searchQuery!, nowUtc, ct);
            speechPromptTokens = speech.PromptTokens;
            speechCompletionTokens = speech.CompletionTokens;

            if (speech.Declined)
            {
                await WriteLogAsync(window,
                    new LogNote(ProactiveOutcomes.SkippedAfterSearch, data, null,
                        "联网核实后模型选择不说", null, outcome,
                        memoryCount, memoryChars, searchQuery, searchUsed,
                        speechPromptTokens, speechCompletionTokens), ct);
                return;
            }

            if (speech.Line is not null)
                line = speech.Line;
            else
                searchDetail = $"联网改写失败，已发草稿：{Truncate(speech.Error, 160)}";
        }

        var result = await _speaker.SpeakAsync(window, line, config, ct);
        var logDetail = string.IsNullOrWhiteSpace(searchDetail)
            ? result.Detail
            : string.IsNullOrWhiteSpace(result.Detail)
                ? searchDetail
                : $"{searchDetail}；{result.Detail}";
        await WriteLogAsync(window,
            new LogNote(result.Outcome, data, line, logDetail, result.MessageId, outcome,
                memoryCount, memoryChars, searchQuery, searchUsed,
                speechPromptTokens, speechCompletionTokens), ct);

        if (result.Outcome is not (ProactiveOutcomes.SentPassive or ProactiveOutcomes.SentActive))
        {
            _logger.LogWarning("择机发言未送达 group={GroupId} outcome={Outcome} detail={Detail}",
                window.GroupId, result.Outcome, result.Detail);
            return;
        }

        _observe.MarkSent(window.GroupId, DateTime.UtcNow);
        await PersistBotReplyAsync(window.GroupId, line, verdict, ct);
        _logger.LogInformation("择机发言已发出 group={GroupId} channel={Outcome} line={Line}",
            window.GroupId, result.Outcome, line);
    }

    /// <summary>判定提示词 = 择机规则 + 发言倾向 + 当前人设，保证真要开口时和 @ 回复是同一个人。</summary>
    private static string BuildJudgePrompt(BuildContextResult context, int liveliness)
        => ProactivePrompts.SystemPrompt
           + "\n\n【本群的发言倾向】\n" + ProactivePrompts.Tendency(liveliness)
           + "\n\n【你的身份与说话风格】\n" + BuildPersona(context);

    private static string BuildPersona(BuildContextResult context)
        => string.IsNullOrWhiteSpace(context.CharacterPrompt)
            ? context.EffectiveConfig.BuildEffectiveSystemPrompt()
            : context.CharacterPrompt;

    /// <summary>
    /// 第二阶段的联网改写。失败/超时不吞掉这次发言：调用方会退回第一阶段草稿。
    /// 只有模型明确返回空行（核实后决定不说）才算“放弃”，记为 skipped_after_search。
    /// 请求不带图、不带背景历史：画面信息已经在第一阶段的 image_note 里压缩好了。
    /// </summary>
    private async Task<SpeechRewrite> RewriteWithSearchAsync(
        ProactiveWindow window, BuildContextResult context, ProactiveVerdict data,
        string draft, string query, DateTime utcNow, CancellationToken ct)
    {
        var systemPrompt = ProactivePrompts.SpeechSystemPrompt
            + "\n\n【你的身份与说话风格】\n" + BuildPersona(context);
        var userPrompt = ProactivePrompts.BuildSpeechPrompt(
            window, draft, query, ProactivePrompts.SanitizeNote(data.ImageNote), data.Topic, utcNow);
        var startedAt = DateTime.UtcNow;

        try
        {
            // ChatAsync 默认继承配置里的联网开关，这里正是它该生效的一步；
            // 上游若确定性拒绝搜索，AiChatService 会自动退回不检索重发，不会把这次改写打没。
            var response = await _ai.ChatAsync(
                systemPrompt,
                new List<(string Role, string Content)> { ("user", userPrompt) },
                context.EffectiveConfig, ct);

            var line = ProactivePrompts.SanitizeLine(response.Content);
            var tookMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "择机联网改写完成 group={GroupId} 用时={Ms}ms query={Query} declined={Declined}",
                window.GroupId, tookMs, query, line is null);

            return new SpeechRewrite(line, line is null, null,
                response.PromptTokens, response.CompletionTokens);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "择机联网改写失败，退回草稿 group={GroupId}", window.GroupId);
            return new SpeechRewrite(null, false, ex.Message, 0, 0);
        }
    }

    private async Task<ProactiveChatConfig> LoadConfigAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        return await repo.GetProactiveConfigAsync(ct);
    }

    /// <summary>
    /// 取本群的记忆摘要。开关不允许注入时返回 null —— 与"这个群没写摘要"走同一条路，
    /// 调用方不必分叉，提示词那边本来也只认非空文本。
    /// </summary>
    private async Task<GroupMemory?> LoadGroupMemoryAsync(
        string groupId, ProactiveChatConfig config, CancellationToken ct)
    {
        if (!config.MemoryInjectionEnabled) return null;

        using var scope = _scopeFactory.CreateScope();
        var groupConfig = await scope.ServiceProvider.GetRequiredService<IConfigRepository>()
            .GetGroupConfigAsync(groupId, ct);
        if (groupConfig?.MemoryInjectionEnabled == false) return null;

        return await scope.ServiceProvider.GetRequiredService<IGroupMemoryRepository>().GetAsync(groupId, ct);
    }

    private readonly record struct LogNote(
        string Outcome,
        ProactiveVerdict? Data = null,
        string? Line = null,
        string? Detail = null,
        string? SentMessageId = null,
        StructuredOutcome<ProactiveVerdict>? Usage = null,
        int MemoryCount = 0,
        int MemoryPromptChars = 0,
        string? SearchQuery = null,
        bool SearchUsed = false,
        int SpeechPromptTokens = 0,
        int SpeechCompletionTokens = 0);

    /// <summary>第二阶段结果。Line 与 Declined 互斥；两者都空表示调用失败，调用方退回草稿。</summary>
    private readonly record struct SpeechRewrite(
        string? Line, bool Declined, string? Error, int PromptTokens, int CompletionTokens);

    private async Task WriteLogAsync(ProactiveWindow window, LogNote note, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ProactiveChatLogs.Add(new ProactiveChatLogEntity
            {
                GroupId = window.GroupId,
                EvaluatedAt = DateTime.UtcNow,
                MessageCount = window.Messages.Count,
                ImageCount = window.ImageCount,
                MemoryCount = note.MemoryCount,
                MemoryPromptChars = note.MemoryPromptChars,
                Outcome = note.Outcome,
                // 留痕里的 ShouldSpeak 由“最终有没有话发出去”推导。不能回退到草稿：
                // 联网改写后放弃的那次，第一阶段虽然写了 line，也不算“本来要说话”。
                ShouldSpeak = !string.IsNullOrWhiteSpace(note.Line),
                Topic = Truncate(note.Data?.Topic),
                Line = Truncate(note.Line),
                Reason = Truncate(note.Data?.Reason),
                Detail = Truncate(note.Detail),
                SentMessageId = note.SentMessageId,
                Model = note.Usage?.Model,
                PromptTokens = note.Usage?.PromptTokens ?? 0,
                CompletionTokens = note.Usage?.CompletionTokens ?? 0,
                SearchQuery = Truncate(note.SearchQuery, ProactivePrompts.MaxQueryChars),
                SearchUsed = note.SearchUsed,
                SpeechPromptTokens = note.SpeechPromptTokens,
                SpeechCompletionTokens = note.SpeechCompletionTokens
            });
            await db.SaveChangesAsync(ct);

            // 每条留痕（说与不说都算）都推给面板：择机页的今日统计与决策留痕
            // 靠这个事件自动更新，否则管理员看到的一直是进页时的旧数字。
            var evalNotifier = scope.ServiceProvider.GetRequiredService<INotificationService>();
            await evalNotifier.NotifyProactiveEvaluatedAsync(new
            {
                GroupId = window.GroupId,
                Outcome = note.Outcome,
                EvaluatedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "择机发言留痕写入失败 group={GroupId}", window.GroupId);
        }
    }

    /// <summary>插话说出口就要进聊天历史，否则面板记录与群里看到的内容对不上。</summary>
    private async Task PersistBotReplyAsync(
        string groupId, string line, StructuredResult<ProactiveVerdict> verdict, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var api = scope.ServiceProvider.GetRequiredService<IQqBotApiService>();
            var notifier = scope.ServiceProvider.GetRequiredService<INotificationService>();

            await repo.SaveMessageAsync(new ChatMessage
            {
                MessageId = $"bot-{Guid.NewGuid():N}",
                ChatType = 0,
                GroupId = groupId,
                UserId = api.BotId,
                UserName = "Bot",
                PlainText = line,
                IsBotMessage = true,
                IsProactive = true,
                AiModelUsed = verdict.Model,
                PromptTokens = verdict.PromptTokens,
                CompletionTokens = verdict.CompletionTokens,
                CreatedAt = DateTime.UtcNow
            }, ct);

            await notifier.NotifyAiResponseCompletedAsync(new
            {
                GroupId = groupId, Model = verdict.Model,
                TotalTokens = verdict.PromptTokens + verdict.CompletionTokens,
                Content = line
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "择机发言落库失败 group={GroupId}", groupId);
        }
    }

    private static string? Truncate(string? text, int max = 500)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= max ? text : text[..max];
    }
}
