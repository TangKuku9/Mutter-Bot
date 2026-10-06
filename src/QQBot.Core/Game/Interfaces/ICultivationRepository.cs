using QQBot.Core.Game.Models;

namespace QQBot.Core.Game.Interfaces;

public record PlayerInventoryItem(string ItemId, string ItemType, string ItemName, int Quantity);

public record CultivationEventLog(
    string GroupId,
    string? MemberOpenId,
    string EventType,
    string DetailJson,
    string? AiRawOutput,
    string AppliedChangesJson);

public record LeaderboardEntry(string MemberOpenId, string Nickname, int RealmIndex, long CultivationPoints);

public record PlayerSummary(
    string MemberOpenId,
    string Nickname,
    int RealmIndex,
    long CultivationPoints,
    long SpiritStones,
    int Lifespan,
    DateTime UpdatedAt);

public interface ICultivationRepository
{
    Task<PlayerProfile?> GetPlayerAsync(string groupId, string memberOpenId, CancellationToken ct = default);
    Task<PlayerProfile> GetOrCreatePlayerAsync(string groupId, string memberOpenId, string nickname, CancellationToken ct = default);
    Task SavePlayerAsync(PlayerProfile player, CancellationToken ct = default);

    Task<GroupWorld> GetOrCreateGroupWorldAsync(string groupId, CancellationToken ct = default);
    /// <summary>只读获取群世界（不存在返回 null，不创建；用于 Web 看板）。</summary>
    Task<GroupWorld?> GetGroupWorldAsync(string groupId, CancellationToken ct = default);
    Task SaveGroupWorldAsync(GroupWorld world, CancellationToken ct = default);

    Task<List<PlayerInventoryItem>> GetInventoryAsync(string groupId, string memberOpenId, CancellationToken ct = default);
    Task UpdateInventoryItemAsync(string groupId, string memberOpenId, string itemId, string itemType, string itemName, int delta, CancellationToken ct = default);

    Task<DateTime?> GetCooldownAsync(string groupId, string memberOpenId, string actionType, CancellationToken ct = default);

    /// <summary>
    /// 一次查多个动作的冷却结束时间（菜单要标注“剩 5时19分”，不能每个按钮各查一次）。
    /// 只返回仍在冷却中的项；不在冷却的动作不会出现在结果里。
    /// </summary>
    Task<Dictionary<string, DateTime>> GetActiveCooldownsAsync(
        string groupId, string memberOpenId, IReadOnlyCollection<string> actionTypes, CancellationToken ct = default);
    Task SetCooldownAsync(string groupId, string memberOpenId, string actionType, TimeSpan duration, CancellationToken ct = default);

    Task AppendEventLogAsync(CultivationEventLog log, CancellationToken ct = default);

    Task<List<LeaderboardEntry>> GetGroupLeaderboardAsync(string groupId, int top, CancellationToken ct = default);

    /// <summary>获取群内全部玩家存档摘要（用于面板运维）。</summary>
    Task<List<PlayerSummary>> GetGroupPlayersAsync(string groupId, CancellationToken ct = default);

    /// <summary>获取群事件日志（用于 Web 看板）。</summary>
    Task<List<CultivationEventLogEntry>> GetEventLogsAsync(string groupId, int top, CancellationToken ct = default);

    /// <summary>获取所有有活跃玩家的群 ID（用于月榜主动推送）。</summary>
    Task<List<string>> GetActiveGroupIdsAsync(CancellationToken ct = default);

    /// <summary>统计玩家自指定时间起各事件类型的动作次数（用于每日任务进度，只读）。</summary>
    Task<Dictionary<string, int>> GetPlayerActionCountsAsync(string groupId, string memberOpenId, DateTime sinceUtc, CancellationToken ct = default);
}

public record CultivationEventLogEntry(
    long Id,
    string GroupId,
    string? MemberOpenId,
    string EventType,
    string DetailJson,
    string? AiRawOutput,
    string AppliedChangesJson,
    DateTime CreatedAt);
