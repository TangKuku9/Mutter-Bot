using QQBot.Core.Game.Models;
using QQBot.Core.Game.Services;

namespace QQBot.Core.Game.Engines;

/// <summary>
/// 切磋引擎。纯计算对决（发起即结算，无会话无建表），不修改存档（由服务层应用）。
/// 战力 = 境界权重 × 功法有效系数 × 灵根效率，加种子化扰动；败者零损失。
/// </summary>
public class SparEngine
{
    /// <summary>切磋冷却（分钟），防刷。</summary>
    public const int CooldownMinutes = 30;

    public SparResult Duel(PlayerProfile challenger, PlayerProfile opponent, string groupId, DateTime now)
    {
        // 确定性种子：双方 OpenId 排序保证对称（谁发起不影响掷点）
        var ids = new[] { challenger.MemberOpenId, opponent.MemberOpenId };
        Array.Sort(ids, StringComparer.Ordinal);
        var seed = DeterministicSeed($"{groupId}|spar|{ids[0]}|{ids[1]}|{now:yyyy-MM-dd HH}");
        var rng = new Random(seed);

        var scoreA = CombatPower(challenger) * (0.9 + rng.NextDouble() * 0.2);
        var scoreB = CombatPower(opponent) * (0.9 + rng.NextDouble() * 0.2);
        var challengerWins = scoreA >= scoreB;
        var winner = challengerWins ? challenger : opponent;
        var loser = challengerWins ? opponent : challenger;

        // 彩头：小额且随境界缩放（与探索奖励量级一致）
        var cultDelta = (long)Math.Round(15 * Math.Pow(3.0, winner.RealmIndex));

        // 三回合叙事：独立种子从风味池抽句，与上方战力掷点隔离
        var narrSeed = $"{groupId}|spar-narr|{ids[0]}|{ids[1]}|{now:yyyy-MM-dd HH}";
        var narrative =
            $"第一回合：{FlavorText.Pick("spar_open", narrSeed)}\n"
            + $"第二回合：{FlavorText.Pick("spar_clash", narrSeed)}\n"
            + $"决胜：{FlavorText.Pick("spar_win", narrSeed)}";

        return new SparResult(challengerWins, winner.MemberOpenId,
            GameTextFormatter.DisplayName(winner), GameTextFormatter.DisplayName(loser),
            cultDelta, narrative,
            FlavorText.Pick("spar_lose", narrSeed));
    }

    private static double CombatPower(PlayerProfile p) =>
        Math.Pow(3.0, p.RealmIndex)
        * Technique.EffectiveRateFactor(p.TechniqueId, p.SpiritRootType)
        * SpiritRoot.RateFactor(p.SpiritRootGrade);

    private static int DeterministicSeed(string key)
    {
        var hash = 5381;
        foreach (var c in key)
            hash = ((hash << 5) + hash) ^ c;
        return hash & 0x7FFFFFFF;
    }
}

public record SparResult(
    bool ChallengerWins,
    string WinnerOpenId,
    string WinnerName,
    string LoserName,
    long CultDelta,
    string Narrative,
    string Consolation);
