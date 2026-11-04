using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Core.Models.QQOfficial;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Services;
using Mutter.Web.Controllers;
using Mutter.Web.Models;

internal static partial class RuntimeChecks
{
    private static async Task GatewayHandoffAsync()
    {
        // 直接走真实入队和派发方法，只替换网络接入；不连接 QQ。
        using var client = new QqGatewayWebSocketClient(null!, Options.Create(new MutterOptions()), null!,
            NullLogger<QqGatewayWebSocketClient>.Instance);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var dispatch = typeof(QqGatewayWebSocketClient).GetMethod("DispatchAsync", flags)!
            .CreateDelegate<Func<QqDispatch, CancellationToken, Task>>(client);
        var enqueue = typeof(QqGatewayWebSocketClient).GetMethod("HandleDispatchAsync", flags)!;
        var getSeq = typeof(QqGatewayWebSocketClient).GetMethod("GetLastSeq", flags)!;
        var firstStarted = Signal(); var releaseFirst = Signal(); var releaseSecond = Signal();
        var completed = new List<int>();
        client.DispatchReceived += async (message, token) =>
        {
            if (message.Seq == 101) { firstStarted.SetResult(); await releaseFirst.Task.WaitAsync(token); }
            if (message.Seq == 102) await releaseSecond.Task.WaitAsync(token);
            completed.Add(message.Seq);
        };
        await using var queue = new GatewayDispatchQueue(dispatch, NullLogger.Instance, capacity: 1,
            drainTimeout: TimeSpan.FromSeconds(3));
        using var oldConnection = new CancellationTokenSource();
        Task Enqueue(int seq, CancellationToken ct) => (Task)enqueue.Invoke(client,
            [new GatewayPayload { Op = GatewayOp.Dispatch, T = "GROUP_AT_MESSAGE_CREATE", S = seq,
                D = JsonSerializer.SerializeToElement(new { id = $"SYNTHETIC-{seq}" }) }, ct, queue])!;
        try
        {
            await Enqueue(101, oldConnection.Token);
            await firstStarted.Task;
            await Enqueue(102, oldConnection.Token);
            var blocked = Enqueue(103, oldConnection.Token);
            Require(!blocked.IsCompleted, "a full handoff queue must apply backpressure");
            oldConnection.Cancel();
            try { await blocked; throw new InvalidOperationException("blocked enqueue must be cancelled"); }
            catch (OperationCanceledException) { }
            Require((int?)getSeq.Invoke(client, null) == 102, "resume must not skip an event that failed to enter the queue");

            // 新连接重用队列；旧连接取消不影响已经交给处理器的事件。
            var retried = Enqueue(103, default);
            releaseFirst.SetResult();
            await retried;
            var drain = queue.DisposeAsync().AsTask();
            Require(!drain.IsCompleted, "closing must wait for accepted events before unsubscribing handlers");
            releaseSecond.SetResult();
            await drain;
            Require(completed.SequenceEqual([101, 102, 103]) && (int?)getSeq.Invoke(client, null) == 103,
                "reconnect and close must hand off every accepted event exactly once, in order");
        }
        finally { releaseFirst.TrySetResult(); releaseSecond.TrySetResult(); }

        var slowStarted = Signal(); var cancelled = Signal();
        var slow = new GatewayDispatchQueue(async (_, token) =>
        {
            slowStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.TrySetResult(); }
        }, NullLogger.Instance, drainTimeout: TimeSpan.FromMilliseconds(50));
        await slow.EnqueueAsync(new QqDispatch("SYNTHETIC", null, 1, default), default);
        await slowStarted.Task;
        await slow.DisposeAsync();
        Require(cancelled.Task.IsCompleted, "forced shutdown must cancel an event handler after the drain deadline");
    }

    private static async Task HostShutdownAsync()
    {
        using var host = new HostBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<TimeProvider>(new TestClock());
                services.AddSingleton<ConversationWorkQueue>();
                services.AddHostedService(sp => sp.GetRequiredService<ConversationWorkQueue>());
            }).Build();
        var queue = host.Services.GetRequiredService<ConversationWorkQueue>();
        var started = Signal(); var release = Signal();
        var queuedRan = false;
        queue.TryEnqueue("g:SYNTHETIC", async ct => { started.SetResult(); await release.Task.WaitAsync(ct); });
        queue.TryEnqueue("g:SYNTHETIC", ct =>
        {
            ct.ThrowIfCancellationRequested(); queuedRan = true; return Task.CompletedTask;
        });
        await host.StartAsync();
        try
        {
            await started.Task;
            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            Require(!queue.Snapshot().Accepting && !queue.TryEnqueue("late", _ => Task.CompletedTask),
                "the real ApplicationStopping event must immediately close admission");
            release.SetResult();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await host.StopAsync(deadline.Token);
            Require(queuedRan && queue.Snapshot() is { Waiting: 0, Running: 0 }, "accepted work must drain without host cancellation");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task BudgetDefaultsAsync()
    {
        Require(new AiConfig().MaxInputTokens == 24576 && new AiConfigEntity().MaxInputTokens == 24576
            && new AiConfigInputDto().MaxInputTokens == 24576, "all new configuration entry points must share the default");
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await repo.SaveAiConfigAsync(env.Config() with { MaxInputTokens = 16384 });
        await db.EnsureSchemaUpgradedAsync();
        Require((await repo.GetAiConfigAsync())!.MaxInputTokens == 16384, "schema upgrade must preserve an existing input budget");
        await db.AiConfigs.ExecuteDeleteAsync();
        var response = await new ConfigController(db, scope.ServiceProvider.GetRequiredService<AiSearchCapabilityStore>()).GetAiConfig();
        Require(((OkObjectResult)response.Result!).Value is AiConfigDto { MaxInputTokens: 24576 },
            "an unconfigured installation must show the new default through the real API");
    }

    private static async Task RuntimeTaskOrderingAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var queue = env.Services.GetRequiredService<ConversationWorkQueue>();
        var release = Signal(); var userStarted = Signal();
        for (var i = 0; i < 60; i++)
            queue.TryEnqueue($"g:SYNTHETIC-BG-{i}", ct => release.Task.WaitAsync(ct), background: true);
        queue.TryEnqueue("g:SYNTHETIC-USER", ct => { userStarted.SetResult(); return release.Task.WaitAsync(ct); });
        await queue.StartAsync(default);
        try
        {
            await userStarted.Task;
            using var scope = env.Services.CreateScope();
            var response = (OkObjectResult)await new RuntimeController(queue,
                scope.ServiceProvider.GetRequiredService<AppDbContext>(), env.Images).Get(default);
            var tasks = JsonSerializer.SerializeToElement(response.Value).GetProperty("Queue").GetProperty("Tasks")
                .EnumerateArray().ToArray();
            Require(tasks.Length == 60 && tasks.Any(t => t.GetProperty("ConversationId").GetString() == "g:SYNTHETIC-USER")
                && tasks[0].GetProperty("State").GetString() == "running", "active tasks must be selected before limiting the list");
            Require(tasks.All(t => t.GetProperty("Steps").GetArrayLength() == 0), "polling must continue to omit task steps");
        }
        finally
        {
            release.TrySetResult();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await queue.StopAsync(deadline.Token);
        }
    }
}
