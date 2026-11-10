using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Models;
using Mutter.Core.Proactive;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
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
        await db.EnsureSchemaUpgradedAsync();
        await db.EnsureSchemaUpgradedAsync();
        db.ChangeTracker.Clear();
        var reader = new ChatTimelineReader(db);
        var first = await reader.ReadAsync("GROUP", null, null, 1, 50, null);
        Require(first.Items.Count == 50 && first.TotalCount == 73, "messages and silent decisions form one paged timeline");
        var second = await reader.ReadAsync("GROUP", null, null, 2, 50, first.Snapshot);
        Require(second.Items.Count == 23 && !first.Items.Select(i => $"{i.Kind}:{i.Id}").Intersect(second.Items.Select(i => $"{i.Kind}:{i.Id}")).Any(), "pages do not overlap");
        db.ChatMessages.Add(new ChatMessage { MessageId = "new-after-snapshot", GroupId = "GROUP", UserId = "x", UserName = "新消息", PlainText = "新增", CreatedAt = time.AddDays(1) });
        await db.SaveChangesAsync();
        var frozen = await reader.ReadAsync("GROUP", null, null, 2, 50, first.Snapshot);
        Require(frozen.TotalCount == 73 && frozen.Items.Select(i => i.Id).SequenceEqual(second.Items.Select(i => i.Id)), "new arrivals do not shift older pages");
        Require((await reader.ReadAsync("GROUP", null, "历史关键字", 1, 50, null)).TotalCount == 1, "search reaches records beyond the first page");
        Require((await reader.ReadAsync("GROUP", "observe", "甲同学", 1, 50, null)).TotalCount == 72, "sender search composes with group and source filters");
        Require((await reader.ReadAsync("GROUP", null, "%_", 1, 50, null)).TotalCount == 1, "SQL wildcard characters are searched literally");
        Require((await reader.ReadAsync("GROUP", null, "ok [X]", 1, 50, null)).TotalCount == 1, "ASCII case folding and literal punctuation");
        Require((await reader.ReadAsync("GROUP", "decision", "历史理由", 1, 50, null)).TotalCount == 1, "decision reasons are searchable");
        var legacy = await reader.DecisionAsync(oldId);
        Require(legacy is { Window: null, MemoryCount: null, SearchUsed: null, PromptTokens: null } && legacy.Reason == "历史理由仍然可查", "old diagnostic defaults remain unknown rather than invented zeroes");

        var window = new ProactiveWindow { GroupId = "GROUP", LastMessageId = "history-1", LastMessageUtcAt = time,
            Messages = [new ObservedMessage("甲同学", "原始窗口", "history-1", time, ["https://example.invalid/temporary-image"])] };
        db.OutgoingReplies.Add(new OutgoingReply { Id = "synthetic-outgoing", GroupId = "GROUP", IsProactive = true,
            State = DeliveryStates.Sent, PlatformMessageId = "platform-sent", Content = "实际回复", CreatedAt = time });
        var bot = new ChatMessage { MessageId = "synthetic-outgoing", GroupId = "GROUP", UserId = "BOT", UserName = "Bot",
            IsBotMessage = true, IsProactive = true, PlainText = "实际回复", CreatedAt = time.AddMinutes(4) };
        db.ChatMessages.Add(bot);
        var decision = new ProactiveChatLogEntity { GroupId = "GROUP", SentMessageId = "platform-sent", DetailVersion = 1,
            Outcome = ProactiveOutcomes.SentPassive, Line = "实际回复", Model = "synthetic-model", EvaluatedAt = time.AddMinutes(4),
            WindowSnapshotJson = ProactiveLogSnapshot.Capture(window), MessageCount = 1 };
        db.ProactiveChatLogs.Add(decision);
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
        Require(!decision.WindowSnapshotJson.Contains("temporary-image"), "temporary image addresses are not persisted in window snapshots");
        Require(await reader.DecisionAsync(long.MaxValue) is null, "missing old logs degrade without affecting messages");
        Require(!ChatTimelineReader.TrySnapshot("1:-1", out _, out _) && !ChatTimelineReader.TrySnapshot("bad", out _, out _), "invalid pagination snapshots rejected");
        Require(await db.ChatMessages.AnyAsync(m => m.MessageId == "legacy-row" && m.PlainText == "合成旧数据"), "existing database content remains unchanged");
    }
}
