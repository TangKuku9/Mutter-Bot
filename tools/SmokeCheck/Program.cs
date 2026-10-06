using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QQBot.Core;
using QQBot.Core.Configuration;
using QQBot.Core.Game.Engines;
using QQBot.Core.Game.Models;
using QQBot.Core.Game.Services;
using QQBot.Core.Game.Ui;
using QQBot.Core.Models.QQOfficial;

// 冒烟测试：切磋/每日任务/文案池的行为正确性
var ok = true;
void Check(bool cond, string name)
{
    Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
    if (!cond) ok = false;
}

var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

// ===== 1. FlavorText：池非空 + 确定性 + 装饰概率路径 =====
FlavorText.Validate();
Check(FlavorText.Pools.Count >= 13, $"文案池数量 {FlavorText.Pools.Count} >= 13");
var s1 = FlavorText.Pick("breakthrough_success", "abc|123");
var s2 = FlavorText.Pick("breakthrough_success", "abc|123");
Check(s1 == s2 && s1.Length > 0, "Pick 确定性：同种子同结果");
var d1 = FlavorText.Decorate("基础消息", "checkin", "seed-a");
var d2 = FlavorText.Decorate("基础消息", "checkin", "seed-a");
Check(d1 == d2 && d1.StartsWith("基础消息"), "Decorate 确定性且前缀不变");

// ===== 2. SparEngine：对称性 / 幻影 / 败者零损失 =====
var spar = new SparEngine();
var a = new PlayerProfile
{
    MemberOpenId = "PLAYER_A", RealmIndex = 5, SpiritRootType = 4, SpiritRootGrade = 4,
    TechniqueId = "changsheng", CultivationPoints = 100000
};
var b = new PlayerProfile
{
    MemberOpenId = "PLAYER_B", RealmIndex = 5, SpiritRootType = 2, SpiritRootGrade = 3,
    TechniqueId = "taiyi", CultivationPoints = 200000
};
var r1 = spar.Duel(a, b, "G1", now);
var r2 = spar.Duel(b, a, "G1", now); // 换发起方
Check(r1.WinnerOpenId == r2.WinnerOpenId, $"切磋对称性：谁发起胜者一致（{r1.WinnerOpenId}）");
Check(r1.Narrative == r2.Narrative, "切磋叙事对称一致");
Check(r1.CultDelta == (long)Math.Round(15 * Math.Pow(3.0, r1.ChallengerWins ? a.RealmIndex : b.RealmIndex)),
    $"彩头随胜者境界缩放={r1.CultDelta}");

var phantom = new PlayerProfile
{
    MemberOpenId = "phantom", Nickname = "幻影道人",
    RealmIndex = a.RealmIndex, SpiritRootType = a.SpiritRootType,
    SpiritRootGrade = a.SpiritRootGrade, TechniqueId = a.TechniqueId
};
var r3 = spar.Duel(a, phantom, "G1", now);
Check(!string.IsNullOrEmpty(r3.Narrative) && r3.CultDelta > 0, "幻影切磋可正常结算");
var r3b = spar.Duel(a, phantom, "G1", now.AddHours(2)); // 时间窗变化
Console.WriteLine($"INFO  同对手不同小时：胜者 {(r3.WinnerOpenId == r3b.WinnerOpenId ? "不变" : "变化")}（允许）");

// ===== 3. DailyQuests：条数 / 去重 / 全群一致 / 日期切换 =====
var q1 = DailyQuests.GetDaily("G1", now);
var q2 = DailyQuests.GetDaily("G1", now.AddHours(3)); // 同日不同时刻
var q3 = DailyQuests.GetDaily("G2", now);
var q4 = DailyQuests.GetDaily("G1", now.AddDays(1));
Check(q1.Count == DailyQuests.QuestsPerDay, $"任务条数={q1.Count}");
Check(q1.Select(x => x.EventType).Distinct().Count() == q1.Count, "任务 EventType 无重复");
Check(q1.SequenceEqual(q2), "同日不同时刻任务一致");
Check(q1.All(x => q3.Any(y => y.Id == x.Id)), "同日全群任务一致（候选同源）");
Console.WriteLine($"INFO  G1 今日任务: {string.Join(" / ", q1.Select(x => x.Name))}");
Console.WriteLine($"INFO  G1 明日任务: {string.Join(" / ", q4.Select(x => x.Name))}");
Check(DailyQuests.ClaimCooldownKey(now) == "quest_claim_20260901", $"领奖键={DailyQuests.ClaimCooldownKey(now)}（UTC+8 日界）");
Check(DailyQuests.DayStartUtc(now) == new DateTime(2026, 8, 31, 16, 0, 0, DateTimeKind.Utc), "当日0点=前日16:00 UTC");

// ===== 4. 内容池完整性：新材料/丹方/功法/境界引用一致 =====
Check(AlchemyRecipe.MaterialName("zijing_zhi") == "紫晶芝", "新材料紫晶芝可解析");
foreach (var r in AlchemyRecipe.Recipes)
    foreach (var m in r.Materials.Keys)
        Check(AlchemyRecipe.Materials.Any(x => x.Id == m), $"丹方 {r.Name} 材料 {m} 存在");
Check(AlchemyRecipe.Find("回春丹") is not null && AlchemyRecipe.Find("凝气丹") is not null
    && AlchemyRecipe.Find("紫晶丹") is not null, "三个新丹方可按名查找");
Check(Realm.NameOf(9) == "真仙" && Realm.NameOf(10) == "金仙" && Realm.NameOf(11) == "飞升",
    "新境界：真仙/金仙，越界兜底飞升");
Check(Technique.Techniques.Length == 20, $"功法总数={Technique.Techniques.Length}");
Check(Technique.NameOf("taixu") == "太虚踏天诀", "新功法可解析");

// ===== 5. 富交互（菜单/键盘钳制/能力降级/线上格式）=====
var richOpts = Options.Create(new QQBotOptions
{
    RichUi =
    {
        Enabled = true, Markdown = true,
        KeyboardMode = "Command", CmdInputLinks = true, AtUserTags = true,
    }
});

// 5.1 菜单：每个界面的按钮 data 必须能被现有关键词路由命中，且不踩斜杠命令拦截陷阱
var menuCtx = new MenuContext(
    UsableDanNames: new[] { "回气丹", "筑基丹" },
    RecipeNames: AlchemyRecipe.Recipes.Select(r => r.Name).ToList(),
    EventOptionCount: 3,
    SparOpponentNames: new[] { "清虚", "玄明道人长长长" },
    TribulationOpen: true, IsOwnTribulation: true);

foreach (GameAction act in Enum.GetValues<GameAction>())
{
    var menu = GameMenuCatalog.For(act, menuCtx);
    var longLabels = menu.Where(b => b.Label.Length > MenuButton.MaxLabelChars).Select(b => b.Label).ToList();
    Check(longLabels.Count == 0, $"{act} 按钮文字≤10字符" + (longLabels.Count > 0 ? $" 越界:{string.Join(",", longLabels)}" : ""));
    var badCmd = menu.Where(b => !GameCommandRouter.IsSafeButtonData(b.Command)).Select(b => b.Command).ToList();
    Check(badCmd.Count == 0, $"{act} 菜单指令均可路由且无斜杠" + (badCmd.Count > 0 ? $" 例外:{string.Join("|", badCmd)}" : ""));
    Check(menu.Count <= 25, $"{act} 按钮总数≤25（实为 {menu.Count}）");
}
Check(!GameCommandRouter.IsSafeButtonData("/签到"), "带前导 / 的按钮 data 被拒（避免被当斜杠命令）");
Check(new GameCommandRouter().TryMatchButtonCommand("炼丹 凝气丹")?.Action == GameAction.Alchemy, "按钮 data 与文本指令同源");

// 5.2 键盘钳制：超出官方 5×5 部分被裁，不发出会被拒的消息
var many = Enumerable.Range(1, 26).Select(i => new MenuButton($"按{i}", "面板")).ToList();
var kb = GameKeyboard.From(many, GameKeyboardMode.Command, richOpts.Value.RichUi);
Check(kb?.Content?.Rows.Count == 5, $"26 个按钮钳到 5 行（实为 {kb?.Content?.Rows.Count}）");
Check(kb!.Content!.Rows.All(r => r.Buttons.Count <= 5), "每行按钮≤5");
Check(kb.Content!.Rows.Sum(r => r.Buttons.Count) == 25, "越界按钮被丢弃而非报错");
Check(kb.Content!.Rows.SelectMany(r => r.Buttons).Select(b => b.Id).Distinct().Count() == 25, "按钮 id 键盘内唯一");
Check(kb.Content!.Rows.SelectMany(r => r.Buttons).All(b => b.Action.Type == 2 && !b.Action.Enter),
    "群聊用指令按钮 type=2 且不下发 enter（仅单聊可用）");
Check(kb.Content!.Rows.SelectMany(r => r.Buttons).All(b => b.RenderData.VisitedLabel.Length > 0
    && b.Action.UnsupportTips.Length > 0 && b.Action.Permission.Type == 2),
    "visited_label/兼容文案/权限必填项都有默认值");
var cbKb = GameKeyboard.From(new[] { new MenuButton("选1", "选 1", GroupId: "evt") }, GameKeyboardMode.Callback, richOpts.Value.RichUi);
Check(cbKb!.Content!.Rows[0].Buttons[0].Action.Type == 1 && cbKb.Content!.Rows[0].Buttons[0].GroupId == "evt",
    "回调按钮 type=1 且保留互斥分组");
var cmdGroup = GameKeyboard.From(new[] { new MenuButton("选1", "选 1", GroupId: "evt") }, GameKeyboardMode.Command, richOpts.Value.RichUi);
Check(cmdGroup!.Content!.Rows[0].Buttons[0].GroupId is null, "互斥分组不会误发给指令按钮");
Check(GameKeyboard.From(Array.Empty<MenuButton>(), GameKeyboardMode.Command, richOpts.Value.RichUi) is null,
    "无按钮时不下发空键盘");

// 5.3 能力门控与自动降级
var capOff = new RichUiCapability(Options.Create(new QQBotOptions()));
var capOn = new RichUiCapability(richOpts);
Check(!capOff.UseMarkdown("G1"), "总开关关闭时全走纯文本（零行为变化）");
Check(capOn.UseMarkdown("G1") && capOn.EffectiveKeyboardMode("G1") == GameKeyboardMode.Command, "开关开启时使用按钮");
capOn.ReportDenied(RichUiCapability.CapKeyboard, 304037);
Check(capOn.UseMarkdown("G1") && capOn.EffectiveKeyboardMode("G1") == GameKeyboardMode.None
    && capOn.UseCmdInputLinks("G1"), "按钮被拒 → 保留 markdown 并改用文字链");
capOn.ReportDenied(RichUiCapability.CapMarkdown, 304036);
Check(!capOn.UseMarkdown("G1"), "markdown 被拒 → 回落纯文本");
capOn.ResetForDiagnostics();
Check(capOn.UseMarkdown("G1"), "TTL/重置后可重新探测（权限可能刚开通）");
var capGray = new RichUiCapability(Options.Create(new QQBotOptions
{
    RichUi = { Enabled = true, GroupAllowList = new List<string> { "G1" } }
}));
Check(capGray.UseMarkdown("G1") && !capGray.UseMarkdown("G2"), "白名单只对灰度群生效");
// 配置抗错：写错的枚举不能带下服务，Callback 没开 intent 不能发出无人回应的按钮
var capTypo = new RichUiCapability(Options.Create(new QQBotOptions
{
    RichUi = { Enabled = true, KeyboardMode = "command-but-typed-wrong" }
}));
Check(capTypo.EffectiveKeyboardMode("G1") == GameKeyboardMode.None, "键盘模式写错时退回 None（不崩溃）");
var capCbNoIntent = new RichUiCapability(Options.Create(new QQBotOptions
{
    RichUi = { Enabled = true, KeyboardMode = "Callback", InteractionIntent = false }
}));
Check(capCbNoIntent.EffectiveKeyboardMode("G1") == GameKeyboardMode.Command,
    "Callback 未配 intent 时自动退回 Command（避免按钮永远 3 秒超时）");
var capCb = new RichUiCapability(Options.Create(new QQBotOptions
{
    RichUi = { Enabled = true, KeyboardMode = "Callback", InteractionIntent = true }
}));
Check(capCb.EffectiveKeyboardMode("G1") == GameKeyboardMode.Callback, "Callback + intent 同时开启才用回调按钮");
capCb.ReportInteractionUnavailable();
Check(capCb.EffectiveKeyboardMode("G1") == GameKeyboardMode.Command,
    "网关因 intent 未授权降级后，不再下发回调按钮（退回指令按钮）");
capCb.ReportInteractionAvailable();
Check(capCb.EffectiveKeyboardMode("G1") == GameKeyboardMode.Callback, "真正收到过互动事件后恢复回调按钮");
Check(QqApiErrorCodes.IsMarkdownDenied(304036) && QqApiErrorCodes.IsKeyboardDenied(304037)
    && QqApiErrorCodes.IsKeyboardDenied(40034029) && !QqApiErrorCodes.IsMarkdownDenied(40034029),
    "错误码分类正确（键盘超限不算 markdown 故障）");

// 5.4 呈现器：纯文本永远可用，markdown 只在能力开启时附加
var presenter = new GameUiPresenter(capOn, NullLogger<GameUiPresenter>.Instance);
var richReply = presenter.Compose("G1", GameAction.Status, "══ 修仙面板 ══\n道号：清虚", MenuContext.Empty, "清虚");
Check(richReply.Panel is not null, "富群回复携带面板载荷");
Check(richReply.Reply == "══ 修仙面板 ══\n道号：清虚", "纯文本正文不变（兼作落库与降级内容）");
Check(richReply.Panel!.Markdown.StartsWith("### 修仙面板"), "标题行转为 markdown 标题");
Check(!richReply.Panel.Markdown.Contains("══"), "markdown 去掉了装饰线");
var plainReply = new GameUiPresenter(capOff, NullLogger<GameUiPresenter>.Instance)
    .Compose("G1", GameAction.Status, "══ 修仙面板 ══", MenuContext.Empty, "清虚");
Check(plainReply.Panel is null, "未开富交互时无载荷（保持旧行为）");
var noKbCap = new RichUiCapability(Options.Create(new QQBotOptions { RichUi = { Enabled = true } }));
noKbCap.ReportDenied(RichUiCapability.CapKeyboard, 304038);
var linkReply = new GameUiPresenter(noKbCap, NullLogger<GameUiPresenter>.Instance)
    .Compose("G1", GameAction.Quest, "══ 今日功课 ══\n☑ 签到 1 次（1/1）", MenuContext.Empty, "清虚");
Check(linkReply.Panel!.Keyboard is null && linkReply.Panel.Markdown.Contains("<qqbot-cmd-input"),
    "无按钮权限时降级为可点文字链，菜单不丢失");

// 装饰线不得被误判成标题（曾产出 "### ═" 与 "**─**" 垃圾行）
var statusMd = presenter.Compose("G1", GameAction.Status,
    GameTextFormatter.FormatStatus(a, "闭关42分，修为+3"), MenuContext.Empty, "清虚").Panel!.Markdown;
Check(!statusMd.Contains("### ═") && !statusMd.Contains("**─**") && !statusMd.Contains("═"),
    "markdown 无装饰线垃圾行与残留符号");
Check(statusMd.StartsWith("### 修仙面板") && statusMd.Contains("**挂机**"), "面板标题与挂机小标题正确");
Check(statusMd.Contains("闭关42分，修为+3"), "挂机摘要不丢失");
var shopMd = presenter.Compose("G1", GameAction.Shop, GameTextFormatter.FormatShop(8888), MenuContext.Empty, "清虚").Panel!.Markdown;
Check(!shopMd.Contains("──") && shopMd.Contains("· 灵草") && shopMd.Contains("你的灵石：8,888"),
    "纯文本转 markdown：分隔线丢弃、正文语义不变");
var shopPanelMd = presenter.Compose("G1", GameAction.Shop, GameTextFormatter.FormatShop(8888),
    MenuContext.Empty, "清虚", GameTextFormatter.BuildShop(8888)).Panel!.Markdown;
Check(shopPanelMd.Contains("**你的灵石**：8,888") && shopPanelMd.Contains("- 灵草"),
    "结构化面板 markdown：键值加粗、列表项规范");
var helpMd = presenter.Compose("G1", GameAction.Help, GameTextFormatter.FormatHelp(), MenuContext.Empty, "清虚",
    GameTextFormatter.BuildHelp()).Panel!.Markdown;
Check(helpMd.Contains("@我 修炼 —— 查看面板、结算挂机修为"), "玩法条目在卡片里合并为一行");
Check(helpMd.Contains("@我 事件 —— 查看群奇遇\n"), "无尾句的条目不被拼接");
var shopPanelMd2 = presenter.Compose("G1", GameAction.Shop, GameTextFormatter.FormatShop(8888),
    MenuContext.Empty, "清虚", GameTextFormatter.BuildShop(8888)).Panel!.Markdown;
Check(shopPanelMd2.Contains("用法：@我 购买 <材料名>\n")
    && shopPanelMd2.Contains("@我 出售 <材料名>\n") && !shopPanelMd2.Contains("材料名>@我"),
    "商店用法三行保持独立（不被续行启发式误合）");

// 5.5 协议标签与折行器共存（标签被硬切会直接显示乱码）
var at = GameKeyboard.AtUser("ABC123", "道友1234", true);
Check(at == "<qqbot-at-user id=\"ABC123\" />", "@ 标签格式符合官方新协议");
Check(GameKeyboard.AtUser("ABC", "道友ABC", false) == "道友ABC", "@标签关闭时退回文本");
var atLine = $"① {at}　炼虚　1,234,567　外加很多字的后缀为了逼出折行";
var flowedAt = TextLayout.Flow(atLine);
Check(flowedAt.Contains(at), "Flow 不拆分 qqbot 协议标签");
foreach (var line in flowedAt.Split('\n'))
    Check(!line.Contains("<qqbot") || line.Contains("/>"), $"标签未被切断：{line}");
var linkText = GameKeyboard.RenderInputLinks(new[] { new MenuButton("签到", "签到"), new MenuButton("炼丹 凝气丹", "炼丹 凝气丹") });
Check(linkText.Contains(Uri.EscapeDataString("签到")) && linkText.Contains("reference=\"false\""),
    "文字链 text 已 urlencode 且默认不带引用");

// 5.6 线上传输格式（不连网验证请求体结构）
var textJson = JsonSerializer.Serialize(SendMessageRequest.From(OutboundMessage.Text("hi", "mid", 1)));
Check(!textJson.Contains("markdown") && !textJson.Contains("keyboard") && !textJson.Contains("message_reference"),
    "纯文本请求体不带 markdown/keyboard 字段");
Check(!textJson.Contains(":null"), "空的可选字段不再序列化（避免 API 误判）");
var mdRequest = SendMessageRequest.From(new OutboundMessage
{
    PlainText = "backup", MarkdownText = "### t", Keyboard = GameKeyboard.From(
        new[] { new MenuButton("签到", "签到") }, GameKeyboardMode.Command, richOpts.Value.RichUi),
    MsgId = "mid", MsgSeq = 2, RefMessageId = "REFIDX_x==",
});
var mdJson = JsonSerializer.Serialize(mdRequest);
using var mdDoc = JsonDocument.Parse(mdJson);
var topLevelHasContent = mdDoc.RootElement.TryGetProperty("content", out _);
Check(mdRequest.MsgType == 2 && !topLevelHasContent, "markdown 消息 msg_type=2 且顶层 content 已省略（markdown.content 必须保留）");
Check(mdJson.Contains("\"render_data\"") && mdJson.Contains("\"visited_label\"")
    && mdJson.Contains("\"unsupport_tips\"") && mdJson.Contains("\"rows\"") && mdJson.Contains("\"buttons\""),
    "按钮字段命名与官方协议一致");
Check(mdJson.Contains("\"message_reference\"") && mdJson.Contains("REFIDX_x=="), "引用回复字段正确下发");

// 5.7 互动回调：PUT /interactions/{id} 的体格式与 INTERACTION_CREATE 解析
var ackJson = JsonSerializer.Serialize(InteractionReply.Ok().ToWire());
Check(ackJson.Replace(" ", "") == "{\"code\":0}", $"回应互动为顶层 code（实际 {ackJson}）");
var ackWithBody = JsonSerializer.Serialize(
    new InteractionReply { Code = 0, MarkdownText = "### ok" }.ToWire());
Check(ackWithBody.Contains("\"code\"") && ackWithBody.Contains("\"data\"")
    && ackWithBody.Contains("\"markdown\"") && !ackWithBody.Contains("\"result\""),
    "回应体 data/markdown 层级正确且无旧版 result 包裹");

var evtJson = """
{
  "application_id": "101984245", "chat_type": 1, "type": 11,
  "data": { "resolved": { "button_data": "炼丹 凝气丹", "button_id": "btn_3" }, "type": 11 },
  "group_member_openid": "MEMBER_A", "group_openid": "GROUP_A",
  "id": "06915133-7aef-46ed-94f7-c50939e285ae", "scene": "group", "version": 1
}
""";
var evtObj = JsonSerializer.Deserialize<InteractionCreateEvent>(evtJson,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
Check(evtObj!.NeedsResponse && evtObj.Type == InteractionCreateEvent.TypeInlineKeyboard, "按钮事件需回应");
Check(evtObj.ButtonData == "炼丹 凝气丹" && evtObj.ButtonId == "btn_3", "button_data/button_id 解析正确");
Check(evtObj.GroupOpenid == "GROUP_A" && evtObj.ActorOpenid == "MEMBER_A", "群与点击者 openid 解析正确");
Check(new GameCommandRouter().TryMatchButtonCommand(evtObj.ButtonData)?.Action == GameAction.Alchemy,
    "回调 data 能直接进入既有指令链");

// ===== 6. 体验细节：冷却感知菜单 / 点击后文案 / 菜单顺序 / 卡片进度条 =====
var cds = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
{
    ["practice"] = TimeSpan.FromHours(5.32),
    ["explore"] = TimeSpan.FromMinutes(42),
};
var cdCtx = new MenuContext(Cooldowns: cds);
var statusMenu = GameMenuCatalog.For(GameAction.Status, cdCtx);
Check(statusMenu.Count == 5, $"冷却标注不增不减按钮数（实为 {statusMenu.Count}）");
var cdIdx = statusMenu.Select((b, i) => (b, i)).Where(x => x.b.Label.Contains('剩')).Select(x => x.i).ToList();
var okIdx = statusMenu.Select((b, i) => (b, i)).Where(x => !x.b.Label.Contains('剩')).Select(x => x.i).ToList();
Check(cdIdx.Count == 2 && cdIdx.Min() > okIdx.Max(), $"可点在前、冷却在后（{string.Join("/", statusMenu.Select(b => b.Label))}）");
Check(statusMenu.Any(b => b.Label == "修行剩5时19分") && statusMenu.Any(b => b.Label == "探索剩42分"),
    "冷却时长标注精确到分");
Check(statusMenu.First(b => b.Label.StartsWith("修行剩")).Style == 0, "冷却中按钮置为灰框");
Check(statusMenu.All(b => GameCommandRouter.IsSafeButtonData(b.Command)),
    "标了冷却的按钮仍可路由到原指令（点了会得到准确剩余时间）");
Check(GameMenuCatalog.For(GameAction.Status, MenuContext.Empty).Select(b => b.Label)
    .SequenceEqual(new[] { "修行", "探索", "突破", "背包", "商店" }), "无冷却时菜单顺序与升级前一致");

Check(statusMenu.Single(b => b.Command == "突破").VisitedLabel == "开始渡劫",
    "状态量确定类按钮有点击后反馈");
Check(GameMenuCatalog.For(GameAction.Quest, MenuContext.Empty).All(b => b.VisitedLabel is null),
    "个人循环类按钮不改文案（visited 是全群共享状态，会变误导）");
var trib = GameMenuCatalog.For(GameAction.Breakthrough,
    new MenuContext(TribulationOpen: true, IsOwnTribulation: true));
Check(trib[0] is { Label: "执行渡劫", Command: "突破", Style: 4 } && trib.Any(b => b.Label == "护法"),
    "渡劫会话中发起人看到执行渡劫、群友看到护法");
var evtMenu = GameMenuCatalog.For(GameAction.Event, new MenuContext(EventOptionCount: 3));
Check(evtMenu.Count(b => b.GroupId == "evt_choice") == 3
    && evtMenu.All(b => b.GroupId == "evt_choice" || b.Command is "面板" or "探索"),
    "三个抉择选项同组互斥（事件为单次消费，点一个其余置灰语义正确）");
var helpMenu = GameMenuCatalog.For(GameAction.Help, MenuContext.Empty);
Check(helpMenu.Count == 13 && helpMenu[0].Label == "签到" && helpMenu[5].Label == "今日功课"
    && helpMenu[4].Label == "突破", "主菜单按每日必做→今日可玩→随时可查漏斗排序");

// 卡片进度条：只加在 markdown，纯文本必须一字不变
var maxP = new PlayerProfile
{
    GroupId = "G1", MemberOpenId = "PLAYER_MAX", Nickname = "清虚", RealmIndex = Realm.MaxIndex,
    SpiritRootType = 1, SpiritRootGrade = 5, TechniqueId = null, CultivationPoints = 0, SpiritStones = 0, Lifespan = 300,
};
// 进度条夹具：取一个处于半程的玩家（炼虚阈值 64,800，29,000 约 45%）
var midP = new PlayerProfile
{
    GroupId = "G1", MemberOpenId = "PLAYER_MID", Nickname = "半程", RealmIndex = 5,
    SpiritRootType = 2, SpiritRootGrade = 4, TechniqueId = "changsheng",
    CultivationPoints = 29_000, SpiritStones = 500, Lifespan = 150,
};
var barPanel = GameTextFormatter.BuildStatus(a, "");
Check(!barPanel.RenderPlain().Contains('▓') && !barPanel.RenderPlain().Contains('░'),
    "纯文本不输出进度条（避开 14 当量上限）");
var barMd = GameTextFormatter.BuildStatus(midP, "").Render(PanelMode.Markdown);
Check(barMd.Contains("▓▓▓▓░░░░░░") && barMd.Contains("%"), "markdown 卡片含修为进度条（半程应看到满格+空格）");
Check(!GameTextFormatter.BuildStatus(a, "").Render(PanelMode.Markdown).Contains('░'),
    "修为超阈值时钳到满格，不会出现负空格");

var idleMd = presenter.Compose("G1", GameAction.Status, GameTextFormatter.FormatStatus(midP, "闭关42分，修为+3"),
    MenuContext.Empty, "半程", GameTextFormatter.BuildStatus(midP, "闭关42分，修为+3")).Panel!.Markdown;
Check(idleMd.Contains("**挂机**") && !idleMd.Contains("────"), "小标题转粗体且不留装饰符号");
var maxPanelMd = GameTextFormatter.BuildStatus(maxP, "").Render(PanelMode.Markdown);
Check(!maxPanelMd.Contains('▓'), "已达巅峰时不画进度条");

Console.WriteLine();
Console.WriteLine(ok ? "== 全部冒烟测试通过 ==" : "== 存在失败项 ==");
return ok ? 0 : 1;