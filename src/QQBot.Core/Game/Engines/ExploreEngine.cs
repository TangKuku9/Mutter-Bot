using QQBot.Core.Game.Models;

namespace QQBot.Core.Game.Engines;

/// <summary>
/// 探索引擎（Phase 2 静态事件模板池）。确定性 RNG 抽取事件并结算。
/// Phase 3 将由 AI EventGenerator 补充动态事件。
/// </summary>
public class ExploreEngine
{
    private record Template(string Title, string Narrative, string Type,
        string? MaterialId, int MaterialMin, int MaterialMax,
        long CultivationBase, int LifespanDelta);

    private static readonly Template[] Pool =
    {
        new("灵草谷", "你步入一处灵气氤氲的山谷，采得几株灵草。", "opportunity",
            "lingcao", 1, 3, 20, 0),
        new("古修洞府", "偶得前人遗府，残存修为注入己身。", "opportunity",
            null, 0, 0, 80, 0),
        new("药王遗迹", "药园虽荒，仍余药草若干。", "opportunity",
            "yaocao", 1, 2, 15, 0),
        new("矿脉深处", "你寻得一处隐秘灵矿，凿下几块灵矿。", "opportunity",
            "lingkuang", 1, 2, 10, 0),
        new("玄冰潭", "潭底凝出玄冰露，寒气逼人。", "opportunity",
            "xuanbing_lu", 1, 1, 5, 0),
        new("火焰地脉", "地脉中孕育火焰晶，灼热难当。", "opportunity",
            "huoyan_jing", 1, 1, 5, 0),
        new("心魔劫", "修行不慎引动心魔，修为受损。", "tribulation",
            null, 0, 0, -50, -2),
        new("天雷淬体", "一道天雷劈下，痛失寿元却淬炼了根骨。", "tribulation",
            null, 0, 0, 40, -3),
        new("机缘顿悟", "灵光乍现，你对大道有了新的领悟。", "opportunity",
            null, 0, 0, 120, 0),
        new("妖兽袭击", "林中妖兽扑来，你狼狈逃脱，丢了些灵草。", "tribulation",
            null, 0, 0, -20, -1),
        new("紫晶芝崖", "崖壁上生着几株紫晶芝，紫气莹然。", "opportunity",
            "zijing_zhi", 1, 1, 5, 0),
        new("坠星荒原", "荒原深处拾得一块蕴灵碎石，聊胜于无。", "opportunity",
            "lingkuang", 1, 1, 8, 0),
        new("百草园废圈", "废圈残存灵草丛生，你俯身采了满怀。", "opportunity",
            "lingcao", 2, 4, 12, 0),
        new("雾隐药谷", "浓雾深锁的药谷里，药草带着晨露清香。", "opportunity",
            "yaocao", 1, 3, 10, 0),
        new("寒髓冰洞", "洞底寒气凝成晶露，你冒险取了一小瓶。", "opportunity",
            "xuanbing_lu", 1, 2, 8, 0),
        new("焚天火岩", "火岩缝隙中嵌着火焰晶，灼手而取。", "opportunity",
            "huoyan_jing", 1, 2, 8, 0),
        new("仙人对弈台", "石台棋局犹在，你观棋悟得一丝道韵。", "opportunity",
            null, 0, 0, 100, 0),
        new("灵泉洗髓", "偶遇一眼灵泉，洗去浊气，神完气足。", "opportunity",
            null, 0, 0, 60, 1),
        new("荒塔残卷", "古塔底层拾得残卷，虽不全却也受益。", "opportunity",
            null, 0, 0, 90, 0),
        new("商队遗货", "道旁拾得废弃商队的残货，内有可用之材。", "opportunity",
            "lingcao", 1, 2, 15, 0),
        new("毒瘴迷谷", "误入毒瘴深谷，你屏息疾行，仍被侵蚀了些修为。", "tribulation",
            null, 0, 0, -35, -1),
        new("沙暴劫", "荒漠沙暴骤起，你被埋了半日才爬出，修为受损。", "tribulation",
            null, 0, 0, -30, -1),
        new("邪修围堵", "几名邪修拦路索宝，你破围而出，也受了些轻伤。", "tribulation",
            null, 0, 0, -40, -2),
        new("雷泽迷途", "雷泽边缘电弧纵横，你绕行多日，耗损不小。", "tribulation",
            null, 0, 0, -25, -1),
        new("幻阵困厄", "误入前人幻阵，三日方破，神识疲惫。", "tribulation",
            null, 0, 0, -30, -2),
        new("观瀑悟道", "悬瀑之下静坐三日，水声中隐有大道之音。", "opportunity",
            null, 0, 0, 110, 0),
    };

    public ExploreResult Explore(PlayerProfile player, DateTime now)
    {
        var seed = DeterministicSeed(player.MemberOpenId, now);
        var rng = new Random(seed);
        var tpl = Pool[rng.Next(Pool.Length)];

        var realmScale = Math.Pow(3.0, player.RealmIndex);
        var cultivation = (long)Math.Round(tpl.CultivationBase * realmScale);

        var materials = new Dictionary<string, int>();
        if (tpl.MaterialId is not null && tpl.MaterialMax > 0)
        {
            var qty = rng.Next(tpl.MaterialMin, tpl.MaterialMax + 1);
            if (qty > 0)
            {
                // 境界收益：修为越高历练收获越丰（每境界 +25%，向上取整）
                qty = Math.Max(qty, (int)Math.Ceiling(
                    qty * (1 + player.RealmIndex * GameConstants.ExploreRealmMaterialBonus)));
                materials[tpl.MaterialId] = qty;
            }
        }

        // 灵石收益：奇遇事件掉落灵石，随境界线性增长（+30% 基数）
        var stones = tpl.Type == "opportunity"
            ? (long)Math.Round(GameConstants.ExploreStonesBase
                * (0.8 + rng.NextDouble() * 0.4) * (player.RealmIndex + 1))
            : 0;

        // 功法掉落（15% 概率，品级随境界提升）
        string? techniqueId = null;
        if (rng.NextDouble() < 0.15)
        {
            var tierRoll = rng.NextDouble();
            int tier;
            if (player.RealmIndex >= 4)       // 化神+
                tier = tierRoll < 0.05 ? 4 : tierRoll < 0.25 ? 3 : tierRoll < 0.60 ? 2 : 1;
            else if (player.RealmIndex >= 2)  // 金丹+
                tier = tierRoll < 0.02 ? 4 : tierRoll < 0.15 ? 3 : tierRoll < 0.50 ? 2 : 1;
            else                              // 炼气~筑基
                tier = tierRoll < 0.01 ? 4 : tierRoll < 0.08 ? 3 : tierRoll < 0.40 ? 2 : 1;

            var candidates = Technique.ByTier(tier);
            if (candidates.Length > 0)
                techniqueId = candidates[rng.Next(candidates.Length)].Id;
        }

        var message = BuildMessage(tpl, materials, cultivation, tpl.LifespanDelta, stones);
        if (techniqueId is not null)
        {
            var tech = Technique.Techniques.First(t => t.Id == techniqueId);
            message += $"\n发现功法：{Technique.TierName(tech.Tier)}·{tech.Name}（{tech.Element}系，效率{tech.RateFactor:F2}x）";
        }

        return new ExploreResult(
            tpl.Title, tpl.Narrative, tpl.Type,
            materials, cultivation, tpl.LifespanDelta,
            message, techniqueId, stones);
    }

    private static string BuildMessage(Template tpl, Dictionary<string, int> mats, long cult, int life, long stones)
    {
        // 标题独立成行，叙事交由 TextLayout.Flow 折行（见服务层出口）
        var parts = new List<string> { $"【{tpl.Title}】", tpl.Narrative };
        if (mats.Count > 0)
            parts.Add("获得：" + string.Join("、", mats.Select(m => $"{AlchemyRecipe.MaterialName(m.Key)} ×{m.Value}")));
        if (stones > 0) parts.Add($"灵石 +{stones:N0}");
        if (cult > 0) parts.Add($"修为 +{cult:N0}");
        if (cult < 0) parts.Add($"修为 {cult:N0}");
        if (life < 0) parts.Add($"寿元 {life}");
        return string.Join("\n", parts);
    }

    private static int DeterministicSeed(string memberOpenId, DateTime now)
    {
        var key = $"{memberOpenId}|explore|{now:yyyy-MM-dd HH}";
        var hash = 5381;
        foreach (var c in key)
            hash = ((hash << 5) + hash) ^ c;
        return hash & 0x7FFFFFFF;
    }
}

public record ExploreResult(
    string Title,
    string Narrative,
    string Type,                       // opportunity|tribulation
    Dictionary<string, int> Materials, // 材料奖励（itemId -> qty）
    long CultivationDelta,
    int LifespanDelta,
    string Message,
    string? TechniqueId = null,        // 探索掉落的功法Id
    long StonesDelta = 0);             // 灵石奖励（仅奇遇）
