using QQBot.Core.Game.Models;

namespace QQBot.Core.Game.Engines;

/// <summary>
/// 日常签到/修行引擎。纯计算，不修改存档（由 EventApplier 应用）。
/// </summary>
public class DailyTaskEngine
{
    /// <summary>签到。streak 连续判定窗口 48h（服务层冷却为北京时间次日0点，非固定 20h）。</summary>
    public CheckinResult Checkin(PlayerProfile player, DateTime now)
    {
        var streak = 1;
        if (player.LastCheckinAt is { } last && (now - last).TotalHours < 48)
            streak = player.CheckinStreak + 1;

        var streakBonus = Math.Min(streak - 1, GameConstants.CheckinMaxStreakBonus);
        var stones = GameConstants.CheckinBaseStones + streakBonus * 5;
        var cultivation = GameConstants.CheckinBaseCultivation + streakBonus * 10L;
        // 签到恢复少量寿元，防止硬核玩家寿元耗尽后卡死
        var lifespanGain = 3 + streakBonus;

        return new CheckinResult(streak, stones, cultivation, lifespanGain,
            $"签到成功，连续 {streak} 天\n灵石 +{stones}，修为 +{cultivation}\n寿元 +{lifespanGain}");
    }

    /// <summary>修行：一次性大量修为。冷却由服务层强制 8h。</summary>
    public PracticeResult Practice(PlayerProfile player, DateTime now)
    {
        var rootFactor = SpiritRoot.RateFactor(player.SpiritRootGrade);
        var gained = (long)Math.Round(
            GameConstants.PracticeBaseCultivation * (1 + player.RealmIndex * 0.05) * rootFactor);

        return new PracticeResult(gained,
            $"闭关修行一番，\n修为 +{gained:N0}。");
    }
}

public record CheckinResult(int NewStreak, long Stones, long Cultivation, int LifespanGain, string Message);

public record PracticeResult(long GainedCultivation, string Message);
