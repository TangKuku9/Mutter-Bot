using QQBot.Core.Game.Models;
using QQBot.Core.Game.Services;

namespace QQBot.Core.Game.Ui;

/// <summary>
/// 菜单上下文：构造动态按钮所需的少量数据。刻意只传名字/数量/冷却，
/// 不传存档对象，避免菜单层与判定层产生耦合。
/// </summary>
/// <param name="Cooldowns">动作冷却键 → 剩余时长（仅含仍在冷却中的项），用于把"点了只会吃冷却"的按钮标注出来。</param>
public sealed record MenuContext(
    IReadOnlyList<string>? UsableDanNames = null,
    IReadOnlyList<string>? RecipeNames = null,
    int EventOptionCount = 0,
    IReadOnlyList<string>? SparOpponentNames = null,
    bool TribulationOpen = false,
    bool IsOwnTribulation = false,
    IReadOnlyDictionary<string, TimeSpan>? Cooldowns = null)
{
    /// <summary>无动态数据的普通结算界面。</summary>
    public static readonly MenuContext Empty = new();
}

/// <summary>
/// 修仙系统菜单目录（声明式）。每个界面一份按钮表，渲染成按钮或文字链都由
/// <see cref="GameKeyboard"/> 统一处理；这里只描述"有什么可点、点了等于发什么"。
/// 约定：Command 一律是 GameCommandRouter 能命中的裸关键词，禁止前导 "/"。
/// </summary>
public static class GameMenuCatalog
{
    /// <summary>商店材料（与 CultivationGameService.ResolveShopMaterial 的键保持一致，SmokeCheck 会校验）。</summary>
    public static readonly string[] ShopMaterials = { "灵草", "药草", "灵矿", "玄冰露", "火焰晶" };

    /// <summary>结算类指令的通用后续动作菜单（可点的在前，冷却中的由 Decorate 后置）。</summary>
    private static readonly MenuButton[] ResultMenuBase =
    {
        new("修行", "修行"),
        new("探索", "探索"),
        new("炼丹", "炼丹"),
        new("面板", "面板"),
        new("商店", "商店"),
    };

    public static IReadOnlyList<MenuButton> For(GameAction action, MenuContext ctx) =>
        Decorate(Raw(action, ctx), ctx);

    private static IReadOnlyList<MenuButton> Raw(GameAction action, MenuContext ctx) => action switch
    {
        GameAction.Help => HelpMenu(),
        GameAction.Status => new[]
        {
            new MenuButton("修行", "修行"),
            new MenuButton("探索", "探索"),
            new MenuButton("突破", "突破", VisitedLabel: "开始渡劫", ConfirmText: "冲击境界，失败会折损修为"),
            new MenuButton("背包", "背包"),
            new MenuButton("商店", "商店"),
        },
        GameAction.Shop => ShopMenu(),
        GameAction.Inventory => InventoryMenu(ctx),
        GameAction.Alchemy => RecipeMenu(ctx),
        GameAction.Event => EventMenu(ctx),
        GameAction.Breakthrough => BreakthroughMenu(ctx),
        GameAction.Spar => SparMenu(ctx),
        GameAction.Quest => new[]
        {
            new MenuButton("签到", "签到"),
            new MenuButton("修行", "修行"),
            new MenuButton("探索", "探索"),
            new MenuButton("炼丹", "炼丹"),
            new MenuButton("任务", "任务"),
        },
        GameAction.Leaderboard => new[]
        {
            new MenuButton("排行", "排行"),
            new MenuButton("切磋", "切磋"),
            new MenuButton("面板", "面板"),
        },
        // 结算类（签到/修行/探索/使用/出售/抉择/护法）统一给后续动作入口
        GameAction.Checkin or GameAction.Practice or GameAction.Explore
            or GameAction.UseDan or GameAction.Sell or GameAction.Choose or GameAction.Protect
            or GameAction.FreeAction => ResultMenuBase,
        _ => Array.Empty<MenuButton>(),
    };

    /// <summary>玩法主菜单：按"每日必做 → 今天能玩 → 随时可查"的漏斗排序，一屏点完。</summary>
    private static IReadOnlyList<MenuButton> HelpMenu() => new[]
    {
        new MenuButton("签到", "签到"),
        new MenuButton("修行", "修行"),
        new MenuButton("探索", "探索"),
        new MenuButton("炼丹", "炼丹"),
        new MenuButton("突破", "突破", VisitedLabel: "开始渡劫", ConfirmText: "冲击境界，失败会折损修为"),
        new MenuButton("今日功课", "任务"),
        new MenuButton("看奇遇", "事件"),
        new MenuButton("切磋", "切磋"),
        new MenuButton("商店", "商店"),
        new MenuButton("背包", "背包"),
        new MenuButton("排行榜", "排行"),
        new MenuButton("我的面板", "面板"),
        new MenuButton("自由行动", "行动"),
    };

    private static IReadOnlyList<MenuButton> ShopMenu()
    {
        var list = new List<MenuButton>();
        foreach (var m in ShopMaterials)
            list.Add(new MenuButton($"买{m}", $"购买 {m}", VisitedLabel: "已买入"));
        list.Add(new MenuButton("重置冷却", "购买 重置冷却", VisitedLabel: "已重置", ConfirmText: "花费灵石重置突破冷却"));
        foreach (var m in ShopMaterials.Take(3))
            list.Add(new MenuButton($"卖{m}", $"出售 {m}", VisitedLabel: "已卖出"));
        list.Add(new MenuButton("背包", "背包"));
        list.Add(new MenuButton("炼丹", "炼丹"));
        return list;
    }

    private static IReadOnlyList<MenuButton> InventoryMenu(MenuContext ctx)
    {
        var list = new List<MenuButton>();
        foreach (var dan in (ctx.UsableDanNames ?? Array.Empty<string>()).Take(4))
            list.Add(new MenuButton(Short(dan, 8, "用"), $"使用 {dan}", VisitedLabel: "已服用"));
        list.Add(new MenuButton("商店", "商店"));
        list.Add(new MenuButton("炼丹", "炼丹"));
        list.Add(new MenuButton("探索", "探索"));
        return list;
    }

    private static IReadOnlyList<MenuButton> RecipeMenu(MenuContext ctx)
    {
        var list = new List<MenuButton>();
        foreach (var r in (ctx.RecipeNames ?? Array.Empty<string>()).Take(9))
            list.Add(new MenuButton(Short(r, 8, "炼"), $"炼丹 {r}", VisitedLabel: "开炉中"));
        list.Add(new MenuButton("背包", "背包"));
        return list;
    }

    /// <summary>奇遇抉择：回调模式下同组互斥，选完其余置灰（事件为单次消费，置灰语义正确）。</summary>
    private static IReadOnlyList<MenuButton> EventMenu(MenuContext ctx)
    {
        var list = new List<MenuButton>();
        for (var i = 1; i <= Math.Clamp(ctx.EventOptionCount, 0, 4); i++)
            list.Add(new MenuButton($"选{i}", $"选 {i}", VisitedLabel: "已选定", Style: 4, GroupId: "evt_choice"));
        if (list.Count == 0)
            list.Add(new MenuButton("探索", "探索"));
        list.Add(new MenuButton("面板", "面板"));
        return list;
    }

    /// <summary>渡劫会话进行中：群友一键护法（点后置灰避免重复点击），发起人一键执行渡劫。</summary>
    private static IReadOnlyList<MenuButton> BreakthroughMenu(MenuContext ctx)
    {
        if (!ctx.TribulationOpen)
            return new[]
            {
                new MenuButton("突破", "突破", VisitedLabel: "开始渡劫", ConfirmText: "冲击境界，失败会折损修为"),
                new MenuButton("面板", "面板"),
                new MenuButton("修行", "修行"),
            };
        var list = new List<MenuButton>
        {
            new MenuButton("护法", "护法", VisitedLabel: "已护法"),
            new MenuButton("排行榜", "排行"),
        };
        if (ctx.IsOwnTribulation)
            list.Insert(0, new MenuButton("执行渡劫", "突破", VisitedLabel: "开始渡劫", Style: 4, ConfirmText: "即刻开始渡劫"));
        return list;
    }

    private static IReadOnlyList<MenuButton> SparMenu(MenuContext ctx)
    {
        var list = new List<MenuButton> { new("随机切磋", "切磋", VisitedLabel: "已应战") };
        foreach (var name in (ctx.SparOpponentNames ?? Array.Empty<string>()).Take(4))
            list.Add(new MenuButton(Short(name, 8, "挑"), $"切磋 {name}", VisitedLabel: "已应战"));
        list.Add(new MenuButton("面板", "面板"));
        return list;
    }

    /// <summary>
    /// 冷却感知：仍在冷却的动作改为灰框、标出剩余时间，并排到可点动作之后。
    /// 按钮保持可点（点了会得到精确的剩余时间提示），只是不再假装"现在就能做"。
    /// </summary>
    private static IReadOnlyList<MenuButton> Decorate(
        IReadOnlyList<MenuButton> buttons, MenuContext ctx)
    {
        var cds = ctx.Cooldowns;
        if (cds is null || cds.Count == 0) return buttons;

        var ready = new List<MenuButton>();
        var waiting = new List<MenuButton>();
        foreach (var b in buttons)
        {
            var key = CooldownKeyOf(b);
            if (key is not null && cds.TryGetValue(key, out var left) && left > TimeSpan.Zero)
                waiting.Add(b with { Label = WithCooldownLabel(b.Label, left), Style = 0 });
            else
                ready.Add(b);
        }

        return waiting.Count == 0 ? ready : Merge(ready, waiting);
    }

    private static IReadOnlyList<MenuButton> Merge(List<MenuButton> ready, List<MenuButton> waiting)
    {
        ready.AddRange(waiting);
        return ready;
    }

    /// <summary>按钮对应的冷却键；查看类动作无冷却返回 null。</summary>
    private static string? CooldownKeyOf(MenuButton b) => GameCommandRouter.Match(b.Command)?.Action switch
    {
        GameAction.Practice => "practice",
        GameAction.Explore => "explore",
        GameAction.Alchemy => "alchemy",
        GameAction.Breakthrough => "breakthrough",
        GameAction.Checkin => "daily",
        GameAction.Spar => "spar",
        GameAction.FreeAction => "referee",
        _ => null,
    };

    private static string WithCooldownLabel(string label, TimeSpan left)
    {
        var text = $"{label}剩{Compact(left)}";
        // 放不下就宁可不标注：截断出的"剩5时1…"比不标更让人困惑
        return text.Length <= MenuButton.MaxLabelChars ? text : label;
    }

    private static string Compact(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}时{t.Minutes}分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}分"
        : $"{(int)t.TotalSeconds}秒";

    /// <summary>按钮文字压缩：超长时截断并加前缀，保证 ≤10 字符。</summary>
    private static string Short(string name, int keep, string prefix)
    {
        var s = name.Length <= keep ? name : name[..keep] + "…";
        var label = prefix + s;
        return label.Length <= MenuButton.MaxLabelChars ? label : s;
    }
}
