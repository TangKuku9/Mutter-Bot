namespace QQBot.Core.Game.Models;

/// <summary>
/// 群世界状态领域模型（不耦合 EF）。
/// </summary>
public class GroupWorld
{
    public string GroupId { get; set; } = string.Empty;
    public int WorldQiLevel { get; set; } = 1;
    public DateTime WorldQiUpdatedAt { get; set; } = DateTime.UtcNow;
    public string? PendingEventJson { get; set; }
    public DateTime? PendingEventExpiresAt { get; set; }
    public int PendingEventParticipants { get; set; }
    public DateTime LastEventGeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime LeaderboardSnapshotAt { get; set; } = DateTime.UtcNow;
    public string? TribulationSessionJson { get; set; }

    /// <summary>群灵气浓度对全员挂机效率的乘数：1 + level*0.1。</summary>
    public double WorldQiFactor => 1.0 + WorldQiLevel * 0.1;
}
