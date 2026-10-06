using QQBot.Core.Game.Engines;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;
using QQBot.Core.Game.Ui;

namespace QQBot.Core.Game.Services;

/// <summary>
/// 修仙面板文本层。所有面板以 <see cref="GamePanel"/> 结构化描述为唯一数据源，
/// 同时产出纯文本与 markdown 两个版本：
/// 纯文本沿用既有 14 汉字当量手工排版符号（由 tools/UiGolden 做逐字节回归校验），
/// markdown 交给客户端排版并挂载按钮/文字链。
/// 注：QQ 的 markdown 能力需平台授权，未开通时全链路自动走纯文本分支。
/// </summary>
public static class GameTextFormatter
{
    /// <summary>被动回复时 QQ 自动在消息前渲染 @发送者，估算其首行占宽（@ + 昵称 + 空格），
    /// 封顶 10 防止超长昵称毁掉排版。</summary>
    public static double MentionReserve(string? nickname) =>
        Math.Min(10, 0.78 + TextLayout.Width(nickname ?? string.Empty) + 0.35);

    // ===================== 面板构建（单一数据源） =====================

    public static GamePanel BuildStatus(PlayerProfile p, string idleSummary)
    {
        var threshold = BreakthroughEngine.ThresholdFor(p.RealmIndex);
        var isMax = Realm.IsMax(p.RealmIndex);
        var pct = isMax ? "已达巅峰"
            : $"{Math.Min(100, (int)(p.CultivationPoints * 100.0 / Math.Max(1, threshold)))}%";

        var panel = GamePanel.New()
            .Title("修仙面板")
            .Kv("道号", DisplayName(p))
            .Kv("境界", p.RealmName)
            .Kv("灵根", $"{p.SpiritRootName}（{GradeText(p.SpiritRootGrade)}）");

        var techTier = Technique.TierOf(p.TechniqueId);
        panel.Kv("功法", p.TechniqueName + (techTier > 0 ? $"（{Technique.TierName(techTier)}）" : ""));
        panel.Kv("修为", $"{p.CultivationPoints:N0}（{pct}）");
        if (!isMax)
        {
            // 卡片里补一条进度条（纯文本分支会自动省略，不影响 14 当量排版）
            panel.Progress("修为", Math.Clamp(p.CultivationPoints / (double)Math.Max(1L, threshold), 0d, 1d));
            panel.Kv("突破需", $"{threshold:N0}");
        }
        panel.Kv("灵石", $"{p.SpiritStones:N0}")
             .Kv("寿元", $"{p.Lifespan} 载");
        if (p.PendingBreakthroughBonus > 0)
            panel.Kv("丹药加成", $"+{p.PendingBreakthroughBonus:P0}");

        // 每日诗句：按日期确定性抽取，全群当日一致，面板点睛不喂噪
        var poem = FlavorText.Pick("daily_poem", DateTime.UtcNow.AddHours(8).ToString("yyyyMMdd"));
        if (poem.Length > 0)
            panel.Kv("今日诗云", poem);
        if (!string.IsNullOrEmpty(idleSummary))
            panel.SubTitle("挂机").Line(idleSummary);

        return panel.Bottom();
    }

    public static GamePanel BuildLeaderboard(List<LeaderboardEntry> entries, bool atTags = false)
    {
        var panel = GamePanel.New().Title("群修为榜");
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var medal = i switch { 0 => "①", 1 => "②", 2 => "③", _ => $"{i + 1}." };
            var who = GameKeyboard.AtUser(e.MemberOpenId, DisplayName(e.Nickname, e.MemberOpenId), atTags);
            panel.Line($"{medal} {who}　{Realm.NameOf(e.RealmIndex)}　{e.CultivationPoints:N0}");
        }
        return panel.Bottom();
    }

    /// <summary>玩法条目：Head 为指令行，Tail 为被 14 当量拆开的尾句（null=无尾句）。
    /// 纯文本两行输出与历史逐字节一致，卡片里由 Item 合并为一行。</summary>
    private static readonly (string Head, string? Tail)[] HelpItems =
    {
        ("@我 修炼 —— 查看面板、", "　　结算挂机修为"),
        ("@我 签到 —— 每日领灵石", "　　（20h冷却）"),
        ("@我 修行 —— 闭关获取", "　　大量修为（8h冷却）"),
        ("@我 突破 —— 冲击下一", "　　境界（8h冷却）"),
        ("@我 护法 —— 助渡劫道友", "　　提升成功率"),
        ("@我 探索 —— 历练得材料", "　　修为功法（1h冷却）"),
        ("@我 事件 —— 查看群奇遇", null),
        ("@我 选 <序号> —— 参与", "　　事件抉择"),
        ("@我 炼丹 <丹名> —— 炼药", "　　（30min冷却）"),
        ("@我 切磋 [道友名] ——", "　　斗法赢彩头（30min）"),
        ("@我 任务 —— 今日功课", "　　与奖励"),
        ("@我 背包 —— 查看物品", null),
        ("@我 使用 <丹药名> ——", "　　服用丹药"),
        ("@我 商店 —— 购买材料", "　　/重置冷却"),
        ("@我 出售 <材料名> ——", "　　回收材料（75%）"),
        ("@我 排行 —— 本群修为榜", null),
        ("@我 行动 <描述> —— AI", "　　判定自由行动（5min）"),
    };

    private static readonly string[] HelpTips =
    {
        "挂机即涨修为，每次@我",
        "自动结算。灵根定快慢，",
        "境界越高突破越难。",
        "渡劫分两步：先发起渡",
        "劫邀护法，再突破执行。",
    };

    public static GamePanel BuildHelp()
    {
        var panel = GamePanel.New().Title("修仙玩法");
        foreach (var (head, tail) in HelpItems)
            panel.Item(head, tail);
        panel.Divider();
        foreach (var tip in HelpTips)
            panel.Line(tip);
        return panel;
    }

    public static GamePanel BuildInventory(List<PlayerInventoryItem> inv)
    {
        var panel = GamePanel.New().Title("背包");
        foreach (var i in inv)
        {
            var type = i.ItemType == "dan" ? "丹" : "材";
            panel.Line($"[{type}] {i.ItemName} ×{i.Quantity}");
        }
        return panel.Bottom();
    }

    public static GamePanel BuildRecipes()
    {
        var panel = GamePanel.New().Title("丹方录");
        foreach (var r in Models.AlchemyRecipe.Recipes)
        {
            var mats = string.Join("、", r.Materials.Select(m => $"{AlchemyRecipe.MaterialName(m.Key)}×{m.Value}"));
            panel.Bullet($"{r.Name}（{r.Element}系·{r.BaseSuccess * 100:F0}%）");
            panel.Line($"　 材料：{mats}");
            panel.Line($"　 效用：{r.Desc}");
        }
        panel.Line("注：括号内为成功率");
        panel.Line("用法：@我 炼丹 <丹名>");
        return panel.Bottom();
    }

    public static GamePanel BuildNewPlayer(PlayerProfile p)
    {
        var blessing = FlavorText.Pick("new_player", $"{p.MemberOpenId}|{DateTime.UtcNow:yyyyMMdd}");
        var panel = GamePanel.New()
            .Line("天地灵气汇聚，\n你已踏入修仙之路！")
            .Kv("灵根", $"{p.SpiritRootName}（{GradeText(p.SpiritRootGrade)}）")
            .Kv("当前境界", p.RealmName);
        if (blessing.Length > 0)
            panel.Line(blessing);
        return panel.Footer("输入 @我 帮助 查看玩法");
    }

    /// <summary>每日任务面板。justClaimed：本次调用刚刚发放奖励。</summary>
    public static GamePanel BuildQuest(
        List<(Models.DailyQuests.QuestDef Quest, int Progress)> quests,
        bool allDone, bool claimed, bool justClaimed, long rewardStones)
    {
        var panel = GamePanel.New().Title("今日功课");
        foreach (var (q, progress) in quests)
        {
            var done = progress >= q.Target;
            panel.Line($"{(done ? "☑" : "☐")} {q.Name}（{Math.Min(progress, q.Target)}/{q.Target}）");
        }
        panel.Divider();
        if (justClaimed)
        {
            panel.Line($"功课圆满！灵石 +{rewardStones}");
            panel.Line("已自动发放。");
        }
        else if (claimed)
            panel.Line("今日奖励已领，明日再来。");
        else
        {
            panel.Line("全部完成可得：");
            panel.Line($"　 灵石 +{rewardStones}");
        }
        return panel.Bottom();
    }

    public static GamePanel BuildShop(long playerStones)
    {
        static long SellPrice(long p) => (long)Math.Floor(p * GameConstants.ShopSellPriceRatio);

        var panel = GamePanel.New()
            .Title("灵石坊")
            .Kv("你的灵石", $"{playerStones:N0}")
            .Divider()
            .Bullet($"灵草　 {GameConstants.ShopPriceLingcao} 灵石/株")
            .Bullet($"药草　 {GameConstants.ShopPriceYaocao} 灵石/株")
            .Bullet($"灵矿　 {GameConstants.ShopPriceLingkuang} 灵石/块")
            .Bullet($"玄冰露 {GameConstants.ShopPriceXuanbing} 灵石/瓶")
            .Bullet($"火焰晶 {GameConstants.ShopPriceHuoyan} 灵石/颗")
            .Divider()
            .Bullet($"重置突破冷却 {GameConstants.BtCooldownResetPrice} 灵石")
            .Divider()
            .Line("回收价（购入75%）：")
            .Line($"　 灵草{SellPrice(GameConstants.ShopPriceLingcao)} 药草{SellPrice(GameConstants.ShopPriceYaocao)}")
            .Line($"　 灵矿{SellPrice(GameConstants.ShopPriceLingkuang)} 玄冰露{SellPrice(GameConstants.ShopPriceXuanbing)}")
            .Line($"　 火焰晶{SellPrice(GameConstants.ShopPriceHuoyan)}")
            .Divider()
            .Line("用法：@我 购买 <材料名>")
            .Line("　　　@我 出售 <材料名>")
            .Line("　　　@我 购买 重置冷却");
        return panel.Bottom();
    }

    // ===================== 纯文本出口（历史调用点，输出与基线逐字节一致） =====================

    public static string FormatStatus(PlayerProfile p, string idleSummary) =>
        BuildStatus(p, idleSummary).RenderPlain();

    public static string FormatLeaderboard(List<LeaderboardEntry> entries) =>
        entries.Count == 0
            ? "本群尚无修士上榜，快来 @我 修炼成为第一人！"
            : BuildLeaderboard(entries).RenderPlain();

    public static string FormatHelp() => BuildHelp().RenderPlain();

    public static string FormatInventory(List<PlayerInventoryItem> inv) =>
        inv.Count == 0
            ? "背包空空如也，去 @我 探索 收集材料吧。"
            : BuildInventory(inv).RenderPlain();

    public static string FormatRecipes() => BuildRecipes().RenderPlain();

    public static string FormatNewPlayer(PlayerProfile p) => BuildNewPlayer(p).RenderPlain();

    public static string FormatQuest(
        List<(Models.DailyQuests.QuestDef Quest, int Progress)> quests,
        bool allDone, bool claimed, bool justClaimed, long rewardStones) =>
        BuildQuest(quests, allDone, claimed, justClaimed, rewardStones).RenderPlain();

    public static string FormatShop(long playerStones) => BuildShop(playerStones).RenderPlain();

    // ===================== 通用工具 =====================

    private static string GradeText(int grade) => grade switch
    {
        1 => "天灵根·极品",
        2 => "地灵根·上品",
        3 => "玄灵根·中品",
        4 => "黄灵根·下品",
        _ => "杂灵根·劣品"
    };

    public static string DisplayName(PlayerProfile p) =>
        DisplayName(p.Nickname, p.MemberOpenId);

    public static string DisplayName(string nickname, string memberOpenId)
    {
        if (!string.IsNullOrWhiteSpace(nickname) && nickname != memberOpenId)
            return nickname;
        return "道友" + (memberOpenId.Length >= 4 ? memberOpenId[^4..] : memberOpenId);
    }
}
