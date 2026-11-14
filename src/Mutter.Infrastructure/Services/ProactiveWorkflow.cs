using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Proactive;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;

namespace Mutter.Infrastructure.Services;

/// <summary>
/// 单次主动发言工作流：观察快照、上下文、判定、按需检索、最终策略与投递。
/// </summary>
public sealed class ProactiveWorkflow
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProactiveObserveService _observe;
    private readonly ProactiveSpeaker _speaker;
    private readonly IStructuredAiService _structuredAi;
    private readonly IAiChatService _ai;
    private readonly IContextBuilder _contextBuilder;
    private readonly ILogger<ProactiveWorkflow> _logger;
    private readonly TimeProvider _clock;
    private readonly RuntimeOptions _runtime;

    public ProactiveWorkflow(
        IServiceScopeFactory scopeFactory,
        ProactiveObserveService observe,
        ProactiveSpeaker speaker,
        IStructuredAiService structuredAi,
        IAiChatService ai,
        IContextBuilder contextBuilder,
        ILogger<ProactiveWorkflow> logger, TimeProvider clock, RuntimeOptions runtime)
    {
        _scopeFactory = scopeFactory;
        _observe = observe;
        _speaker = speaker;
        _structuredAi = structuredAi;
        _ai = ai;
        _contextBuilder = contextBuilder;
        _logger = logger;
        _clock = clock;
        _runtime = runtime;
    }

    public async Task RunAsync(ProactiveWindow window, CancellationToken ct)
    {
        var config = await LoadConfigAsync(ct);
        using var scope = _scopeFactory.CreateScope();
        var group = await scope.ServiceProvider.GetRequiredService<IConfigRepository>()
            .GetGroupConfigAsync(window.GroupId, ct);
        if (!config.Enabled || group is not { IsEnabled: true, AiProactiveEnabled: true })
        {
            await WriteLogAsync(window, new LogNote(ProactiveOutcomes.SkippedDisabled), ct);
            return;
        }
        config = config.WithOverrides(group.ProactiveOverrides);
        var blocked = _observe.BlockReason(window.GroupId, config, _clock.GetUtcNow().UtcDateTime);
        if (blocked is not null)
        {
            await WriteLogAsync(window, new LogNote(blocked), ct);
            return;
        }
        blocked = await scope.ServiceProvider.GetRequiredService<IOutgoingReplyStore>()
            .CheckProactiveAsync(window.GroupId, config, ct);
        if (blocked is not null)
        {
            await WriteLogAsync(window, new LogNote(blocked), ct);
            return;
        }
        // 群友原话要不要留，跟着群里的「围观并记录全部消息」走；只 @ 的群不把观察窗口
        // 抄进决策留痕，否则等于绕过群级设置把没入库的聊天记录存了下来。
        var recordWindow = group?.TriggerMode == 2;
        var aiEvaluated = false;
        try
        {
            using var operation = AiCallScope.Begin(ct, "proactive", new(GroupId: window.GroupId));
            operation.Stage = "judge";
            await SpeakIfAllowedAsync(window, config, recordWindow, () => aiEvaluated = true, ct, operation.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            // 队列令牌仍在，说明是模型 120 秒期限到了；这不是发送取消。
            _logger.LogWarning(ex, "择机发言评估超时 group={GroupId}", window.GroupId);
            await WriteLogAsync(window, new LogNote(ProactiveOutcomes.FailedAiCall, Detail: "AI 调用超时"), ct, aiEvaluated: aiEvaluated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "择机发言评估失败 group={GroupId}", window.GroupId);
            await WriteLogAsync(window, new LogNote(ProactiveOutcomes.FailedAiCall, Detail: ex.Message), ct, aiEvaluated: aiEvaluated);
        }
    }

    private async Task SpeakIfAllowedAsync(ProactiveWindow window, ProactiveChatConfig config,
        bool recordWindow, Action onEvaluationStarted, CancellationToken ct, CancellationToken aiToken)
    {
        if (!await _structuredAi.IsAiConfiguredAsync(aiToken))
        {
            await WriteLogAsync(window, new LogNote(ProactiveOutcomes.SkippedAiUnavailable, Detail: "AI 未配置"), ct);
            return;
        }

        var last = window.Messages[^1];
        var nowUtc = _clock.GetUtcNow().UtcDateTime;

        // 只继承人设 / 模型 / 接口地址这类"这个 bot 是谁"的设置；
        // 「上下文条数」与「输出上限」改用择机自己的两个参数。
        // 此前这里直接吃 AI 面板的全局值，于是同一件事被两个面板的旋钮叠乘着控制。
        var context = await _contextBuilder.BuildGroupContextAsync(
            window.GroupId, last.Text, last.SenderName,
            ai => ai with
            {
                MaxContextMessages = config.HistoryMaxMessages,
                // 择机输出上限独立配置，但仍须给上下文留出输入空间；否则小窗口模型会得到 0 预算。
                MaxTokens = AiRequestBudget.ClampOutputTokens(config.EvalMaxTokens, ai.ContextWindowTokens)
            }, aiToken);

        // 窗口消息同时也躺在上下文历史里，渲染出来是同一行「[昵称]: 正文」。
        // 不剔除就会在同一份 prompt 里被喂两遍（背景一次、当前窗口一次），
        // 既白烧 token，又让模型把正在聊的话读成"更早的旧事"。
        var history = window.ExcludeFrom(context.Messages);

        // 窗口里的图片直接交给判定这一调用来读，不另起一次"先描述图片再判断"的调用。
        // 先筛再拼提示词：动图和失效链不附，"附了几张"才不会跟实际发出的对不上。
        var images = await _ai.AttachableImagesAsync(
            window.CollectImages(config.MaxAttachedImages), aiToken);
        var startedAt = nowUtc;

        // 群记忆：全局开关关了连库都不查；群级覆盖 false 表示这个群单独停用注入（摘要仍保留）
        var memory = await LoadGroupMemoryAsync(window.GroupId, config, aiToken);
        // 记的就是真正喂进去的那段：与 BuildUserPrompt 共用同一个字符串，留痕不会和实际对不上
        var memoryText = memory?.Summary?.Trim();
        var memoryCount = string.IsNullOrWhiteSpace(memoryText) ? 0 : 1;
        var memoryChars = memoryCount == 0 ? 0 : memoryText!.Length;
        var automatic = AutomaticMemoryRecall.Empty;
        using var memoryScope = _scopeFactory.CreateScope();
        var memoryGroup = await memoryScope.ServiceProvider.GetRequiredService<IConfigRepository>().GetGroupConfigAsync(window.GroupId, aiToken);
        if (config.MemoryInjectionEnabled && memoryGroup?.MemoryInjectionEnabled != false)
            automatic = await memoryScope.ServiceProvider.GetRequiredService<IAutomaticMemoryRecall>().RecallAsync(window.GroupId,
                string.Join("\n", window.Messages.TakeLast(3).Select(m => m.Text)), window.LastMessageUtcAt, ct: aiToken);

        // 择机判定是内部决策，不该继承全局联网开关：它只是在判断“要不要接话”，
        // 每条群消息都去检索既慢又费 token。联网只服务于最终话术，不服务于判断。
        var judgeConfig = context.EffectiveConfig with { WebSearchEnabled = false };

        var judgePrompt = BuildJudgePrompt(context, config.Liveliness);
        var userPrompt = ProactivePrompts.BuildUserPrompt(window, history, images.Count, memoryText, nowUtc, automatic.Text);
        if (automatic.Count > 0 && judgePrompt.Length + userPrompt.Length + images.Count * AiRequestBudget.ImageTokens + 136
            > AiRequestBudget.InputBudget(judgeConfig.MaxTokens, judgeConfig.ContextWindowTokens, judgeConfig.MaxInputTokens))
        {
            automatic = AutomaticMemoryRecall.Empty;
            userPrompt = ProactivePrompts.BuildUserPrompt(window, history, images.Count, memoryText, nowUtc);
        }
        memoryCount += automatic.Count;
        memoryChars += automatic.Text.Length;
        aiToken.ThrowIfCancellationRequested();
        // 将实际评估阶段与最终投递结果分开，发送前的开关/时段复核不能抹掉已发生的评估。
        onEvaluationStarted();
        var outcome = await _structuredAi.GetStructuredOutcomeAsync<ProactiveVerdict>(
            judgePrompt,
            userPrompt,
            images,
            judgeConfig, aiToken);

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
                    MemoryCount: memoryCount, MemoryPromptChars: memoryChars,
                    ImageCount: images.Count, DiagnosticsKnown: true), ct, recordWindow, aiEvaluated: true);
            return;
        }

        var tookMs = (long)(_clock.GetUtcNow().UtcDateTime - startedAt).TotalMilliseconds;
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
                    MemoryCount: memoryCount, MemoryPromptChars: memoryChars,
                    ImageCount: images.Count, DiagnosticsKnown: true), ct, recordWindow, aiEvaluated: true);
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
            // 判定与改写共享同一条根期限。上游慢时剩余预算可能不够再撑一次完整调用，
            // 与其让改写撞死在期限上（连带把这次发言整个打成超时），不如直接发草稿——改写只是锦上添花。
            if (AiCallScope.Current is { } scope && scope.Remaining < TimeSpan.FromSeconds(_runtime.RewriteMinRemainingSeconds))
            {
                _logger.LogInformation(
                    "生成剩余预算 {Seconds}s 不足以联网改写，直接发草稿 group={GroupId}",
                    (int)scope.Remaining.TotalSeconds, window.GroupId);
            }
            else
            {
                if (AiCallScope.Current is { } operation) operation.Stage = "rewrite";
                searchUsed = true;
                var speech = await RewriteWithSearchAsync(
                    window, context, data, line, searchQuery!, nowUtc, aiToken);
                speechPromptTokens = speech.PromptTokens;
                speechCompletionTokens = speech.CompletionTokens;

                if (speech.Declined)
                {
                    await WriteLogAsync(window,
                        new LogNote(ProactiveOutcomes.SkippedAfterSearch, data, null,
                            "联网核实后模型选择不说", null, outcome,
                            memoryCount, memoryChars, searchQuery, searchUsed,
                            speechPromptTokens, speechCompletionTokens, images.Count, true), ct, recordWindow, aiEvaluated: true);
                    return;
                }

                if (speech.Line is not null)
                    line = speech.Line;
                else
                    searchDetail = $"联网改写失败，已发草稿：{Truncate(speech.Error, 160)}";
            }
        }

        var usage = new AiChatResponse
        {
            Model = verdict.Model,
            PromptTokens = outcome.PromptTokens + speechPromptTokens,
            CompletionTokens = outcome.CompletionTokens + speechCompletionTokens
        };
        // 判定/改写成功后，投递不能再受模型 120 秒期限约束；给平台发送独立的 30 秒窗口。
        using var deliveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deliveryDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await _speaker.SpeakAsync(window, line, config, deliveryDeadline.Token, usage);
        if (result.Outcome is ProactiveOutcomes.SentPassive or ProactiveOutcomes.SentActive)
            _observe.MarkSent(window.GroupId, _clock.GetUtcNow().UtcDateTime);
        var logDetail = string.IsNullOrWhiteSpace(searchDetail)
            ? result.Detail
            : string.IsNullOrWhiteSpace(result.Detail)
                ? searchDetail
                : $"{searchDetail}；{result.Detail}";
        await WriteLogAsync(window,
            new LogNote(result.Outcome, data, line, logDetail, result.MessageId, outcome,
                memoryCount, memoryChars, searchQuery, searchUsed,
                speechPromptTokens, speechCompletionTokens, images.Count, true), ct, recordWindow, aiEvaluated: true);

        if (result.Outcome is not (ProactiveOutcomes.SentPassive or ProactiveOutcomes.SentActive))
        {
            _logger.LogWarning("择机发言未送达 group={GroupId} outcome={Outcome} detail={Detail}",
                window.GroupId, result.Outcome, result.Detail);
            return;
        }

        await NotifySentAsync(window.GroupId, line, usage);
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
        var startedAt = _clock.GetUtcNow().UtcDateTime;

        try
        {
            // 带检索词调用：外搜路线直接用 query 预检索注入；没有外搜时回落内建通道
            //（上游若确定性拒绝搜索，AiChatService 会自动退回不检索重发，不会把改写打没）。
            var response = await _ai.ChatWithSearchQueryAsync(
                systemPrompt,
                new List<(string Role, string Content)> { ("user", userPrompt) },
                context.EffectiveConfig, query, ct);

            var line = ProactivePrompts.SanitizeLine(response.Content);
            var tookMs = (long)(_clock.GetUtcNow().UtcDateTime - startedAt).TotalMilliseconds;
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
        int SpeechCompletionTokens = 0,
        int ImageCount = 0,
        bool DiagnosticsKnown = false);

    /// <summary>第二阶段结果。Line 与 Declined 互斥；两者都空表示调用失败，调用方退回草稿。</summary>
    private readonly record struct SpeechRewrite(
        string? Line, bool Declined, string? Error, int PromptTokens, int CompletionTokens);

    private async Task WriteLogAsync(ProactiveWindow window, LogNote note, CancellationToken ct,
        bool captureWindow = false, bool aiEvaluated = false)
    {
        RuntimeActivity.Current?.Outcome(note.Outcome);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ProactiveChatLogs.Add(new ProactiveChatLogEntity
            {
                GroupId = window.GroupId,
                EvaluatedAt = _clock.GetUtcNow().UtcDateTime,
                MessageCount = window.Messages.Count,
                AiEvaluated = aiEvaluated,
                // 只有走到模型评估（或明确的门禁结果）才知道诊断值；异常兜底没有 images/记忆
                // 的实际值，写 0 会被详情当成“确实没带”，所以保持旧格式让读取端显示未知。
                DetailVersion = note.DiagnosticsKnown || (!aiEvaluated && ProactiveOutcomes.IsBeforeAi(note.Outcome))
                    ? ProactiveChatLogEntity.PrivacyGatedWindowVersion
                    : 0,
                // 观察窗口可能包含群里没入库的原话，只在群允许记录全部消息、
                // 且这次真的进入模型评估时保存；冷却/日限/关闭等门禁结果不落快照。
                WindowSnapshotJson = captureWindow ? ProactiveLogSnapshot.Capture(window) : null,
                ImageCount = note.ImageCount,
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
                EvaluatedAt = _clock.GetUtcNow().UtcDateTime
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "择机发言留痕写入失败 group={GroupId}", window.GroupId);
        }
    }

    private async Task NotifySentAsync(string groupId, string line, AiChatResponse usage)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var notifier = scope.ServiceProvider.GetRequiredService<INotificationService>();
            await notifier.NotifyAiResponseCompletedAsync(new
            {
                GroupId = groupId, Model = usage.Model,
                TotalTokens = usage.TotalTokens, Content = line
            });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "择机发言通知失败 group={GroupId}", groupId); }
    }

    private static string? Truncate(string? text, int max = 500)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= max ? text : text[..max];
    }
}
