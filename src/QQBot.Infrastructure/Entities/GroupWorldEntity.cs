namespace QQBot.Infrastructure.Entities;

/// <summary>
/// 群世界状态。主键为 GroupId，一个群一套世界存档。
/// </summary>
public class GroupWorldEntity
{
    public string GroupId { get; set; } = string.Empty;
    public int WorldQiLevel { get; set; } = 1;          // 群灵气浓度（影响全员挂机效率）
    public DateTime WorldQiUpdatedAt { get; set; } = DateTime.UtcNow;
    public string? PendingEventJson { get; set; }        // 当前未结束的群级事件（AI生成）
    public DateTime? PendingEventExpiresAt { get; set; }
    public int PendingEventParticipants { get; set; } = 0;
    public DateTime LastEventGeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime LeaderboardSnapshotAt { get; set; } = DateTime.UtcNow;
    public string? TribulationSessionJson { get; set; }   // 渡劫护法会话（JSON）
}
