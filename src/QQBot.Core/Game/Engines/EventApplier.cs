using QQBot.Core.Game.Models;

namespace QQBot.Core.Game.Engines;

/// <summary>
/// 事件应用器——修仙RPG 的唯一存档写入口。
/// 所有引擎计算结果均经此处应用到 PlayerProfile 领域模型（数值边界校验后），
/// 再由服务层持久化。Phase 3 起 AI 裁判输出也经此钳制后应用。
/// </summary>
public class EventApplier
{
    public string ApplyIdle(PlayerProfile player, IdleSettlementResult r)
    {
        if (r.GainedCultivation <= 0)
        {
            player.LastSettledAt = r.SettlementTime;
            return string.Empty;
        }

        player.CultivationPoints += r.GainedCultivation;
        player.LastSettledAt = r.SettlementTime;
        return r.Capped
            ? $"闭关已满 {GameConstants.MaxIdleHours}h（收益封顶），本次收获修为 +{r.GainedCultivation:N0}。"
            : $"闭关 {Format(r.ElapsedTime)}，收获修为 +{r.GainedCultivation:N0}。";
    }

    public string ApplyBreakthrough(PlayerProfile player, BreakthroughResult r)
    {
        if (r.Success)
        {
            player.RealmIndex = r.NewRealmIndex;
            player.CultivationPoints = 0;
            player.SpiritStones += r.BonusStones;
            // 突破成功恢复寿元：越高境界恢复越多
            var lifespanGain = 30 + r.NewRealmIndex * 10;
            player.Lifespan += lifespanGain;
            return r.Message + $" 奖励灵石 +{r.BonusStones}，寿元 +{lifespanGain} 载。";
        }

        if (r.Outcome == "failure")
            player.CultivationPoints = Math.Max(0, player.CultivationPoints - r.LostCultivation);

        return r.Message;
    }

    public string ApplyCheckin(PlayerProfile player, CheckinResult r, DateTime now)
    {
        player.CheckinStreak = r.NewStreak;
        player.LastCheckinAt = now;
        player.SpiritStones += r.Stones;
        player.CultivationPoints += r.Cultivation;
        player.Lifespan += r.LifespanGain;
        return r.Message;
    }

    public string ApplyPractice(PlayerProfile player, PracticeResult r)
    {
        player.CultivationPoints += r.GainedCultivation;
        return r.Message;
    }

    /// <summary>AI 裁判修为变化比例钳制到 [min,max]（防作弊）。Phase 3 使用。</summary>
    public static double ClampDelta(double value, double min = -0.3, double max = 0.3) =>
        Math.Clamp(value, min, max);

    private static string Format(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}时{ts.Minutes}分";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}分";
        return $"{(int)ts.TotalSeconds}秒";
    }
}
