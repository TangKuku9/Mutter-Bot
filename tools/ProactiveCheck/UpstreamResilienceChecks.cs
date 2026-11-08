using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutter.Core.Configuration;
using Mutter.Core.EventHandlers;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.QQOfficial;
using Mutter.Core.Proactive;
using Mutter.Core.Services;
using Mutter.Infrastructure.Ai;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Services;

internal static partial class RuntimeChecks
{
    private static async Task CoalescedInputsAsync()
    {
        foreach (var isPrivate in new[] { false, true })
        {
            await using var env = await TestEnvironment.CreateAsync();
            using (var scope = env.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IConfigRepository>()
                    .SaveAiConfigAsync(env.Config() with { MaxContextMessages = 1 });
            var handler = env.Services.GetRequiredService<MessageEventHandler>();
            var queue = env.Services.GetRequiredService<ConversationWorkQueue>();
            var images = new ConcurrentQueue<string>();
            env.Images.Handler = (url, _) =>
            {
                images.Enqueue(url);
                return Task.FromResult<AiResolvedImage?>(new("image/png", [1, 2, 3], url));
            };
            QqDispatch Input(string id, string text, string name, bool image = false)
            {
                List<Attachment>? attachments = image
                    ? [new() { ContentType = "image/png", Url = $"https://synthetic.invalid/{id}.png" }] : null;
                return isPrivate
                    ? new("C2C_MESSAGE_CREATE", id, 1, JsonSerializer.SerializeToElement(new C2cMessageEvent
                    {
                        Id = id, Content = text, Attachments = attachments,
                        Author = new() { UserOpenid = "USER", Username = name }
                    }))
                    : new("GROUP_AT_MESSAGE_CREATE", id, 1, JsonSerializer.SerializeToElement(new GroupMessageEvent
                    {
                        Id = id, GroupOpenid = "GROUP", Content = text, Attachments = attachments,
                        Author = new() { MemberOpenid = name, Username = name }
                    }));
            }
            // 在 worker 启动前确定性合并两条，隔一个不可合并的任务后再入队未来消息。
            await handler.HandleDispatchAsync(Input("batch-b", "SYNTHETIC-B", "USER-B", true));
            await handler.HandleDispatchAsync(Input("batch-c", "SYNTHETIC-C", "USER-C", true));
            queue.TryEnqueue(isPrivate ? "u:USER" : "g:GROUP", _ => Task.CompletedTask);
            await handler.HandleDispatchAsync(Input("future-d", "SYNTHETIC-D", "USER-D"));
            Require(queue.Snapshot().Coalesced == 1, "B/C must share one pending reply");
            var calls = 0;
            env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply($"SYNTHETIC-REPLY-{++calls}"));
            await queue.StartAsync(default);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await queue.StopAsync(stop.Token);
            Require(env.Http.Requests.Count == 2 && env.Api.PassiveCalls == 2, "one reply per batch");
            using var payload = JsonDocument.Parse(env.Http.Requests[0].Body);
            var messages = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            var text = string.Join('\n', messages.SelectMany(m =>
            {
                var content = m.GetProperty("content");
                return content.ValueKind == JsonValueKind.String ? new[] { content.GetString()! }
                    : content.EnumerateArray().Where(p => p.GetProperty("type").GetString() == "text")
                        .Select(p => p.GetProperty("text").GetString()!).ToArray();
            }));
            foreach (var marker in new[] { "SYNTHETIC-B", "SYNTHETIC-C" })
                Require(text.Split(marker).Length == 2, $"{marker} must reach the real model exactly once");
            Require(text.Contains("[USER-B]") && text.Contains("[USER-C]") && !text.Contains("SYNTHETIC-D"),
                "batch must preserve authors and exclude future work, even with one context message");
            Require(messages[^1].GetProperty("content").EnumerateArray()
                .Count(p => p.GetProperty("type").GetString() == "image_url") == 2
                && images.Contains("https://synthetic.invalid/batch-b.png")
                && images.Contains("https://synthetic.invalid/batch-c.png"), "both inputs retain their images");
            using var check = env.Services.CreateScope();
            var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
            var reply = await db.OutgoingReplies.SingleAsync(r => r.Content == "SYNTHETIC-REPLY-1");
            var call = await db.AiCallRecords.SingleAsync(r => r.TriggerMessageId == "batch-c");
            Require(reply.ReplyToMessageId == "batch-c" && reply.AiOperationId == call.OperationId,
                "delivery and usage must belong to the latest batch input");
        }
    }

    private static async Task CoalescingAdmissionAsync()
    {
        using (var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, new TestClock(), 4, 1))
        {
            var expiryStarted = Signal(); var release = Signal(); var runs = 0;
            queue.TryEnqueue("hot", _ => throw new InvalidOperationException("expired work ran"),
                maxQueueAge: TimeSpan.Zero, coalesce: true, onExpired: async ct =>
                { expiryStarted.SetResult(); await release.Task.WaitAsync(ct); });
            await queue.StartAsync(default);
            await expiryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                Require(queue.TryEnqueue("hot", _ => { runs++; return Task.CompletedTask; }, coalesce: true)
                    == EnqueueResult.Accepted, "claimed expired work cannot absorb a new input");
            }
            finally { release.TrySetResult(); }
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await queue.StopAsync(stop.Token);
            Require(runs == 1 && queue.Snapshot().Coalesced == 0, "follow-up must actually run after expired notice");
        }
        using (var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, new TestClock(), 1, 1))
        {
            queue.TryEnqueue("hot", _ => Task.CompletedTask, coalesce: true);
            Require(queue.TryEnqueue("hot", _ => Task.CompletedTask, coalesce: true) == EnqueueResult.Coalesced,
                "merge consumes no capacity and must remain available when full");
            Require(queue.TryEnqueue("other", _ => Task.CompletedTask) == EnqueueResult.Rejected, "new work remains bounded");
        }
        using (var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, new TestClock(), 4, 1))
        {
            var batch = new ConversationReplyBatch(new() { MessageId = "0" }, new());
            queue.TryEnqueue("hot", _ => Task.CompletedTask, coalesce: true, replyBatch: batch);
            for (var i = 1; i < 64; i++)
                Require(queue.TryEnqueue("hot", _ => Task.CompletedTask, coalesce: true,
                    replyBatch: new(new() { MessageId = i.ToString() }, new())) == EnqueueResult.Coalesced,
                    "batch accepts bounded input");
            Require(queue.TryEnqueue("hot", _ => Task.CompletedTask, coalesce: true,
                replyBatch: new(new() { MessageId = "64" }, new())) == EnqueueResult.Accepted
                && batch.Messages.Count == 64, "full batch must create a separate task rather than lose input");
        }
    }

    private static async Task UpstreamTransportHealthAsync()
    {
        var clock = new TestClock();
        var health = new UpstreamHealthTracker(clock, new RuntimeOptions().Normalize());
        var http = new FakeHttp
        {
            Handler = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/v1beta/")
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("synthetic missing route") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"synthetic success\"}]}}]}") })
        };
        using var client = new HttpClient(http);
        var transport = new HttpModelTransport(client, NullLogger<HttpModelTransport>.Instance, clock: clock, health: health);
        var config = new Mutter.Core.Models.Config.AiConfig
        { ApiBaseUrl = "https://synthetic.invalid/v1", ApiKey = "synthetic", ModelName = "synthetic" };
        for (var i = 0; i < 6; i++)
        {
            var result = await transport.SendGeminiNativeBodyAsync(new(), config, default);
            Require(result.Content == "synthetic success" && !health.IsDegraded, "successful route fallback must stay healthy");
            clock.Advance(TimeSpan.FromMinutes(2));
        }
        Require(health.Snapshot() is { WindowFailure: 0, WindowSuccess: 6 }, "404 discovery is not an availability sample");
        foreach (var useDate in new[] { false, true })
        {
            var limited = new UpstreamHealthTracker(clock);
            var rateTransport = new HttpModelTransport(client, NullLogger<HttpModelTransport>.Instance, clock: clock, health: limited);
            var until = clock.GetUtcNow().AddMinutes(10);
            http.Handler = (_, _, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("synthetic limit") };
                response.Headers.RetryAfter = useDate ? new RetryConditionHeaderValue(until)
                    : new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
                return Task.FromResult(response);
            };
            await CaptureAsync(() => rateTransport.SendOpenAiAsync(new("s", [AiMessage.Plain("user", "u")]), config, null, default));
            Require(limited.Snapshot().DegradedUntil == until, "both Retry-After formats must preserve the server's full cooldown");
            for (var i = 0; i < 4; i++) limited.Record(true, 1);
            clock.Advance(TimeSpan.FromMinutes(9));
            Require(limited.IsDegraded && !limited.CanScheduleBackground, "successful traffic must not shorten Retry-After");
            clock.Advance(TimeSpan.FromMinutes(1));
            Require(!limited.IsDegraded, "healthy traffic may recover after the full server cooldown");
        }
    }

    private static async Task ProactiveRecoveryAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var observe = env.Services.GetRequiredService<ProactiveObserveService>();
        var queue = env.Services.GetRequiredService<ConversationWorkQueue>();
        var health = env.Services.GetRequiredService<UpstreamHealthTracker>();
        using var scheduler = new ProactiveChatHostedService(env.Services.GetRequiredService<IServiceScopeFactory>(),
            observe, env.Services.GetRequiredService<ProactiveWorkflow>(), queue, health,
            env.Services.GetRequiredService<RuntimeOptions>(), env.Clock, NullLogger<ProactiveChatHostedService>.Instance);
        var tick = typeof(ProactiveChatHostedService).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<CancellationToken, Task>>(scheduler);
        observe.Observe("GROUP", "SYNTHETIC", "synthetic topic", "synthetic-window");
        env.Clock.Advance(TimeSpan.FromSeconds(61));
        await tick(default); // 健康时排入的后台任务，执行前上游才故障。
        health.Record(false, 1, 500); health.Record(false, 1, 500);
        await queue.StartAsync(default);
        async Task Drain()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (queue.Snapshot() is { } state && (state.Waiting > 0 || state.Running > 0))
                await Task.Delay(5, timeout.Token);
        }
        await Drain();
        Require(env.Http.Requests.Count == 0 && observe.PendingWindows().Count == 1,
            "queued background work must recheck degradation without consuming its observation window");
        await tick(default);
        Require(queue.Snapshot().Waiting == 0, "cooldown must pause scheduling");
        env.Clock.Advance(TimeSpan.FromSeconds(61));
        var probeStarted = Signal(); var release = Signal();
        env.Http.Handler = async (_, _, ct) =>
        {
            probeStarted.TrySetResult(); await release.Task.WaitAsync(ct);
            return JsonReply("{\"reason\":\"synthetic decline\"}");
        };
        try
        {
            await tick(default);
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(health.IsDegraded && !health.CanScheduleBackground
                && !health.TryBeginBackgroundCall(out _), "only one recovery probe may be in flight");
        }
        finally { release.TrySetResult(); }
        await Drain();
        Require(!health.CanScheduleBackground, "completed probes are paced, not retried on each tick");
        // 完全没有前台调用，依靠三次后续主动探测累计四次成功，恢复常规调度。
        for (var i = 0; i < 3; i++)
        {
            observe.Observe("GROUP", "SYNTHETIC", "synthetic follow-up", $"synthetic-{i}");
            env.Clock.Advance(TimeSpan.FromSeconds(61));
            await tick(default);
            await Drain();
        }
        Require(env.Http.Requests.Count == 4 && !health.IsDegraded, "proactive-only traffic must recover without manual foreground calls");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await queue.StopAsync(stop.Token);
    }
}
