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

public interface ICultivationRepository
{
    Task<PlayerProfile?> GetPlayerAsync(string groupId, string memberOpenId, CancellationToken ct = default);
    Task<PlayerProfile> GetOrCreatePlayerAsync(string groupId, string memberOpenId, string nickname, CancellationToken ct = default);
    Task SavePlayerAsync(PlayerProfile player, CancellationToken ct = default);

    Task<GroupWorld> GetOrCreateGroupWorldAsync(string groupId, CancellationToken ct = default);
    Task SaveGroupWorldAsync(GroupWorld world, CancellationToken ct = default);

    Task<List<PlayerInventoryItem>> GetInventoryAsync(string groupId, string memberOpenId, CancellationToken ct = default);
    Task UpdateInventoryItemAsync(string groupId, string memberOpenId, string itemId, string itemType, string itemName, int delta, CancellationToken ct = default);

    Task<DateTime?> GetCooldownAsync(string groupId, string memberOpenId, string actionType, CancellationToken ct = default);
    Task SetCooldownAsync(string groupId, string memberOpenId, string actionType, TimeSpan duration, CancellationToken ct = default);

    Task AppendEventLogAsync(CultivationEventLog log, CancellationToken ct = default);

    Task<List<LeaderboardEntry>> GetGroupLeaderboardAsync(string groupId, int top, CancellationToken ct = default);

    /// <summary>获取群事件日志（用于 Web 看板）。</summary>
    Task<List<CultivationEventLogEntry>> GetEventLogsAsync(string groupId, int top, CancellationToken ct = default);

    /// <summary>获取所有有活跃玩家的群 ID（用于月榜主动推送）。</summary>
    Task<List<string>> GetActiveGroupIdsAsync(CancellationToken ct = default);
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
