using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QQBot.Core.Configuration;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;
using QQBot.Core.Game.Services;
using QQBot.Core.Game.Ui;

// config <path>：按生产同样的方式绑定 appsettings，验证“运维手改 JSON”能正确生效。
if (args.Length > 1 && args[0].Equals("config", StringComparison.OrdinalIgnoreCase))
{
    var root = new ConfigurationBuilder().AddJsonFile(args[1], optional: false).Build();
    var bound = new QQBotOptions();
    root.GetSection("QQBot").Bind(bound);
    var ru = bound.RichUi;
    var probe = new RichUiCapability(Options.Create(bound));

    Console.WriteLine($"文件：{args[1]}");
    Console.WriteLine($"AppId 已读到：{(bound.AppId.Length > 0 ? "是" : "否")}（密钥不打印）");
    Console.WriteLine($"Enabled={ru.Enabled} Markdown={ru.Markdown} KeyboardMode='{ru.KeyboardMode}' → {ru.ResolveKeyboardMode()}");
    Console.WriteLine($"CmdInputLinks={ru.CmdInputLinks} AtUserTags={ru.AtUserTags} MessageReference={ru.MessageReference} InteractionIntent={ru.InteractionIntent}");
    Console.WriteLine($"GroupAllowList={ru.GroupAllowList.Count} 个 Ttl={ru.CapabilityTtlMinutes}min 行列={ru.MaxRows}×{ru.MaxButtonsPerRow} 上限字符={ru.MaxMarkdownChars}");
    // 探针用第一个白名单群（未配白名单时用占位 G1），否则判定的永远是“不在灰度名单”的群
    var probeGroup = ru.GroupAllowList.Count > 0 ? ru.GroupAllowList[0] : "G1";
    Console.WriteLine($"生效判定（群 {probeGroup[..Math.Min(10, probeGroup.Length)]}…）："
        + $"UseMarkdown={probe.UseMarkdown(probeGroup)} 键盘={probe.EffectiveKeyboardMode(probeGroup)} "
        + $"文字链={probe.UseCmdInputLinks(probeGroup)} @标签={probe.UseAtTags(probeGroup)}");
    return;
}

// 纯文本面板黄金基线导出：重构渲染层前后各跑一次并 diff，
// 以此机械证明"markdown 升级不影响既有纯文本排版"（本项目排版历史多次回炉，必须硬校验）。
// 用法：UiGolden            → 导出纯文本
//       UiGolden markdown   → 导出 markdown（含按钮/文字链，仅重构后可用）
var wantMarkdown = args.Length > 0 && args[0].Equals("markdown", StringComparison.OrdinalIgnoreCase);

var sb = new StringBuilder();
void Section(string name, string body)
{
    sb.Append("### ").Append(name).Append('\n');
    sb.Append(body);
    sb.Append("\n===END===\n");
}

// ===== 固定 fixture（不含时间随机性；日诗/欢迎语按日期种子，同日稳定） =====
var player = new PlayerProfile
{
    GroupId = "GOLDENGROUP",
    MemberOpenId = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA0001",
    Nickname = "",
    RealmIndex = 5,
    CultivationPoints = 1234567,
    SpiritRootType = 2,
    SpiritRootGrade = 1,
    TechniqueId = "changsheng",
    PendingBreakthroughBonus = 0.2,
    SpiritStones = 8888,
    Lifespan = 197,
    CheckinStreak = 3,
};

var maxRealmPlayer = new PlayerProfile
{
    GroupId = "GOLDENGROUP",
    MemberOpenId = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB0002",
    Nickname = "清虚",
    RealmIndex = Realm.MaxIndex,
    CultivationPoints = 99999999,
    SpiritRootType = 4,
    SpiritRootGrade = 5,
    TechniqueId = null,
    SpiritStones = 0,
    Lifespan = 0,
};

var entries = new List<LeaderboardEntry>
{
    new("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA0001", "", 5, 1234567),
    new("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC0003", "玄yna", 4, 888888),
    new("DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD0004", "玄yn", 4, 777),
    new("EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE0005", "", 2, 12345),
};

var inventory = new List<PlayerInventoryItem>
{
    new("lingcao", "material", "灵草", 12),
    new("huiqi_dan", "dan", "回气丹", 3),
    new("zhuji_dan", "dan", "筑基丹", 1),
    new("huochun_dan", "dan", "回春丹", 7),
    new("lingkuang", "material", "灵矿", 1),
};

var questNow = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
var quests = DailyQuests.GetDaily("GOLDENGROUP", questNow);
var questStates = quests
    .Select((q, i) => (Quest: q, Progress: i == 0 ? q.Target : i))
    .ToList();

Section("status", GameTextFormatter.FormatStatus(player, "闭关42分，修为+3"));
Section("status_no_idle", GameTextFormatter.FormatStatus(player, ""));
Section("status_max_realm", GameTextFormatter.FormatStatus(maxRealmPlayer, "闭关满8小时，修为+520"));
Section("leaderboard", GameTextFormatter.FormatLeaderboard(entries));
Section("leaderboard_empty", GameTextFormatter.FormatLeaderboard(new List<LeaderboardEntry>()));
Section("help", GameTextFormatter.FormatHelp());
Section("inventory", GameTextFormatter.FormatInventory(inventory));
Section("inventory_empty", GameTextFormatter.FormatInventory(new List<PlayerInventoryItem>()));
Section("recipes", GameTextFormatter.FormatRecipes());
Section("quest_partial", GameTextFormatter.FormatQuest(
    questStates.Select(s => (s.Quest, s.Progress)).ToList(), false, false, false, DailyQuests.RewardStones));
Section("quest_just_claimed", GameTextFormatter.FormatQuest(
    questStates.Select(s => (s.Quest, s.Progress)).ToList(), true, true, true, DailyQuests.RewardStones));
Section("quest_claimed", GameTextFormatter.FormatQuest(
    questStates.Select(s => (s.Quest, s.Progress)).ToList(), true, true, false, DailyQuests.RewardStones));
Section("new_player", GameTextFormatter.FormatNewPlayer(player));
Section("shop", GameTextFormatter.FormatShop(8888));

if (wantMarkdown)
{
    // 面板化渲染：同一份内容再导出 markdown 版（含文字链）供人工核对
    Section("md_status", MarkdownProbe.Status(player, "闭关42分，修为+3"));
    Section("md_help", MarkdownProbe.Help());
    Section("md_shop", MarkdownProbe.Shop(8888));
    Section("md_leaderboard", MarkdownProbe.Leaderboard(entries));
    Section("md_event", MarkdownProbe.Event());
    Section("md_keyboard", MarkdownProbe.KeyboardJson());
}

Console.Write(sb.ToString());

/// <summary>markdown 导出：无按钮权限配置下应看到文字链；切成 Command 模式可看按钮 JSON。</summary>
static class MarkdownProbe
{
    public static string Status(PlayerProfile p, string idle) =>
        Render(GameAction.Status, GameTextFormatter.FormatStatus(p, idle), MenuContext.Empty, p.RealmName,
            GameTextFormatter.BuildStatus(p, idle));

    public static string Help() =>
        Render(GameAction.Help, GameTextFormatter.FormatHelp(), MenuContext.Empty, "帮助",
            GameTextFormatter.BuildHelp());

    public static string Shop(long stones) =>
        Render(GameAction.Shop, GameTextFormatter.FormatShop(stones), MenuContext.Empty, "商店",
            GameTextFormatter.BuildShop(stones));

    public static string Leaderboard(List<LeaderboardEntry> entries) =>
        Render(GameAction.Leaderboard, GameTextFormatter.FormatLeaderboard(entries), MenuContext.Empty, "排行",
            GameTextFormatter.BuildLeaderboard(entries, false));

    public static string Event() =>
        Render(GameAction.Event, "══ 断崖逢仙 ══\n崖下灵光隐现。\n1. 探身去看（中险）\n2. 取石掷之（低险）\n3. 绕道而行（无险）",
            new MenuContext(EventOptionCount: 3), "奇遇");

    public static string KeyboardJson() =>
        System.Text.Json.JsonSerializer.Serialize(new GameKeyboardPresenter().Build(), new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    private static string Render(GameAction action, string plain, MenuContext ctx, string nickname, GamePanel? panel = null)
    {
        var cap = new RichUiCapability(Options.Create(new QQBotOptions
        {
            RichUi = { Enabled = true, Markdown = true, KeyboardMode = "None", CmdInputLinks = true, AtUserTags = true }
        }));
        var presenter = new GameUiPresenter(cap, NullLogger<GameUiPresenter>.Instance);
        // 与生产一致：面板类界面传 panel（用结构化 markdown 渲染，含进度条），叙事类传 null
        return presenter.Compose("GOLDENGROUP", action, plain, ctx, nickname, panel).Panel!.Markdown;
    }
}

/// <summary>导出 Command 模式下的完整键盘结构，便于比对官方字段命名。</summary>
internal sealed class GameKeyboardPresenter
{
    public object Build()
    {
        var cap = new RichUiCapability(Options.Create(new QQBotOptions
        {
            RichUi = { Enabled = true, Markdown = true, KeyboardMode = "Command" }
        }));
        var menu = GameMenuCatalog.For(GameAction.Shop, MenuContext.Empty);
        return new { buttons = menu.Count, keyboard = GameKeyboard.From(menu, GameKeyboardMode.Command, cap.Options) };
    }
}
