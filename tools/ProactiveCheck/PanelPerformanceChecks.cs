using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Services;
using Mutter.Core.Models.Config;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Services;
using Mutter.Web.Controllers;
using Mutter.Web.Services;

internal static partial class RuntimeChecks
{
    private static async Task UsageLedgerAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ai = env.Services.GetRequiredService<IAiChatService>();
        var structured = env.Services.GetRequiredService<IStructuredAiService>();
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("合成回复", 11, 7));
        using (var operation = AiCallScope.Begin(default, "reply", new("GROUP", "ALICE", "Alice", "synthetic-incoming")))
        {
            await ai.ChatAsync("system", [("user", "question")], env.Config());
            await env.Services.GetRequiredService<ReplyDispatcher>().SendAsync(Reply(), false);
        }
        using (AiCallScope.Begin(default, "proactive", new(GroupId: "GROUP")))
        {
            env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{\"reason\":\"无需接话\"}", 13, 5));
            await structured.GetStructuredOutcomeAsync<Mutter.Core.Proactive.ProactiveVerdict>("s", "u", env.Config());
        }
        using (AiCallScope.Begin(default, "reply", new("GROUP", "BOB", "Bob")))
        {
            env.Http.Handler = (_, _, _) => throw new HttpRequestException("synthetic network failure");
            try { await ai.ChatAsync("s", [("user", "u")], env.Config()); } catch (InvalidOperationException) { }
        }
        using (AiCallScope.Begin(default, "reply", new("GROUP", "ALICE", "Alice")))
        {
            var n = 0;
            env.Http.Handler = (_, _, _) => Task.FromResult(++n == 1
                ? JsonReply("invalid content", 10, 2) : JsonReply("{\"line\":\"回复\"}", 10, 3));
            await structured.GetStructuredOutcomeAsync<Mutter.Core.Proactive.ProactiveVerdict>("s", "u", env.Config());
        }
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChatMessages.Add(new ChatMessage
        {
            MessageId = "legacy-usage", IsBotMessage = true, AiModelUsed = "legacy", UserId = "BOT",
            PromptTokens = 6, CompletionTokens = 3, CreatedAt = env.Clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var totals = await repo.GetAiUsageSummaryAsync(7);
        Require(totals.CallCount == 5 && totals.FailedCount == 1 && totals.UnknownUsageCount == 1 && totals.LegacyCount == 1,
            "physical attempts, declined proactive calls, failures and legacy replies must remain distinct");
        Require(totals.PromptTokens + totals.CompletionTokens == 70, "usage counted once including malformed-content repair");
        var users = await repo.GetUserTokenStatsAsync(7, 10);
        Require(users.Count == 2 && users.Single(u => u.UserId == "ALICE").TotalTokens == 43
            && users.All(u => u.UserId != "BOT"), "attribute usage to requester, not the bot or a proactive window participant");
        var groups = await repo.GetGroupTokenStatsAsync(7, 10);
        Require(groups.Single().TotalTokens == 61, "group usage includes proactive and failed requests");
        Require((await repo.GetDailyTokenStatsAsync(7)).Sum(d => d.TotalTokens) == 70, "daily aggregation parity");
        Require(await db.AiCallRecords.Select(c => c.OperationId).Distinct().CountAsync() == 4, "repair attempts share operation");
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("probe", 2, 1));
        using var probeClient = new HttpClient(env.Http, disposeHandler: false);
        var probe = new WebSearchProbeRunner(probeClient, NullLogger<WebSearchProbeRunner>.Instance,
            env.Services.GetRequiredService<IAiCallStore>(), env.Clock);
        await probe.SendAsync("https://example.invalid/v1", "synthetic", "openai", null, "synthetic-model");
        Require(await db.AiCallRecords.CountAsync(c => c.Purpose == "probe") == 1, "manual probes are also accounted for");
    }

    private static async Task StatisticsPerformanceAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = env.Clock.GetUtcNow().UtcDateTime;
            db.ChatMessages.AddRange(Enumerable.Range(0, 5000).Select(i => new ChatMessage
            {
                MessageId = $"synthetic-large-{i}", GroupId = "GROUP", IsBotMessage = true,
                AiModelUsed = "legacy", PromptTokens = 10, CompletionTokens = 2, CreatedAt = now,
                PlainText = new string('文', 1024), RawSegmentsJson = new string('x', 1024)
            }));
            await db.SaveChangesAsync();
        }
        var statistics = new PanelStatisticsService(env.Services.GetRequiredService<IServiceScopeFactory>(), env.Clock);
        env.Faults.Commands.Clear();
        var elapsed = Stopwatch.StartNew();
        var report = await statistics.UsageAsync(7, default);
        var firstMilliseconds = elapsed.ElapsedMilliseconds;
        var queried = env.Faults.Commands.Count;
        Require(report.Summary.LegacyCount == 5000 && report.Summary.PromptTokens == 50000, "large dataset aggregation");
        Require(queried > 0 && env.Faults.Commands.All(sql => !sql.Contains("PlainText") && !sql.Contains("RawSegmentsJson")),
            "statistics must never materialize message bodies");
        Require(env.Faults.Commands.Any(sql => sql.Contains("GROUP BY") && sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase)),
            "aggregation must execute in SQLite");
        var parallel = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => statistics.UsageAsync(7, default)));
        Require(parallel.All(r => ReferenceEquals(r, report)) && env.Faults.Commands.Count == queried, "cached requests do not repeat queries");
        env.Clock.Advance(TimeSpan.FromSeconds(11));
        Require(!ReferenceEquals(report, await statistics.UsageAsync(7, default)), "cache expires");
        Console.WriteLine($"      合成 5000 条大记录：首次汇总 {firstMilliseconds} ms；10 次缓存命中没有重复 SQL");
    }

    private sealed class ImageHttp : HttpMessageHandler
    {
        public int Calls;
        public int InFlight;
        public int Maximum;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref InFlight);
            int before;
            do { before = Maximum; } while (active > before && Interlocked.CompareExchange(ref Maximum, active, before) != before);
            try
            {
                await Task.Delay(30, ct);
                var bytes = new byte[500];
                bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4e; bytes[3] = 0x47;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            finally { Interlocked.Decrement(ref InFlight); }
        }
    }
    private sealed class ImageClients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static async Task ImageLimitsAsync()
    {
        var clock = new TestClock();
        var http = new ImageHttp();
        using var client = new HttpClient(http);
        using var resolver = new AiImageResolver(new ImageClients(client), NullLogger<AiImageResolver>.Instance,
            clock, new(MaxEntries: 2, MaxBytes: 1800));
        using var cancel = new CancellationTokenSource();
        var first = resolver.ResolveAsync("https://example.invalid/same.png", cancel.Token);
        var second = resolver.ResolveAsync("https://example.invalid/same.png");
        cancel.Cancel();
        try { await first; } catch (OperationCanceledException) { }
        Require(await second is not null && http.Calls == 1, "same image single flight and cancellation isolation");
        var images = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => resolver.ResolveAsync($"https://example.invalid/{i}.png")));
        Require(images.All(i => i != null) && http.Maximum is > 1 and <= 4, "parallel downloads bounded at four");
        Require(resolver.Snapshot() is { Entries: <= 2, Bytes: <= 1800 }, "both item and byte limits enforced");
        clock.Advance(TimeSpan.FromMinutes(3));
        Require(resolver.Snapshot() is { Entries: 0, Bytes: 0 }, "expired payloads released");
        var over = "data:image/png;base64," + Convert.ToBase64String(new byte[AiImageResolver.MaxImageBytes + 1]);
        Require(await resolver.ResolveAsync(over) is null, "decoded data URI must obey byte limit");
    }

    private static async Task SharedMemoryAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using (var scope = env.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IGroupMemoryRepository>().SaveAsync("GROUP", "合成群记忆：每周讨论阅读");
        var builder = env.Services.GetRequiredService<IContextBuilder>();
        var context = await builder.BuildGroupContextAsync("GROUP", "question", "Alice");
        Require(context.ComposeSystemPrompt().Contains("合成群记忆"), "ordinary group reply receives memory");
        var proactive = await ((ContextBuilder)builder).BuildGroupContextAsync("GROUP", "question", "Alice", c => c);
        Require(proactive.GroupMemory is null, "proactive workflow injects once via its own policy");
        Require((await builder.BuildPrivateContextAsync("ALICE", "question")).GroupMemory is null, "no cross-conversation memory leakage");
        using var configs = env.Services.CreateScope();
        var repo = configs.ServiceProvider.GetRequiredService<IConfigRepository>();
        var config = env.Config(); config.GroupMemoryInjectionEnabled = false;
        await repo.SaveAiConfigAsync(config);
        Require((await builder.BuildGroupContextAsync("GROUP", "q", "A")).GroupMemory is null, "ordinary global memory switch");
        Require((await repo.GetProactiveConfigAsync()).MemoryInjectionEnabled, "proactive switch remains independent");
        config.GroupMemoryInjectionEnabled = true;
        await repo.SaveAiConfigAsync(config);
        var group = (await repo.GetGroupConfigAsync("GROUP"))!; group.MemoryInjectionEnabled = false;
        await repo.SaveGroupConfigAsync(group);
        Require((await builder.BuildGroupContextAsync("GROUP", "q", "A")).GroupMemory is null, "group memory override respected");
    }

    private static async Task AtomicGroupSettingsAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using (var seed = env.Services.CreateScope())
            await seed.ServiceProvider.GetRequiredService<IGroupMemoryRepository>().SaveAsync("GROUP", "原始合成记忆");
        var update = new GroupsController.GroupConfigUpdateDto
        {
            IsEnabled = false, Name = "Synthetic Group", UpdateMemory = true, GroupMemory = "新合成记忆"
        };
        using (var scope = env.Services.CreateScope())
        {
            env.Faults.FailGroupMemoryWrite = true;
            try
            {
                await new GroupsController(scope.ServiceProvider.GetRequiredService<AppDbContext>()).UpdateGroupConfig("GROUP", update, default);
                throw new InvalidOperationException("injected memory failure was not observed");
            }
            catch (DbUpdateException) { }
            finally { env.Faults.FailGroupMemoryWrite = false; }
        }
        using (var read = env.Services.CreateScope())
        {
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            Require((await db.GroupConfigs.SingleAsync(g => g.GroupId == "GROUP")).IsEnabled
                && (await db.GroupMemories.SingleAsync()).Summary == "原始合成记忆"
                && !await db.KnownGroups.AnyAsync(), "failed save must roll back config, memory and group name");
            Require(await new GroupsController(db).UpdateGroupConfig("GROUP", update, default) is OkObjectResult, "combined save succeeds");
        }
        using (var read = env.Services.CreateScope())
        {
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            Require(!(await db.GroupConfigs.SingleAsync()).IsEnabled && (await db.GroupMemories.SingleAsync()).Summary == "新合成记忆",
                "all changes committed together");
            update.UpdateMemory = false; update.GroupMemory = "不能写入的合成摘要";
            await new GroupsController(db).UpdateGroupConfig("GROUP", update, default);
            Require((await db.GroupMemories.SingleAsync()).Summary == "新合成记忆", "legacy client and unavailable memory do not overwrite summary");
        }
    }

    private static async Task RetentionAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var now = env.Clock.GetUtcNow().UtcDateTime;
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var failed = Call("old-failed", now.AddDays(-91));
            failed.State = "network_error";
            failed.CompletionTokens = null;
            db.AiCallRecords.AddRange(Call("old-call", now.AddDays(-91)), failed, Call("recent-call", now.AddDays(-89)));
            db.ActiveMessageAttempts.AddRange(
                new ActiveMessageAttempt { Key = "old", Scope = "group", AttemptedAt = now.AddDays(-91) },
                new ActiveMessageAttempt { Key = "recent", Scope = "group", AttemptedAt = now.AddDays(-89) });
            db.OutgoingReplies.AddRange(
                Reply("old-reply", DeliveryStates.Sent, now.AddDays(-91)),
                Reply("old-paired", DeliveryStates.Sent, now.AddDays(-91)),
                Reply("recent-reply", DeliveryStates.Sent, now.AddDays(-89)),
                Reply("old-pending", DeliveryStates.Pending, now.AddDays(-91)));
            db.ChatMessages.AddRange(
                new ChatMessage { MessageId = "modern-chat", IsBotMessage = true, AiModelUsed = "synthetic",
                    AiOperationId = "old-call", PromptTokens = 3, CompletionTokens = 2, CreatedAt = now.AddDays(-91) },
                new ChatMessage { MessageId = "old-paired", IsBotMessage = true, AiModelUsed = "synthetic",
                    PromptTokens = 3, CompletionTokens = 2, CreatedAt = now.AddDays(-91) });
            await db.SaveChangesAsync();
        }

        var retention = new DataRetentionService(env.Services.GetRequiredService<IServiceScopeFactory>(),
            env.Clock, NullLogger<DataRetentionService>.Instance);
        using var read = env.Services.CreateScope();
        var check = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var queries = new Mutter.Infrastructure.Repositories.AiUsageQueries(check);
        var before = JsonSerializer.Serialize(await queries.SummaryAsync(null, default));
        var recentBefore = JsonSerializer.Serialize(await queries.SummaryAsync(now.AddDays(-90), default));

        env.Faults.FailRetentionDelete = true;
        var rolledBack = false;
        try { await retention.CleanupAsync(default); }
        catch (InvalidOperationException) { rolledBack = true; }
        finally { env.Faults.FailRetentionDelete = false; }
        Require(rolledBack && !await check.AiUsageArchives.AnyAsync() && await check.AiCallRecords.CountAsync() == 3,
            "a failed delete must roll back both the archive increment and the detail deletion");

        await retention.CleanupAsync(default);
        await retention.CleanupAsync(default);
        Require(await check.AiCallRecords.CountAsync() == 1
            && await check.ActiveMessageAttempts.CountAsync() == 1
            && await check.OutgoingReplies.CountAsync() == 2
            && await check.OutgoingReplies.AnyAsync(r => r.Id == "old-pending") && await check.ChatMessages.CountAsync() == 2,
            "retention must remove only expired terminal records");
        Require(JsonSerializer.Serialize(await queries.SummaryAsync(null, default)) == before,
            "cumulative calls, failures, unknown usage and tokens must survive cleanup without double counting legacy replies");
        Require(JsonSerializer.Serialize(await queries.SummaryAsync(now.AddDays(-90), default)) == recentBefore,
            "archived totals must not enter a recent-date report");
        var archive = await check.AiUsageArchives.SingleAsync();
        Require(archive is { CallCount: 2, FailedCount: 1, UnknownUsageCount: 1, LegacyCount: 1 },
            "repeated cleanup must not archive the same usage twice");

        static AiCallRecord Call(string id, DateTime at) => new()
        {
            Id = id, OperationId = id, Model = "synthetic", Transport = "openai",
            StartedAt = at, CompletedAt = at, ElapsedMs = 1, State = "success", PromptTokens = 3, CompletionTokens = 2
        };
        static OutgoingReply Reply(string id, string state, DateTime at) => new()
        {
            Id = id, BotId = "BOT", Content = "synthetic", State = state, CreatedAt = at,
            Model = state == DeliveryStates.Pending ? null : "synthetic", PromptTokens = 3, CompletionTokens = 2
        };
    }

    private static async Task RuntimeSnapshotAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var queue = env.Services.GetRequiredService<IConversationWorkQueue>();
        var worker = (Mutter.Infrastructure.Services.ConversationWorkQueue)queue;
        var started = Signal(); var release = Signal();
        queue.TryEnqueue("g:GROUP", async ct =>
        {
            using var operation = AiCallScope.Begin(ct, "reply", new("GROUP", "ALICE", "Alice"));
            RuntimeActivity.Current!.Mark("context");
            started.SetResult();
            await release.Task.WaitAsync(ct);
            await env.Services.GetRequiredService<IAiChatService>().ChatAsync("s", [("user", "u")], env.Config(), ct);
            await env.Services.GetRequiredService<ReplyDispatcher>().SendAsync(Reply(), false, ct);
        }, description: "Alice");
        queue.TryEnqueue("g:GROUP", _ => Task.CompletedTask);
        Require(queue.Snapshot() is { Waiting: 2, Running: 0 }, "queued snapshot");
        await worker.StartAsync(default); await started.Task;
        Require(queue.Snapshot() is { Waiting: 1, Running: 1 }, "running snapshot");
        var id = queue.Snapshot().Tasks.Single(t => t.State == "running").Id;
        release.SetResult();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(stop.Token);
        var task = queue.Snapshot().Tasks.Single(t => t.Id == id);
        Require(task.State == "completed" && task.Outcome == "sent" && task.Steps.Any(s => s.Stage == "context"), "terminal task history");
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Require((await db.AiCallRecords.SingleAsync()).OperationId == id && (await db.OutgoingReplies.SingleAsync()).AiOperationId == id,
            "queue, model and delivery share an operation identifier");
        var detail = await new RuntimeController(queue, db, env.Images,
            new UpstreamHealthTracker(TimeProvider.System)).GetOperation(id, default);
        Require(detail is OkObjectResult, "operation detail endpoint");
    }
}
