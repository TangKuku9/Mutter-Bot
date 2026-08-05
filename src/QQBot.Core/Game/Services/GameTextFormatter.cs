using System.Text;
using QQBot.Core.Game.Engines;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;

namespace QQBot.Core.Game.Services;

/// <summary>
/// 纯文本面板渲染（QQ 不渲染 Markdown，用符号+换行排版）。
/// </summary>
public static class GameTextFormatter
{
    public static string FormatStatus(PlayerProfile p, string idleSummary)
    {
        var threshold = BreakthroughEngine.ThresholdFor(p.RealmIndex);
        var progress = Realm.IsMax(p.RealmIndex)
            ? "已达巅峰"
            : $"{p.CultivationPoints:N0} / {threshold:N0}";

        var sb = new StringBuilder();
        sb.AppendLine("══════ 修仙面板 ══════");
        sb.AppendLine($"道号：{DisplayName(p)}");
        sb.AppendLine($"境界：{p.RealmName}");
        sb.AppendLine($"灵根：{p.SpiritRootName}（{GradeText(p.SpiritRootGrade)}）");
        var techTier = Technique.TierOf(p.TechniqueId);
        sb.AppendLine($"功法：{p.TechniqueName}" + (techTier > 0 ? $"（{Technique.TierName(techTier)}）" : ""));
        sb.AppendLine($"修为：{progress}");
        sb.AppendLine($"灵石：{p.SpiritStones:N0}");
        sb.AppendLine($"寿元：{p.Lifespan} 载");
        if (p.PendingBreakthroughBonus > 0)
            sb.AppendLine($"丹药加成：突破 +{p.PendingBreakthroughBonus:P0}（待消耗）");
        if (!string.IsNullOrEmpty(idleSummary))
            sb.AppendLine($"──── 挂机 ────\n{idleSummary}");
        sb.Append("════════════════════");
        return sb.ToString();
    }

    public static string FormatLeaderboard(List<LeaderboardEntry> entries)
    {
        if (entries.Count == 0)
            return "本群尚无修士上榜，快来 @我 修炼成为第一人！";

        var sb = new StringBuilder();
        sb.AppendLine("════ 群修为榜 ════");
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var medal = i switch { 0 => "①", 1 => "②", 2 => "③", _ => $"{i + 1}." };
            sb.AppendLine($"{medal} {DisplayName(e.Nickname, e.MemberOpenId)}　{Realm.NameOf(e.RealmIndex)}　{e.CultivationPoints:N0}修为");
        }
        sb.Append("══════════════════");
        return sb.ToString();
    }

    public static string FormatHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("══════ 修仙RPG 玩法 ══════");
        sb.AppendLine("@我 修炼  —— 查看面板并结算挂机修为");
        sb.AppendLine("@我 签到  —— 每日签到领灵石/修为（20h冷却）");
        sb.AppendLine("@我 修行  —— 闭关一次获取大量修为（8h冷却）");
        sb.AppendLine("@我 突破  —— 修为达标则冲击下一境界（8h冷却）");
        sb.AppendLine("@我 护法  —— 为正在渡劫的道友护法，增加其成功率");
        sb.AppendLine("@我 探索  -- 外出历练获取材料/修为/功法（1h冷却）");
        sb.AppendLine("@我 事件  -- 查看群级奇遇事件，AI动态生成");
        sb.AppendLine("@我 选 <序号>  —— 选择事件选项参与抉择");
        sb.AppendLine("@我 炼丹  —— 查看丹方；炼丹 <丹名> 炼制（30min冷却）");
        sb.AppendLine("@我 背包  -- 查看物品");
        sb.AppendLine("@我 使用 <丹药名>  -- 服用丹药获得即时效果");
        sb.AppendLine("@我 商店  -- 灵石坊，购买材料/重置冷却");
        sb.AppendLine("@我 排行  —— 查看本群修为榜");
        sb.AppendLine("@我 帮助  —— 显示本帮助");
        sb.AppendLine("────────────────────────");
        sb.AppendLine("挂机即可累积修为，每次 @我 都会自动结算。");
        sb.AppendLine("灵根品级影响修炼速度，越高境界突破越难。");
        sb.AppendLine("突破分两步：先发起渡劫，群友可护法，再次突破执行。");
        sb.Append("@我 行动 <描述>  -- AI裁判判定自由行动（5min冷却）");
        return sb.ToString();
    }

    public static string FormatInventory(List<PlayerInventoryItem> inv)
    {
        if (inv.Count == 0)
            return "背包空空如也，去 @我 探索 收集材料吧。";

        var sb = new StringBuilder();
        sb.AppendLine("══════ 背包 ══════");
        foreach (var i in inv)
        {
            var type = i.ItemType == "dan" ? "丹" : "材";
            sb.AppendLine($"[{type}] {i.ItemName} ×{i.Quantity}");
        }
        sb.Append("════════════════════");
        return sb.ToString();
    }

    public static string FormatRecipes()
    {
        var sb = new StringBuilder();
        sb.AppendLine("══════ 丹方录 ══════");
        foreach (var r in Models.AlchemyRecipe.Recipes)
        {
            var mats = string.Join("、", r.Materials.Select(m => $"{AlchemyRecipe.MaterialName(m.Key)}×{m.Value}"));
            sb.AppendLine($"· {r.Name}（{r.Element}系，成功率{r.BaseSuccess * 100:F0}%）");
            sb.AppendLine($"  材料：{mats}");
            sb.AppendLine($"  效用：{r.Desc}");
        }
        sb.AppendLine("用法：@我 炼丹 <丹名>");
        sb.Append("════════════════════");
        return sb.ToString();
    }

    public static string FormatNewPlayer(PlayerProfile p)
    {
        return $"天地灵气汇聚，你已踏入修仙之路！\n" +
               $"灵根：{p.SpiritRootName}（{GradeText(p.SpiritRootGrade)}）\n" +
               $"当前境界：{p.RealmName}\n" +
               $"输入 \"@我 帮助\" 查看玩法。";
    }

    public static string FormatShop(long playerStones)
    {
        var sb = new StringBuilder();
        sb.AppendLine("══════ 灵石坊 ══════");
        sb.AppendLine($"你的灵石：{playerStones:N0}");
        sb.AppendLine("──────────────────");
        sb.AppendLine($"· 灵草　 {GameConstants.ShopPriceLingcao} 灵石/株");
        sb.AppendLine($"· 药草　 {GameConstants.ShopPriceYaocao} 灵石/株");
        sb.AppendLine($"· 灵矿　 {GameConstants.ShopPriceLingkuang} 灵石/块");
        sb.AppendLine($"· 玄冰露 {GameConstants.ShopPriceXuanbing} 灵石/瓶");
        sb.AppendLine($"· 火焰晶 {GameConstants.ShopPriceHuoyan} 灵石/颗");
        sb.AppendLine("──────────────────");
        sb.AppendLine($"· 重置突破冷却 {GameConstants.BtCooldownResetPrice} 灵石");
        sb.AppendLine("用法：@我 购买 <材料名> <数量>");
        sb.AppendLine("　　　@我 购买 重置冷却");
        sb.Append("════════════════════");
        return sb.ToString();
    }

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
