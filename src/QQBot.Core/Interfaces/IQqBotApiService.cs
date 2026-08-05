using QQBot.Core.Models;

namespace QQBot.Core.Interfaces;

public interface IQqBotApiService
{
    string BotId { get; }
    string BotName { get; }

    Task InitializeAsync(CancellationToken ct = default);
    Task SendGroupMessageAsync(string groupOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default);
    Task SendPrivateMessageAsync(string userOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default);
    Task<string?> UploadMediaAsync(string groupOpenId, int fileType, string url, CancellationToken ct = default);
}
