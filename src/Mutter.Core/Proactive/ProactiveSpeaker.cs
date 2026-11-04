using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;

namespace Mutter.Core.Proactive;

public sealed record ProactiveSendResult(string Outcome, string? MessageId = null, string? Detail = null);

/// <summary>选择当前可用的被动槽位，最终策略与发送账本由统一出口负责。</summary>
public sealed class ProactiveSpeaker(ReplyDispatcher dispatcher, IQqBotApiService api, TimeProvider clock)
{
    public async Task<ProactiveSendResult> SpeakAsync(ProactiveWindow window, string line,
        ProactiveChatConfig config, CancellationToken ct = default, AiChatResponse? usage = null)
    {
        DeliveryResult result;
        try
        {
            result = await dispatcher.SendAsync(new OutgoingReply
            {
                GroupId = window.GroupId, BotId = api.BotId, IsProactive = true,
                ReplyToMessageId = window.PassiveSlotAlive(clock.GetUtcNow().UtcDateTime) ? window.LastMessageId : null,
                Content = line, Model = usage?.Model,
                PromptTokens = usage?.PromptTokens ?? 0, CompletionTokens = usage?.CompletionTokens ?? 0
            }, config.AllowActiveChannel, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 预占/回执阶段被取消，平台还没有收到发送请求。
            return new ProactiveSendResult(ProactiveOutcomes.SkippedCancelled, Detail: "发送前已取消");
        }
        var outcome = result.Sent
            ? result.Channel == "active" ? ProactiveOutcomes.SentActive : ProactiveOutcomes.SentPassive
            : result.State == DeliveryStates.Unknown ? ProactiveOutcomes.DeliveryUnknown
            : ProactiveOutcomes.FromDeliveryDetail(result.Detail);
        return new ProactiveSendResult(outcome, result.MessageId, result.Detail);
    }
}
