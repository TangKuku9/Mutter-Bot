using QQBot.Core.Game.Models;

namespace QQBot.Core.Game.Engines;

/// <summary>
/// 挂机惰性结算引擎。玩家下次访问时用 now - LastSettledAt 一次性计算累积修为。
/// 纯计算，不修改存档（由 EventApplier 应用）。
/// </summary>
public class IdleSettlementEngine
{
    public IdleSettlementResult Settle(PlayerProfile player, GroupWorld world, DateTime now)
    {
        var elapsed = now - player.LastSettledAt;
        if (elapsed <= TimeSpan.Zero)
            return IdleSettlementResult.Empty(now);

        // 1. 封顶（防止无限挂机碾压在线玩家）
        var cappedHours = Math.Min(elapsed.TotalHours, GameConstants.MaxIdleHours);

        // 2. 衰减（超4小时部分效率减半）
        var fullRateHours = Math.Min(cappedHours, GameConstants.IdleDecayAfterHours);
        var decayedHours = Math.Max(0, cappedHours - GameConstants.IdleDecayAfterHours);
        var effectiveHours = fullRateHours + decayedHours * 0.5;

        // 3. 速率 = 灵根系数 × 功法系数 × 群灵气浓度 × IdleRateMultiplier
        var rootFactor = SpiritRoot.RateFactor(player.SpiritRootGrade);
        var techFactor = Technique.EffectiveRateFactor(player.TechniqueId, player.SpiritRootType);
        var ratePerHour = GameConstants.BaseIdleRatePerHour
                          * rootFactor * techFactor * world.WorldQiFactor * player.IdleRateMultiplier;

        // 4. 境界衰减（越高境界挂机效率递减，防数值膨胀）
        var realmDecay = Math.Pow(0.9, player.RealmIndex);
        var gained = (long)Math.Round(effectiveHours * ratePerHour * realmDecay);

        return new IdleSettlementResult(
            GainedCultivation: gained,
            ElapsedTime: elapsed,
            EffectiveHours: effectiveHours,
            Capped: elapsed.TotalHours > GameConstants.MaxIdleHours,
            SettlementTime: now);
    }
}

public record IdleSettlementResult(
    long GainedCultivation,
    TimeSpan ElapsedTime,
    double EffectiveHours,
    bool Capped,
    DateTime SettlementTime)
{
    public static IdleSettlementResult Empty(DateTime now) =>
        new(0, TimeSpan.Zero, 0, false, now);
}
