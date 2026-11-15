using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Interfaces;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;

internal static partial class RuntimeChecks
{
    private static async Task AutomaticMemoryBudgetAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await ConfigureMemoryAsync(env);
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.AiProviderConnections.SingleAsync(c => c.Id == "MEMORY-CONNECTION")).TimeoutSeconds = 90;
            await db.SaveChangesAsync();
        }
        var source = await NewMemoryMessageAsync(env);
        var started = Signal();
        var foreground = Signal();
        env.Http.Handler = async (_, _, token) =>
        {
            started.TrySetResult();
            // 实际等待超过旧的 30 秒截止，才能发现仍然藏在某层的提前取消。
            await Task.Delay(TimeSpan.FromSeconds(31), token);
            return JsonReply("{\"operations\":[]}");
        };
        var queue = env.Services.GetRequiredService<ConversationWorkQueue>();
        await queue.StartAsync(default);
        try
        {
            await env.Services.GetRequiredService<GroupMemoryLearningService>().TickAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(queue.TryEnqueue("g:GROUP", _ => { foreground.TrySetResult(); return Task.CompletedTask; }) == EnqueueResult.Accepted,
                "foreground work is admitted while memory waits for its provider");
            await foreground.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await queue.StopAsync(stop.Token);
        }
        using var read = env.Services.CreateScope();
        var state = (await read.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync("GROUP"))!;
        Require(state.LastOutcome == "success" && state.LastProcessedRowId == source && state.CallsToday == 1,
            "a memory response slower than 30 seconds completes under its configured 90-second budget");
        var call = await read.ServiceProvider.GetRequiredService<AppDbContext>().AiCallRecords.SingleAsync();
        Require(call.State == "success" && call.ElapsedMs >= 30_000,
            "slow successful extraction stays a single actual request");
    }

    private static async Task AutomaticMemoryTimeoutAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var options = MemoryOptions();
        options.CountEnabled = false; options.ScheduleEnabled = true;
        options.Schedules = [new() { Time = env.Clock.GetUtcNow().AddHours(8).AddMinutes(1).ToString("HH:mm") }];
        await ConfigureMemoryAsync(env, options);
        env.Clock.Advance(TimeSpan.FromMinutes(2));
        var source = await NewMemoryMessageAsync(env);
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 缩短合成连接的超时以快速覆盖真实 HTTP 取消，而不是仅向仓储传一个 timeout 字符串。
            (await db.AiProviderConnections.SingleAsync(c => c.Id == "MEMORY-CONNECTION")).TimeoutSeconds = 1;
            await db.SaveChangesAsync();
        }
        env.Http.Handler = async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonReply("unreachable");
        };
        var learner = env.Services.GetRequiredService<GroupMemoryLearningService>();
        await learner.RunGroupAsync("GROUP");
        await learner.RunGroupAsync("GROUP");
        Require(env.Http.Requests.Count == 1, "an immediate timeout retry is suppressed");
        env.Clock.Advance(TimeSpan.FromMinutes(6)); await learner.RunGroupAsync("GROUP");
        env.Clock.Advance(TimeSpan.FromMinutes(11)); await learner.RunGroupAsync("GROUP");
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.EnsureSchemaUpgradedAsync(); // 重启式的幂等升级不能清掉新的退避状态。
            var state = await db.GroupMemoryLearningStates.SingleAsync();
            Require(state.LastProcessedRowId < source && state.RetryThroughRowId == source && state.LastOutcome == "timeout"
                && state.ConsecutiveFailures == 0 && state.TransientFailures == 3 && state.CallsToday == 3,
                "three real connection timeouts retain the same batch, quota and durable transient counter");
            Require(state.NextAttemptAt == env.Clock.GetUtcNow().UtcDateTime.AddMinutes(20),
                "third transient failure backs off for twenty minutes");
            Require(await db.AiCallRecords.CountAsync(c => c.State == "timeout" && c.Purpose == "group_memory") == 3,
                "wrapped connection cancellations are recorded as timeouts, not user cancellations");
        }
        env.Clock.Advance(TimeSpan.FromMinutes(21));
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{\"operations\":[]}"));
        await learner.RunGroupAsync("GROUP");
        using (var scope = env.Services.CreateScope())
        {
            var state = (await scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync("GROUP"))!;
            Require(state.LastOutcome == "success" && state.LastProcessedRowId == source && state.RetryThroughRowId == 0
                && state.TransientFailures == 0 && state.CallsToday == 4,
                "a schedule-only batch retries without a new schedule and valid empty output completes it");
        }

        await ConfigureMemoryAsync(env);
        await NewMemoryMessageAsync(env, "取消测试的新消息");
        env.Clock.Advance(TimeSpan.FromMinutes(2)); // 上一次成功探测后仍有连接级冷却。
        using var cancel = new CancellationTokenSource();
        env.Http.Handler = (_, _, token) =>
        {
            cancel.Cancel();
            return Task.FromException<HttpResponseMessage>(new TaskCanceledException("synthetic socket cancellation",
                new IOException("synthetic read failure", new SocketException((int)SocketError.OperationAborted)), token));
        };
        var health = env.Services.GetRequiredService<GroupMemoryUpstreamHealth>().ForConnection("MEMORY-CONNECTION");
        var failures = health.Snapshot().WindowFailure;
        try { await learner.RunGroupAsync("GROUP", cancel.Token); throw new InvalidOperationException("host cancellation must propagate"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        using var read = env.Services.CreateScope();
        Require(await read.ServiceProvider.GetRequiredService<AppDbContext>().AiCallRecords.AnyAsync(c => c.State == "cancelled")
            && health.Snapshot().WindowFailure == failures,
            "host cancellation stays cancellation and does not mark the provider unhealthy");
    }

    private static async Task AutomaticMemoryPoisonBatchAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await ConfigureMemoryAsync(env);
        var source = await NewMemoryMessageAsync(env);
        var learner = env.Services.GetRequiredService<GroupMemoryLearningService>();
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("invalid output"));
        await learner.RunGroupAsync("GROUP");
        var later = await NewMemoryMessageAsync(env, "这是第一次失败之后的新消息，不能跟着旧批次跳过。");
        env.Clock.Advance(TimeSpan.FromMinutes(6));
        env.Http.Handler = (_, _, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("synthetic timeout",
            new IOException("synthetic read failure", new SocketException((int)SocketError.OperationAborted))));
        await learner.RunGroupAsync("GROUP");
        using (var scope = env.Services.CreateScope())
        {
            var state = (await scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync("GROUP"))!;
            Require(state.ConsecutiveFailures == 0 && state.TransientFailures == 1 && state.RetryThroughRowId == source,
                "a timeout interrupts the poison-output streak while retaining the original boundary");
        }
        env.Clock.Advance(TimeSpan.FromMinutes(6));
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("invalid output"));
        await learner.RunGroupAsync("GROUP");
        env.Clock.Advance(TimeSpan.FromMinutes(6));
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{\"operations\":[", finish: "length"));
        await learner.RunGroupAsync("GROUP");
        env.Clock.Advance(TimeSpan.FromMinutes(11));
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("invalid output"));
        await learner.RunGroupAsync("GROUP");
        using (var scope = env.Services.CreateScope())
        {
            var state = (await scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync("GROUP"))!;
            Require(state.LastOutcome == "skipped_after_failures" && state.LastProcessedRowId == source && source < later
                && state.ConsecutiveFailures == 0 && state.TransientFailures == 0 && state.RetryThroughRowId == 0,
                "only three consecutive invalid/truncated outputs skip the fixed original batch");
        }
        foreach (var request in env.Http.Requests)
        {
            using var outer = JsonDocument.Parse(request.Body);
            var user = outer.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "user");
            using var batch = JsonDocument.Parse(user.GetProperty("content").GetString()!);
            var messages = batch.RootElement.GetProperty("messages");
            Require(messages.GetArrayLength() == 1 && messages[0].GetProperty("id").GetInt64() == source,
                "new arrivals are excluded from every retry of the old batch");
        }
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{\"operations\":[]}"));
        env.Clock.Advance(TimeSpan.FromMinutes(16));
        await learner.RunGroupAsync("GROUP");
        using var read = env.Services.CreateScope();
        Require((await read.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync("GROUP"))!.LastProcessedRowId == later,
            "messages after a skipped poison batch can be learned normally");
    }

    private static async Task AutomaticMemoryConnectionHealthAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await ConfigureMemoryAsync(env);
        var global = env.Services.GetRequiredService<UpstreamHealthTracker>();
        global.Record(false, 1); global.Record(false, 1);
        var health = env.Services.GetRequiredService<GroupMemoryUpstreamHealth>();
        var learner = env.Services.GetRequiredService<GroupMemoryLearningService>();
        await NewMemoryMessageAsync(env);
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{\"operations\":[]}"));
        await learner.RunGroupAsync("GROUP");
        Require(env.Http.Requests.Count == 1 && global.IsDegraded && global.Snapshot().WindowSuccess == 0,
            "an independent memory provider works during foreground degradation and does not alter foreground samples");
        await NewMemoryMessageAsync(env);
        env.Http.Handler = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10)) } });
        await learner.RunGroupAsync("GROUP");
        Require(health.ForConnection("MEMORY-CONNECTION").IsDegraded, "memory 429 closes that connection's background gate");
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiProviderConnections.Add(new AiProviderConnectionEntity
            {
                Id = "OTHER-MEMORY-CONNECTION", ApiBaseUrl = "https://other-memory.example.test/v1",
                ApiKey = "synthetic-other-key", DefaultModel = "memory-small",
                ModelsJson = """[{"id":"memory-small","contextWindowTokens":8192,"maxOutputTokens":1024}]"""
            });
            await db.SaveChangesAsync();
            var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            var repo = scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>();
            foreach (var group in new[] { "SAME-CONNECTION-GROUP", "OTHER-CONNECTION-GROUP" })
            {
                await configs.SaveGroupConfigAsync(new() { GroupId = group, IsEnabled = true });
                var options = MemoryOptions();
                if (group == "OTHER-CONNECTION-GROUP") options.ProviderConnectionId = "OTHER-MEMORY-CONNECTION";
                await repo.SaveConfigAsync(group, options, 0, default);
            }
        }
        await NewMemoryMessageAsync(env, group: "SAME-CONNECTION-GROUP");
        await NewMemoryMessageAsync(env, group: "OTHER-CONNECTION-GROUP");
        env.Clock.Advance(TimeSpan.FromMinutes(6));
        await learner.RunGroupAsync("GROUP");
        await learner.RunGroupAsync("SAME-CONNECTION-GROUP");
        Require(env.Http.Requests.Count == 2, "Retry-After blocks both groups sharing a connection even after the group cooldown expires");
        using (var scope = env.Services.CreateScope())
        {
            var state = (await scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync("SAME-CONNECTION-GROUP"))!;
            Require(state.CallsToday == 0 && state.LastOutcome == "baseline_set", "gate rejection happens before quota reservation");
        }
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{\"operations\":[]}"));
        await learner.RunGroupAsync("OTHER-CONNECTION-GROUP");
        Require(env.Http.Requests.Count == 3 && env.Http.Requests[^1].Url.Host == "other-memory.example.test",
            "a different summary connection continues learning");
        await learner.TickAsync();
        Require(!env.Services.GetRequiredService<ConversationWorkQueue>().Snapshot().Tasks.Any(),
            "scheduling also skips degraded summary connections");
        env.Clock.Advance(TimeSpan.FromMinutes(5));
        await learner.RunGroupAsync("GROUP");
        Require(env.Http.Requests.Count == 4, "one memory probe is allowed after Retry-After expires");
        env.Clock.Advance(TimeSpan.FromMinutes(2));
        Require(health.ForConnection("MEMORY-CONNECTION").CanScheduleBackground,
            "completed probes release their lease so later groups can recover");
    }

    private static async Task AutomaticMemoryRequestFailuresAsync()
    {
        foreach (var (status, outcome) in new[] { (401, "request_rejected"), (408, "timeout"), (429, "rate_limited"), (0, "network_error") })
        {
            await using var env = await TestEnvironment.CreateAsync();
            await ConfigureMemoryAsync(env);
            var source = await NewMemoryMessageAsync(env);
            env.Http.Handler = (_, _, _) => status == 0
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic network error",
                    new IOException("synthetic read failure", new SocketException((int)SocketError.ConnectionReset))))
                : Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
            await env.Services.GetRequiredService<GroupMemoryLearningService>().RunGroupAsync("GROUP");
            using var scope = env.Services.CreateScope();
            var state = (await scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync("GROUP"))!;
            Require(state.LastOutcome == outcome && state.ConsecutiveFailures == 0 && state.LastProcessedRowId < source
                && state.RetryThroughRowId == source && state.CallsToday == 1,
                $"HTTP/network failure {status} is classified without skipping or refunding an actual request");
            Require(state.NextAttemptAt == env.Clock.GetUtcNow().UtcDateTime.AddMinutes(status == 401 ? 60 : 5),
                "configuration rejection pauses longer than transient failures");
        }
    }

    private static async Task AutomaticMemoryFailureUpgradeAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await ConfigureMemoryAsync(env);
        var source = await NewMemoryMessageAsync(env);
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await db.GroupMemoryLearningStates.SingleAsync();
            state.ConsecutiveFailures = 2; state.LastOutcome = "timeout";
            state.QuotaDay = AutomaticMemoryRepository.Day(env.Clock.GetUtcNow().UtcDateTime); state.CallsToday = 2;
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"GroupMemoryLearningStates\" DROP COLUMN \"TransientFailures\"");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"GroupMemoryLearningStates\" DROP COLUMN \"RetryThroughRowId\"");
            db.ChangeTracker.Clear();
            await db.EnsureSchemaUpgradedAsync(); await db.EnsureSchemaUpgradedAsync();
            state = await db.GroupMemoryLearningStates.SingleAsync();
            Require(state.ConsecutiveFailures == 0 && state.TransientFailures == 2 && state.RetryThroughRowId == 0
                && state.LastProcessedRowId < source && state.CallsToday == 2 && state.Enabled,
                "old timeout failure counts are cleared once while cursor, enablement and durable quota survive upgrade");
        }
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("invalid output"));
        await env.Services.GetRequiredService<GroupMemoryLearningService>().RunGroupAsync("GROUP");
        using var read = env.Services.CreateScope();
        var upgraded = (await read.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync("GROUP"))!;
        Require(upgraded.ConsecutiveFailures == 1 && upgraded.LastProcessedRowId < source && upgraded.CallsToday == 3,
            "first invalid output after two legacy timeouts cannot trigger the old third-failure skip");
    }
}
