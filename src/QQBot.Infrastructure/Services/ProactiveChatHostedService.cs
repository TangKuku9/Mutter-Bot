using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.Config;
using QQBot.Core.Proactive;
using QQBot.Infrastructure.Data;
using QQBot.Infrastructure.Entities;

namespace QQBot.Infrastructure.Services;

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
        var context = await _contextBuilder.BuildGroupContextAsync(
            window.GroupId, last.Text, last.SenderName, ct);

        // 窗口里已带当前消息，上下文最后一条就是它，去掉避免同一句进 prompt 两次
        IReadOnlyList<(string Role, string Content)> history = context.Messages.Count > 1
            ? context.Messages[..^1]
            : new List<(string Role, string Content)>();

        // 窗口里的图片直接交给判定这一调用来读，不另起一次"先描述图片再判断"的调用。
        // 先筛再拼提示词：动图和失效链不附，"附了几张"才不会跟实际发出的对不上。
        var images = await _ai.AttachableImagesAsync(
            window.CollectImages(ProactivePrompts.MaxAttachedImages), ct);
        var startedAt = DateTime.UtcNow;

        var verdict = await _structuredAi.GetStructuredAsync<ProactiveVerdict>(
            BuildJudgePrompt(context, config.Liveliness),
            ProactivePrompts.BuildUserPrompt(window, history, images.Count),
            images,
            context.EffectiveConfig, ct);

        if (verdict is null)
        {
            await WriteLogAsync(window, new LogNote(ProactiveOutcomes.SkippedAiUnavailable, Detail: "结构化输出解析失败"), ct);
            return;
        }

        var tookMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogInformation("择机评估完成 group={GroupId} 用时={Ms}ms 消息={Count} 附图={Images}/{Total}",
            window.GroupId, tookMs, window.Messages.Count, images.Count, window.ImageCount);

        var data = verdict.Data;
        var line = ProactivePrompts.SanitizeLine(data.Line);

        // 唯一决策口：有话就说，没话就不说。不再有 should_speak 一票否决
        // （它曾把模型已经写好的那句话连同 token 一起丢掉）。
        if (line is null)
        {
            await WriteLogAsync(window, new LogNote(ProactiveOutcomes.SkippedAiDeclined, data, Usage: verdict), ct);
            return;
        }

        var result = await _speaker.SpeakAsync(window, line, config, ct);
        await WriteLogAsync(window, new LogNote(result.Outcome, data, line, result.Detail, result.MessageId, verdict), ct);

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
    {
        var persona = string.IsNullOrWhiteSpace(context.CharacterPrompt)
            ? context.EffectiveConfig.BuildEffectiveSystemPrompt()
            : context.CharacterPrompt;

        return ProactivePrompts.SystemPrompt
            + "\n\n【本群的发言倾向】\n" + ProactivePrompts.Tendency(liveliness)
            + "\n\n【你的身份与说话风格】\n" + persona;
    }

    private async Task<ProactiveChatConfig> LoadConfigAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        return await repo.GetProactiveConfigAsync(ct);
    }

    private readonly record struct LogNote(
        string Outcome,
        ProactiveVerdict? Data = null,
        string? Line = null,
        string? Detail = null,
        string? SentMessageId = null,
        StructuredResult<ProactiveVerdict>? Usage = null);

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
                Outcome = note.Outcome,
                // 留痕里的 ShouldSpeak 由"有没有 line"推导，仅供面板统计，不再是独立决策
                ShouldSpeak = !string.IsNullOrWhiteSpace(note.Line ?? note.Data?.Line),
                Topic = Truncate(note.Data?.Topic),
                Line = Truncate(note.Line ?? note.Data?.Line),
                Reason = Truncate(note.Data?.Reason),
                Detail = Truncate(note.Detail),
                SentMessageId = note.SentMessageId,
                Model = note.Usage?.Model,
                PromptTokens = note.Usage?.PromptTokens ?? 0,
                CompletionTokens = note.Usage?.CompletionTokens ?? 0
            });
            await db.SaveChangesAsync(ct);
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
