using QQBot.Core.Models;

namespace QQBot.Core.Interfaces;

public interface IActiveMessageService
{
    Task<ActiveMessageSendResult> SendGroupActiveAsync(string groupOpenId, string content, CancellationToken ct = default);
    Task<ActiveMessageSendResult> SendPrivateActiveAsync(string userOpenId, string content, CancellationToken ct = default);
    Task<ActiveMessageQuotaStatus> GetGroupQuotaStatusAsync(string groupOpenId, CancellationToken ct = default);
    Task<ActiveMessageQuotaStatus> GetUserQuotaStatusAsync(string userOpenId, CancellationToken ct = default);
    Task MarkGroupRejectedAsync(string groupOpenId, CancellationToken ct = default);
    Task MarkUserRejectedAsync(string userOpenId, CancellationToken ct = default);
    Task ClearGroupRejectedAsync(string groupOpenId, CancellationToken ct = default);
    Task ClearUserRejectedAsync(string userOpenId, CancellationToken ct = default);
}
