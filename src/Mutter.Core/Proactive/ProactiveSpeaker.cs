using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Microsoft.Extensions.Logging;

namespace Mutter.Core.Proactive;

/// <summary>发言执行结果：Outcome 取 <see cref="ProactiveOutcomes"/> 中的常量。</summary>
public sealed record ProactiveSendResult(string Outcome, string? MessageId = null, string? Detail = null);

/// <summary>
/// 择机发言的出口。按面板选定的通道策略发送：
/// 被动槽位仍在官方 5 分钟时效内就带 msg_id（不占主动频控、不会被判拒收），
/// 槽位失效且允许时才退化为 msg_id=null 的真主动消息。
/// </summary>
public sealed class ProactiveSpeaker
{
    private readonly IQqBotApiService _apiService;
    private readonly IActiveMessageService _activeMessage;
    private readonly ReplySeqTracker _seqTracker;
    private readonly ILogger<ProactiveSpeaker> _logger;

    public ProactiveSpeaker(
        IQqBotApiService apiService,
        IActiveMessageService activeMessage,
        ReplySeqTracker seqTracker,
        ILogger<ProactiveSpeaker> logger)
    {
        _apiService = apiService;
        _activeMessage = activeMessage;
        _seqTracker = seqTracker;
        _logger = logger;
    }

    public async Task<ProactiveSendResult> SpeakAsync(
        ProactiveWindow window, string line, ProactiveChatConfig config, CancellationToken ct = default)
    {
        var passiveAlive = window.PassiveSlotAlive(DateTime.UtcNow);

        if (passiveAlive)
        {
            try
            {
                var sentId = await _apiService.SendGroupMessageAsync(
                    window.GroupId, line, window.LastMessageId, _seqTracker.Next(window.LastMessageId), ct);
                return new ProactiveSendResult(ProactiveOutcomes.SentPassive, sentId);
            }
            catch (QqApiException ex)
            {
                _logger.LogWarning(
                    "择机发言被动槽位被拒，尝试主动通道 group={GroupId} err={ErrCode}", window.GroupId, ex.ErrCode);
            }
            catch (Exception ex)
            {
                // 传输层异常无法判断消息是否已投递，此时再补一条主动消息可能重复发送，宁可本次不说
                _logger.LogWarning(ex, "择机发言发送异常，放弃本条 group={GroupId}", window.GroupId);
                return new ProactiveSendResult(ProactiveOutcomes.FailedApiError, null, ex.Message);
            }
        }

        if (!config.AllowActiveChannel)
            return new ProactiveSendResult(
                ProactiveOutcomes.FailedNoChannel, null,
                passiveAlive ? "被动被拒且未开启主动通道" : "被动槽已过期且未开启主动通道");

        var active = await _activeMessage.SendGroupActiveAsync(window.GroupId, line, ct);
        if (active.Success)
            return new ProactiveSendResult(ProactiveOutcomes.SentActive, active.MessageId);

        var outcome = active.Reason switch
        {
            "rejected" => ProactiveOutcomes.FailedReceiverRejected,
            "quota_exhausted" or "rate_limited" => ProactiveOutcomes.FailedRateLimited,
            _ => ProactiveOutcomes.FailedApiError
        };
        return new ProactiveSendResult(outcome, null, active.ErrorMessage ?? active.Reason);
    }
}
