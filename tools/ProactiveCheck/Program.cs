using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mutter.Core.EventHandlers;
using Mutter.Core;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Models.QQOfficial;
using Mutter.Core.Proactive;
using Mutter.Core.Services;
using Mutter.Infrastructure;
using Mutter.Infrastructure.Data;
using Microsoft.Extensions.Hosting;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;
using Mutter.Web.Controllers;

// 择机主动发言自检：纯逻辑门禁 + 生产库副本上的迁移幂等 + DI 装配。
// 用法：
//   dotnet run --project tools/ProactiveCheck -- [源库路径]
//   dotnet run --project tools/ProactiveCheck -- --logic-only
// --logic-only 只跑不依赖数据库的第 1~3 段，供 CI 使用（第 4~6 段需要真实库）。
var logicOnly = args.Contains("--logic-only");
var sourceDb = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "mutter-bot.db";
if (!logicOnly && !File.Exists(sourceDb))
{
    Console.WriteLine($"源库不存在：{sourceDb}（需先做一致性快照，避免拷到半截 WAL）");
    Console.WriteLine("若只想跑不依赖数据库的逻辑段（1~3），加 --logic-only。");
    return 2;
}

var ok = true;
void Check(bool cond, string name)
{
    Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
    if (!cond) ok = false;
}

static ProactiveChatConfig Config(int windowSeconds = 15, int cooldownMinutes = 1,
    int maxPerGroupPerDay = 10, int maxGlobalPerDay = 100,
    int quietStart = 0, int quietEnd = 0) => new ProactiveChatConfig
{
    Enabled = true,
    WindowSeconds = windowSeconds,
    CooldownMinutes = cooldownMinutes,
    MaxPerGroupPerDay = maxPerGroupPerDay,
    MaxGlobalPerDay = maxGlobalPerDay,
    QuietStartHour = quietStart,
    QuietEndHour = quietEnd
}.Normalize();

Console.WriteLine("== 1. 配置钳制与静默时段 ==");
var clamped = new ProactiveChatConfig
{
    WindowSeconds = 5, WindowMaxMessages = 999, CooldownMinutes = 0,
    ActivePerGroupPerMinute = 999,
    ActivePerGroupPerDay = 99999, ActivePerAccountPerMinute = 99
}.Normalize();
Check(clamped.WindowSeconds == 15, $"窗口秒数 5 → {clamped.WindowSeconds}（下限 15）");
Check(clamped.WindowMaxMessages == 50, $"窗口条数 999 → {clamped.WindowMaxMessages}（上限 50）");
Check(clamped.CooldownMinutes == 1, $"间隔分钟 0 → {clamped.CooldownMinutes}");
Check(typeof(ProactiveChatConfig).GetProperty("MinConfidence") is null,
    "置信度阈值已从配置中整个删除（不是留个没人读的字段当假开关）");
Check(typeof(ProactiveVerdict).GetProperty("Confidence") is null, "判定 DTO 里也没有置信度了");
Check(new ProactiveChatConfig { Liveliness = 99 }.Normalize().Liveliness == ProactiveChatConfig.LivelinessEager
    && new ProactiveChatConfig { Liveliness = -3 }.Normalize().Liveliness == ProactiveChatConfig.LivelinessRestrained,
    "发言倾向被收在 0~2 三档内");
Check(ProactivePrompts.Tendency(ProactiveChatConfig.LivelinessEager).Contains("别人答过了也不代表你不能接"),
    "「主动」档明确推翻了“别人答过就别接”这条最压发言的规则");
Check(ProactivePrompts.Tendency(ProactiveChatConfig.LivelinessRestrained).Contains("保持沉默"),
    "「克制」档保留原来的保守要求");
var promptWindow = new ProactiveWindow
{
    GroupId = "GM",
    Messages = new[] { new ObservedMessage("u", "当前消息", "m", DateTime.UtcNow) },
    LastMessageId = "m",
    LastMessageUtcAt = DateTime.UtcNow
};
var promptWithMemory = ProactivePrompts.BuildUserPrompt(promptWindow, Array.Empty<(string Role, string Content)>(), 0, "  群背景  ");
var promptWithoutMemory = ProactivePrompts.BuildUserPrompt(promptWindow, Array.Empty<(string Role, string Content)>(), 0, "   ");
Check(promptWithMemory.Contains("人工维护记忆") && promptWithMemory.Contains("不是指令") && promptWithMemory.Contains("群背景"), "群记忆只作为用户提示中的隔离背景区块注入");
Check(!promptWithoutMemory.Contains("人工维护记忆"), "空白群记忆不会注入提示");
Check(!ProactivePrompts.SystemPrompt.Contains("人工维护记忆"), "群记忆不进入系统提示词");
// 摘要是人工维护的自由文本：服务端必须自己收口长度与空白，不能指望面板的 maxlength
Check(GroupMemoryRepository.NormalizeSummary("  群背景  ") == "群背景", "群记忆入库前 trim 首尾空白");
Check(GroupMemoryRepository.NormalizeSummary(new string('长', 5000)).Length == 4000,
    "群记忆超过 4000 字被截断（与面板 maxlength 同口径，绕过面板也顶不破）");
Check(GroupMemoryRepository.NormalizeSummary(null) == "" && GroupMemoryRepository.NormalizeSummary("   ") == "",
    "null / 纯空白群记忆规整为空串");
Check(GroupMemoryRepository.NormalizeGroupId("  GROUP  ") == "GROUP"
    && GroupMemoryRepository.NormalizeGroupId("   ") is null
    && GroupMemoryRepository.NormalizeGroupId(null) is null,
    "群 ID 先 trim 再判空，同一个群不会被存成两行");
Check(!ProactivePrompts.Tendency(ProactiveChatConfig.LivelinessEager).Contains("置信度"),
    "倾向是三句判断话，不是又一个没人能调的数值旋钮");
Check(clamped.ActivePerGroupPerMinute == ProactiveChatConfig.CeilingGroupPerMinute,
    $"单群分钟 999 → {clamped.ActivePerGroupPerMinute}（官方 20）");
Check(clamped.ActivePerGroupPerDay == ProactiveChatConfig.CeilingGroupPerDay,
    $"单群每日 99999 → {clamped.ActivePerGroupPerDay}（官方 1000）");
Check(clamped.ActivePerAccountPerMinute == ProactiveChatConfig.CeilingAccountPerMinute,
    $"账号分钟 99 → {clamped.ActivePerAccountPerMinute}（官方 60）");

// 北京时间 00:30（UTC 前一日 16:30）落在 23→8 静默段内
var quietUtc = new DateTime(2026, 10, 5, 16, 30, 0, DateTimeKind.Utc);
var talkUtc = new DateTime(2026, 10, 5, 4, 0, 0, DateTimeKind.Utc);
var night = Config(quietStart: 23, quietEnd: 8);
Check(night.IsQuietNow(quietUtc), "静默 23→8：北京 00:30 静默");
Check(!night.IsQuietNow(talkUtc), "静默 23→8：北京 12:00 不静默");
Check(!Config().IsQuietNow(quietUtc), "静默两值相等视为不静默");

Console.WriteLine("== 2. 观察窗口与发言门禁 ==");
var observe = new ProactiveObserveService();
var dueCfg = Config(windowSeconds: 15);
observe.Observe("G1", "泷奈", "今晚吃什么", "msg-1");
observe.Observe("G1", "千束", "<@!123> 我我我", "msg-2");
observe.Observe("G1", "呆宝", "拉面", "msg-3");

Check(observe.PendingWindows().Single().Pending == 3, "三条围观消息进同一窗口");
Check(!observe.IsDue("G1", dueCfg, DateTime.UtcNow), "窗口未满时不到评估时间");
Check(observe.IsDue("G1", dueCfg, DateTime.UtcNow.AddSeconds(20)), "攒够窗口时长后可评估");

observe.Observe("G1", "泷奈", "", "msg-empty");
observe.Observe("G1", "泷奈", "  ", "msg-blank");
Check(observe.PendingWindows().Single().Pending == 3, "空文本不入窗（不会把空白当话）");

var window = observe.Take("G1", 2) ?? throw new InvalidOperationException("Take 不应返回空窗口");
Check(window.Messages.Count == 2, $"maxMessages=2 时只带入最近 2 条（实际 {window.Messages.Count}）");
Check(window.LastMessageId == "msg-3", $"槽位取最后一条消息 id（{window.LastMessageId}）");
Check(observe.PendingWindows().Count == 0, "Take 之后窗口清空");
Check(!observe.IsDue("G1", dueCfg, DateTime.UtcNow.AddSeconds(60)), "空窗口不重复触发评估");

var now = DateTime.UtcNow;

Console.WriteLine("== 2b. 表情与图片进窗口 ==");
var imgObs = new ProactiveObserveService();
imgObs.Observe("GI", "某人", "[表情图]", "i1", new[] { "https://multimedia.nt.qq.com.cn/a" });
imgObs.Observe("GI", "某人", "[图片]", "i2", new[] { "https://multimedia.nt.qq.com.cn/b" });
imgObs.Observe("GI", "某人", "这图里是啥", "i3");
imgObs.Observe("GI", "某人", "[表情图]", "i4", new[] { "https://multimedia.nt.qq.com.cn/c" });
Check(imgObs.PendingWindows().Single().Pending == 4, "表情/图片消息不再被当成空内容丢掉");
var imgWindow = imgObs.Take("GI", 20) ?? throw new InvalidOperationException("Take 不应返回空窗口");
Check(imgWindow.ImageCount == 3, $"窗口统计到 3 张图（实际 {imgWindow.ImageCount}）");
var picked = imgWindow.CollectImages(2);
Check(picked.Count == 2 && picked[0].EndsWith("/b") && picked[1].EndsWith("/c"),
    "附带图片取最近的 N 张并保持时间正序（旧图链接会失效，带上也没意义）");
Check(imgWindow.CollectImages(0).Count == 0 && imgWindow.CollectImages(9).Count == 3,
    "上限 0 时不带图，上限充足时全带");

Check(new ProactiveWindow
{
    GroupId = "G1", Messages = new[] { new ObservedMessage("a", "b", "m", now) },
    LastMessageId = "m", LastMessageUtcAt = now.AddMinutes(-4)
}.PassiveSlotAlive(now), "被动槽位 4 分钟内视为有效");
Check(!new ProactiveWindow
{
    GroupId = "G1", Messages = new[] { new ObservedMessage("a", "b", "m", now) },
    LastMessageId = "m", LastMessageUtcAt = now.AddMinutes(-6)
}.PassiveSlotAlive(now), "被动槽位 6 分钟后视为过期");
Check(!new ProactiveWindow
{
    GroupId = "G1", Messages = Array.Empty<ObservedMessage>(),
    LastMessageId = "", LastMessageUtcAt = now
}.PassiveSlotAlive(now), "无 msg_id 时不走被动通道");

var capCfg = Config(maxPerGroupPerDay: 3, maxGlobalPerDay: 100);
var t0 = new DateTime(2026, 10, 5, 4, 0, 0, DateTimeKind.Utc);
for (var i = 0; i < 2; i++)
{
    var at = t0.AddMinutes(i * 2);
    observe.MarkSent("G1", at);
    Check(observe.BlockReason("G1", capCfg, at.AddSeconds(61)) is null, $"已发 {i + 1} 条，间隔过后仍可继续");
}
observe.MarkSent("G1", t0.AddMinutes(4));
Check(observe.BlockReason("G1", capCfg, t0.AddMinutes(5)) == ProactiveOutcomes.SkippedGroupCap,
    "已发满 3 条 → skipped_group_cap");
observe.MarkSent("G1", t0.AddMinutes(6));
Check(observe.BlockReason("G1", capCfg, t0.AddMinutes(6)) == ProactiveOutcomes.SkippedCooldown,
    "同群间隔未到 → skipped_cooldown");
Check(observe.BlockReason("G1", capCfg, t0.AddMinutes(10)) == ProactiveOutcomes.SkippedGroupCap,
    "单群日上限用尽 → skipped_group_cap");

var globalCfg = Config(maxPerGroupPerDay: 10, maxGlobalPerDay: 2);
var g = new ProactiveObserveService();
g.MarkSent("GA", t0);
g.MarkSent("GB", t0.AddMinutes(2));
Check(g.BlockReason("GC", globalCfg, t0.AddMinutes(4)) == ProactiveOutcomes.SkippedGlobalCap,
    "全局日上限用尽 → skipped_global_cap");

Console.WriteLine("== 2c. 全量/@ 双事件的 msg_id 闸门（@ 它不回话的根因） ==");
var gate = new GroupMessageGate();
Check(gate.ShouldHandle("M1", false, out _), "全量事件首次到达要处理");
Check(!gate.ShouldHandle("M1", false, out _), "同一条全量消息重复投递被丢弃");
Check(gate.ShouldHandle("M1", true, out var reAt1) && reAt1,
    "全量先到时后来的 @ 事件必须补走回复链路（旧逻辑在这里直接把 @ 吞了）");
Check(!gate.ShouldHandle("M1", true, out _), "补走过一次 @ 后，@ 事件再重复也不会回第二遍");
Check(gate.ShouldHandle("M2", true, out var reAt2) && !reAt2, "@ 事件先到时按正常 @ 处理，不算补走");
Check(!gate.ShouldHandle("M2", false, out _), "@ 先到后全量事件不再重复落库");

Console.WriteLine("== 3. 文案清洗与 msg_seq 共享 ==");
Check(ProactivePrompts.SanitizeLine(null) is null, "null 不发言");
Check(ProactivePrompts.SanitizeLine("   ") is null, "纯空白不发言");
Check(ProactivePrompts.SanitizeLine(" 今天   吃   拉面 ") == "今天 吃 拉面", "压缩多余空白并 trim");
Check(ProactivePrompts.SanitizeLine(new string('字', 60))!.Length == ProactivePrompts.MaxLineChars,
    $"超长截断到 {ProactivePrompts.MaxLineChars} 字");

var seq = new ReplySeqTracker();
Check(seq.Next(null) is null, "主动消息（msg_id 空）不带 msg_seq");
Check(seq.Next("m1") == 1 && seq.Next("m1") == 2 && seq.Next("m1") == 3, "同一 msg_id 递增 msg_seq");
Check(seq.Next("m2") == 1, "不同 msg_id 各自计数");

Console.WriteLine("== 3b. 结构化 AI 输出解析（生产日志里的真实样本） ==");
// 2026-10-05 23:30 线上原文：```json 围栏 + 中文字符串值里被插了裸换行，
// 当时两路解析全部失败，择机评估被打成 skipped_ai_unavailable
var realRaw = @"```json
{
  ""should_speak"": false,
  ""confidence"": 0.2
,
  ""line"": """",
  ""topic"": ""QQ开放机器人平台"",
  ""reason"": ""群
友在连续发表个人看法，没有提问或抛出话题，插嘴容易变成无意义的纯附
和""
}
```";
var repaired = StructuredAiService.ExtractRepairableJson(realRaw);
Check(repaired is not null, "围栏 + 裸换行的输出仍能被提取修复");
Check(repaired!.Contains("\\n友"), "字符串值里的裸换行被转义");
var verdictRaw = System.Text.Json.JsonSerializer.Deserialize<ProactiveVerdict>(
    repaired!, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
Check(verdictRaw!.Line == "",
    "修复后判定可读（旧样本里的 should_speak/confidence 字段被直接忽略，不再进 DTO）");
Check(typeof(ProactiveVerdict).GetProperty("ShouldSpeak") is null,
    "should_speak 已删除：说话与否只剩 line 一个出口，不会再出现「想好了却不发」这种两个门打架");
Check(verdictRaw!.Reason!.Contains("友在连续") && verdictRaw.Reason.Contains('\n'),
    "理由里的折行按换行保留（不吞字）");
Check(ProactivePrompts.SanitizeLine(verdictRaw.Line) is null, "空 line 不会被当成要发言");
Check(ProactivePrompts.SanitizeLine("纯附\n和") == "纯附 和", "真要发言的 line 会压平折行，不会把换行发进群");
var withProse = StructuredAiService.ExtractRepairableJson("{\"should_speak\": true, \"confidence\": 0.9}");
Check(withProse is not null && withProse.Contains("should_speak"), "普通对象输出照常解析");
Check(StructuredAiService.ExtractRepairableJson("模型今天不肯输出 JSON") is null, "没有对象时返回 null 让调用方兜底");

using (var testServices = new ServiceCollection().AddLogging().BuildServiceProvider())
{
    var logger = testServices.GetRequiredService<ILogger<StructuredAiService>>();
    var scopeFactory = testServices.GetRequiredService<IServiceScopeFactory>();
    var secondRoundRaw = "第二轮原文：模型拒绝输出 JSON";
    var secondRoundService = new StructuredAiService(scopeFactory, new ScriptedAiChatService(
        new AiChatResponse { Content = "第一轮没有 JSON" },
        new AiChatResponse
        {
            Content = "第二轮清洗后内容",
            RawContent = secondRoundRaw,
            Model = "test-model",
            PromptTokens = 91,
            CompletionTokens = 17
        }), logger);
    var secondRoundOutcome = await secondRoundService.GetStructuredOutcomeAsync<ProactiveVerdict>(
        "system", "user", new AiConfig());
    Check(secondRoundOutcome.FailureKind == StructuredAiFailureKind.Unparsable
        && secondRoundOutcome.RawContent == secondRoundRaw
        && secondRoundOutcome.PromptTokens == 91 && secondRoundOutcome.CompletionTokens == 17,
        "第二轮解析失败保留模型原文与本次用量");

    var truncatedService = new StructuredAiService(scopeFactory, new ScriptedAiChatService(
        new AiChatResponse { Content = "第一轮没有 JSON" },
        new AiChatResponse { Content = "截断内容", FinishReason = "length", PromptTokens = 33, CompletionTokens = 64 }), logger);
    var truncatedOutcome = await truncatedService.GetStructuredOutcomeAsync<ProactiveVerdict>(
        "system", "user", new AiConfig());
    Check(truncatedOutcome.FailureKind == StructuredAiFailureKind.Truncated
        && truncatedOutcome.PromptTokens == 33 && truncatedOutcome.CompletionTokens == 64,
        "finish_reason=length 单独分类并保留 tokens");

    var apiErrorService = new StructuredAiService(scopeFactory, new ScriptedAiChatService(
        new InvalidOperationException("第一轮错误"), new InvalidOperationException("第二轮 API 错误摘要")), logger);
    var apiErrorOutcome = await apiErrorService.GetStructuredOutcomeAsync<ProactiveVerdict>(
        "system", "user", new AiConfig());
    Check(apiErrorOutcome.FailureKind == StructuredAiFailureKind.ApiError
        && apiErrorOutcome.ErrorMessage == "第二轮 API 错误摘要",
        "API 调用失败返回末次异常摘要");
}

Console.WriteLine("== 3c. 表情贴纸文案归一化 ==");
var parser = new MessageParser();
var sticker = parser.Parse("<faceType=6,faceId=\"0\",ext=\"eyJ0ZXh0IjoiIn0=\">",
    new List<Attachment> { new() { ContentType = "image/jpeg", Url = "https://multimedia.nt.qq.com.cn/x" } });
Check(sticker.PlainText == "[表情图]", $"群表情包（无名但有图）→ {sticker.PlainText}");
var liked = parser.Parse("<faceType=1,faceId=\"76\",ext=\"eyJ0ZXh0Ijoi6LWeIn0=\">", null);
Check(liked.PlainText == "[表情:赞]", $"系统表情带名字 → {liked.PlainText}");
var cheer = parser.Parse("<faceType=4,faceId=\"\",ext=\"eyJ0ZXh0IjoiW+asouWRvF0ifQ==\">", null);
Check(cheer.PlainText == "[表情:欢呼]", $"官方已带方括号的名称不重复加括号 → {cheer.PlainText}");
var mixed = parser.Parse("这个好吃<faceType=1,faceId=\"351\",ext=\"eyJ0ZXh0Ijoi5pWy5pWyIn0=\">", null);
Check(mixed.PlainText == "这个好吃[表情:敲敲]", "表情段混在正文里时只替换该段");
var picOnly = parser.Parse("", new List<Attachment> { new() { ContentType = "image/png", Url = "https://multimedia.nt.qq.com.cn/y" } });
Check(picOnly.PlainText == "[图片]" && picOnly.HasImage, "纯图片消息有占位，不会被下游当空文本丢掉");
var textOnly = parser.Parse("今晚吃拉面", null);
Check(textOnly.PlainText == "今晚吃拉面" && !textOnly.HasImage, "普通文本原样保留");
var bogus = parser.Parse("<faceType=6,faceId=\"0\",ext=\"这不是base64\">", null);
Check(bogus.PlainText == "[表情]", "ext 解不出来时降级成 [表情]，不抛异常");
Check(MessageParser.NormalizeFaceTags("[表情图]", true) == "[表情图]", "已归一化的文本幂等，回填可重复执行");

Console.WriteLine("== 3d. 判定提示词：只留说与不说 ==");
Check(!ProactivePrompts.SystemPrompt.Contains("confidence"), "提示词不再要求模型自报置信度");
Check(!ProactivePrompts.SystemPrompt.Contains("0.5"), "删掉了「拿不准就给 0.5 以下」——它和面板阈值本来就是互相打架的");
Check(ProactivePrompts.SystemPrompt.Contains("[图片]") && ProactivePrompts.SystemPrompt.Contains("没附上的图不要臆测"),
    "提示词要求先读图、且不许臆测没附上的图");
var oneImage = new ProactiveWindow
{
    GroupId = "GP",
    Messages = new[] { new ObservedMessage("某人", "[图片]", "m", now, new[] { "https://multimedia.nt.qq.com.cn/z" }) },
    LastMessageId = "m", LastMessageUtcAt = now
};
var noTuple = Array.Empty<(string, string)>();
Check(ProactivePrompts.BuildUserPrompt(oneImage, noTuple, 1).Contains("已全部附"),
    "图都附上时如实告诉模型");
Check(ProactivePrompts.BuildUserPrompt(oneImage, noTuple, 0).Contains("一张都没能附上"),
    "一张图都没附上时如实说明，而不是让模型以为自己看到了");
var threeImages = new ProactiveWindow
{
    GroupId = "GP", LastMessageId = "m2", LastMessageUtcAt = now,
    Messages = new[]
    {
        new ObservedMessage("某人", "[图片]", "m1", now, new[] { "u1", "u2" }),
        new ObservedMessage("某人", "[表情图]", "m2", now, new[] { "u3" })
    }
};
Check(threeImages.ImageCount == 3 && threeImages.CollectImages(2).SequenceEqual(new[] { "u2", "u3" }),
    "多消息多图时按时间展平并截最近 N 张");
Check(ProactivePrompts.BuildUserPrompt(threeImages, noTuple, 1).Contains("共 3 张图片，只附了最近的 1 张"),
    "部分附上时说清附了几张");

Console.WriteLine("== 3c2. 背景历史与窗口消息去重（同一行曾被喂两遍） ==");
// 线上真实形状：context.Messages = 历史 + 当前条；窗口那批同时也躺在历史里
var dedupWindow = new ProactiveWindow
{
    GroupId = "GD", LastMessageId = "m4", LastMessageUtcAt = now,
    Messages = new[]
    {
        new ObservedMessage("泷奈", "晚点说", "m3", now),
        new ObservedMessage("千束", "在吗", "m4", now)
    }
};
var ctxMessages = new List<(string Role, string Content)>
{
    ("user", "[呆宝]: 早上好"),      // 真正的旧背景，必须保留
    ("assistant", "早"),             // bot 自己的历史，必须保留
    ("user", "[泷奈]: 晚点说"),      // 与窗口重复，必须剔除
    ("user", "[千束]: 在吗")         // 它同时是 context 最后一条，Evaluate 当初靠 [..^1] 剪掉
};
var deduped = dedupWindow.ExcludeFrom(ctxMessages);
Check(deduped.Count == 2 && deduped[0].Content == "[呆宝]: 早上好" && deduped[1].Content == "早",
    $"窗口消息从背景历史中剔除，只留真正的旧背景（{ctxMessages.Count} → {deduped.Count} 条）");
Check(!deduped.Any(m => m.Content.Contains("晚点说")) && !deduped.Any(m => m.Content.Contains("[千束]")),
    "被剔除的正是窗口里那两行，不是别的历史消息");

var afterPrunePrompt = ProactivePrompts.BuildUserPrompt(dedupWindow, deduped, 0);
Check(afterPrunePrompt.Split("晚点说").Length - 1 == 1 && afterPrunePrompt.Split("在吗").Length - 1 == 1,
    "最终 prompt 里同一行只出现一次（此前背景段与窗口段各渲染一遍）");
Check(ProactivePrompts.BuildUserPrompt(dedupWindow, ctxMessages, 0).Split("晚点说").Length - 1 == 2,
    "反向对照：不做去重时确实渲染两遍，说明这条断言不是空转");

// 不同人说的同一句话不能被当成同一条剔掉（所以要带上昵称一起比）
var sameWords = new ProactiveWindow
{
    GroupId = "GE", LastMessageId = "s2", LastMessageUtcAt = now,
    Messages = new[] { new ObservedMessage("千束", "好", "s2", now) }
};
var sameWordsHistory = new List<(string, string)>
{
    ("user", "[泷奈]: 好"),
    ("user", "[千束]: 好")
};
var sameWordsKept = sameWords.ExcludeFrom(sameWordsHistory);
Check(sameWordsKept.Count == 1 && sameWordsKept[0].Content == "[泷奈]: 好",
    "同名话不同人：只剔掉窗口里那个人说的，另一个人的原话保留");

// 上游折行清理会改空白，归一化后仍要能匹配上
var wsWindow = new ProactiveWindow
{
    GroupId = "GF", LastMessageId = "w1", LastMessageUtcAt = now,
    Messages = new[] { new ObservedMessage("呆宝", "我 今 天 不 来 了", "w1", now) }
};
Check(wsWindow.ExcludeFrom(new List<(string, string)> { ("user", "[呆宝]: 我今天不来了") }).Count == 0,
    "只差空白的历史行同样被剔除（窗口压过空白、入库行是上游原文）");

// 同一条消息出现两次时只抵消一次，不能把真实的历史也吃掉
var twiceWindow = new ProactiveWindow
{
    GroupId = "GG", LastMessageId = "t1", LastMessageUtcAt = now,
    Messages = new[] { new ObservedMessage("呆宝", "哈哈", "t1", now) }
};
var twiceKept = twiceWindow.ExcludeFrom(new List<(string, string)>
{
    ("user", "[呆宝]: 哈哈"),
    ("user", "[呆宝]: 哈哈")
});
Check(twiceKept.Count == 1, $"按条数逐个抵消而不是整类删光（2 条留 {twiceKept.Count} 条）");
Check(dedupWindow.ExcludeFrom(new List<(string, string)>()).Count == 0, "空历史原样返回，不抛");

Console.WriteLine("== 3d2. @ 识别码判定（全量模式下 @ 它不回话的正解） ==");
Check(BotMention.IsMentioned("<@11112222333344445555666677778888> 中午吃什么", "11112222333344445555666677778888"),
    "识别码出现在 at 段里即判为 @ 机器人");
Check(BotMention.IsMentioned("<@!11112222333344445555666677778888> 在吗", "11112222333344445555666677778888"),
    "带 ! 前缀与小写 openid 同样认得");
Check(!BotMention.IsMentioned("<@99998888777766665555444433332222> 庸医", "11112222333344445555666677778888"),
    "@ 的是群友时不误判（宁可少回话，也不能见 @ 就答）");
Check(!BotMention.IsMentioned("<@11112222333344445555666677778888> 在吗", null),
    "群没配识别码时一律不按 @ 处理");
Check(BotMention.Strip("<@11112222333344445555666677778888> 中午吃什么", "11112222333344445555666677778888") == "中午吃什么",
    "喂模型前剥掉 at 段，与官方 @ 事件下发的文本一致");
Check(BotMention.Strip("中午吃什么", "11112222333344445555666677778888") == "中午吃什么", "没有 at 段时不误伤正文");

Console.WriteLine("== 3e. 附图类型嗅探（GIF 会把整次调用打死） ==");
Check(AiChatService.DetectImageMime(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }) == "image/gif", "认得出 GIF 文件头");
Check(AiChatService.DetectImageMime(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }) == "image/jpeg", "认得出 JPEG 文件头");
Check(AiChatService.DetectImageMime(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }) == "image/png", "认得出 PNG 文件头");
Check(AiChatService.DetectImageMime(System.Text.Encoding.ASCII.GetBytes("RIFFxxxxWEBP")) == "image/webp", "认得出 WEBP 文件头");
Check(AiChatService.DetectImageMime(new byte[] { 1, 2, 3 }) is null, "认不出的一律 null");
Check(!AiChatService.SupportedImageMime("image/gif"), "GIF 不附给模型（线上就是这个把评估打成 500 的）");
Check(AiChatService.SupportedImageMime("image/png") && AiChatService.SupportedImageMime("image/jpeg"), "png/jpeg 照附");
Check(!AiChatService.SupportedImageMime(null) && !AiChatService.SupportedImageMime("image/webp"),
    "类型未知或模型侧没确认过的，宁可只留文字占位");
Check(ProactiveChatConfig.DefaultMaxAttachedImages == 3,
    "附图张数默认仍是 3（token 与时效的折中），但它已从常量变成面板可调项");
Check(new ProactiveChatConfig { MaxAttachedImages = 99 }.Normalize().MaxAttachedImages
        == ProactiveChatConfig.CeilingAttachedImages,
    $"附图张数 99 → {ProactiveChatConfig.CeilingAttachedImages}（上限收敛）");
Check(new ProactiveChatConfig { MaxAttachedImages = -5 }.Normalize().MaxAttachedImages == 0,
    "附图张数 -5 → 0，即面板填 0 表示一张图都不带（纯文字判定）");
Check(ProactiveChatConfig.DefaultHistoryMaxMessages == 8 && ProactiveChatConfig.DefaultEvalMaxTokens == 5120,
    "择机自己的背景条数与输出上限有独立默认值（不再吃 AI 面板的 20 / 2048）");
Check(new ProactiveChatConfig { HistoryMaxMessages = -1, EvalMaxTokens = 1 }.Normalize()
        is { HistoryMaxMessages: 0, EvalMaxTokens: 64 },
    "择机的历史条数与输出上限同样被钳制在可用区间");

Console.WriteLine("== 3e2. @ 回复的附图张数（旧代码固定只取第一张） ==");
var five = new[] { "u1", "u2", "u3", "u4", "u5" };
var replyCfg = new AiConfig();
Check(replyCfg.MaxAttachedImages == 3, $"@ 回复默认带 {replyCfg.MaxAttachedImages} 张（与择机默认一致）");
Check(replyCfg.CapAttachedImages(five).SequenceEqual(new[] { "u1", "u2", "u3" }),
    "五张图按原顺序取前三张——旧代码在这里只带 u1，其余四张静默丢弃");
Check(replyCfg.CapAttachedImages(new[] { "a" }).SequenceEqual(new[] { "a" }),
    "只发一张时照常带上，不受上限影响");
Check(new AiConfig { MaxAttachedImages = 0 }.CapAttachedImages(five).Count == 0,
    "面板填 0 时一张都不带（不回退默认值）");
Check(new AiConfig { MaxAttachedImages = 10 }.CapAttachedImages(five).Count == 5,
    "上限高于实际张数时全带，不凭空补图");
Check(replyCfg.CapAttachedImages(Array.Empty<string>()).Count == 0, "没图时不报错");
Check(AiConfig.CeilingAttachedImages == 10, "附图上限是 10，服务端按它钳制面板提交值");

Console.WriteLine("== 3f. 上游硬折行清理（全部用线上真实原文） ==");
Check(CjkTextFixer.Cleanse("伴郎有什么好看的！还没吃呢，饿死了 (ᗜ ˰ ᗜ\n)\n")
    == "伴郎有什么好看的！还没吃呢，饿死了 (ᗜ ˰ ᗜ)",
    "折进气球表情括号里的换行接回去，行尾空行删掉");
Check(CjkTextFixer.Cleanse("催啥催嘛，我这转半\n天都打不开，到底是什么神秘网站(ᗜ ˰ ᗜ)\n")
    == "催啥催嘛，我这转半天都打不开，到底是什么神秘网站(ᗜ ˰ ᗜ)",
    "半句话中间被插的换行接回去");
Check(CjkTextFixer.Cleanse("催催催，我这打不开啊，里面到底有啥好康的 (ᗜ\n˰ᗜ)\n")
    == "催催催，我这打不开啊，里面到底有啥好康的 (ᗜ˰ᗜ)",
    "ᗜ 与 ˰ 之间这类非汉字字符的折行同样能接（不能只按 CJK 范围判断）");
Check(CjkTextFixer.Cleanse("天 禄0-3放得可以说是\n毫无心理负担") == "天禄0-3放得可以说是毫无心理负担",
    "两个汉字之间的空格是噪声，删掉");
Check(CjkTextFixer.Cleanse("天禄赢了\n\n下一把继续") == "天禄赢了\n\n下一把继续",
    "真空行分段是真意图，保留");
Check(CjkTextFixer.Cleanse("今日任务：\n- 修炼 3 次\n- 采药 2 次") == "今日任务：\n- 修炼 3 次\n- 采药 2 次",
    "冒号引出的列表不被压成一行");
Check(CjkTextFixer.Cleanse("突破\n1. 引气\n2. 筑基") == "突破\n1. 引气\n2. 筑基",
    "行首是编号的行按结构保留（菜单与结算排版不能毁掉）");
Check(CjkTextFixer.Cleanse("The proxy wrapped\nthis sentence") == "The proxy wrapped this sentence",
    "英文两侧都是字词时补回一个空格而不是粘在一起");
Check(CjkTextFixer.Cleanse(null) == "" && CjkTextFixer.Cleanse("   \n  ") == "", "空与纯空白安全");

// 同一个上游毛病也是结构化 JSON 解析失败的根源：接回换行只会让解析更稳
var jsonBroken = CjkTextFixer.Cleanse("{\n  \"line\": \"天\n禄赢了\",\n  \"topic\": \"比赛\"\n}");
var repairedJson = StructuredAiService.ExtractRepairableJson(jsonBroken)!;
Check(System.Text.Json.JsonSerializer.Deserialize<ProactiveVerdict>(repairedJson)!.Line == "天禄赢了",
    "折在 JSON 字符串值里的换行被接回，判定解析不再靠运气");

if (logicOnly)
{
    Console.WriteLine("\n--logic-only：跳过依赖数据库的第 4~6 段。");
    Console.WriteLine(ok ? "\n全部通过" : "\n存在失败项");
    return ok ? 0 : 1;
}

Console.WriteLine("== 4. 生产库副本上的迁移幂等 ==");
var targetDb = Path.Combine(AppContext.BaseDirectory, "mutter-bot.db");
foreach (var suffix in new[] { "", "-wal", "-shm" })
{
    var src = targetDb + suffix;
    if (File.Exists(src)) File.Delete(src);
    if (File.Exists(sourceDb + suffix)) File.Copy(sourceDb + suffix, src, true);
}
Console.WriteLine($"源库：{sourceDb} → 副本 {targetDb}（生产库只读，不改）");

long MessagesBefore;
List<string> legacyNumericGroupIds;
var flagsBefore = new Dictionary<string, long>();
using (var probe = new SqliteConnection($"Data Source={targetDb}"))
{
    probe.Open();
    // 归一化上线前按原文入库的历史行：迁移要把它们换成占位，且不动已经归一化好的行
    using (var fix = probe.CreateCommand())
    {
        fix.CommandText = """
            INSERT INTO ChatMessages ("MessageId","ChatType","GroupId","UserId","UserName","PlainText","RawSegmentsJson","HasImage","HasAtBot","IsBotMessage","PromptTokens","CompletionTokens","CreatedAt")
            VALUES
             ('fx-face-1',0,'TESTGROUP-FACE','u','某人','<faceType=6,faceId="0",ext="eyJ0ZXh0IjoiIn0=">','[]',1,0,0,0,0,'2026-10-01 00:00:00'),
             ('fx-face-2',0,'TESTGROUP-FACE','u','某人','<faceType=1,faceId="76",ext="eyJ0ZXh0Ijoi6LWeIn0=">','[]',0,0,0,0,0,'2026-10-01 00:00:01'),
             ('fx-face-3',0,'TESTGROUP-FACE','u','某人','[表情图]','[]',1,0,0,0,0,'2026-10-01 00:00:02');
            """;
        fix.ExecuteNonQuery();
    }
    MessagesBefore = Scalar(probe, "SELECT COUNT(*) FROM ChatMessages");
    // 迁移前先取一份「数字群号时代」的键快照：按形状（纯数字）识别，
    // 不把任何具体群号写进仓库，因此换一个库跑也成立。
    legacyNumericGroupIds = ReadNumericGroupIds(probe);
    flagsBefore = ReadFlags(probe);
}

using (var db = new AppDbContext())
{
    await db.Database.EnsureCreatedAsync();
    await db.EnsureSchemaUpgradedAsync();
}
// 第二次执行必须不抛（线上部署会重复跑到这段）
using (var db = new AppDbContext())
{
    await db.EnsureSchemaUpgradedAsync();
}

using (var probe = new SqliteConnection($"Data Source={targetDb}"))
{
    probe.Open();
    Check(Scalar(probe, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProactiveChatConfigs'") == 1,
        "ProactiveChatConfigs 建表成功");
    Check(Scalar(probe, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProactiveChatLogs'") == 1,
        "ProactiveChatLogs 建表成功");
    Check(Scalar(probe, "SELECT COUNT(*) FROM pragma_table_info('GroupConfigs') WHERE name='AiProactiveEnabled'") == 1,
        "GroupConfigs.AiProactiveEnabled 列补齐");
    Check(Scalar(probe, "SELECT COUNT(*) FROM pragma_index_list('ProactiveChatLogs')") == 2, "留痕表两个索引就位");
    Check(Scalar(probe, "SELECT COUNT(*) FROM ChatMessages") == MessagesBefore, $"聊天历史未被迁移改动（{MessagesBefore} 条）");
    var legacyKept = ReadNumericGroupIds(probe);
    Check(legacyNumericGroupIds.All(legacyKept.Contains),
        $"数字群号时代的 {legacyNumericGroupIds.Count} 条配置未被迁移丢失");
    var flagsAfter = ReadFlags(probe);
    var preserved = flagsBefore.All(kv => flagsAfter.TryGetValue(kv.Key, out var v) && v == kv.Value);
    Check(preserved, $"迁移不改动任何群的参与位（原有 {flagsBefore.Count} 群）");
    var seededNow = flagsAfter.Where(kv => !flagsBefore.ContainsKey(kv.Key)).ToList();
    Check(seededNow.All(kv => kv.Value == 0),
        $"补齐出来的群一律默认不参与择机发言（新增 {seededNow.Count} 个：{string.Join(",", seededNow.Select(kv => kv.Key[..Math.Min(8, kv.Key.Length)] + "=" + kv.Value))}）");
}

Console.WriteLine("== 4b. 整数主键遗留修复（面板保存 500 的根因） ==");
using (var probe = new SqliteConnection($"Data Source={targetDb}"))
{
    probe.Open();
    var ddl = ScalarText(probe, "SELECT sql FROM sqlite_master WHERE type='table' AND name='GroupConfigs'") ?? "";
    Check(ddl.Contains("\"GroupId\" TEXT NOT NULL"), "GroupConfigs 主键已重建为 TEXT（可存 openid 十六进制串）");
    var legacyText = ReadNumericGroupIds(probe);
    Check(legacyNumericGroupIds.All(legacyText.Contains)
          && ScalarText(probe, "SELECT typeof(GroupId) FROM GroupConfigs LIMIT 1") == "text",
        $"数字群号时代的历史配置原样保留（{legacyNumericGroupIds.Count} 条，键转为文本）");
    Check(ScalarText(probe, "SELECT typeof(UserId) FROM PrivateChatConfigs LIMIT 1") == "text",
        "PrivateChatConfigs.UserId 同样转为 TEXT 主键");
}

// 真正走一遍面板路径：给一个十六进制 openid 存群配置
using (var db = new AppDbContext())
{
    var repo = new Mutter.Infrastructure.Repositories.ConfigRepository(db);
    const string hexGroup = "AAAABBBBCCCCDDDDEEEEFFFF00001111";
    await repo.SaveGroupConfigAsync(new GroupConfig
    {
        GroupId = hexGroup, IsEnabled = true, TriggerMode = 2, AiProactiveEnabled = true,
        CustomSystemPrompt = "本群专属提示词"
    });
    var read = await repo.GetGroupConfigAsync(hexGroup);
    Check(read is { AiProactiveEnabled: true }, "openid 群配置写入→读回成功（原 500 场景已消除）");
    Check(read!.TriggerMode == 2 && read.CustomSystemPrompt == "本群专属提示词", "围观档与群专属提示词写读一致");

    // 已存在的群应被补上默认配置行，面板才有地方落开关
    var all = await repo.GetAllGroupConfigsAsync();
    var known = await repo.GetKnownGroupsAsync();
    Check(known.All(k => all.Any(g => g.GroupId == k.GroupId)),
        $"每个在册群都有配置行（{known.Count} 群），面板才有地方落开关");
    Check(all.Any(g => g.GroupId == hexGroup),
        "补齐不会挤掉已有的 openid 群配置");

    var clean = all.First(g => g.GroupId == hexGroup);
    clean.AiProactiveEnabled = false;
    clean.CustomSystemPrompt = null;
    await repo.SaveGroupConfigAsync(clean);
}
using (var db2 = new AppDbContext())
{
    await db2.EnsureSchemaUpgradedAsync();   // 再跑一次：修复与补齐都必须幂等
}
using (var probe = new SqliteConnection($"Data Source={targetDb}"))
{
    probe.Open();
    var total = Scalar(probe, "SELECT COUNT(*) FROM GroupConfigs");
    var distinct = Scalar(probe, "SELECT COUNT(DISTINCT GroupId) FROM GroupConfigs");
    Check(total == distinct && distinct >= 4,
        $"重复执行幂等：键无重复，历史配置与在册群都在（{total}/{distinct}）");
    Check(Scalar(probe, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%__textkey'") == 0, "重建用的临时表已清掉");
}

using (var probe = new SqliteConnection($"Data Source={targetDb}"))
{
    probe.Open();
    var gone = Scalar(probe, "SELECT COUNT(*) FROM pragma_table_info('GroupConfigs') "
        + "WHERE name IN ('Keywords','ProactiveKeywords','ProactiveEnabled')");
    Check(gone == 0, $"关键词三列已从 GroupConfigs 删除（残留 {gone}）");
    Check(Scalar(probe, "SELECT COUNT(*) FROM pragma_table_info('ProactiveChatConfigs') "
        + "WHERE name='PersistObservedMessages'") == 0, "全局落库开关列已删除（改由每群触发方式决定）");
    Check(Scalar(probe, "SELECT COUNT(*) FROM pragma_table_info('ProactiveChatConfigs') "
        + "WHERE name='MinConfidence'") == 0, "置信度阈值列已从库中删除");
    Check(Scalar(probe, "SELECT COUNT(*) FROM pragma_table_info('ProactiveChatConfigs') "
        + "WHERE name='Liveliness'") == 1, "发言倾向列已补上");
    Check(Scalar(probe, "SELECT COUNT(*) FROM pragma_table_info('ProactiveChatConfigs') "
        + "WHERE name IN ('MaxAttachedImages','HistoryMaxMessages','EvalMaxTokens')") == 3,
        "择机自己的附图/背景/输出三列已补上（老库补成与原来等价的默认值）");
    Check(Scalar(probe, "SELECT COUNT(*) FROM pragma_table_info('AiConfigs') "
        + "WHERE name='MaxAttachedImages'") == 1, "AiConfigs.附图张数列已补上（@ 回复不再固定只带一张）");
    Check(Scalar(probe, "SELECT COUNT(*) FROM pragma_table_info('ProactiveChatLogs') "
        + "WHERE name='Confidence'") == 0, "留痕表的置信度列已删除");
    Check(Scalar(probe, "SELECT COUNT(*) FROM pragma_table_info('ProactiveChatLogs') "
        + "WHERE name='ImageCount'") == 1, "留痕表补上附图数列");
    Check(ScalarText(probe, "SELECT PlainText FROM ChatMessages WHERE MessageId='fx-face-1'") == "[表情图]",
        "存量群表情包原文回填为 [表情图]");
    Check(ScalarText(probe, "SELECT PlainText FROM ChatMessages WHERE MessageId='fx-face-2'") == "[表情:赞]",
        "存量系统表情回填出了名字");
    Check(ScalarText(probe, "SELECT PlainText FROM ChatMessages WHERE MessageId='fx-face-3'") == "[表情图]",
        "回填幂等：已归一化的行不被二次改写");
}

Console.WriteLine("== 5. DI 装配与默认关闭时的空转 ==");
var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
services.AddCoreServices();
services.AddInfrastructureServices();
services.AddSingleton<INotificationService, NoopNotification>();
await using var sp = services.BuildServiceProvider();

using (var scope = sp.CreateScope())
{
    var pdb0 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
    // 真实库里用户可能已经保存过配置（面板开启择机），先把这张表清空再验默认态
    await pdb0.Database.ExecuteSqlRawAsync("DELETE FROM ProactiveChatConfigs");
    var initial = await repo.GetProactiveConfigAsync();
    Check(!initial.Enabled, "库中无配置行时默认关闭");

    await repo.SaveProactiveConfigAsync(new ProactiveChatConfig
    {
        Enabled = true, WindowSeconds = 3, ActivePerGroupPerMinute = 500, AllowActiveChannel = false,
        MaxAttachedImages = 7, HistoryMaxMessages = 15, EvalMaxTokens = 1024
    });
    var saved = await repo.GetProactiveConfigAsync();
    Check(saved.WindowSeconds == 15 && saved.ActivePerGroupPerMinute == 20, "保存时同样钳制到合法区间");
    Check(!saved.AllowActiveChannel, "只允许被动槽的开关能存住（过期即沉默）");
    // 逐列手工映射最容易漏字段：写进去读不回来，面板就会"保存了但没生效"
    Check(saved.MaxAttachedImages == 7 && saved.HistoryMaxMessages == 15 && saved.EvalMaxTokens == 1024,
        $"择机自己的三个规模参数写读一致（附图 {saved.MaxAttachedImages} / 背景 {saved.HistoryMaxMessages} / 输出 {saved.EvalMaxTokens}）");

    var groups = await repo.GetAllGroupConfigsAsync();
    var flagsNow = ReadFlagsOn(targetDb);
    Check(groups.Count == flagsNow.Count && groups.All(g => flagsNow[g.GroupId] == (g.AiProactiveEnabled ? 1 : 0)),
        "EF 读回的参与位与库内实际值逐群一致");

    var oneGroup = groups.First();
    oneGroup.AiProactiveEnabled = true;
    await repo.SaveGroupConfigAsync(oneGroup);
    var back = await repo.GetGroupConfigAsync(oneGroup.GroupId);
    Check(back!.AiProactiveEnabled, "群级参与位写读一致");
    oneGroup.AiProactiveEnabled = false;
    await repo.SaveGroupConfigAsync(oneGroup);

    // 熔断复位：留痕表与配置回到关闭态
    await repo.SaveProactiveConfigAsync(new ProactiveChatConfig { Enabled = false });

    // @ 回复的附图张数也要能穿过逐列手工映射（写进去读不回来，面板就会"保存了但没生效"）
    await repo.SaveAiConfigAsync(new AiConfig { MaxAttachedImages = 6 });
    var aiBack = await repo.GetAiConfigAsync();
    Check(aiBack is { MaxAttachedImages: 6 }, $"@ 回复附图张数写读一致（{aiBack?.MaxAttachedImages}）");
    await repo.SaveAiConfigAsync(new AiConfig { MaxAttachedImages = 0 });
    Check((await repo.GetAiConfigAsync()) is { MaxAttachedImages: 0 },
        "@ 回复附图张数填 0 也存得住（不被默认值顶掉）");
}

Check(sp.GetService<MessageEventHandler>() is not null, "MessageEventHandler 依赖图完整（含观察窗口与 seq 共享）");
Check(sp.GetService<ProactiveSpeaker>() is not null, "ProactiveSpeaker 依赖图完整");
// AddHostedService 只登记 IHostedService 映射，按具体类型解析不到，须从集合里取
var hosted = sp.GetRequiredService<IEnumerable<IHostedService>>().OfType<ProactiveChatHostedService>().Single();
Check(true, "ProactiveChatHostedService 已随宿主注册且依赖图完整");

long LogsBefore;
using (var probe = new SqliteConnection($"Data Source={targetDb}"))
{
    probe.Open();
    LogsBefore = Scalar(probe, "SELECT COUNT(*) FROM ProactiveChatLogs");
}
using (var cts = new CancellationTokenSource())
{
    await hosted.StartAsync(cts.Token);
    await Task.Delay(1200);   // 首个 tick 在 StartAsync 内同步跑完，这里再等一拍确认循环不写库
    await hosted.StopAsync(cts.Token);
}
using (var probe = new SqliteConnection($"Data Source={targetDb}"))
{
    probe.Open();
    Check(Scalar(probe, "SELECT COUNT(*) FROM ProactiveChatLogs") == LogsBefore,
        "总开关关闭时评估循环不产生任何留痕（即不调 AI、不发言）");
}

Console.WriteLine("== 5b. 来源筛选与投递诊断 ==");
using (var scope = sp.CreateScope())
{
    var mdb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var msgRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
    // 用独立群名，避免和该群真实历史混在一起计数
    var g1 = "TESTGROUP-PROACTIVE";

    await msgRepo.SaveMessagesAsync(new[]
    {
        new Mutter.Core.Models.ChatMessage { MessageId = "t-at", ChatType = 0, GroupId = g1, UserId = "u", UserName = "泷奈", PlainText = "@bot 在吗", HasAtBot = true },
        new Mutter.Core.Models.ChatMessage { MessageId = "t-obs", ChatType = 0, GroupId = g1, UserId = "u", UserName = "千束", PlainText = "今晚吃拉面" },
        new Mutter.Core.Models.ChatMessage { MessageId = "t-reply", ChatType = 0, GroupId = g1, UserId = "bot", UserName = "Bot", PlainText = "在的", IsBotMessage = true, AiModelUsed = "test" },
        new Mutter.Core.Models.ChatMessage { MessageId = "t-pro", ChatType = 0, GroupId = g1, UserId = "bot", UserName = "Bot", PlainText = "拉面配煎蛋", IsBotMessage = true, IsProactive = true, AiModelUsed = "test" },
    });

    foreach (var (source, expect) in new[] { ("at", 1L), ("observe", 1L), ("bot", 1L), ("proactive", 1L) })
    {
        var (_, count) = await msgRepo.GetPagedMessagesAsync(g1, 1, 50, source);
        Check(count == expect, $"来源筛选 {source} 命中 {count} 条（期望 {expect}）");
    }
    var (allItems, allCount) = await msgRepo.GetPagedMessagesAsync(g1, 1, 50);
    Check(allCount == 4 && allItems.Any(x => x.IsProactive), $"不带筛选时四类消息齐备（{allCount} 条）");

    // @ 事件后到时只补标记，不重复入库：面板的 at/围观 归属要能更正过来
    Check(await msgRepo.MarkAtBotAsync("t-obs") == 1, "全量先入库的普通消息可被补记为“被 @ 的是 bot”");
    Check((await msgRepo.GetPagedMessagesAsync(g1, 1, 50, "at")).Item2 == 2L
        && (await msgRepo.GetPagedMessagesAsync(g1, 1, 50, "observe")).Item2 == 0L,
        "补记后面板来源归属自动更正，且不多出重复行");
    Check(await msgRepo.MarkAtBotAsync("t-不存在") == 0, "没落库的消息补记返回 0 行而不是抛异常");

    mdb.ChatMessages.RemoveRange(mdb.ChatMessages.Where(m => m.MessageId.StartsWith("t-")));
    await mdb.SaveChangesAsync();
}

using (var probe = new SqliteConnection($"Data Source={targetDb}"))
{
    probe.Open();
    Check(Scalar(probe, "SELECT COUNT(*) FROM pragma_table_info('ChatMessages') WHERE name='IsProactive'") == 1,
        "ChatMessages.IsProactive 列已补");
}

Console.WriteLine("== 5c. 私聊上下文（两侧都要、按人隔离、断档丢弃） ==");
using (var scope = sp.CreateScope())
{
    var pdb2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var msgRepo2 = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
    var ctxBuilder = scope.ServiceProvider.GetRequiredService<IContextBuilder>();
    var pn = DateTime.UtcNow;
    var older = pn.AddDays(-9);

    Mutter.Core.Models.ChatMessage Row(string id, string user, bool isBot, string text, DateTime at, string? peer = null)
        => new()
        {
            MessageId = id, ChatType = 1, GroupId = null,
            UserId = isBot ? "BOTID" : user, UserName = isBot ? "Bot" : "阿明",
            PlainText = text, IsBotMessage = isBot, PeerUserId = peer,
            AiModelUsed = isBot ? "test" : null, CreatedAt = at
        };

    await msgRepo2.SaveMessagesAsync(new[]
    {
        Row("p-a1", "PA", false, "那个网站你打开了吗", older),
        Row("p-a2", "PA", false, "咕咕嘎嘎", pn.AddMinutes(-6)),
        Row("p-a3", "PA", true, "催啥催嘛，我这转半天打不开", pn.AddMinutes(-5), peer: "PA"),
        Row("p-a4", "PA", false, "行吧", pn.AddMinutes(-2)),
        Row("p-b1", "PB", false, "别问，问就是不告诉你", pn.AddMinutes(-4)),
        Row("p-b2", "PB", true, "小气鬼", pn.AddMinutes(-3), peer: "PB"),
        Row("p-a5", "PA", true, "（游戏系统回复，非 AI）签到成功", pn.AddMinutes(-1), peer: "PA")
    });
    await pdb2.ChatMessages.Where(m => m.MessageId == "p-a5")
        .ExecuteUpdateAsync(m => m.SetProperty(x => x.AiModelUsed, (string?)null));

    var both = await msgRepo2.GetPrivateHistoryAsync("PA", 20);
    Check(both.Any(m => m.IsBotMessage) && both.Any(m => !m.IsBotMessage),
        "私聊历史把 bot 那一侧也带回来（只取用户发言会让 AI 自己编上一轮答过什么）");
    Check(both.All(m => m.MessageId.StartsWith("p-a")),
        $"只含本段对话，另一个私聊对象的消息不串味（取到 {both.Count} 条）");

    var fresh = await msgRepo2.GetPrivateHistoryAsync("PA", 20, pn.AddHours(-2));
    Check(fresh.All(m => m.CreatedAt > pn.AddHours(-2)) && fresh.Count < both.Count,
        $"时间边界生效：断档 9 天的旧话题被丢掉（{both.Count} → {fresh.Count} 条）");

    var convo = (await ctxBuilder.BuildPrivateContextAsync("PA", "行吧", "阿明")).Messages;
    Check(convo.Count(m => m.Role == "assistant") >= 1, "拼出的私聊对话里有 assistant 轮次");
    Check(convo.Count(m => m.Role == "user" && m.Content.Contains("行吧")) == 1,
        "本条消息不会既在历史里又被追加一遍（重复追问的错觉源头）");
    Check(convo.All(m => !m.Content.Contains("签到成功")),
        "游戏系统回复仍不进上下文，免得 AI 学走系统播报的口吻");

    await pdb2.ChatMessages.Where(m => m.MessageId.StartsWith("p-")).ExecuteDeleteAsync();
}

Console.WriteLine("== 5d. 上下文条数保证与 token 输入预算 ==");
// 阶梯本身是纯函数，先验它
Check(ContextBuilder.CandidateFetchLadder(20, 40).First() == 40,
    $"面板填 20 条时首次取 40 条候选（够过滤掉的纯图片/系统回复腾地方）");
Check(ContextBuilder.CandidateFetchLadder(20, 40).SequenceEqual(new[] { 40, 80, 160, 200 }),
    "候选不够就翻倍，直到 200 上限为止");
Check(ContextBuilder.CandidateFetchLadder(100, 40).First() == 100,
    "面板填 100 条时首次取 100 条候选（不小于用户要的条数）");
Check(ContextBuilder.CandidateFetchLadder(1000, 40).Last() == 200,
    "面板填 1000 条时候选被 200 上限封住（不会无界查库）");
Check(ContextBuilder.CandidateFetchLadder(20, 40).Distinct().Count()
        == ContextBuilder.CandidateFetchLadder(20, 40).Count,
    "阶梯是有界的：封顶后不会无限产出同一个值（否则调用方每轮都会白查一次库）");
Check(ContextBuilder.InputTokenBudget(2048) == 1536 && ContextBuilder.InputTokenBudget(512) == 384,
    "输入预算由「最大 Tokens」按 0.75 折算，仍是同一个旋钮在管（不新增第二个人看不懂的数字）");
Check(ContextBuilder.InputTokenBudget(0) == 0,
    "预算换算对 0 不抛异常（服务端已把提交值钳到 ≥64，这里只是防御）");

using (var scope = sp.CreateScope())
{
    var pdb3 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var msgRepo3 = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
    var repo3 = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
    var ctx3 = scope.ServiceProvider.GetRequiredService<IContextBuilder>();
    const string g3 = "CTXG";

    var t = DateTime.UtcNow.AddMinutes(-10);
    var rows = new List<Mutter.Core.Models.ChatMessage>();
    // 真实对话 20 条，间隔着 60 条「非 AI 生成的 bot 回复」——后者会被建对话时丢掉
    for (var i = 0; i < 80; i++)
    {
        var isDistractor = i % 4 != 0;
        rows.Add(new Mutter.Core.Models.ChatMessage
        {
            MessageId = $"c-{i:D3}", ChatType = 0, GroupId = g3,
            UserId = isDistractor ? "bot" : "u1",
            UserName = isDistractor ? "Bot" : "阿明",
            PlainText = isDistractor ? "（系统播报，非 AI）签到成功" : $"第 {i} 句话说点什么",
            IsBotMessage = isDistractor,
            AiModelUsed = null,   // 非 AI 生成 -> 建对话时被丢掉，正是要制造的干扰行
            CreatedAt = t.AddSeconds(i)
        });
    }
    await msgRepo3.SaveMessagesAsync(rows);

    await repo3.SaveAiConfigAsync(new AiConfig { MaxContextMessages = 20, MaxTokens = 2048 });
    var built = await ctx3.BuildGroupContextAsync(g3, "现在这句", "阿明");

    var historyPart = built.Messages.Where(m => !m.Content.Contains("现在这句")).ToList();
    Check(historyPart.Count == 20,
        $"面板填 20 条就真给 20 条（实际 {historyPart.Count}）——此前直接按 20 条取候选，"
        + "被过滤掉的行会让实际条数缩水");
    Check(historyPart.All(m => !m.Content.Contains("系统播报")),
        "被过滤掉的非 AI 系统回复确实没有混进来");
    Check(built.Messages[^1].Content == "[阿明]: 现在这句", "当前这条消息排在对话最后");
    Check(built.EstimatedTokens <= ContextBuilder.InputTokenBudget(2048),
        $"估算 token 不超输入预算（{built.EstimatedTokens} ≤ {ContextBuilder.InputTokenBudget(2048)}）");

    // 条数够但内容很长时：预算必须真正生效，不能被"至少保留 8 条"盖过
    await pdb3.ChatMessages.Where(m => m.MessageId.StartsWith("c-")).ExecuteDeleteAsync();
    var longRows = Enumerable.Range(0, 20).Select(i => new Mutter.Core.Models.ChatMessage
    {
        MessageId = $"c-long-{i:D3}", ChatType = 0, GroupId = g3,
        UserId = "u1", UserName = "阿明",
        PlainText = new string('长', 400),   // 400 字 ≈ 200 token，20 条 ≈ 4000 token
        CreatedAt = t.AddSeconds(i)
    }).ToList();
    await msgRepo3.SaveMessagesAsync(longRows);
    var longBuilt = await ctx3.BuildGroupContextAsync(g3, "结尾一句", "阿明");
    Check(longBuilt.EstimatedTokens <= ContextBuilder.InputTokenBudget(2048),
        $"内容超长时被砍进预算（{longBuilt.EstimatedTokens} ≤ {ContextBuilder.InputTokenBudget(2048)}）");
    Check(longBuilt.Messages.Count < 20,
        $"确实发生了裁剪（{longBuilt.Messages.Count} 条 < 20 条面板设定值）");
    Check(longBuilt.Messages[^1].Content == "[阿明]: 结尾一句",
        "裁剪从最旧的一条开始，本轮消息始终保留");

    await pdb3.ChatMessages.Where(m => m.MessageId.StartsWith("c-")).ExecuteDeleteAsync();
}

Console.WriteLine("== 6. 面板接口契约（含 EF 查询翻译） ==");
// 与 ASP.NET Core 默认一致的 camelCase + 不转义中文
var wire = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
using (var scope = sp.CreateScope())
{
    var pdb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
    var gateway = new NoopGateway();
    var memoryRepo = sp.GetRequiredService<IGroupMemoryRepository>();
    var controller = new ProactiveController(pdb, repo, sp.GetRequiredService<IActiveMessageService>(), observe, gateway, memoryRepo);

    // Ok(...) 走 Result 而非 Value，两个口都得兜一下
    static object Payload<T>(ActionResult<T> r) => r.Value ?? ((OkObjectResult)r.Result!).Value!;
    // 群记忆三个口返回的是裸 IActionResult，没有泛型 Result 可兜
    static object OkPayload(IActionResult r) => ((OkObjectResult)r).Value!;

    await pdb.Database.ExecuteSqlRawAsync("DELETE FROM ProactiveChatLogs");

    var put = await controller.UpdateConfig(new ProactiveController.ProactiveConfigDto
    {
        Enabled = true, WindowSeconds = 1, ActivePerGroupPerMinute = 888, QuietStartHour = 99
    }, default);
    var putJson = JsonSerializer.Serialize(Payload(put), wire);
    Check(putJson.Contains("\"windowSeconds\":15") && putJson.Contains("\"activePerGroupPerMinute\":20"),
        "面板提交的越界值被服务端钳制后才落库");
    Check(putJson.Contains("\"quietStartHour\":0"), "非法小时值退回 0 而不是写坏数据");
    Check(!putJson.Contains("onfidence"), "面板契约里已经没有置信度这个旋钮");
    var got = await controller.GetConfig(default);
    var gotJson = JsonSerializer.Serialize(Payload(got), wire);
    Check(gotJson.Contains("\"enabled\":true") && gotJson.Contains("\"ceilingGroupPerDay\":1000"),
        "GET config 回读生效值并附带官方上限供表单约束");
    Check(gotJson.Contains("\"maxAttachedImages\":3") && gotJson.Contains("\"historyMaxMessages\":8")
        && gotJson.Contains("\"evalMaxTokens\":512"),
        "附图/背景/输出三个新旋钮出现在面板契约里，且是择机自己的默认值");
    Check(gotJson.Contains("\"ceilingAttachedImages\":10"),
        "附图张数上限随契约下发，面板据此限制输入框");
    Check(!gotJson.Contains("\"maxContextMessages\"") && !gotJson.Contains("\"maxTokens\""),
        "择机配置契约里不再出现 AI 面板那两个字段（解耦后两者各管各的）");

    // 面板改成 0 张图必须能存住，否则「不看图」这个选择会被默认值悄悄顶掉
    await controller.UpdateConfig(new ProactiveController.ProactiveConfigDto
    {
        Enabled = true, MaxAttachedImages = 0, HistoryMaxMessages = 0
    }, default);
    var zero = JsonSerializer.Serialize(Payload(await controller.GetConfig(default)), wire);
    Check(zero.Contains("\"maxAttachedImages\":0") && zero.Contains("\"historyMaxMessages\":0"),
        "附图 0 与背景 0 都存得住（0 是合法选择，不能被当成未填而回退默认值）");

    // 群记忆面板契约：空壳、trim 落库、版本自增、截断、跨群隔离、清空
    await pdb.Database.ExecuteSqlRawAsync("DELETE FROM GroupMemories");
    const string memA = "TESTGROUP-MEMORY-A";
    const string memB = "TESTGROUP-MEMORY-B";

    var memMissing = JsonSerializer.Serialize(OkPayload(await controller.GetGroupMemory(memA, default)), wire);
    Check(memMissing.Contains("\"summary\":\"\"") && memMissing.Contains("\"version\":0"),
        "GET 没存过摘要的群回空壳而不是 404（面板打开编辑框不会整个失败）");

    var memSaved = JsonSerializer.Serialize(OkPayload(await controller.PutGroupMemory(memA,
        new ProactiveController.GroupMemoryDto { Summary = "  本群背景  " }, default)), wire);
    Check(memSaved.Contains("\"summary\":\"本群背景\"") && memSaved.Contains("\"version\":1"),
        "PUT 回读 trim 后的摘要，首次版本号为 1");
    Check((await memoryRepo.GetAsync(memA))?.Summary == "本群背景", "摘要确实落库，不是只在响应里回显");

    var memIsolated = JsonSerializer.Serialize(OkPayload(await controller.GetGroupMemory(memB, default)), wire);
    Check(memIsolated.Contains("\"summary\":\"\""), "换一个群 ID 读不到别群的摘要（接口层不跨群泄露）");

    var memLong = JsonSerializer.Serialize(OkPayload(await controller.PutGroupMemory(memA,
        new ProactiveController.GroupMemoryDto { Summary = new string('长', 5000) }, default)), wire);
    Check(memLong.Contains("\"version\":2") && (await memoryRepo.GetAsync(memA))?.Summary.Length == 4000,
        "覆盖保存版本号自增，超长摘要按 4000 字截断后真的写进库");

    var memBlank = JsonSerializer.Serialize(OkPayload(await controller.PutGroupMemory(memA,
        new ProactiveController.GroupMemoryDto { Summary = "   " }, default)), wire);
    Check(memBlank.Contains("\"summary\":\"\"") && memBlank.Contains("\"version\":0")
          && await memoryRepo.GetAsync(memA) is null,
        "PUT 空白摘要等于清空，不留一行像模像样的空记忆");

    await controller.PutGroupMemory(memB, new ProactiveController.GroupMemoryDto { Summary = "别群背景" }, default);
    var memCleared = JsonSerializer.Serialize(OkPayload(await controller.DeleteGroupMemory(memB, default)), wire);
    Check(memCleared.Contains("群记忆已清空") && await memoryRepo.GetAsync(memB) is null, "DELETE 清空指定群的摘要");
    Check(await controller.GetGroupMemory("   ", default) is BadRequestObjectResult,
        "空白群 ID 返回 400，而不是写出一行无主摘要");
    Check(await controller.PutGroupMemory(memB, new ProactiveController.GroupMemoryDto { Summary = null }, default)
          is OkObjectResult,
        "PUT 缺 summary 字段走清空路径而不是 500");

    pdb.ProactiveChatLogs.AddRange(
        new ProactiveChatLogEntity { GroupId = "G1", EvaluatedAt = DateTime.UtcNow, Outcome = ProactiveOutcomes.SentPassive, MessageCount = 4, ImageCount = 2, Line = "今晚吃拉面" },
        new ProactiveChatLogEntity { GroupId = "G1", EvaluatedAt = DateTime.UtcNow, Outcome = ProactiveOutcomes.SkippedAiDeclined, MessageCount = 3 });
    await pdb.SaveChangesAsync();

    var all = await controller.GetLogs(null, null, 1, 10, default);
    var sent = await controller.GetLogs(null, ProactiveOutcomes.SentPassive, 1, 10, default);
    var allJson = JsonSerializer.Serialize(Payload(all), wire);
    var sentJson = JsonSerializer.Serialize(Payload(sent), wire);
    Check(allJson.Contains("\"totalCount\":2") && sentJson.Contains("\"totalCount\":1"),
        "留痕分页与按结论过滤（EF 查询可翻译）");
    Check(sentJson.Contains("今晚吃拉面"), "已插话记录带出原话");
    Check(allJson.Contains("\"imageCount\":2") && allJson.Contains("\"imageCount\":0"),
        "留痕把这次到底看了几张图带给面板");

    var routeGroup = (await repo.GetAllGroupConfigsAsync()).First();
    routeGroup.IsEnabled = true;
    routeGroup.TriggerMode = 0;                       // 只回被 @ 的：更能验证 @ 判定
    routeGroup.BotMentionOpenId = "11112222333344445555666677778888";
    await repo.SaveGroupConfigAsync(routeGroup);
    var router = sp.GetRequiredService<Mutter.Core.Services.MessageRouter>();
    var hitRoute = await router.RouteGroupAsync(routeGroup.GroupId, false,
        "<@11112222333344445555666677778888> 中午吃什么");
    Check(hitRoute.ShouldProcess && hitRoute.BotMentionCode == routeGroup.BotMentionOpenId && !hitRoute.ShouldObserve,
        "只回 @ 的群里，全量事件靠识别码判定为 @：走回复、不进观察窗口");
    var missRoute = await router.RouteGroupAsync(routeGroup.GroupId, false, "<@99998888777766665555444433332222> 庸医");
    Check(!missRoute.ShouldProcess && missRoute.BotMentionCode is null,
        "@ 群友的消息不会被错认成 @ 机器人");
    routeGroup.BotMentionOpenId = null;
    await repo.SaveGroupConfigAsync(routeGroup);
    var noneRoute = await router.RouteGroupAsync(routeGroup.GroupId, false,
        "<@11112222333344445555666677778888> 中午吃什么");
    Check(!noneRoute.ShouldProcess, "没配识别码时保持现状，不瞎猜");

    var configs = await repo.GetAllGroupConfigsAsync();
    var oneGroup = configs.First(g => !g.AiProactiveEnabled);
    oneGroup.AiProactiveEnabled = true;
    await repo.SaveGroupConfigAsync(oneGroup);

    var st = await controller.GetStatus(default);
    var stJson = JsonSerializer.Serialize(Payload(st), wire);
    var expectedParticipating = (await repo.GetAllGroupConfigsAsync()).Count(g => g.AiProactiveEnabled && g.IsEnabled);
    Check(stJson.Contains($"\"participatingGroups\":{expectedParticipating}"),
        $"状态里的参与群数与群配置一致（{expectedParticipating}）");
    Check(stJson.Contains("\"todaySent\":1") && stJson.Contains("\"todayEvaluations\":2"),
        "今日按结论统计取的是北京日界内的留痕");
    Check(stJson.Contains("\"quotas\":[") && stJson.Contains("\"minuteQuota\":") && stJson.Contains("\"usedToday\":"),
        "每群主动消息余量随状态返回（数值取自当前配置，不断死具体数字）");
    Check(stJson.Contains("\"pendingWindows\":[]"), "窗口取空后状态不再报待评估");
    Check(stJson.Contains("只收到 @ 事件") && stJson.Contains("GROUP_MESSAGE_CREATE"),
        "平台没下发全量事件时，状态直接给出排查方向而不是让人猜");

    var eager = await controller.UpdateConfig(new ProactiveController.ProactiveConfigDto
    {
        Enabled = true, Liveliness = 2, QuietStartHour = 1, QuietEndHour = 7
    }, default);
    var eagerJson = JsonSerializer.Serialize(Payload(eager), wire);
    Check(eagerJson.Contains("\"liveliness\":2"), "发言倾向能从面板存住并回读生效值");

    oneGroup.AiProactiveEnabled = false;
    await repo.SaveGroupConfigAsync(oneGroup);
    await repo.SaveProactiveConfigAsync(new ProactiveChatConfig { Enabled = false });
    pdb.ProactiveChatLogs.RemoveRange(await pdb.ProactiveChatLogs.ToListAsync());
    await pdb.SaveChangesAsync();
}

foreach (var suffix in new[] { "", "-wal", "-shm" })
{
    var f = Path.Combine(AppContext.BaseDirectory, "mutter-bot.db" + suffix);
    if (File.Exists(f)) File.Delete(f);
}

Console.WriteLine(ok ? "\n全部通过" : "\n存在失败项");
return ok ? 0 : 1;

static Dictionary<string, long> ReadFlags(SqliteConnection conn)
{
    var map = new Dictionary<string, long>();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT GroupId, AiProactiveEnabled FROM GroupConfigs";
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) map[reader.GetString(0)] = reader.GetInt64(1);
    return map;
}

static Dictionary<string, long> ReadFlagsOn(string db)
{
    using var conn = new SqliteConnection($"Data Source={db}");
    conn.Open();
    return ReadFlags(conn);
}

// 「数字群号时代」的键：早期用 QQ 群号做键，迁移后是 TEXT。
// 只按形状（非空且全为数字）识别，不引用任何具体群号，
// 这样既覆盖了历史键，也不会把线上群号带进仓库。
static List<string> ReadNumericGroupIds(SqliteConnection conn)
{
    var ids = new List<string>();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT GroupId FROM GroupConfigs WHERE GroupId <> '' AND GroupId NOT GLOB '*[^0-9]*'";
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) ids.Add(reader.GetString(0));
    return ids;
}

static string? ScalarText(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return cmd.ExecuteScalar() as string;
}

static long Scalar(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(cmd.ExecuteScalar());
}

// 只收到 @ 事件、没有全量事件：诊断提示必须点明是平台侧没下发，而不是我们没落库
file sealed class NoopGateway : IQqGatewayConnection
{
    private readonly Dictionary<string, long> _counts = new() { ["GROUP_AT_MESSAGE_CREATE"] = 7 };
    public IReadOnlyDictionary<string, long> EventCounts => _counts;
    public bool IsConnected => true;
    public event EventHandler<bool>? ConnectionStateChanged { add { } remove { } }
    public event EventHandler<Mutter.Core.Models.QQOfficial.QqDispatch>? DispatchReceived { add { } remove { } }
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class NoopNotification : INotificationService
{
    public Task NotifyNewMessageAsync(object messageDto) => Task.CompletedTask;
    public Task NotifyConnectionStatusAsync(bool isConnected, string info) => Task.CompletedTask;
    public Task NotifyAiResponseStartedAsync(string? groupId, string senderName) => Task.CompletedTask;
    public Task NotifyAiResponseCompletedAsync(object responseDto) => Task.CompletedTask;
}

file sealed class ScriptedAiChatService : IAiChatService
{
    private readonly Queue<object> _responses;

    public ScriptedAiChatService(params object[] responses) => _responses = new Queue<object>(responses);

    public Task<AiChatResponse> ChatAsync(string systemPrompt,
        List<(string Role, string Content)> conversationHistory, AiConfig config,
        CancellationToken ct = default) => Next();

    public Task<AiChatResponse> ChatAsync(string systemPrompt,
        List<(string Role, string Content, IReadOnlyList<string>? ImageUrls)> conversationHistory,
        AiConfig config, CancellationToken ct = default) => Next();

    public Task<AiChatResponse> ChatWithImagesAsync(string systemPrompt, string userPrompt,
        IReadOnlyList<string> imageUrls, AiConfig config, string? responseFormat = null,
        CancellationToken ct = default) => Next();

    public Task<IReadOnlyList<string>> AttachableImagesAsync(IReadOnlyList<string> imageUrls,
        CancellationToken ct = default) => Task.FromResult(imageUrls);

    public Task<AiChatResponse> ChatAsync(string systemPrompt,
        List<(string Role, string Content)> conversationHistory, AiConfig config,
        string? responseFormat, CancellationToken ct = default) => Next();

    private Task<AiChatResponse> Next()
    {
        var response = _responses.Dequeue();
        return response is Exception exception
            ? Task.FromException<AiChatResponse>(exception)
            : Task.FromResult((AiChatResponse)response);
    }
}
