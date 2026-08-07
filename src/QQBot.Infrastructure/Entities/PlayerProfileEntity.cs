namespace QQBot.Infrastructure.Entities;

/// <summary>
/// 修仙RPG 玩家存档。主键为 (GroupId, MemberOpenId) 复合唯一。
/// </summary>
public class PlayerProfileEntity
{
    public long Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string MemberOpenId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;

    // 修仙核心
    public int RealmIndex { get; set; } = 0;        // 境界序号（0=炼气一层）
    public long CultivationPoints { get; set; } = 0; // 当前修为
    public int SpiritRootType { get; set; } = 0;    // 灵根类型（五行+变异）
    public int SpiritRootGrade { get; set; } = 3;   // 灵根品级（1最优-5最差）
    public string? TechniqueId { get; set; }        // 当前主修功法
    public double PendingBreakthroughBonus { get; set; } = 0; // 待消耗的突破加成（丹药）

    // 挂机结算
    public DateTime LastSettledAt { get; set; } = DateTime.UtcNow;
    public double IdleRateMultiplier { get; set; } = 1.0;

    // 资源
    public long SpiritStones { get; set; } = 0;
    public int Lifespan { get; set; } = 200;

    // 日常
    public int CheckinStreak { get; set; } = 0;
    public DateTime? LastCheckinAt { get; set; }

    // 元数据
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    /// <summary>乐观并发令牌：每次存档自增，配合 EF IsConcurrencyToken，
    /// 令并发覆写（用旧值整行写回）直接抛 DbUpdateConcurrencyException 而非静默回退。</summary>
    public int Version { get; set; }
}
