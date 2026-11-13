using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;
using Mutter.Web.Controllers;

internal static partial class RuntimeChecks
{
    private static AutomaticMemoryConfig MemoryOptions() => new()
    {
        Enabled = true, CountEnabled = true, Count = 1, IntervalEnabled = false, ScheduleEnabled = false,
        ProviderConnectionId = "MEMORY-CONNECTION", ModelName = "memory-small"
    };

    private static async Task ConfigureMemoryAsync(TestEnvironment env, AutomaticMemoryConfig? options = null)
    {
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.AiProviderConnections.AnyAsync(c => c.Id == "MEMORY-CONNECTION"))
        {
            db.AiProviderConnections.Add(new AiProviderConnectionEntity
            {
                Id = "MEMORY-CONNECTION", Name = "Synthetic memory provider", Protocol = "openai",
                ApiBaseUrl = "https://memory.example.test/v1", ApiKey = "synthetic-memory-key", DefaultModel = "memory-small",
                ModelsJson = """[{"id":"memory-small","contextWindowTokens":8192,"maxOutputTokens":1024}]"""
            });
            await db.SaveChangesAsync();
        }
        var repo = scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>();
        var state = await repo.StateAsync("GROUP");
        await repo.SaveConfigAsync("GROUP", options ?? MemoryOptions(), state?.Revision ?? 0, default);
    }

    private static async Task<long> NewMemoryMessageAsync(TestEnvironment env, string text = "我不吃辣，推荐吃饭的地方时记得避开。", string group = "GROUP", string user = "MEMBER-A", bool at = true)
    {
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = new ChatMessage
        {
            GroupId = group, UserId = user, UserName = "Synthetic member", MessageId = Guid.NewGuid().ToString("N"),
            PlainText = text, HasAtBot = at, CreatedAt = env.Clock.GetUtcNow().UtcDateTime
        };
        db.ChatMessages.Add(message); await db.SaveChangesAsync(); return message.Id;
    }

    private static async Task AutomaticMemoryUpgradeAsync()
    {
        await using var env = await TestEnvironment.CreateAsync(legacy: true);
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                WITH RECURSIVE old(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM old WHERE n < 50000)
                INSERT INTO "ChatMessages" ("MessageId","ChatType","GroupId","UserId","UserName","PlainText",
                    "RawSegmentsJson","HasImage","HasAtBot","IsBotMessage","IsProactive","CreatedAt")
                SELECT 'memory-legacy-' || n,0,'GROUP','SYNTHETIC','Synthetic','合成历史：我喜欢喝茶','',0,1,0,0,'2026-11-02 04:00:00' FROM old;
                """);
            await db.EnsureSchemaUpgradedAsync(); await db.EnsureSchemaUpgradedAsync();
            Require(await db.ChatMessages.CountAsync(m => m.GroupId == "GROUP") == 50000, "idempotent upgrade preserves fifty thousand rows");
            Require(!await db.GroupMemoryLearningStates.AnyAsync(), "schema upgrade never enables or backfills memory");
        }
        var learner = env.Services.GetRequiredService<GroupMemoryLearningService>();
        await learner.TickAsync();
        Require(env.Http.Requests.Count == 0, "old production startup makes zero model calls");
        await ConfigureMemoryAsync(env);
        using (var scope = env.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>();
            var state = (await repo.StateAsync("GROUP"))!;
            Require(state.LastProcessedRowId >= 50000 && await repo.PrepareAsync("GROUP", false, default) is null,
                "enabling establishes tail boundary instead of draining historical records");
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.OpenConnectionAsync();
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN SELECT Id FROM ChatMessages WHERE GroupId = 'GROUP' AND Id > 50000 ORDER BY Id LIMIT 40";
            using var reader = await command.ExecuteReaderAsync();
            var plan = "";
            while (await reader.ReadAsync()) plan += reader.GetString(3);
            Require(plan.Contains("IX_ChatMessages_MemoryCursor"), "incremental reads use group cursor index");
        }
        await learner.RunGroupAsync("GROUP");
        Require(env.Http.Requests.Count == 0, "first enable with old backlog still makes zero calls");
        var next = await NewMemoryMessageAsync(env);
        using (var scope = env.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>();
            var batch = await repo.PrepareAsync("GROUP", false, default);
            Require(batch is { Messages.Count: 1 } && batch.Messages[0].Id == next, "only a post-enable row becomes eligible");
        }
        var off = MemoryOptions(); off.Enabled = false;
        await ConfigureMemoryAsync(env, off);
        await NewMemoryMessageAsync(env, "暂停期间的消息不应被自动回灌");
        await ConfigureMemoryAsync(env);
        using (var scope = env.Services.CreateScope())
            Require(await scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().PrepareAsync("GROUP", false, default) is null,
                "re-enable starts a new boundary, including messages accumulated while paused");
    }

    private static async Task AutomaticMemoryFlowAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await ConfigureMemoryAsync(env);
        var source = await NewMemoryMessageAsync(env);
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply(JsonSerializer.Serialize(new
        {
            operations = new[]
            {
                new { op = "add", content = "成员自述不吃辣。", subjectUserId = "MEMBER-A", sourceRowIds = new[] { source }, evidence = "我不吃辣", tags = new[] { "饮食", "辣" } },
                new { op = "add", content = "另一成员不吃辣。", subjectUserId = "MEMBER-B", sourceRowIds = new[] { source }, evidence = "我不吃辣", tags = new[] { "饮食" } },
                new { op = "add", content = "臆造的记忆", subjectUserId = "MEMBER-A", sourceRowIds = new[] { source + 999 }, evidence = "不存在", tags = Array.Empty<string>() }
            }
        })));
        await env.Services.GetRequiredService<GroupMemoryLearningService>().RunGroupAsync("GROUP");
        Require(env.Http.Requests.Count == 1 && env.Http.Requests[0].Url.Host == "memory.example.test"
            && env.Http.Requests[0].Body.Contains("memory-small"), "single request uses independent memory connection and model");
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Require(await db.GroupMemoryItems.CountAsync() == 1, "unverified source and wrong member are rejected");
            Require(await db.AiCallRecords.AnyAsync(c => c.Purpose == "group_memory" && c.Stage == "extract"), "memory uses actual-call usage ledger");
            Require((await scope.ServiceProvider.GetRequiredService<IConfigRepository>().GetAiConfigAsync())!.ModelName == env.Config().ModelName,
                "chat model configuration remains independent");
            var recall = scope.ServiceProvider.GetRequiredService<IAutomaticMemoryRecall>();
            Require((await recall.RecallAsync("GROUP", "吃辣吗")).Count == 1, "Chinese short tags recall locally");
            Require((await recall.RecallAsync("OTHER-GROUP", "吃辣吗")).Count == 0, "memories do not leak across groups");
            Require((await recall.RecallAsync("GROUP", "今天天气如何")).Count == 0, "unrelated queries do not force a memory");
            Require((await recall.RecallAsync("GROUP", "辣", beforeRowId: source - 1)).Count == 0, "later facts cannot leak into earlier queued turns");
            var controller = new AutomaticMemoryController(db, scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>(), env.Clock);
            var result = (OkObjectResult)await controller.Get("GROUP", default);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, AutomaticMemoryConfig.JsonOptions));
            Require(json.RootElement.GetProperty("config").GetProperty("providerConnectionId").GetString() == "MEMORY-CONNECTION"
                && json.RootElement.GetProperty("items").GetArrayLength() == 1
                && json.RootElement.GetProperty("items")[0].GetProperty("expiresAt").GetDateTime().Kind == DateTimeKind.Utc,
                "real management API returns config, items and unambiguous UTC timestamps without credentials");
            Require(await controller.Sources("OTHER-GROUP", (await db.GroupMemoryItems.SingleAsync()).Id, default) is NotFoundResult,
                "source lookup cannot cross groups");
            var connections = new AiConnectionsController(db, env.Services.GetRequiredService<IHttpClientFactory>(),
                env.Services.GetRequiredService<IAiModelExecutor>());
            Require(await connections.Delete("MEMORY-CONNECTION", default) is ConflictObjectResult,
                "a connection referenced by memory cannot be removed");
        }
        var built = await env.Services.GetRequiredService<IContextBuilder>().BuildGroupContextAsync("GROUP", "吃辣吗", "Synthetic");
        Require(built.AutomaticMemory.Count == 1 && built.Messages[0].Content.Contains("本群自动记忆")
            && !built.ComposeSystemPrompt().Contains("成员自述不吃辣"), "automatic memory is low-priority user background, not system instructions");
        var privateContext = await env.Services.GetRequiredService<IContextBuilder>().BuildPrivateContextAsync("MEMBER-A", "吃辣吗");
        Require(privateContext.AutomaticMemory.Count == 0, "private chat does not use group memories");
        Require(env.Http.Requests.Count == 1, "recall requires no extra model calls");
    }

    private static async Task AutomaticMemoryTriggersAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var options = MemoryOptions(); options.IntervalEnabled = true; options.IntervalMinutes = 1; options.ScheduleEnabled = true;
        options.Schedules = [new() { Time = env.Clock.GetUtcNow().UtcDateTime.AddHours(8).AddMinutes(1).ToString("HH:mm") }];
        await ConfigureMemoryAsync(env, options);
        env.Clock.Advance(TimeSpan.FromMinutes(2));
        var source = await NewMemoryMessageAsync(env);
        using var scope = env.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>();
        var batch = (await repo.PrepareAsync("GROUP", true, default))!;
        Require(batch.Reasons.Count == 3, "count, interval and schedule are combined into one batch");
        using (var clearScope = env.Services.CreateScope()) await clearScope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().ClearAsync("GROUP", default);
        Require(!await repo.CompleteAsync(batch, new(), "success", default), "clearing rejects an in-flight old patch even with a tracked EF snapshot");
        using (var read = env.Services.CreateScope())
        {
            var store = read.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>();
            Require((await store.StateAsync("GROUP"))!.LastProcessedRowId == source && (await store.ItemsAsync("GROUP")).Count == 0,
                "clear preserves new-message boundary and old completion cannot revive data");
            Require(await store.PrepareAsync("GROUP", true, default) is null, "no new records means no paid call even after scheduled triggers");
        }
        var monday = new DateTime(2026, 11, 16, 1, 0, 0, DateTimeKind.Utc);
        Require(new MemorySchedule { Frequency = "weekly", Weekday = 1, Time = "09:00" }.NextAfter(monday) == monday.AddDays(7),
            "same weekly slot is never replayed");
        Require(AutomaticMemoryConfig.Decode("broken").Enabled == false, "corrupt config fails closed");
        using (var change = env.Services.CreateScope())
        {
            var configRepo = change.ServiceProvider.GetRequiredService<IConfigRepository>();
            var group = (await configRepo.GetGroupConfigAsync("GROUP"))!;
            group.TriggerMode = 2;
            await configRepo.SaveGroupConfigAsync(group);
            var state = (await change.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync("GROUP"))!;
            Require(state.LastCompletedAt == env.Clock.GetUtcNow().UtcDateTime, "record-mode changes use the same clock and rebuild the learning boundary");
        }
        env.Clock.Advance(TimeSpan.FromDays(2));
        await env.Services.GetRequiredService<GroupMemoryLearningService>().SkipMissedSchedulesAsync();
        using (var check = env.Services.CreateScope())
            Require((await check.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync("GROUP"))!.LastScheduleCheckAt == env.Clock.GetUtcNow().UtcDateTime,
                "restart skips missed schedule slots without resetting the history cursor or quota");
    }

    private static async Task AutomaticMemoryFailureAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await ConfigureMemoryAsync(env);
        var source = await NewMemoryMessageAsync(env);
        env.Http.Handler = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var learner = env.Services.GetRequiredService<GroupMemoryLearningService>();
        await learner.RunGroupAsync("GROUP");
        Require(env.Http.Requests.Count == 1, "memory failure must not cause transport retries or format repair calls");
        await learner.RunGroupAsync("GROUP");
        Require(env.Http.Requests.Count == 1, "persistent cooldown suppresses an immediate retry");
        env.Clock.Advance(TimeSpan.FromMinutes(6)); await learner.RunGroupAsync("GROUP");
        env.Clock.Advance(TimeSpan.FromMinutes(11)); await learner.RunGroupAsync("GROUP");
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await db.GroupMemoryLearningStates.SingleAsync();
            Require(state.CallsToday == 3 && state.LastProcessedRowId == source && state.LastOutcome == "skipped_after_failures",
                "failed requests consume durable quota and poison batch eventually advances");
            state.CallsToday = 12; await db.SaveChangesAsync();
        }
        await NewMemoryMessageAsync(env); env.Clock.Advance(TimeSpan.FromHours(1)); await learner.RunGroupAsync("GROUP");
        Require(env.Http.Requests.Count == 3, "daily quota survives new scopes and stops additional calls");
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = env.Clock.GetUtcNow().UtcDateTime;
            var state = await db.GroupMemoryLearningStates.SingleAsync();
            state.CallsToday = 0;
            for (var i = 0; i < 4; i++) db.GroupMemoryLearningStates.Add(new()
            {
                GroupId = "QUOTA-GROUP-" + i, QuotaDay = AutomaticMemoryRepository.Day(now), CallsToday = 12
            });
            await db.SaveChangesAsync();
        }
        await learner.RunGroupAsync("GROUP");
        Require(env.Http.Requests.Count == 3, "global request budget includes other groups and persists while they are disabled");
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = env.Clock.GetUtcNow().UtcDateTime;
            db.GroupMemoryItems.AddRange(Enumerable.Range(0, 130).Select(i => new GroupMemoryItemEntity
            {
                GroupId = "GROUP", Content = "合成容量 " + i, LastSeenAt = now.AddMinutes(i), FirstSeenAt = now,
                ExpiresAt = now.AddDays(1)
            }));
            db.GroupMemoryItems.Add(new GroupMemoryItemEntity { GroupId = "GROUP", Content = "过期咖啡偏好", TagsJson = "[\"咖啡\"]", ExpiresAt = now.AddMinutes(-1) });
            await db.SaveChangesAsync();
            Require((await scope.ServiceProvider.GetRequiredService<IAutomaticMemoryRecall>().RecallAsync("GROUP", "咖啡")).Count == 0, "expired records never enter recall");
            var items = await scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().ItemsAsync("GROUP");
            Require(items.Count == 128, "even a malformed oversized existing store is bounded on reads");
        }
    }
}
