using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Models;
using Mutter.Core.Proactive;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Web.Controllers;
using Mutter.Web.Models;
using Mutter.Web.Services;

internal static partial class RuntimeChecks
{
    private static async Task ChatTimelineAsync()
    {
        await using var env = await TestEnvironment.CreateAsync(legacy: true);
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var time = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 72; i++) db.ChatMessages.Add(new ChatMessage
        {
            MessageId = $"history-{i}", GroupId = "GROUP", UserId = "sender-a", UserName = "甲同学",
            PlainText = i == 0 ? "只在旧页出现的历史关键字" : i == 1 ? "100%_OK [x]" : $"讨论 {i}", CreatedAt = time.AddSeconds(i)
        });
        var oldLog = new ProactiveChatLogEntity { GroupId = "GROUP", EvaluatedAt = time.AddMinutes(2),
            Outcome = ProactiveOutcomes.SkippedAiDeclined, Reason = "历史理由仍然可查" };
        db.ProactiveChatLogs.Add(oldLog);
        await db.SaveChangesAsync();
        var oldId = oldLog.Id;
        // 真实旧表缺列，反复升级只补结构，保留原聊天与留痕。
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ProactiveChatLogs\" DROP COLUMN \"DetailVersion\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ProactiveChatLogs\" DROP COLUMN \"WindowSnapshotJson\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ProactiveChatLogs\" DROP COLUMN \"AiEvaluated\"");
        // 模拟升级前尚无时间线索引的已有库，验证运行时补索引而不依赖 EnsureCreated。
        var timelineIndexes = db.Model.GetEntityTypes().SelectMany(e => e.GetIndexes())
            .Select(i => i.GetDatabaseName()!).Where(name => name.Contains("_Timeline", StringComparison.Ordinal)).ToArray();
        Require(timelineIndexes.Length == 11, "the model declares global/per-group timeline indexes and the exact-link covering index");
        await db.Database.OpenConnectionAsync();
        foreach (var name in timelineIndexes)
        {
            // 标识符来自已声明的 EF 模型，按 SQLite 标识符规则转义后执行 DDL。
            await using var drop = db.Database.GetDbConnection().CreateCommand();
            drop.CommandText = "DROP INDEX IF EXISTS \"" + name.Replace("\"", "\"\"") + "\"";
            await drop.ExecuteNonQueryAsync();
        }
        await db.EnsureSchemaUpgradedAsync();
        await db.EnsureSchemaUpgradedAsync();
        var upgradedIndexes = await db.Database.SqlQueryRaw<string>("""
            SELECT "name" AS "Value" FROM "sqlite_master" WHERE "type" = 'index' AND "name" LIKE '%_Timeline%'
            """).ToListAsync();
        Require(timelineIndexes.Order().SequenceEqual(upgradedIndexes.Order()),
            "old databases gain the same indexes as fresh databases and repeated upgrades remain idempotent");
        foreach (var index in db.Model.GetEntityTypes().SelectMany(e => e.GetIndexes())
            .Where(i => timelineIndexes.Contains(i.GetDatabaseName())))
        {
            var columns = await db.Database.SqlQuery<string>($"""
                SELECT "name" AS "Value" FROM pragma_index_info({index.GetDatabaseName()}) ORDER BY "seqno"
                """).ToListAsync();
            Require(columns.SequenceEqual(index.Properties.Select(p => p.Name)),
                $"upgraded index '{index.GetDatabaseName()}' has the same covering columns as a fresh database");
        }
        db.ChangeTracker.Clear();
        Require((await db.ProactiveChatLogs.SingleAsync(l => l.Id == oldId)).AiEvaluated is null,
            "schema upgrade leaves old evaluation phases unknown rather than writing false");
        var reader = new ChatTimelineReader(db);
        var first = await reader.ReadAsync("GROUP", null, null, 1, 50, null);
        Require(first.Items.Count == 50 && first.TotalCount == 73 && first.HasMore, "messages and silent decisions form one paged timeline");
        var latest = await reader.ReadLatestAsync("GROUP", null, 50);
        Require(latest.TotalCount is null && latest.Items.Count == 50 && latest.HasMore,
            "the lightweight first page skips the exact total but still reports that older records exist");
        Require(latest.Items.Select(i => $"{i.Kind}:{i.Id}").SequenceEqual(first.Items.Select(i => $"{i.Kind}:{i.Id}")),
            "the lightweight first page keeps the exact page's order and decision linking");
        var clamped = await reader.ReadLatestAsync("GROUP", null, 0);
        Require(clamped.Items.Count == 1 && clamped.HasMore, "the reader clamps its own page size instead of returning an empty lightweight page with hasMore");
        var frozenCount = await reader.CountAsync("GROUP", null, null, latest.Snapshot);
        Require(frozenCount.TotalCount == 73 && frozenCount.Snapshot == latest.Snapshot,
            "the deferred count uses the first page's frozen snapshot");
        var second = await reader.ReadAsync("GROUP", null, null, 2, 50, first.Snapshot);
        Require(second.Items.Count == 23 && !first.Items.Select(i => $"{i.Kind}:{i.Id}").Intersect(second.Items.Select(i => $"{i.Kind}:{i.Id}")).Any(), "pages do not overlap");
        var newArrival = new ChatMessage { MessageId = "new-after-snapshot", GroupId = "GROUP", UserId = "x", UserName = "新消息", PlainText = "新增", CreatedAt = time.AddDays(1) };
        db.ChatMessages.Add(newArrival);
        await db.SaveChangesAsync();
        var frozen = await reader.ReadAsync("GROUP", null, null, 2, 50, first.Snapshot);
        Require(frozen.TotalCount == 73 && frozen.Items.Select(i => i.Id).SequenceEqual(second.Items.Select(i => i.Id)), "new arrivals do not shift older pages");
        var frozenLatest = await reader.ReadLatestAsync("GROUP", null, 50);
        Require(frozenLatest.Items[0].Id == newArrival.Id && frozenLatest.TotalCount is null,
            "the lightweight page observes new arrivals without pretending to know their count");
        Require((await reader.CountAsync("GROUP", null, null, first.Snapshot)).TotalCount == 73
            && (await reader.CountAsync("GROUP", null, null, frozenLatest.Snapshot)).TotalCount == 74,
            "deferred counts follow both an old snapshot and the newly opened range");
        Require((await reader.ReadAsync("GROUP", null, "历史关键字", 1, 50, null)).TotalCount == 1, "search reaches records beyond the first page");
        Require((await reader.ReadAsync("GROUP", "observe", "甲同学", 1, 50, null)).TotalCount == 72, "sender search composes with group and source filters");
        Require((await reader.ReadAsync("GROUP", null, "%_", 1, 50, null)).TotalCount == 1, "SQL wildcard characters are searched literally");
        Require((await reader.ReadAsync("GROUP", null, "ok [X]", 1, 50, null)).TotalCount == 1, "ASCII case folding and literal punctuation");
        Require((await reader.ReadAsync("GROUP", "decision", "历史理由", 1, 50, null)).TotalCount == 1, "decision reasons are searchable");
        var legacy = await reader.DecisionAsync(oldId);
        Require(legacy is { Window: null, MemoryCount: null, SearchUsed: null, PromptTokens: null } && legacy.Reason == "历史理由仍然可查", "old diagnostic defaults remain unknown rather than invented zeroes");

        // 直接构造一条带快照的留痕，验证读取路径；写入时的群级隐私策略由
        // RuntimeChecks.SnapshotPolicyAsync 覆盖，不能把这里的 mode 0 群当成允许落快照。
        var window = new ProactiveWindow { GroupId = "GROUP", LastMessageId = "history-1", LastMessageUtcAt = time,
            Messages = [new ObservedMessage("甲同学", "原始窗口", "history-1", time, ["https://example.invalid/temporary-image"])] };
        db.OutgoingReplies.Add(new OutgoingReply { Id = "synthetic-outgoing", GroupId = "GROUP", IsProactive = true,
            State = DeliveryStates.Sent, PlatformMessageId = "platform-sent", Content = "实际回复", CreatedAt = time });
        var bot = new ChatMessage { MessageId = "synthetic-outgoing", GroupId = "GROUP", UserId = "BOT", UserName = "Bot",
            IsBotMessage = true, IsProactive = true, PlainText = "实际回复", CreatedAt = time.AddMinutes(4) };
        db.ChatMessages.Add(bot);
        var decision = new ProactiveChatLogEntity { GroupId = "GROUP", SentMessageId = "platform-sent", DetailVersion = 2, AiEvaluated = true,
            Outcome = ProactiveOutcomes.SentPassive, Line = "实际回复", Model = "synthetic-model", EvaluatedAt = time.AddMinutes(4),
            WindowSnapshotJson = ProactiveLogSnapshot.Capture(window), MessageCount = 1 };
        db.ProactiveChatLogs.Add(decision);
        // 隐私门槛上线前写的 DetailVersion=1 快照不再从详情接口返回。
        var oldSnapshotLog = new ProactiveChatLogEntity { GroupId = "GROUP", DetailVersion = 1,
            Outcome = ProactiveOutcomes.SentPassive, Line = "旧版窗口", EvaluatedAt = time.AddMinutes(3).AddSeconds(30),
            WindowSnapshotJson = ProactiveLogSnapshot.Capture(window), MessageCount = 1 };
        db.ProactiveChatLogs.Add(oldSnapshotLog);
        // 模型调用失败时 Model 还没回写，但这次确实带了 2 张图、1 条记忆；
        // 不能因为 Model 为空就把已知值显示成“未记录”。
        var failedCall = new ProactiveChatLogEntity { GroupId = "GROUP", EvaluatedAt = time.AddMinutes(5),
            DetailVersion = 2, AiEvaluated = true, Outcome = ProactiveOutcomes.FailedAiCall, Model = null,
            ImageCount = 2, MemoryCount = 1, MemoryPromptChars = 30, SearchUsed = false, MessageCount = 2 };
        db.ProactiveChatLogs.Add(failedCall);
        var oldFallback = new ProactiveChatLogEntity { GroupId = "GROUP", EvaluatedAt = time.AddMinutes(5),
            DetailVersion = 1, Outcome = ProactiveOutcomes.FailedAiCall, Model = null };
        var oldPositive = new ProactiveChatLogEntity { GroupId = "GROUP", EvaluatedAt = time.AddMinutes(5),
            DetailVersion = 1, Outcome = ProactiveOutcomes.FailedAiCall, MemoryCount = 1, ImageCount = 2, SearchUsed = true };
        var postModelGate = new ProactiveChatLogEntity { GroupId = "GROUP", EvaluatedAt = time.AddMinutes(5),
            DetailVersion = 2, AiEvaluated = true, Outcome = ProactiveOutcomes.SkippedQuietHours };
        var beforeModelGate = new ProactiveChatLogEntity { GroupId = "GROUP", EvaluatedAt = time.AddMinutes(5),
            DetailVersion = 2, AiEvaluated = false, Outcome = ProactiveOutcomes.SkippedQuietHours };
        db.ProactiveChatLogs.AddRange(oldFallback, oldPositive, postModelGate, beforeModelGate);
        db.ProactiveChatLogs.Add(new ProactiveChatLogEntity { GroupId = "OTHER", SentMessageId = "platform-sent",
            Outcome = ProactiveOutcomes.SentPassive, EvaluatedAt = time.AddMinutes(4), Line = "另一个群" });
        var directBot = new ChatMessage { MessageId = "platform-legacy", GroupId = "GROUP", UserId = "BOT", UserName = "历史Bot",
            IsBotMessage = true, IsProactive = true, PlainText = "旧版主动回复", CreatedAt = time.AddMinutes(3) };
        var directLog = new ProactiveChatLogEntity { GroupId = "GROUP", SentMessageId = "platform-legacy",
            Outcome = ProactiveOutcomes.SentActive, EvaluatedAt = time.AddMinutes(3) };
        var unlinkedBot = new ChatMessage { MessageId = "not-logged", GroupId = "GROUP", UserId = "BOT", UserName = "历史Bot",
            IsBotMessage = true, IsProactive = true, PlainText = "实际回复", CreatedAt = time.AddMinutes(4) };
        db.ChatMessages.AddRange(directBot, unlinkedBot); db.ProactiveChatLogs.Add(directLog);
        await db.SaveChangesAsync();
        var related = await reader.ReadAsync("GROUP", null, null, 1, 100, null);
        Require(related.Items.Single(i => i.Kind == "message" && i.Id == bot.Id).DecisionId == decision.Id, "synthetic chat IDs resolve through the platform receipt, within the same group");
        Require(related.Items.Single(i => i.Kind == "message" && i.Id == directBot.Id).DecisionId == directLog.Id,
            "old platform message IDs also resolve their recorded decision");
        Require(related.Items.Single(i => i.Kind == "message" && i.Id == unlinkedBot.Id).DecisionId is null,
            "identical text and timestamps never substitute for an exact message reference");
        Require(!related.Items.Any(i => i.Kind == "decision" && i.Id == decision.Id), "a sent decision is attached to its message without a duplicate timeline event");
        Require((await reader.ReadAsync("GROUP", "proactive", "Mutter", 1, 50, null)).TotalCount == 1,
            "the displayed Mutter sender name is searchable even when legacy storage calls it Bot");
        var detail = await reader.DecisionAsync(decision.Id);
        Require(detail is { MemoryCount: 0, SearchUsed: false } && detail.Window?.Messages.Single().Text == "原始窗口",
            "new diagnostics distinguish recorded zeroes and preserve the exact input window");
        var failedDetail = await reader.DecisionAsync(failedCall.Id);
        Require(failedDetail is { ImageCount: 2, MemoryCount: 1, MemoryPromptChars: 30, SearchUsed: false, PromptTokens: null },
            "a failed call keeps recorded diagnostics while unknown token usage stays unknown");
        Require(await reader.DecisionAsync(oldFallback.Id) is { ImageCount: null, MemoryCount: null, SearchUsed: null, PromptTokens: null, SpeechPromptTokens: null },
            "version 1 fallback zeroes must remain unknown after upgrade");
        Require(await reader.DecisionAsync(oldPositive.Id) is { ImageCount: 2, MemoryCount: 1, SearchUsed: true },
            "known positive diagnostics from version 1 remain visible");
        Require(await reader.DecisionAsync(postModelGate.Id) is { PromptTokens: null, CompletionTokens: null },
            "a post-model gate cannot turn missing provider usage into known zeroes");
        Require(await reader.DecisionAsync(beforeModelGate.Id) is { PromptTokens: 0, CompletionTokens: 0 },
            "an explicit pre-model phase establishes zero model usage");
        Require(await reader.DecisionAsync(oldSnapshotLog.Id) is { Window: null },
            "windows written before the privacy gate are not returned from the detail endpoint");
        Require(decision.WindowSnapshotJson is not null && !decision.WindowSnapshotJson.Contains("temporary-image"),
            "temporary image addresses are not persisted in window snapshots");
        Require(await reader.DecisionAsync(long.MaxValue) is null, "missing old logs degrade without affecting messages");

        // 轻量首屏不接受查找参数：直接构造控制器验证契约（Bad Result 不依赖 HttpContext）。
        var controller = new ChatTimelineController(reader);
        Require(await controller.Latest("GROUP", null, 50, "关键字") is { Result: BadRequestObjectResult },
            "the lightweight endpoint rejects search explicitly instead of silently ignoring it");

        // 取键与取行之间 DataRetentionService 可能已清理记录，键列表会引用不存在的行。
        // 私有方法不改可见性，用反射构造含已删除行的键列表，回归“跳过而不是 KeyNotFound”。
        var goneMessage = new ChatMessage { MessageId = "deleted-before-materialize", GroupId = "GROUP",
            UserId = "x", UserName = "用户", PlainText = "已被清理", CreatedAt = time.AddMinutes(6) };
        var goneLog = new ProactiveChatLogEntity { GroupId = "GROUP", EvaluatedAt = time.AddMinutes(6),
            Outcome = ProactiveOutcomes.SkippedAiDeclined };
        db.ChatMessages.Add(goneMessage);
        db.ProactiveChatLogs.Add(goneLog);
        await db.SaveChangesAsync();
        var goneMessageId = goneMessage.Id;
        var goneLogId = goneLog.Id;
        db.ChatMessages.Remove(goneMessage);
        db.ProactiveChatLogs.Remove(goneLog);
        await db.SaveChangesAsync();
        var memberFlags = BindingFlags.NonPublic | BindingFlags.Instance;
        var keyType = typeof(ChatTimelineReader).GetNestedType("TimelineKey", memberFlags);
        var materialize = typeof(ChatTimelineReader).GetMethod("MaterializeAsync", memberFlags);
        Require(keyType is not null && materialize is not null, "the private materialize step remains reachable for the deleted-row regression");
        object MakeKey(int kind, long id)
        {
            var key = Activator.CreateInstance(keyType!)!;
            keyType!.GetProperty("Kind")!.SetValue(key, kind);
            keyType.GetProperty("Id")!.SetValue(key, id);
            keyType.GetProperty("Time")!.SetValue(key, time);
            return key;
        }
        var keyList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(keyType!))!;
        keyList.Add(MakeKey(0, bot.Id)); keyList.Add(MakeKey(0, goneMessageId));
        keyList.Add(MakeKey(1, decision.Id)); keyList.Add(MakeKey(1, goneLogId));
        var materialized = await (Task<List<ChatTimelineItemDto>>)materialize!.Invoke(reader, [keyList, decision.Id, CancellationToken.None])!;
        Require(materialized.Select(i => $"{i.Kind}:{i.Id}").SequenceEqual([$"message:{bot.Id}", $"decision:{decision.Id}"]),
            "materializing a page skips rows deleted between the key and row queries instead of failing with KeyNotFound");

        Require(!ChatTimelineReader.TrySnapshot("1:-1", out _, out _) && !ChatTimelineReader.TrySnapshot("bad", out _, out _), "invalid pagination snapshots rejected");
        Require(await db.ChatMessages.AnyAsync(m => m.MessageId == "legacy-row" && m.PlainText == "合成旧数据"), "existing database content remains unchanged");

        // ID 顺序故意不同于时间顺序，并在一微秒内混排两种记录；覆盖候选时间截断的同刻边界。
        var boundaryMessages = new List<ChatMessage>();
        var boundaryLogs = new List<ProactiveChatLogEntity>();
        for (var i = 0; i < 65; i++) boundaryMessages.Add(new ChatMessage
        {
            MessageId = $"boundary-{i}", GroupId = "BOUNDARY", CreatedAt = time.AddTicks(i % 7),
            IsBotMessage = i % 3 == 0, IsProactive = i % 6 == 0, HasAtBot = i % 3 == 1
        });
        for (var i = 0; i < 70; i++) boundaryLogs.Add(new ProactiveChatLogEntity
        {
            GroupId = "BOUNDARY", EvaluatedAt = time.AddTicks(i % 7),
            SentMessageId = i % 6 == 0 ? $"boundary-{i % 60}" : null
        });
        db.ChatMessages.AddRange(boundaryMessages);
        db.ProactiveChatLogs.AddRange(boundaryLogs);
        await db.SaveChangesAsync();
        foreach (var size in new[] { 1, 50, 100 })
        foreach (var filter in ChatTimelineReader.Sources)
        {
            var bounded = await reader.ReadLatestAsync("BOUNDARY", filter, size);
            var reference = await reader.ReadAsync("BOUNDARY", filter, null, 1, size, bounded.Snapshot);
            var expectedMessages = boundaryMessages.Where(m => filter switch
            {
                "observe" => !m.IsBotMessage && !m.HasAtBot,
                "at" => !m.IsBotMessage && m.HasAtBot,
                "bot" => m.IsBotMessage && !m.IsProactive,
                "proactive" => m.IsBotMessage && m.IsProactive,
                "decision" => false,
                _ => true
            }).Select(m => (Kind: 0, m.Id, Time: m.CreatedAt));
            var expectedLogs = boundaryLogs.Where(l => filter == "decision" || filter == ""
                && !boundaryMessages.Any(m => m.IsBotMessage && m.IsProactive && m.MessageId == l.SentMessageId))
                .Select(l => (Kind: 1, l.Id, Time: l.EvaluatedAt));
            var expected = expectedMessages.Concat(expectedLogs).OrderByDescending(k => k.Time)
                .ThenByDescending(k => k.Kind).ThenByDescending(k => k.Id).ToArray();
            Require(bounded.Items.Select(i => $"{i.Kind}:{i.Id}").SequenceEqual(expected.Take(size)
                .Select(k => $"{(k.Kind == 0 ? "message" : "decision")}:{k.Id}"))
                && reference.TotalCount == expected.Length && bounded.HasMore == (expected.Length > size),
                $"source '{filter}' size {size} agrees with the independently sorted fixture");
            Require(bounded.HasMore == reference.HasMore && bounded.Items.Select(i => $"{i.Kind}:{i.Id}:{i.DecisionId}")
                .SequenceEqual(reference.Items.Select(i => $"{i.Kind}:{i.Id}:{i.DecisionId}")),
                $"bounded source '{filter}' size {size} preserves tick/kind/ID ties, links, and hasMore");
        }
        var empty = await reader.ReadLatestAsync("NO-RECORDS", null, 50);
        Require(empty.Items.Count == 0 && !empty.HasMore && empty.TotalCount is null,
            "a group with no saved records still returns a valid empty first page");
    }
}
