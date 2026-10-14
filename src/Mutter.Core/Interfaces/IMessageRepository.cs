using Mutter.Core.Models;

namespace Mutter.Core.Interfaces;

public interface IMessageRepository
{
    Task SaveMessageAsync(ChatMessage message, CancellationToken ct = default);
    Task SaveMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken ct = default);
    Task<List<ChatMessage>> GetGroupHistoryAsync(string groupId, int limit = 20, CancellationToken ct = default);
    /// <summary>某个私聊对象的双向历史。since 用于丢弃早已断档的旧话题，避免污染新对话。</summary>
    Task<List<ChatMessage>> GetPrivateHistoryAsync(
        string userId, int limit = 20, DateTime? since = null, CancellationToken ct = default);
    Task<bool> IsMessageIdExistsAsync(string messageId, CancellationToken ct = default);

    /// <summary>
    /// 把已入库的一条消息补记为"@ 的是 bot"。全量事件先入库、@ 事件后到时用，
    /// 否则面板会把被 @ 的消息显示成纯围观。返回被更新的行数（0 表示这条本来没落库）。
    /// </summary>
    Task<int> MarkAtBotAsync(string messageId, CancellationToken ct = default);
    /// <summary>
    /// 分页取聊天历史。<paramref name="source"/> 为来源筛选：
    /// at=群友@机器人、observe=群友未@（围观）、bot=机器人应答、proactive=机器人择机插话，null=全部。
    /// </summary>
    Task<(List<ChatMessage> Items, int TotalCount)> GetPagedMessagesAsync(
        string? groupId, int page, int pageSize, string? source = null, CancellationToken ct = default);
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
