namespace QQBot.Infrastructure.Entities;

/// <summary>
/// 冷却记录。索引 (GroupId, MemberOpenId, ActionType)。
/// </summary>
public class GameCooldownEntity
{
    public long Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string MemberOpenId { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty; // breakthrough/alchemy/explore/daily/referee
    public DateTime AvailableAt { get; set; }              // 冷却结束时间
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
