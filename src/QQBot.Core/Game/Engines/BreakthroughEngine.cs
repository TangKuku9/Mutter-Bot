using QQBot.Core.Game.Models;

namespace QQBot.Core.Game.Engines;

/// <summary>
/// 突破/渡劫判定引擎。确定性 RNG（种子化），可复现防作弊。
/// 纯计算，不修改存档（由 EventApplier 应用）。
/// </summary>
public class BreakthroughEngine
{
    /// <summary>尝试从当前境界突破。调用前应由服务校验冷却。</summary>
    public BreakthroughResult Attempt(PlayerProfile player, DateTime now) =>
        Attempt(player, now, 0);

    /// <summary>尝试突破，可附加护法加成（bonusChance 为 0~0.25 的额外成功率）。</summary>
    public BreakthroughResult Attempt(PlayerProfile player, DateTime now, double bonusChance)
    {
        if (Realm.IsMax(player.RealmIndex))
            return new BreakthroughResult(Success: false, Outcome: "max",
                NewRealmIndex: player.RealmIndex, RolledChance: 0, LostCultivation: 0,
                BonusStones: 0, Message: "已达渡劫之巅，无境可破。");

        var current = Realm.Realms[player.RealmIndex];
        if (player.CultivationPoints < current.Threshold)
            return new BreakthroughResult(Success: false, Outcome: "insufficient",
                NewRealmIndex: player.RealmIndex, RolledChance: 0, LostCultivation: 0,
                BonusStones: 0,
                Message: $"修为不足，无法突破。\n需 {current.Threshold:N0}（当前 {player.CultivationPoints:N0}）");

        // 基础成功率 ± 灵根/功法修饰 ± 护法加成
        var baseRate = current.BreakRate;
        var rootBonus = SpiritRoot.BreakthroughBonus(player.SpiritRootGrade);
        var chance = Math.Clamp(baseRate + rootBonus + bonusChance, 0.05, 0.99);

        // 种子化 RNG：Hash(memberOpenId + realmIndex + date + 当前修为) 可复现；
        // 混入当前修为：每次真实尝试后修为必然变化（失败-20%/成功清零），
        // 保证同一天内重试掷点不同，避免"同日重试注定复现同一结果"的陷阱。
        var seed = DeterministicSeed(player.MemberOpenId, player.RealmIndex, now, player.CultivationPoints);
        var rng = new Random(seed);
        var roll = rng.NextDouble();
        var success = roll < chance;

        if (success)
        {
            return new BreakthroughResult(Success: true, Outcome: "success",
                NewRealmIndex: player.RealmIndex + 1, RolledChance: chance, LostCultivation: 0,
                BonusStones: (long)GameConstants.BreakthroughSuccessBonusStones,
                Message: $"天劫降临，一举突破！\n晋升【{Realm.NameOf(player.RealmIndex + 1)}】");
        }

        var lost = (long)Math.Round(player.CultivationPoints * GameConstants.BreakthroughFailureLossRatio);
        return new BreakthroughResult(Success: false, Outcome: "failure",
            NewRealmIndex: player.RealmIndex, RolledChance: chance, LostCultivation: lost,
            BonusStones: 0,
            Message: $"突破失败，走火入魔！\n损失 {lost:N0} 修为。\n道心不可废，再接再厉。");
    }

    /// <summary>当前境界突破所需修为。</summary>
    public static long ThresholdFor(int realmIndex) =>
        (uint)realmIndex < Realm.Realms.Length ? Realm.Realms[realmIndex].Threshold : long.MaxValue;

    private static int DeterministicSeed(string memberOpenId, int realmIndex, DateTime now, long cultivationPoints)
    {
        var key = $"{memberOpenId}|{realmIndex}|{now:yyyy-MM-dd}|{cultivationPoints}";
        var hash = 5381;
        foreach (var c in key)
            hash = ((hash << 5) + hash) ^ c;
        return hash & 0x7FFFFFFF;
    }
}

public record BreakthroughResult(
    bool Success,
    string Outcome,        // success/failure/insufficient/max
    int NewRealmIndex,
    double RolledChance,
    long LostCultivation,
    long BonusStones,
    string Message);
