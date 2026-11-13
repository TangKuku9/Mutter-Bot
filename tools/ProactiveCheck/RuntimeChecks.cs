using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutter.Core;
using Mutter.Core.Configuration;
using Mutter.Core.EventHandlers;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Models.QQOfficial;
using Mutter.Core.Proactive;
using Mutter.Core.Services;
using Mutter.Infrastructure;
using Mutter.Infrastructure.Ai;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;
using Mutter.Web.Services;

/// <summary>只使用合成标识符、临时 SQLite 和假 HTTP，不读取部署数据或访问外部服务。</summary>
internal static partial class RuntimeChecks
{
    public static async Task<bool> RunAsync()
    {
        var ok = true;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("会话顺序、跨会话并行、有界接纳与排空", QueueOrderAsync),
            ("主动任务优先级、过期与停机取消", QueueLifecycleAsync),
            ("上游健康降级、滞回与恢复", UpstreamHealthAsync),
            ("同会话排队回复合并", CoalescingAsync),
            ("合并消息的真实上下文、图片与投递关联", CoalescedInputsAsync),
            ("过期任务领取与满队列合并", CoalescingAdmissionAsync),
            ("协议回退健康判定与 Retry-After 两种格式", UpstreamTransportHealthAsync),
            ("主动评估冷却探测与无前台流量恢复", ProactiveRecoveryAsync),
            ("单次尝试预算与健康上报", AttemptBudgetAsync),
            ("模型协议、结构化用量与错误重试政策", ModelContractsAsync),
            ("多模态搜索、配置快照和取消", ModelMultimodalAsync),
            ("最终请求预算与超长当前消息", BudgetAsync),
            ("投递未知、成功历史与用量去重", DeliveryAsync),
            ("发送成功后记账失败及重启恢复配额", ReceiptFailureAsync),
            ("主动消息分钟/日配额持久化", ActiveQuotaAsync),
            ("模型运行期间关闭开关/群参与/进入静默", FinalPolicyAsync),
            ("群级择机参数、预设保存与旧库继承", ProactiveParametersAsync),
            ("群级窗口调度、模型预算与最终发送限制", ProactiveGroupRuntimeAsync),
            ("群参数部分更新契约与无配置群首次保存", ProactiveGroupContractAsync),
            ("择机覆盖解码容错与预算兜底", ProactiveOverridesCodecAsync),
            ("择机调度静默、日限门禁与状态边界", ProactiveSchedulerGuardsAsync),
            ("外搜 Key 服务商归属与旧打码兼容", AiKeyOwnershipAsync),
            ("观察窗口快照的群级隐私与门禁边界", SnapshotPolicyAsync),
            ("排队消息历史边界与上一轮回复", TurnHistoryAsync),
            ("完整消息管线串行回复与持久化", MessagePipelineAsync),
            ("跨群主动日限的原子预占", ProactiveReservationAsync),
            ("旧模型升级幂等与数据保留", SchemaAsync),
            ("聊天历史查找、稳定分页、精确决策关联与旧库兼容", ChatTimelineAsync),
            ("10 万消息下的时间线去重规模", TimelineScaleAsync),
            ("实际调用账本、用户归属、失败和历史用量", UsageLedgerAsync),
            ("统计 SQL 聚合、缓存与大记录集", StatisticsPerformanceAsync),
            ("图片缓存容量、下载并发与取消隔离", ImageLimitsAsync),
            ("普通回复群记忆与各层开关", SharedMemoryAsync),
            ("群设置与记忆保存事务", AtomicGroupSettingsAsync),
            ("运行队列快照与调用过程关联", RuntimeSnapshotAsync),
            ("运行账本保留策略与累计统计事务", RetentionAsync),
            ("空回执归档幂等与决策阶段计数兼容", ReviewBoundariesAsync),
            ("网关重连缓冲、恢复序号与关闭排空", GatewayHandoffAsync),
            ("真实宿主停机关闭入口与排空", HostShutdownAsync),
            ("新装预算默认值与旧配置保留", BudgetDefaultsAsync),
            ("高负载运行列表保留正在执行任务", RuntimeTaskOrderingAsync)
        })
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(30)); Console.WriteLine($"PASS  {name}"); }
            catch (Exception ex) { ok = false; Console.WriteLine($"FAIL  {name}: {ex}"); }
        }
        return ok;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task QueueOrderAsync()
    {
        var clock = new TestClock();
        using var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, clock, 3, 2);
        var first = Signal(); var other = Signal(); var release = Signal(); var second = Signal();
        var order = new ConcurrentQueue<string>();
        Require(queue.TryEnqueue("a", async ct =>
        {
            order.Enqueue("a1-start"); first.SetResult(); await release.Task.WaitAsync(ct); order.Enqueue("a1-end");
        }) == EnqueueResult.Accepted, "first accepted");
        Require(queue.TryEnqueue("a", _ => { order.Enqueue("a2"); second.SetResult(); return Task.CompletedTask; }) == EnqueueResult.Accepted, "second accepted");
        Require(queue.TryEnqueue("b", _ => { other.SetResult(); return Task.CompletedTask; }) == EnqueueResult.Accepted, "other accepted");
        Require(queue.TryEnqueue("c", _ => Task.CompletedTask) == EnqueueResult.Rejected, "capacity must include running and pending work");
        await queue.StartAsync(default);
        await Task.WhenAll(first.Task, other.Task).WaitAsync(TimeSpan.FromSeconds(3));
        Require(!second.Task.IsCompleted, "same conversation must not run concurrently");
        release.SetResult();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await queue.StopAsync(deadline.Token);
        Require(order.ToArray().SequenceEqual(new[] { "a1-start", "a1-end", "a2" }), "conversation order");
        Require(queue.TryEnqueue("d", _ => Task.CompletedTask) == EnqueueResult.Rejected, "stop must close admission");
    }

    private static async Task QueueLifecycleAsync()
    {
        var clock = new TestClock();
        using (var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, clock, 10, 1))
        {
            var order = new List<string>(); var expired = false;
            queue.TryEnqueue("bg", _ => { order.Add("background"); return Task.CompletedTask; }, background: true);
            queue.TryEnqueue("fg", _ => { order.Add("foreground"); return Task.CompletedTask; });
            queue.TryEnqueue("old", _ => throw new InvalidOperationException("expired work executed"),
                maxQueueAge: TimeSpan.Zero, onExpired: _ => { expired = true; return Task.CompletedTask; });
            await queue.StartAsync(default);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await queue.StopAsync(stop.Token);
            Require(expired && order.SequenceEqual(new[] { "foreground", "background" }), "priority and expiry");
        }
        using (var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, clock))
        {
            var bgStarted = Signal(); var fgStarted = Signal(); var release = Signal(); var bg2Started = false;
            queue.TryEnqueue("bg1", async ct => { bgStarted.SetResult(); await release.Task.WaitAsync(ct); }, background: true);
            queue.TryEnqueue("bg2", _ => { bg2Started = true; return Task.CompletedTask; }, background: true);
            await queue.StartAsync(default);
            await bgStarted.Task;
            queue.TryEnqueue("fg", _ => { fgStarted.SetResult(); return Task.CompletedTask; });
            await fgStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!bg2Started, "background must leave foreground execution slots available");
            release.SetResult();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await queue.StopAsync(stop.Token);
        }
        using (var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, clock))
        {
            var started = Signal(); var cancelled = Signal();
            queue.TryEnqueue("slow", async ct =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { cancelled.SetResult(); }
            });
            await queue.StartAsync(default); await started.Task;
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            await queue.StopAsync(stop.Token);
            Require(cancelled.Task.IsCompleted && queue.ExecuteTask!.IsCompleted, "stop must await cancelled workers");
        }
        using (var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, clock, 10, 1))
        {
            using var host = new CancellationTokenSource();
            var started = Signal(); var release = Signal();
            var inFlightCancelled = false; var queuedCancelled = false; var queuedRan = false;
            queue.TryEnqueue("a", async ct =>
            {
                started.SetResult();
                await release.Task;
                inFlightCancelled = ct.IsCancellationRequested;
            });
            queue.TryEnqueue("a", ct =>
            {
                queuedCancelled = ct.IsCancellationRequested;
                queuedRan = true;
                return Task.CompletedTask;
            });
            await queue.StartAsync(host.Token);
            await started.Task;

            // 宿主停机先触发 ApplicationStopping，然后才调用 StopAsync。
            host.Cancel();
            await Task.Delay(50);
            Require(queue.TryEnqueue("late", _ => Task.CompletedTask) == EnqueueResult.Rejected, "host stop must close admission");
            release.SetResult();

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await queue.StopAsync(stop.Token);
            Require(queuedRan && !inFlightCancelled && !queuedCancelled,
                "accepted work must drain with the queue-owned token");
        }
    }

    private static Task UpstreamHealthAsync()
    {
        var clock = new TestClock();
        var options = new RuntimeOptions().Normalize();   // 默认阈值：窗口 10，失败阈值 3，恢复 4/5
        var health = new UpstreamHealthTracker(clock, options);
        health.Record(false, 40_000);
        health.Record(true, 20_000);
        health.Record(false, 50_000);
        Require(!health.IsDegraded, "零星失败（夹着成功）不降级");
        health.Record(true, 15_000);
        health.Record(false, 60_000);
        Require(health.IsDegraded, "窗口内失败达到阈值判定降级");
        clock.Advance(TimeSpan.FromSeconds(61));
        Require(health.IsDegraded, "没有足够成功样本时不自动恢复（滞回）");
        health.Record(true, 16_000);
        health.Record(true, 16_000);
        Require(health.IsDegraded, "近 5 次成功不足 4 次不恢复");
        health.Record(true, 17_000);
        health.Record(true, 17_000);
        Require(!health.IsDegraded, "近 5 次中成功 4 次后恢复");
        var snapshot = health.Snapshot();
        Require(!snapshot.Degraded && snapshot.WindowSuccess > 0 && snapshot.P50Ms > 0 && snapshot.P95Ms >= snapshot.P50Ms,
            "快照带窗口成败计数与延迟分位（面板展示用）");

        var spread = new UpstreamHealthTracker(clock, options);
        spread.Record(false, 1);
        spread.Record(false, 1);
        Require(spread.IsDegraded, "连续两次失败立即降级（不等窗口攒满）");

        var rate = new UpstreamHealthTracker(clock, options);
        rate.Record(false, 100, 429, TimeSpan.FromSeconds(5));
        Require(rate.Snapshot() is { Degraded: true, DegradedUntil: { } until1 }
            && (until1 - clock.GetUtcNow()).TotalSeconds is > 58 and < 61,
            "429 的 Retry-After 低于下限时按最短降级时长兜底");
        rate.Record(false, 100, 500);
        clock.Advance(TimeSpan.FromMinutes(5));   // 远超最短降级时长：恢复只看近期成功样本
        rate.Record(true, 10);
        rate.Record(true, 10);
        rate.Record(true, 10);
        Require(rate.IsDegraded, "过了最短降级时长，近 5 次成功仍不足 4 次不恢复");
        rate.Record(true, 10);
        Require(!rate.IsDegraded, "近 5 次中成功 4 次后恢复");
        return Task.CompletedTask;
    }

    private static async Task CoalescingAsync()
    {
        var clock = new TestClock();
        // 阶段一：排队中的回复吸收后续 @，并刷新过期时间；被合并的 @ 不产生任何超时提示。
        using (var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, clock, 4, 1))
        {
            var expiredNotice = 0;
            Require(queue.TryEnqueue("g:hot", _ => Task.CompletedTask, maxQueueAge: TimeSpan.FromSeconds(100),
                onExpired: _ => { expiredNotice++; return Task.CompletedTask; }, coalesce: true) == EnqueueResult.Accepted,
                "第一条 @ 正常排队");
            clock.Advance(TimeSpan.FromSeconds(90));
            Require(queue.TryEnqueue("g:hot", _ => Task.CompletedTask, maxQueueAge: TimeSpan.FromSeconds(100), coalesce: true)
                == EnqueueResult.Coalesced, "第二条 @ 合并进已有排队回复");
            clock.Advance(TimeSpan.FromSeconds(90));   // t=180：早已越过原始 100s 有效期，靠刷新才活着
            await queue.StartAsync(default);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await queue.StopAsync(stop.Token);
            Require(expiredNotice == 0, "合并刷新过期时间：排队回复仍被执行，且没有多余的超时提示");
            Require(queue.Snapshot() is { Coalesced: 1, Rejected: 0 }, "合并计数进快照");
        }
        // 阶段二：管理员指令不可合并。
        using (var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, clock, 4, 1))
        {
            Require(queue.TryEnqueue("g:x", _ => Task.CompletedTask) == EnqueueResult.Accepted, "管理员指令入队");
            Require(queue.TryEnqueue("g:x", _ => Task.CompletedTask, coalesce: true) == EnqueueResult.Accepted,
                "会话最新排队项不可合并时，AI 回复照常单独排队");
            Require(queue.Snapshot().Coalesced == 0, "管理员指令不会被吸收");
        }
        // 阶段三：正在执行的一项不能被并进去。
        using (var queue = new ConversationWorkQueue(NullLogger<ConversationWorkQueue>.Instance, clock, 4, 1))
        {
            var running = Signal(); var release = Signal();
            queue.TryEnqueue("g:y", async ct => { running.SetResult(); await release.Task.WaitAsync(ct); }, coalesce: true);
            await queue.StartAsync(default);
            await running.Task;
            Require(queue.TryEnqueue("g:y", _ => Task.CompletedTask, coalesce: true) == EnqueueResult.Accepted,
                "正在执行的回复不能被合并，新 @ 正常排队");
            release.TrySetResult();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await queue.StopAsync(stop.Token);
        }
    }

    private static async Task AttemptBudgetAsync()
    {
        // 挂死的上游：尝试在单次预算处中断并按失败上报，而不是耗光整条根期限。
        AiCallScope.Configure(TimeSpan.FromSeconds(120));
        try
        {
            var health = new UpstreamHealthTracker(new TestClock(), new RuntimeOptions
            {
                HealthMinDegradeSeconds = 0
            });
            var hanging = new FakeHttp
            {
                Handler = async (_, _, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return null!;
                }
            };
            var transport = new HttpModelTransport(new HttpClient(hanging), NullLogger<HttpModelTransport>.Instance,
                health: health, runtime: new RuntimeOptions
                {
                    AiTimeoutSeconds = 120, AiPerAttemptTimeoutSeconds = 3, DegradedPerAttemptTimeoutSeconds = 1
                });
            var config = new AiConfig { ApiBaseUrl = "https://upstream.test", ModelName = "synthetic-model", ApiKey = "k" };
            var request = new AiChatRequest("system", new[] { new AiMessage("user", "hello", []) });

            var firstAt = Stopwatch.GetTimestamp();
            var firstError = await CaptureAsync(() => transport.SendOpenAiAsync(request, config, null, CancellationToken.None));
            Require(firstError?.Message.Contains("timeout") == true, "挂起的尝试按超时分类");
            Require(Stopwatch.GetElapsedTime(firstAt) < TimeSpan.FromSeconds(10),
                "尝试预算在单次上限处中断（而不是耗光 120s 根期限）");
            Require(!health.IsDegraded, "单次挂起只是失败，未达降级条件");

            await CaptureAsync(() => transport.SendOpenAiAsync(request, config, null, CancellationToken.None));
            Require(health.IsDegraded, "连续两次挂起判定上游降级");

            var degradedAt = Stopwatch.GetTimestamp();
            await CaptureAsync(() => transport.SendOpenAiAsync(request, config, null, CancellationToken.None));
            Require(Stopwatch.GetElapsedTime(degradedAt) < TimeSpan.FromSeconds(2.5),
                "降级期间单次尝试上限进一步收紧，坏尝试更快腾出槽位");
            Require(health.Snapshot() is { WindowSuccess: 0, WindowFailure: 3 }, "每次尝试都如实进健康窗口");
        }
        finally
        {
            AiCallScope.Configure(TimeSpan.FromSeconds(120));
        }

        // 根期限剩余预算可读，且随时间消耗（给单次尝试取 min(上限, 剩余) 用）。
        AiCallScope.Configure(TimeSpan.FromSeconds(30));
        using (var scope = AiCallScope.Begin(CancellationToken.None, "reply"))
        {
            Require(scope.Remaining > TimeSpan.FromSeconds(28), "根期限剩余预算可读");
        }
        AiCallScope.Configure(TimeSpan.FromSeconds(120));
        using (AiCallScope.Begin(CancellationToken.None, "reply"))
        {
            Require(AiCallScope.Current!.Remaining > TimeSpan.FromSeconds(118), "配置下发能改变根期限");
        }
        AiCallScope.Configure(TimeSpan.FromSeconds(120));
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static async Task ModelContractsAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ai = env.Services.GetRequiredService<IAiChatService>();
        var structured = env.Services.GetRequiredService<IStructuredAiService>();
        var config = env.Config();
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("你好", 11, 7));
        var reply = await ai.ChatAsync("system", [("user", "hello")], config);
        Require(reply.Content == "你好" && reply.TotalTokens == 18, "response mapping");
        using (var payload = JsonDocument.Parse(env.Http.Requests.Last().Body))
        {
            var root = payload.RootElement;
            Require(root.GetProperty("model").GetString() == "synthetic-model", "model mapping");
            Require(root.GetProperty("max_tokens").GetInt32() == 512, "output limit mapping");
            Require(root.GetProperty("messages")[0].GetProperty("role").GetString() == "system", "system role");
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError })
        {
            var before = env.Http.Requests.Count;
            env.Http.Handler = (_, _, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("synthetic error") });
            var failed = await structured.GetStructuredOutcomeAsync<ProactiveVerdict>("system", "user", config);
            Require(failed.FailureKind == StructuredAiFailureKind.ApiError && env.Http.Requests.Count == before + 1,
                $"{status} must not trigger a format fallback");
        }
        env.Http.Handler = (_, _, _) => throw new HttpRequestException("synthetic transport interruption");
        var count = env.Http.Requests.Count;
        await structured.GetStructuredOutcomeAsync<ProactiveVerdict>("s", "u", config);
        Require(env.Http.Requests.Count == count + 1, "unknown transport outcome must not be replayed");

        var sequence = 0;
        env.Http.Handler = (_, _, _) => Task.FromResult(Interlocked.Increment(ref sequence) == 1
            ? JsonReply("unparseable", 13, 5) : JsonReply("{\"line\":\"hi\"}", 17, 7));
        var repaired = await structured.GetStructuredOutcomeAsync<ProactiveVerdict>("s", "u", config);
        Require(repaired.Result?.Data.Line == "hi" && repaired.PromptTokens == 30 && repaired.CompletionTokens == 12,
            "structured fallback must sum both attempts exactly once");
        sequence = 0;
        // 关掉联网搜索：400 在启用搜索时会先走“退回不检索”的重试，测不出格式分类本身的差异。
        var formatOnly = config with { WebSearchEnabled = false };
        env.Http.Handler = (_, _, _) => Task.FromResult(++sequence == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("response_format json_object unsupported") }
            : JsonReply("{\"reason\":\"没有接话必要\"}"));
        var unsupported = await structured.GetStructuredOutcomeAsync<ProactiveVerdict>("s", "u", formatOnly);
        Require(unsupported.Result is not null && sequence == 2, "explicit JSON mode rejection may fall back");
        // “请求体不是合法 JSON”之类的 4xx 与 response_format 无关：不能当成格式不支持再打一次。
        var beforeMalformed = env.Http.Requests.Count;
        env.Http.Handler = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("We could not parse the JSON body of your request")
        });
        var malformed = await structured.GetStructuredOutcomeAsync<ProactiveVerdict>("s", "u", formatOnly);
        Require(malformed.FailureKind == StructuredAiFailureKind.ApiError && env.Http.Requests.Count == beforeMalformed + 1,
            "unrelated json errors must not masquerade as a format rejection");
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{}"));
        Require((await structured.GetStructuredOutcomeAsync<ProactiveVerdict>("s", "u", config)).FailureKind
            == StructuredAiFailureKind.Unparsable, "empty object is not an explicit decline");
        count = env.Http.Requests.Count;
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{\"line\":", finish: "length"));
        var truncated = await structured.GetStructuredOutcomeAsync<ProactiveVerdict>("s", "u", config);
        Require(truncated.FailureKind == StructuredAiFailureKind.Truncated && env.Http.Requests.Count == count + 1,
            "truncation must not retry with unchanged output budget");
    }

    private static async Task ModelMultimodalAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var ai = env.Services.GetRequiredService<IAiChatService>();
        var config = env.Config() with
        {
            WebSearchEnabled = true, WebSearchEndpoint = "openai", WebSearchShape = "enable_search"
        };
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("看到了"));
        await ai.ChatWithImagesAsync("system", "图里是什么", ["synthetic://image"], config, "json_object");
        using (var payload = JsonDocument.Parse(env.Http.Requests.Last().Body))
        {
            var root = payload.RootElement;
            Require(root.GetProperty("enable_search").GetBoolean(), "custom search parameter must survive adapter");
            Require(root.GetProperty("response_format").GetProperty("type").GetString() == "json_object", "JSON mode mapping");
            Require(root.GetProperty("messages")[1].GetProperty("content")[1].GetProperty("image_url")
                .GetProperty("url").GetString()!.StartsWith("data:image/png;base64,"), "image bytes must survive adapter");
        }
        var imageGate = Signal(); var releaseImage = Signal();
        env.Images.Handler = async (url, ct) =>
        {
            imageGate.SetResult(); await releaseImage.Task.WaitAsync(ct); return new AiResolvedImage("image/png", [1, 2, 3], url);
        };
        var original = env.Config();
        var pending = ai.ChatWithImagesAsync("system", "user", ["synthetic://snapshot"], original);
        await imageGate.Task;
        original.ModelName = "changed-model"; original.ApiKey = "changed-synthetic-key"; original.ApiBaseUrl = "https://changed.invalid/v1";
        releaseImage.SetResult(); await pending;
        Require(env.Http.Requests.Last().Url.Host == "example.invalid" && env.Http.Requests.Last().Authorization == "Bearer synthetic-key",
            "in-flight request must retain endpoint and credential snapshot");
        await ai.ChatAsync("system", [("user", "new turn")], original);
        Require(env.Http.Requests.Last().Url.Host == "changed.invalid" && env.Http.Requests.Last().Body.Contains("changed-model"),
            "next request must use updated config");
        env.Images.Handler = null;
        var stages = 0;
        env.Http.Handler = (_, _, _) => Task.FromResult(++stages == 1
            ? JsonReply("图片描述：测试图片\n检索词：测试主题", 10, 2) : JsonReply("最终回答", 20, 3));
        config.WebSearchImageCapable = false;
        config.WebSearchImageMode = AiConfig.WebSearchImageModeTwoStage;
        var twoStage = await ai.ChatWithImagesAsync("system", "user", ["synthetic://image"], config);
        Require(stages == 2 && twoStage.TotalTokens == 35, "two-stage usage must include description and final response");
        config.WebSearchEndpoint = "gemini"; config.WebSearchShape = "gemini_native";
        config.WebSearchImageCapable = true; config.WebSearchImageMode = AiConfig.WebSearchImageModeAuto;
        env.Http.Handler = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"native\"}]},\"finishReason\":\"MAX_TOKENS\"}],\"usageMetadata\":{\"promptTokenCount\":9,\"candidatesTokenCount\":5}}")
        });
        var native = await ai.ChatWithImagesAsync("system", "user", ["synthetic://image"], config);
        Require(native.FinishReason == "length" && native.TotalTokens == 14, "Gemini truncation and usage mapping");
        Require(env.Http.Requests.Last().Body.Contains("google_search") && env.Http.Requests.Last().Body.Contains("inline_data"),
            "native search and image must coexist");
        env.Http.Handler = async (_, _, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return JsonReply("unreachable"); };
        var count = env.Http.Requests.Count;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        try { await ai.ChatAsync("system", [("user", "cancel")], env.Config(), cancel.Token); throw new InvalidOperationException("cancel ignored"); }
        catch (OperationCanceledException) { }
        Require(env.Http.Requests.Count == count + 1, "cancel must not replay request");
    }

    private static async Task BudgetAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var config = env.Config() with { MaxInputTokens = 512 };
        var request = new AiChatRequest(new string('系', 100),
            [AiMessage.Plain("user", new string('旧', 300)), AiMessage.Plain("assistant", new string('答', 200)), AiMessage.Plain("user", "当前问题")]);
        var fitted = AiRequestBudget.Fit(request, config);
        Require(AiRequestBudget.Estimate(fitted) <= 512 && fitted.Messages.Last().Text == "当前问题", "final request must fit");
        Require(fitted.Messages.Count == 2, "retain all history that still fits instead of jumping to a fixed floor");
        var ai = env.Services.GetRequiredService<IAiChatService>();
        var before = env.Http.Requests.Count;
        foreach (var system in new[] { "s", new string('系', 700) })
        {
            try { await ai.ChatAsync(system, [("user", new string('长', 600))], config); throw new InvalidOperationException("oversize accepted"); }
            catch (AiInputTooLargeException) { }
        }
        Require(env.Http.Requests.Count == before, "oversize must fail before paid model invocation");
        Require(ContextBuilder.InputTokenBudget(512) == ContextBuilder.InputTokenBudget(2048), "input and output budgets independent");

        // 小窗口模型下，择机输出上限必须给输入留空间，不能把预算压成 0。
        var evalTokens = AiRequestBudget.ClampOutputTokens(8192, 4096);
        Require(evalTokens == 2560 && AiRequestBudget.InputBudget(evalTokens, 4096) >= AiRequestBudget.MinInputTokens,
            "output ceiling must preserve the minimum input budget");

        // 图片超限时先降级为纯文本重试，正文能放下就不该整轮失败。
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("看到了"));
        var imageCount = env.Http.Requests.Count;
        var imageConfig = config with { ContextWindowTokens = 4096, MaxInputTokens = 2048, MaxTokens = 512 };
        var downgraded = await ai.ChatWithImagesAsync("system", "看图", ["synthetic://image"], imageConfig);
        Require(downgraded.Content == "看到了" && env.Http.Requests.Count == imageCount + 1,
            "oversize images must retry once without images");
        Require(!env.Http.Requests.Last().Body.Contains("image_url"), "retry must actually drop image payloads");
    }

    private static OutgoingReply Reply(string group = "GROUP", bool proactive = false) => new()
    {
        GroupId = group, BotId = "BOT", ReplyToMessageId = "synthetic-message", Content = "合成回复",
        IsProactive = proactive, Model = "synthetic-model", PromptTokens = 11, CompletionTokens = 7
    };

    private static async Task DeliveryAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var dispatcher = env.Services.GetRequiredService<ReplyDispatcher>();
        env.Api.Passive = _ => throw new HttpRequestException("synthetic lost receipt");
        var unknown = await dispatcher.SendAsync(Reply(), true);
        Require(unknown.State == DeliveryStates.Unknown && env.Api.ActiveCalls == 0, "uncertain passive result must not fall back");
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Require(!await db.ChatMessages.AnyAsync(), "unsent content must not enter history");
            Require(await scope.ServiceProvider.GetRequiredService<IMessageRepository>().SumTokensAsync() == 18, "failed delivery usage retained");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var beforeCalls = env.Api.PassiveCalls;
            var skipped = await dispatcher.SendAsync(Reply(), true, cancelled.Token);
            Require(skipped.State == DeliveryStates.Skipped
                && skipped.Detail == ProactiveOutcomes.SkippedCancelled
                && env.Api.PassiveCalls == beforeCalls,
                "pre-send cancellation must be skipped, not unknown");
        }
        Require(ProactiveOutcomes.FromDeliveryDetail(ProactiveOutcomes.SkippedCancelled)
            == ProactiveOutcomes.SkippedCancelled, "known outcome must survive");
        Require(ProactiveOutcomes.FromDeliveryDetail("raw platform error")
            == ProactiveOutcomes.FailedApiError, "raw platform text must not become an outcome code");
        env.Api.Passive = null;
        var outgoing = Reply();
        var sent = await dispatcher.SendAsync(outgoing, true);
        Require(sent.Sent, "success result");
        await env.Services.GetRequiredService<IOutgoingReplyStore>().CompleteAsync(outgoing, sent, default);
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Require(await db.ChatMessages.CountAsync() == 1, "receipt completion idempotent");
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            Require(await repo.SumTokensAsync() == 36 && (await repo.GetAiUsageSummaryAsync()).LegacyCount == 2
                && await repo.CountAiCallsAsync() == 0, "legacy reply usage is preserved without inventing physical call counts");
            Require((await repo.GetDailyTokenStatsAsync(3650)).Sum(s => s.TotalTokens) == 36, "usage chart includes failed delivery");
        }
    }

    private static async Task ReceiptFailureAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var real = env.Services.GetRequiredService<IOutgoingReplyStore>();
        var failedStore = new ReceiptFailingStore(real);
        var dispatcher = new ReplyDispatcher(env.Api, env.Services.GetRequiredService<IActiveMessageService>(),
            env.Services.GetRequiredService<ReplySeqTracker>(), failedStore, env.Services.GetRequiredService<IServiceScopeFactory>(),
            env.Clock, NullLogger<ReplyDispatcher>.Instance);
        var result = await dispatcher.SendAsync(Reply(proactive: true), true);
        Require(result.Sent && env.Api.PassiveCalls == 1, "receipt failure must not reverse platform success");
        var restarted = new OutgoingReplyStore(env.Services.GetRequiredService<IServiceScopeFactory>(), env.Clock);
        var limits = new ProactiveChatConfig { Enabled = true, CooldownMinutes = 1, MaxPerGroupPerDay = 1 }.Normalize();
        Require(await restarted.CheckProactiveAsync("GROUP", limits, default) == ProactiveOutcomes.SkippedCooldown,
            "pending intent must restore cooldown after restart");
        env.Clock.Advance(TimeSpan.FromMinutes(2));
        Require(await restarted.CheckProactiveAsync("GROUP", limits, default) == ProactiveOutcomes.SkippedGroupCap,
            "pending intent must conservatively restore daily quota after restart");
    }

    private static async Task ActiveQuotaAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using (var scope = env.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IConfigRepository>().SaveProactiveConfigAsync(new ProactiveChatConfig
            {
                Enabled = true, ActivePerGroupPerMinute = 1, ActivePerGroupPerDay = 2, ActivePerAccountPerMinute = 60
            });
        ActiveMessageService Restart() => new(env.Api, env.Services.GetRequiredService<IServiceScopeFactory>(),
            env.Services.GetRequiredService<RichUiCapability>(), NullLogger<ActiveMessageService>.Instance, env.Clock);
        Require((await Restart().SendGroupActiveAsync("GROUP", "first")).Success, "first active send");
        Require((await Restart().SendGroupActiveAsync("GROUP", "second")).Reason == "rate_limited", "minute survives restart");
        env.Clock.Advance(TimeSpan.FromSeconds(61));
        Require((await Restart().SendGroupActiveAsync("GROUP", "second")).Success, "minute window expires");
        env.Clock.Advance(TimeSpan.FromSeconds(61));
        Require((await Restart().SendGroupActiveAsync("GROUP", "third")).Reason == "rate_limited", "daily survives restart");
        Require((await Restart().GetGroupQuotaStatusAsync("GROUP")).UsedToday == 2, "panel reads durable attempts");
        env.Clock.Advance(TimeSpan.FromDays(1));
        env.Faults.FailMonthlyReceipt = true;
        Require((await Restart().SendGroupActiveAsync("GROUP", "new day")).Success, "monthly receipt failure must preserve send success");
        Require(env.Api.ActiveCalls == 3, "rate-limited attempts must not touch platform");
    }

    private static async Task SnapshotPolicyAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{\"reason\":\"无需接话\"}"));
        var workflow = env.Services.GetRequiredService<ProactiveWorkflow>();
        var now = env.Clock.GetUtcNow().UtcDateTime;

        // 默认群 TriggerMode=0：只回被 @ 的消息，观察窗口里的群友原话不能借决策留痕落库。
        await workflow.RunAsync(Window("window-0", now), default);
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var log = await db.ProactiveChatLogs.SingleAsync();
            Require(log.Outcome == ProactiveOutcomes.SkippedAiDeclined && log.WindowSnapshotJson is null,
                "mode 0 groups must not persist observed chatter in decision snapshots");
        }

        using (var scope = env.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            var group = (await repo.GetGroupConfigAsync("GROUP"))!;
            group.TriggerMode = 2;
            await repo.SaveGroupConfigAsync(group);
        }
        await workflow.RunAsync(Window("window-2", now), default);
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var logs = await db.ProactiveChatLogs.OrderBy(l => l.Id).ToListAsync();
            var snapshot = logs.Count == 2 ? logs[1].WindowSnapshotJson : null;
            Require(logs.Count == 2 && snapshot is not null && snapshot.Contains("合成窗口"),
                "mode 2 groups keep the exact window used for a model decision");
        }

        // 附图张数应记“实际随请求附上的”，不是窗口里出现过几张图。
        using (var scope = env.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IConfigRepository>().SaveProactiveConfigAsync(
                new ProactiveChatConfig { Enabled = true, QuietStartHour = 0, QuietEndHour = 0, MaxAttachedImages = 0 });
        await workflow.RunAsync(ImageWindow("window-image", now), default);
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var log = await db.ProactiveChatLogs.OrderBy(l => l.Id).LastAsync();
            Require(log.ImageCount == 0 && log.WindowSnapshotJson is not null,
                "image count reflects what was actually attached, not what appeared in the window");
        }

        // 异常兜底时 images/记忆没有拿到实际值：写 DetailVersion=0，详情显示未知而不是 0。
        using (var scope = env.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IConfigRepository>().SaveProactiveConfigAsync(
                new ProactiveChatConfig { Enabled = true, QuietStartHour = 0, QuietEndHour = 0, MaxAttachedImages = 1 });
        env.Images.Handler = (_, _) => throw new InvalidOperationException("synthetic image failure");
        await workflow.RunAsync(ImageWindow("window-error", now), default);
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var log = await db.ProactiveChatLogs.OrderBy(l => l.Id).LastAsync();
            var detail = await new ChatTimelineReader(db).DecisionAsync(log.Id);
            Require(log.Outcome == ProactiveOutcomes.FailedAiCall && log.DetailVersion == 0
                && log.AiEvaluated == false
                && detail is { ImageCount: null, MemoryCount: null, SearchUsed: null },
                "exception fallbacks must leave diagnostics unknown instead of inventing zeroes");
        }

        env.Services.GetRequiredService<ProactiveObserveService>().MarkSent("GROUP", now);
        await workflow.RunAsync(Window("window-3", now), default);
        using (var scope = env.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var log = await db.ProactiveChatLogs.OrderBy(l => l.Id).LastAsync();
            Require(log.Outcome == ProactiveOutcomes.SkippedCooldown && log.WindowSnapshotJson is null,
                "cooldown skips stay out of snapshot storage even for mode 2 groups");
            Require(log.AiEvaluated == false, "a cooldown gate before model evaluation records the actual phase");
        }

        static ProactiveWindow Window(string id, DateTime now) => new()
        {
            GroupId = "GROUP", LastMessageId = id, LastMessageUtcAt = now,
            Messages = [new ObservedMessage("虚拟用户", "合成窗口", id, now)]
        };
        static ProactiveWindow ImageWindow(string id, DateTime now) => new()
        {
            GroupId = "GROUP", LastMessageId = id, LastMessageUtcAt = now,
            Messages = [new ObservedMessage("虚拟用户", "合成窗口", id, now, ["https://example.invalid/a.png"])]
        };
    }

    private static async Task FinalPolicyAsync()
    {
        foreach (var change in new[] { "global", "group", "quiet" })
        {
            await using var env = await TestEnvironment.CreateAsync();
            if (change == "quiet") env.Clock.Set(new DateTimeOffset(2026, 11, 3, 14, 59, 55, TimeSpan.Zero));
            using (var scope = env.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IConfigRepository>().SaveProactiveConfigAsync(new ProactiveChatConfig
                {
                    Enabled = true, QuietStartHour = 23, QuietEndHour = 8
                });
            env.Http.Handler = async (_, _, ct) =>
            {
                using var scope = env.Services.CreateScope();
                var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                if (change == "global") await configs.SaveProactiveConfigAsync(new ProactiveChatConfig { Enabled = false }, ct);
                if (change == "group") await configs.SaveGroupConfigAsync(new GroupConfig { GroupId = "GROUP", IsEnabled = true, AiProactiveEnabled = false }, ct);
                if (change == "quiet") env.Clock.Advance(TimeSpan.FromSeconds(10));
                return JsonReply("{\"line\":\"接一句\",\"reason\":\"合成判定\"}");
            };
            var now = env.Clock.GetUtcNow().UtcDateTime;
            await env.Services.GetRequiredService<ProactiveWorkflow>().RunAsync(new ProactiveWindow
            {
                GroupId = "GROUP", LastMessageId = "window-message", LastMessageUtcAt = now,
                Messages = [new ObservedMessage("虚拟用户", "合成话题", "window-message", now)]
            }, default);
            Require(env.Api.PassiveCalls == 0 && env.Api.ActiveCalls == 0, $"final policy must block {change}");
            using var checkScope = env.Services.CreateScope();
            var log = await checkScope.ServiceProvider.GetRequiredService<AppDbContext>().ProactiveChatLogs.SingleAsync();
            Require(log.Outcome == (change == "quiet" ? ProactiveOutcomes.SkippedQuietHours : ProactiveOutcomes.SkippedDisabled),
                $"final policy reason {change}: {log.Outcome}");
            Require(log.AiEvaluated == true && env.Http.Requests.Count > 0,
                $"the completed model phase survives final policy blocking: {change}");
            // 状态接口按真实当前日统计，固定时钟只负责复现运行期间的策略切换。
            log.EvaluatedAt = DateTime.UtcNow;
            await checkScope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync();
            Require(await ReadTodayDecisionsAsync(checkScope.ServiceProvider) == 1,
                $"today decisions include model work blocked at delivery: {change}");
        }
    }

    private static async Task TurnHistoryAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var time = DateTime.UtcNow.AddMinutes(-1);
        await repo.SaveMessagesAsync(new[]
        {
            new ChatMessage { MessageId = "current", ChatType = 1, UserId = "USER", UserName = "User", PlainText = "第二问", CreatedAt = time },
            new ChatMessage { MessageId = "previous-reply", ChatType = 1, UserId = "BOT", PeerUserId = "USER", PlainText = "第一问的回复", IsBotMessage = true, AiModelUsed = "test", CreatedAt = time.AddSeconds(1) },
            new ChatMessage { MessageId = "future", ChatType = 1, UserId = "USER", UserName = "User", PlainText = "第三问", CreatedAt = time }
        });
        using var turn = new ConversationTurnScope("current", time);
        var built = await env.Services.GetRequiredService<IContextBuilder>().BuildPrivateContextAsync("USER", "第二问", "User");
        Require(built.Messages.Any(m => m.Content == "第一问的回复") && built.Messages.All(m => !m.Content.Contains("第三问")),
            "history boundary must exclude future input while retaining completed previous turn");
        Require(built.Messages.Count(m => m.Content.Contains("第二问")) == 1 && built.Messages.Last().Content == "[User]: 第二问",
            "current message once, at end");
    }

    private static async Task SchemaAsync()
    {
        await using var env = await TestEnvironment.CreateAsync(legacy: true);
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Require(await db.ChatMessages.AnyAsync(m => m.MessageId == "legacy-row"), "schema upgrade must retain legacy rows");
        Require(!await db.AiUsageArchives.AnyAsync(), "an old database must gain an empty usage archive");
        await db.EnsureSchemaUpgradedAsync();
        Require(await db.ChatMessages.CountAsync(m => m.MessageId == "legacy-row") == 1, "repeated upgrade is idempotent");
        var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await repo.SaveAiConfigAsync(env.Config() with { ContextWindowTokens = 65536, MaxInputTokens = 20000 });
        var config = await repo.GetAiConfigAsync();
        Require(config is { ContextWindowTokens: 65536, MaxInputTokens: 20000 }, "budget config roundtrip");
    }

    private static async Task MessagePipelineAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var handler = env.Services.GetRequiredService<MessageEventHandler>();
        var queue = env.Services.GetRequiredService<ConversationWorkQueue>();
        var started = Signal(); var release = Signal(); var secondStarted = Signal();
        var calls = 0;
        env.Http.Handler = async (_, _, ct) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) { started.SetResult(); await release.Task.WaitAsync(ct); }
            else secondStarted.SetResult();
            return JsonReply(call == 1 ? "第一答" : "第二答");
        };
        QqDispatch Dispatch(string id, string text) => new("GROUP_AT_MESSAGE_CREATE", id, 1,
            JsonSerializer.SerializeToElement(new GroupMessageEvent
            {
                Id = id, GroupOpenid = "GROUP", Content = text,
                Author = new AuthorMember { MemberOpenid = "USER", Username = "虚拟用户" }
            }));
        await queue.StartAsync(default);
        await handler.HandleDispatchAsync(Dispatch("first-input", "第一问"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await handler.HandleDispatchAsync(Dispatch("second-input", "第二问"));
        Require(!secondStarted.Task.IsCompleted, "second turn must wait for first delivery");
        release.SetResult();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.StopAsync(deadline.Token);
        Require(env.Api.PassiveCalls == 2, "both turns must be sent");
        using var firstPayload = JsonDocument.Parse(env.Http.Requests[0].Body);
        Require(firstPayload.RootElement.GetProperty("messages").EnumerateArray()
            .All(m => !m.GetProperty("content").GetString()!.Contains("第二问")), "first prompt must not see future input");
        using var secondPayload = JsonDocument.Parse(env.Http.Requests[1].Body);
        var texts = secondPayload.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("content").GetString()).ToList();
        Require(texts.Contains("第一答") && texts.Count(t => t?.Contains("第二问") == true) == 1,
            "second turn must see confirmed first reply and current input exactly once");
        using var scope = env.Services.CreateScope();
        Require(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatMessages.CountAsync() == 4,
            "both inputs and both confirmed replies persisted");
    }

    private static async Task ProactiveReservationAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using (var scope = env.Services.CreateScope())
        {
            var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            await configs.SaveGroupConfigAsync(new GroupConfig { GroupId = "OTHER", IsEnabled = true, AiProactiveEnabled = true });
            await configs.SaveProactiveConfigAsync(new ProactiveChatConfig
            {
                Enabled = true, MaxGlobalPerDay = 1, QuietStartHour = 0, QuietEndHour = 0
            });
        }
        var dispatcher = env.Services.GetRequiredService<ReplyDispatcher>();
        var results = await Task.WhenAll(dispatcher.SendAsync(Reply(proactive: true), true),
            dispatcher.SendAsync(Reply("OTHER", true), true));
        Require(results.Count(r => r.Sent) == 1 && results.Count(r => r.Detail == ProactiveOutcomes.SkippedGlobalCap) == 1,
            "concurrent groups must atomically share the global cap");
    }

    private static HttpResponseMessage JsonReply(string content, int prompt = 3, int completion = 2, string finish = "stop") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "synthetic-model", choices = new[] { new { message = new { content }, finish_reason = finish } },
                usage = new { prompt_tokens = prompt, completion_tokens = completion }
            }), Encoding.UTF8, "application/json")
        };

    private sealed class TestEnvironment : IAsyncDisposable
    {
        private readonly string _file = Path.GetTempFileName();
        public ServiceProvider Services { get; private set; } = null!;
        public TestClock Clock { get; } = new();
        public FakeApi Api { get; } = new();
        public FakeHttp Http { get; } = new();
        public FakeImages Images { get; } = new();
        public MonthlyReceiptFault Faults { get; } = new();
        public AiConfig Config() => new()
        {
            ApiBaseUrl = "https://example.invalid/v1", ApiKey = "synthetic-key", ModelName = "synthetic-model",
            MaxTokens = 512, SystemPrompt = "合成测试系统提示"
        };

        public static async Task<TestEnvironment> CreateAsync(bool legacy = false)
        {
            var env = new TestEnvironment();
            try
            {
                var services = new ServiceCollection().AddLogging();
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                services.AddCoreServices().AddInfrastructureServices();
                services.AddSingleton<TimeProvider>(env.Clock);
                services.AddSingleton<IQqBotApiService>(env.Api);
                services.AddSingleton<INotificationService, FakeNotifications>();
                services.AddSingleton<IAiImageResolver>(env.Images);
                services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={env._file};Pooling=False").AddInterceptors(env.Faults));
                services.AddHttpClient<HttpModelTransport>().ConfigurePrimaryHttpMessageHandler(() => env.Http);
                env.Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
                using var scope = env.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (legacy)
                {
                    var script = db.Database.GenerateCreateScript();
                    script = System.Text.RegularExpressions.Regex.Replace(script,
                        "CREATE TABLE \"(?:OutgoingReplies|ActiveMessageAttempts|AiCallRecords|AiUsageArchives)\" \\([\\s\\S]*?\\);", "");
                    script = System.Text.RegularExpressions.Regex.Replace(script,
                        "CREATE INDEX [^;]*ON \"(?:OutgoingReplies|ActiveMessageAttempts|AiCallRecords)\"[^;]*;", "");
                    script = System.Text.RegularExpressions.Regex.Replace(script,
                        "^.*\"(?:ContextWindowTokens|MaxInputTokens|GroupMemoryInjectionEnabled|AiOperationId)\" [^\\r\\n]*\\r?\\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);
                    await db.Database.ExecuteSqlRawAsync(script);
                    await db.Database.ExecuteSqlRawAsync("""
                        INSERT INTO "ChatMessages" ("MessageId", "ChatType", "UserId", "UserName", "PlainText",
                            "RawSegmentsJson", "HasImage", "HasAtBot", "IsBotMessage", "IsProactive", "CreatedAt")
                        VALUES ('legacy-row', 0, '', '', '合成旧数据', '', 0, 0, 0, 0, '2026-11-03 04:00:00');
                        """);
                }
                else await db.Database.EnsureCreatedAsync();
                await db.EnsureSchemaUpgradedAsync();
                await db.EnsureSchemaUpgradedAsync();
                var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                await config.SaveAiConfigAsync(env.Config());
                await config.SaveGroupConfigAsync(new GroupConfig { GroupId = "GROUP", IsEnabled = true, AiProactiveEnabled = true });
                await config.SaveProactiveConfigAsync(new ProactiveChatConfig { Enabled = true, QuietStartHour = 0, QuietEndHour = 0 });
                return env;
            }
            catch { await env.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            if (Services is not null) await Services.DisposeAsync();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_file + suffix);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 3, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
        public void Set(DateTimeOffset time) => _now = time;
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        public record Request(Uri Url, string Body, string? Authorization);
        public List<Request> Requests { get; } = new();
        public Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Request(request.RequestUri!, body, request.Headers.Authorization?.ToString()));
            return Handler is null ? JsonReply("合成回复") : await Handler(request, body, cancellationToken);
        }
    }

    private sealed class FakeImages : IAiImageResolver
    {
        public Func<string, CancellationToken, Task<AiResolvedImage?>>? Handler { get; set; }
        public Task<AiResolvedImage?> ResolveAsync(string url, CancellationToken ct = default) =>
            Handler?.Invoke(url, ct) ?? Task.FromResult<AiResolvedImage?>(new("image/png", [1, 2, 3], url));
    }

    private sealed class FakeApi : IQqBotApiService
    {
        public string BotId => "BOT";
        public string BotName => "Synthetic";
        public int PassiveCalls { get; private set; }
        public int ActiveCalls { get; private set; }
        public Func<string, Task<string?>>? Passive { get; set; }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> SendGroupMessageAsync(string groupOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default)
        {
            PassiveCalls++; return Passive?.Invoke(content) ?? Task.FromResult<string?>("synthetic-sent");
        }
        public Task<string?> SendPrivateMessageAsync(string userOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default) =>
            SendGroupMessageAsync(userOpenId, content, msgId, msgSeq, ct);
        public Task<string?> SendGroupRichAsync(string groupOpenId, OutboundMessage msg, CancellationToken ct = default)
        {
            ActiveCalls++; return Task.FromResult<string?>("synthetic-active");
        }
        public Task<string?> SendPrivateRichAsync(string userOpenId, OutboundMessage msg, CancellationToken ct = default) => SendGroupRichAsync(userOpenId, msg, ct);
        public Task<bool> DeleteGroupMessageAsync(string groupOpenId, string messageId, CancellationToken ct = default) => Task.FromResult(true);
        public Task ReplyInteractionAsync(string interactionId, InteractionReply reply, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> UploadMediaAsync(string groupOpenId, int fileType, string url, CancellationToken ct = default) => Task.FromResult<string?>("synthetic-media");
    }

    private sealed class FakeNotifications : INotificationService
    {
        public Task NotifyNewMessageAsync(object messageDto) => Task.CompletedTask;
        public Task NotifyConnectionStatusAsync(bool isConnected, string info) => Task.CompletedTask;
        public Task NotifyAiResponseStartedAsync(string? groupId, string senderName) => Task.CompletedTask;
        public Task NotifyAiResponseCompletedAsync(object responseDto) => Task.CompletedTask;
        public Task NotifyProactiveEvaluatedAsync(object evaluationDto) => Task.CompletedTask;
    }

    private sealed class ReceiptFailingStore(IOutgoingReplyStore inner) : IOutgoingReplyStore
    {
        public Task<string?> PrepareAsync(OutgoingReply reply, ProactiveChatConfig? limits, CancellationToken ct) => inner.PrepareAsync(reply, limits, ct);
        public Task<string?> CheckProactiveAsync(string groupId, ProactiveChatConfig limits, CancellationToken ct) => inner.CheckProactiveAsync(groupId, limits, ct);
        public Task CompleteAsync(OutgoingReply reply, DeliveryResult result, CancellationToken ct) => throw new InvalidOperationException("synthetic receipt failure");
    }

    private sealed class MonthlyReceiptFault : DbCommandInterceptor
    {
        public bool FailMonthlyReceipt { get; set; }
        public bool FailGroupMemoryWrite { get; set; }
        public bool FailRetentionDelete { get; set; }
        public List<string> Commands { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            if (FailGroupMemoryWrite && (command.CommandText.Contains("UPDATE \"GroupMemories\"")
                || command.CommandText.Contains("INSERT INTO \"GroupMemories\"")))
                throw new InvalidOperationException("synthetic group memory write failure");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailRetentionDelete && command.CommandText.Contains("DELETE FROM \"AiCallRecords\""))
                throw new InvalidOperationException("synthetic retention delete failure");
            if (FailMonthlyReceipt && command.CommandText.Contains("INSERT INTO \"ActiveMessageQuotas\""))
                throw new InvalidOperationException("synthetic monthly receipt failure");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
