using QQBot.Core.Models;
using QQBot.Core.Models.QQOfficial;

namespace QQBot.Core.Interfaces;

public interface IActiveMessageService
{
    Task<ActiveMessageSendResult> SendGroupActiveAsync(string groupOpenId, string content, CancellationToken ct = default);
    Task<ActiveMessageSendResult> SendPrivateActiveAsync(string userOpenId, string content, CancellationToken ct = default);

    /// <summary>
    /// 主动推送富消息（markdown / 按钮）。受面板配置的分钟/每日频控约束
    /// （上限钳制在官方 20 条/群/分钟、1000 条/群/日内）；能力被拒时自动降级纯文本，仍只计 1 条。
    /// </summary>
    Task<ActiveMessageSendResult> SendGroupActiveRichAsync(string groupOpenId, OutboundMessage msg, CancellationToken ct = default);
    Task<ActiveMessageQuotaStatus> GetGroupQuotaStatusAsync(string groupOpenId, CancellationToken ct = default);
    Task<ActiveMessageQuotaStatus> GetUserQuotaStatusAsync(string userOpenId, CancellationToken ct = default);
    Task MarkGroupRejectedAsync(string groupOpenId, CancellationToken ct = default);
    Task MarkUserRejectedAsync(string userOpenId, CancellationToken ct = default);
    Task ClearGroupRejectedAsync(string groupOpenId, CancellationToken ct = default);
    Task ClearUserRejectedAsync(string userOpenId, CancellationToken ct = default);
}
