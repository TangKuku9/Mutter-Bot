namespace QQBot.Core.Game.Models;

/// <summary>
/// 玩家存档领域模型（不耦合 EF）。
/// </summary>
public class PlayerProfile
{
    public long Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string MemberOpenId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;

    public int RealmIndex { get; set; }
    public long CultivationPoints { get; set; }
    public int SpiritRootType { get; set; }
    public int SpiritRootGrade { get; set; } = 3;
    public string? TechniqueId { get; set; }
    public double PendingBreakthroughBonus { get; set; }

    public DateTime LastSettledAt { get; set; } = DateTime.UtcNow;
    public double IdleRateMultiplier { get; set; } = 1.0;

    public long SpiritStones { get; set; }
    public int Lifespan { get; set; } = 200;

    public int CheckinStreak { get; set; }
    public DateTime? LastCheckinAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    public string RealmName => Realm.NameOf(RealmIndex);
    public string SpiritRootName => SpiritRoot.NameOf(SpiritRootType);
    public string TechniqueName => Technique.NameOf(TechniqueId);
}
