using QQBot.Core.Models;

namespace QQBot.Core.Interfaces;

public interface IContextBuilder
{
    Task<BuildContextResult> BuildGroupContextAsync(string groupId, string currentMessage, string senderName, CancellationToken ct = default);
    Task<BuildContextResult> BuildPrivateContextAsync(string userId, string currentMessage, string senderName = "User", CancellationToken ct = default);
}
