namespace QQBot.Core.Game.Models;

/// <summary>
/// 每日任务定义。全群当日任务一致（日期种子抽取），进度借审计日志计数推断，
/// 领奖防重借通用冷却表，零新表零新列。
/// </summary>
public static class DailyQuests
{
    public record QuestDef(string Id, string Name, string EventType, int Target);

    /// <summary>任务候选池（EventType 对应 CultivationEventLogs 的事件类型）。</summary>
    private static readonly QuestDef[] Candidates =
    {
        new("checkin1",  "签到 1 次",   "daily",    1),
        new("practice1", "闭关修行 1 次", "practice", 1),
        new("explore2",  "外出探索 2 次", "explore",  2),
        new("alchemy1",  "炼丹 1 次",   "alchemy",  1),
        new("spar1",     "切磋 1 次",   "spar",     1),
        new("explore1",  "外出探索 1 次", "explore",  1),
    };

    /// <summary>每日任务条数。</summary>
    public const int QuestsPerDay = 3;

    /// <summary>完成任务奖励灵石（小额，≤ 签到奖励）。</summary>
    public const long RewardStones = 50;

    /// <summary>
    /// 用日期种子抽取当日任务（全服当日一致），按 EventType 去重避免重复项。
    /// groupId 保留于签名以便将来改为按群差异化。
    /// </summary>
    public static List<QuestDef> GetDaily(string groupId, DateTime nowUtc)
    {
        var dayKey = nowUtc.AddHours(8).ToString("yyyyMMdd"); // UTC+8 日界，全服一致
        var seed = DeterministicSeed($"dailyquests|{dayKey}");
        var rng = new Random(seed);

        var picked = new List<QuestDef>();
        var pool = Candidates.ToList();
        while (picked.Count < QuestsPerDay && pool.Count > 0)
        {
            var q = pool[rng.Next(pool.Count)];
            pool.RemoveAll(c => c.EventType == q.EventType);
            picked.Add(q);
        }
        return picked;
    }

    /// <summary>领奖防重的冷却键（当日唯一）。</summary>
    public static string ClaimCooldownKey(DateTime nowUtc) =>
        $"quest_claim_{nowUtc.AddHours(8):yyyyMMdd}";

    /// <summary>当日（UTC+8）0 点对应的 UTC 时间，用于日志计数起点。</summary>
    public static DateTime DayStartUtc(DateTime nowUtc) =>
        nowUtc.AddHours(8).Date.AddHours(-8);

    private static int DeterministicSeed(string key)
    {
        var hash = 5381;
        foreach (var c in key)
            hash = ((hash << 5) + hash) ^ c;
        return hash & 0x7FFFFFFF;
    }
}
