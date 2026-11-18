using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Models.QQOfficial;
using Mutter.Core.Proactive;
using Mutter.Core.Services.WebSearch;

namespace Mutter.Core.Services;

/// <summary>统一投递出口：最终策略、持久化意图、平台回执和聊天历史各有明确语义。</summary>
public sealed class ReplyDispatcher(
    IQqBotApiService api, IActiveMessageService active, ReplySeqTracker sequences,
    IOutgoingReplyStore store, IServiceScopeFactory scopes, TimeProvider clock,
    RichUiCapability richUi, ILogger<ReplyDispatcher> logger, GroupStickerReplies? stickerReplies = null)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recent = new();

    public async Task<DeliveryResult> SendAsync(OutgoingReply reply, bool allowActive, CancellationToken ct = default,
        SearchReplyMarkdown? searchMarkdown = null, string? stickerHash = null)
    {
        if (ct.IsCancellationRequested)
            return new DeliveryResult(DeliveryStates.Skipped, Detail: ProactiveOutcomes.SkippedCancelled);
        reply.AiOperationId ??= AiCallScope.Current?.OperationId ?? RuntimeActivity.Current?.Id;
        RuntimeActivity.Current?.Mark("delivery_policy");
        ProactiveChatConfig? limits = null;
        if (reply.IsProactive)
        {
            using var scope = scopes.CreateScope();
            var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            limits = await configs.GetProactiveConfigAsync(ct);
            var group = await configs.GetGroupConfigAsync(reply.GroupId!, ct);
            if (!limits.Enabled || group is not { IsEnabled: true, AiProactiveEnabled: true })
                return new DeliveryResult(DeliveryStates.Skipped, Detail: ProactiveOutcomes.SkippedDisabled);
            limits = limits.WithOverrides(group.ProactiveOverrides);
            if (limits.IsQuietNow(clock.GetUtcNow().UtcDateTime))
                return new DeliveryResult(DeliveryStates.Skipped, Detail: ProactiveOutcomes.SkippedQuietHours);
            allowActive = limits.AllowActiveChannel;
        }

        reply.CreatedAt = clock.GetUtcNow().UtcDateTime;
        string? blocked;
        try { blocked = await store.PrepareAsync(reply, limits, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new DeliveryResult(DeliveryStates.Skipped, Detail: ProactiveOutcomes.SkippedCancelled);
        }
        if (blocked is not null) return new DeliveryResult(DeliveryStates.Skipped, Detail: blocked);
        if (reply.IsProactive || reply.GroupId is null || reply.ReplyToMessageId is null
            || !richUi.UseMarkdown(reply.GroupId))
            searchMarkdown = null;
        PreparedGroupSticker? sticker = null;
        if (searchMarkdown is null && stickerHash is not null && stickerReplies is not null
            && reply.GroupId is not null && (reply.ReplyToMessageId is not null || reply.IsProactive && allowActive)
            && richUi.UseMarkdown(reply.GroupId))
        {
            sticker = await stickerReplies.PrepareAsync(reply.GroupId, stickerHash, reply.Content, reply.IsProactive,
                richUi.Options.MaxMarkdownChars, ct);
            searchMarkdown = sticker?.Message;
        }
        var conversation = reply.GroupId is null ? $"u:{reply.PeerUserId}" : $"g:{reply.GroupId}";
        var dedupContent = searchMarkdown?.Image is { } image ? $"{reply.Content}\0{image}" : reply.Content;
        var fingerprint = $"{conversation}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dedupContent)))}";
        DeliveryResult result;
        if (limits?.IsQuietNow(clock.GetUtcNow().UtcDateTime) == true)
            result = new DeliveryResult(DeliveryStates.Skipped, Detail: ProactiveOutcomes.SkippedQuietHours);
        else if (!reply.IsProactive && _recent.TryGetValue(fingerprint, out var last)
            && clock.GetUtcNow() - last < TimeSpan.FromSeconds(8))
            result = new DeliveryResult(DeliveryStates.Skipped, Detail: ProactiveOutcomes.SkippedDuplicate);
        else
        {
            result = await SendCoreAsync(reply, allowActive, searchMarkdown, ct);
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
        if (sticker is not null)
            await stickerReplies!.CompleteAsync(reply.GroupId!, stickerHash!, sticker, result);
        RuntimeActivity.Current?.Outcome(result.State, result.Detail);
        return result;
    }

    private async Task<DeliveryResult> SendCoreAsync(OutgoingReply reply, bool allowActive,
        SearchReplyMarkdown? searchMarkdown, CancellationToken ct)
    {
        // 只有真正开始调用平台接口之后的取消才算“结果未知”。发送前取消没有副作用，
        // 记为 skipped 才能和网络中断区分开，也不会白占主动发言配额。
        var sendStarted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            RuntimeActivity.Current?.Mark("sending");
            if (searchMarkdown is not null && reply.ReplyToMessageId is not null)
            {
                var result = await SendPassiveSearchReplyAsync(reply, searchMarkdown, ct);
                if (result is not null) return result;
            }
            else if (reply.ReplyToMessageId is not null)
            {
                var seq = sequences.Next(reply.ReplyToMessageId) ?? 0;
                var markdown = TryCreateMarkdown(reply, reply.ReplyToMessageId, seq);
                sendStarted = true;
                try
                {
                    var id = markdown is null
                        ? await SendPlainPassiveAsync(reply, reply.ReplyToMessageId, seq, ct)
                        : await SendRichPassiveAsync(reply, markdown, ct);
                    if (markdown is not null)
                    {
                        richUi.MarkOk(RichUiCapability.CapMarkdown);
                        richUi.CountMarkdownSent();
                    }
                    return new DeliveryResult(DeliveryStates.Sent, id, "passive");
                }
                catch (QqApiException ex) when (markdown is not null && QqApiErrorCodes.IsMarkdownDenied(ex.ErrCode))
                {
                    // 平台已明确拒绝 Markdown；重发纯文本是明确的降级，不是回执不明时的盲重试。
                    richUi.ReportDenied(RichUiCapability.CapMarkdown, ex.ErrCode);
                    richUi.CountPlainFallback();
                    richUi.CountDegradeRetry();
                    logger.LogWarning("Markdown 回复被拒，降级纯文本 id={Id} code={Code}", reply.Id, ex.ErrCode);
                    var retrySeq = sequences.Next(reply.ReplyToMessageId) ?? 0;
                    try
                    {
                        var id = await SendPlainPassiveAsync(reply, reply.ReplyToMessageId, retrySeq, ct);
                        return new DeliveryResult(DeliveryStates.Sent, id, "passive");
                    }
                    catch (QqApiException plainEx)
                    {
                        if (plainEx.ErrCode is >= 500 and <= 599)
                            return new DeliveryResult(DeliveryStates.Unknown, Detail: plainEx.Message);
                        // 纯文本也被平台明确拒收：恢复原有被动失败→主动通道策略，不能误记未知。
                        sendStarted = false;
                        logger.LogWarning("Passive fallback rejected id={Id} code={Code}", reply.Id, plainEx.ErrCode);
                        if (!allowActive) return new DeliveryResult(DeliveryStates.Failed, Detail: ProactiveOutcomes.FailedNoChannel);
                    }
                }
                catch (QqApiException ex)
                {
                    if (ex.ErrCode is >= 500 and <= 599)
                        return new DeliveryResult(DeliveryStates.Unknown, Detail: ex.Message);
                    // 平台明确拒收，本条没有发出去；后续改走主动通道仍属发送前。
                    sendStarted = false;
                    logger.LogWarning("Passive reply rejected id={Id} code={Code}", reply.Id, ex.ErrCode);
                    if (!allowActive) return new DeliveryResult(DeliveryStates.Failed, Detail: ProactiveOutcomes.FailedNoChannel);
                }
            }
            if (!allowActive) return new DeliveryResult(DeliveryStates.Failed, Detail: ProactiveOutcomes.FailedNoChannel);
            ct.ThrowIfCancellationRequested();
            sendStarted = true;
            var activeMarkdown = reply.IsProactive && searchMarkdown?.Image is { } image
                ? new OutboundMessage
                {
                    PlainText = reply.Content, MarkdownText = searchMarkdown.Text + "\n\n" + image,
                    MarkdownWithoutImage = searchMarkdown.Text, ForceVerifyImageResource = true
                }
                : reply.GroupId is not null ? TryCreateMarkdown(reply, null, null) : null;
            var activeResult = reply.GroupId is not null
                ? activeMarkdown is not null
                    ? await active.SendGroupActiveRichAsync(reply.GroupId, activeMarkdown, ct)
                    : await active.SendGroupActiveAsync(reply.GroupId, reply.Content, ct)
                : await active.SendPrivateActiveAsync(reply.PeerUserId!, reply.Content, ct);
            return activeResult.Success
                ? new DeliveryResult(DeliveryStates.Sent, activeResult.MessageId, "active",
                    HasImage: activeResult.HasImage, ImageMaterialRejected: activeResult.ImageMaterialRejected)
                : new DeliveryResult(activeResult.Reason == "unknown" ? DeliveryStates.Unknown : DeliveryStates.Failed,
                    Detail: activeResult.Reason is "rejected" ? ProactiveOutcomes.FailedReceiverRejected
                        : activeResult.Reason is "quota_exhausted" or "rate_limited" ? ProactiveOutcomes.FailedRateLimited
                        : activeResult.ErrorMessage ?? ProactiveOutcomes.FailedApiError,
                    ImageMaterialRejected: activeResult.ImageMaterialRejected);
        }
        catch (OperationCanceledException) when (!sendStarted)
        {
            logger.LogInformation("Delivery cancelled before platform call id={Id}", reply.Id);
            return new DeliveryResult(DeliveryStates.Skipped, Detail: ProactiveOutcomes.SkippedCancelled);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delivery outcome unknown id={Id}; will not replay", reply.Id);
            return new DeliveryResult(DeliveryStates.Unknown, Detail: ex.Message);
        }
    }

    /// <summary>明确拒收时可以更换格式。不确定时保留原发送意图。</summary>
    private async Task<DeliveryResult?> SendPassiveSearchReplyAsync(OutgoingReply reply,
        SearchReplyMarkdown search, CancellationToken ct)
    {
        var maxChars = richUi.Options.MaxMarkdownChars;
        string? markdown = search.Text.Length <= maxChars ? search.Text : null;
        var withImage = markdown is not null && search.Image is { } image
            && markdown.Length + image.Length + 2 <= maxChars;
        if (withImage) markdown += "\n\n" + search.Image;
        var materialRejected = false;

        while (true)
        {
            if (ct.IsCancellationRequested)
                return new DeliveryResult(DeliveryStates.Skipped, Detail: ProactiveOutcomes.SkippedCancelled);
            var message = new OutboundMessage
            {
                PlainText = reply.Content,
                MarkdownText = markdown,
                ForceVerifyImageResource = withImage,
                MsgId = reply.ReplyToMessageId,
                MsgSeq = sequences.Next(reply.ReplyToMessageId)
            };
            try
            {
                var id = await SendRichPassiveAsync(reply, message, ct);
                if (string.IsNullOrEmpty(id))
                    return new DeliveryResult(DeliveryStates.Unknown, Detail: "QQ 未返回确认消息 ID");
                if (markdown is not null)
                {
                    richUi.MarkOk(RichUiCapability.CapMarkdown);
                    richUi.CountMarkdownSent();
                }
                return new DeliveryResult(DeliveryStates.Sent, id, "passive",
                    HasImage: withImage, ImageMaterialRejected: materialRejected);
            }
            catch (QqApiException ex)
            {
                var code = ex.ErrCode;
                if (QqApiErrorCodes.IsDeliveryUncertain(ex))
                    return new DeliveryResult(DeliveryStates.Unknown, Detail: $"QQ 未确认送达 ({code})");
                if (ex.HttpStatus == 429 || code == 40034100)
                    return new DeliveryResult(DeliveryStates.Failed, Detail: ProactiveOutcomes.FailedRateLimited);
                if (code is 304027 or 304103 or 40034005 or 40034128)
                    return null; // 被动槽位失效，交回现有主动通道策略；不再携带图片。
                if (markdown is null)
                    return new DeliveryResult(DeliveryStates.Failed, Detail: $"QQ 拒收搜索回复 ({code})");

                if (withImage && QqApiErrorCodes.IsImageDenied(code))
                {
                    materialRejected = QqApiErrorCodes.IsImageMaterialDenied(code);
                    markdown = search.Text;
                }
                else if (QqApiErrorCodes.IsMarkdownDenied(code) || code is 40054007 or 40054010 or 304003)
                {
                    if (code is 304036 or 40034127 or 50056)
                        richUi.ReportDenied(RichUiCapability.CapMarkdown, code);
                    richUi.CountPlainFallback();
                    markdown = null;
                }
                else
                    return new DeliveryResult(DeliveryStates.Failed, Detail: $"QQ 拒收搜索回复 ({code})");

                withImage = false;
                richUi.CountDegradeRetry();
                logger.LogInformation("搜索回复降级 id={Id} code={Code} trace={TraceId}", reply.Id, code, ex.TraceId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "搜索回复未确认 id={Id}", reply.Id);
                return new DeliveryResult(DeliveryStates.Unknown, Detail: "QQ 发送结果未确认");
            }
        }
    }

    private OutboundMessage? TryCreateMarkdown(OutgoingReply reply, string? msgId, int? msgSeq)
    {
        if (!richUi.UseMarkdown(reply.GroupId)) return null;
        var formatted = AiReplyMarkdownFormatter.Format(reply.Content, msgId, msgSeq);
        return formatted.MarkdownText is { Length: > 0 } markdown
            && markdown.Length <= richUi.Options.MaxMarkdownChars ? formatted : null;
    }

    private Task<string?> SendPlainPassiveAsync(OutgoingReply reply, string? msgId, int? msgSeq, CancellationToken ct) =>
        reply.GroupId is not null
            ? api.SendGroupMessageAsync(reply.GroupId, reply.Content, msgId, msgSeq, ct)
            : api.SendPrivateMessageAsync(reply.PeerUserId!, reply.Content, msgId, msgSeq, ct);

    private Task<string?> SendRichPassiveAsync(OutgoingReply reply, OutboundMessage message, CancellationToken ct) =>
        reply.GroupId is not null
            ? api.SendGroupRichAsync(reply.GroupId, message, ct)
            : api.SendPrivateRichAsync(reply.PeerUserId!, message, ct);
}
