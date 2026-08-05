using QQBot.Core.Models;

namespace QQBot.Core.Interfaces;

public interface IMessageRepository
{
    Task SaveMessageAsync(ChatMessage message, CancellationToken ct = default);
    Task SaveMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken ct = default);
    Task<List<ChatMessage>> GetGroupHistoryAsync(string groupId, int limit = 20, CancellationToken ct = default);
    Task<List<ChatMessage>> GetPrivateHistoryAsync(string userId, int limit = 20, CancellationToken ct = default);
    Task<bool> IsMessageIdExistsAsync(string messageId, CancellationToken ct = default);
    Task<(List<ChatMessage> Items, int TotalCount)> GetPagedMessagesAsync(string? groupId, int page, int pageSize, CancellationToken ct = default);
    Task<long> CountMessagesAsync(DateTime? since = null, CancellationToken ct = default);
    Task<long> CountAiCallsAsync(CancellationToken ct = default);
    Task<long> SumTokensAsync(CancellationToken ct = default);
    Task<List<DailyTokenStats>> GetDailyTokenStatsAsync(int days, CancellationToken ct = default);
    Task<List<GroupTokenStats>> GetGroupTokenStatsAsync(int days, int top, CancellationToken ct = default);
    Task<List<UserTokenStats>> GetUserTokenStatsAsync(int days, int top, CancellationToken ct = default);
}

public class DailyTokenStats
{
    public string Date { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public int CallCount { get; set; }
}

public class GroupTokenStats
{
    public string GroupId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int TotalTokens { get; set; }
    public int CallCount { get; set; }
}

public class UserTokenStats
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public int TotalTokens { get; set; }
    public int CallCount { get; set; }
}
