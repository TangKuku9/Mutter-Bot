using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Proactive;

namespace Mutter.Core.Services;

/// <summary>统一投递出口：最终策略、持久化意图、平台回执和聊天历史各有明确语义。</summary>
public sealed class ReplyDispatcher(
    IQqBotApiService api, IActiveMessageService active, ReplySeqTracker sequences,
    IOutgoingReplyStore store, IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ReplyDispatcher> logger)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recent = new();

    public async Task<DeliveryResult> SendAsync(OutgoingReply reply, bool allowActive, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ProactiveChatConfig? limits = null;
        if (reply.IsProactive)
        {
            using var scope = scopes.CreateScope();
            var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            limits = await configs.GetProactiveConfigAsync(ct);
            var group = await configs.GetGroupConfigAsync(reply.GroupId!, ct);
            if (!limits.Enabled || group is not { IsEnabled: true, AiProactiveEnabled: true })
                return new DeliveryResult(DeliveryStates.Skipped, Detail: ProactiveOutcomes.SkippedDisabled);
            if (limits.IsQuietNow(clock.GetUtcNow().UtcDateTime))
                return new DeliveryResult(DeliveryStates.Skipped, Detail: ProactiveOutcomes.SkippedQuietHours);
            allowActive = limits.AllowActiveChannel;
        }

        reply.CreatedAt = clock.GetUtcNow().UtcDateTime;
        var blocked = await store.PrepareAsync(reply, limits, ct);
        if (blocked is not null) return new DeliveryResult(DeliveryStates.Skipped, Detail: blocked);
        var conversation = reply.GroupId is null ? $"u:{reply.PeerUserId}" : $"g:{reply.GroupId}";
        var fingerprint = $"{conversation}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reply.Content)))}";
        DeliveryResult result;
        if (!reply.IsProactive && _recent.TryGetValue(fingerprint, out var last)
            && clock.GetUtcNow() - last < TimeSpan.FromSeconds(8))
            result = new DeliveryResult(DeliveryStates.Skipped, Detail: "skipped_duplicate");
        else
        {
            result = await SendCoreAsync(reply, allowActive, ct);
            if (result.Sent)
            {
                _recent[fingerprint] = clock.GetUtcNow();
                if (_recent.Count > 512)
                    foreach (var item in _recent.Where(p => clock.GetUtcNow() - p.Value > TimeSpan.FromSeconds(8)))
                        _recent.TryRemove(item.Key, out _);
            }
        }

        // 取消/超时可能发生在平台已接受之后，记账使用独立的有限截止时间。
        using var completionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await store.CompleteAsync(reply, result, completionDeadline.Token); }
        catch (Exception ex)
        {
            // 已有平台回执不能因数据库故障变成发送失败，更不能触发再次发送。
            logger.LogError(ex, "Delivery receipt could not be persisted id={Id} state={State}", reply.Id, result.State);
            result = result with { Detail = $"{result.Detail}；投递回执暂未写入，待发送记录保留供核查" };
        }
        return result;
    }

    private async Task<DeliveryResult> SendCoreAsync(OutgoingReply reply, bool allowActive, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (reply.ReplyToMessageId is not null)
            {
                try
                {
                    var seq = sequences.Next(reply.ReplyToMessageId);
                    var id = reply.GroupId is not null
                        ? await api.SendGroupMessageAsync(reply.GroupId, reply.Content, reply.ReplyToMessageId, seq, ct)
                        : await api.SendPrivateMessageAsync(reply.PeerUserId!, reply.Content, reply.ReplyToMessageId, seq, ct);
                    return new DeliveryResult(DeliveryStates.Sent, id, "passive");
                }
                catch (QqApiException ex)
                {
                    if (ex.ErrCode is >= 500 and <= 599)
                        return new DeliveryResult(DeliveryStates.Unknown, Detail: ex.Message);
                    logger.LogWarning("Passive reply rejected id={Id} code={Code}", reply.Id, ex.ErrCode);
                    if (!allowActive) return new DeliveryResult(DeliveryStates.Failed, Detail: ProactiveOutcomes.FailedNoChannel);
                }
            }
            if (!allowActive) return new DeliveryResult(DeliveryStates.Failed, Detail: ProactiveOutcomes.FailedNoChannel);
            ct.ThrowIfCancellationRequested();
            var activeResult = reply.GroupId is not null
                ? await active.SendGroupActiveAsync(reply.GroupId, reply.Content, ct)
                : await active.SendPrivateActiveAsync(reply.PeerUserId!, reply.Content, ct);
            return activeResult.Success
                ? new DeliveryResult(DeliveryStates.Sent, activeResult.MessageId, "active")
                : new DeliveryResult(activeResult.Reason == "unknown" ? DeliveryStates.Unknown : DeliveryStates.Failed,
                    Detail: activeResult.Reason is "rejected" ? ProactiveOutcomes.FailedReceiverRejected
                        : activeResult.Reason is "quota_exhausted" or "rate_limited" ? ProactiveOutcomes.FailedRateLimited
                        : activeResult.ErrorMessage ?? ProactiveOutcomes.FailedApiError);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delivery outcome unknown id={Id}; will not replay", reply.Id);
            return new DeliveryResult(DeliveryStates.Unknown, Detail: ex.Message);
        }
    }
}
