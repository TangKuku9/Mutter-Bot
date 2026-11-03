using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutter.Core;
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

/// <summary>只使用合成标识符、临时 SQLite 和假 HTTP，不读取部署数据或访问外部服务。</summary>
internal static class RuntimeChecks
{
    public static async Task<bool> RunAsync()
    {
        var ok = true;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("会话顺序、跨会话并行、有界接纳与排空", QueueOrderAsync),
            ("主动任务优先级、过期与停机取消", QueueLifecycleAsync),
            ("模型协议、结构化用量与错误重试政策", ModelContractsAsync),
            ("多模态搜索、配置快照和取消", ModelMultimodalAsync),
            ("最终请求预算与超长当前消息", BudgetAsync),
            ("投递未知、成功历史与用量去重", DeliveryAsync),
            ("发送成功后记账失败及重启恢复配额", ReceiptFailureAsync),
            ("主动消息分钟/日配额持久化", ActiveQuotaAsync),
            ("模型运行期间关闭开关/群参与/进入静默", FinalPolicyAsync),
            ("排队消息历史边界与上一轮回复", TurnHistoryAsync),
            ("完整消息管线串行回复与持久化", MessagePipelineAsync),
            ("跨群主动日限的原子预占", ProactiveReservationAsync),
            ("旧模型升级幂等与数据保留", SchemaAsync)
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
        }), "first accepted");
        Require(queue.TryEnqueue("a", _ => { order.Enqueue("a2"); second.SetResult(); return Task.CompletedTask; }), "second accepted");
        Require(queue.TryEnqueue("b", _ => { other.SetResult(); return Task.CompletedTask; }), "other accepted");
        Require(!queue.TryEnqueue("c", _ => Task.CompletedTask), "capacity must include running and pending work");
        await queue.StartAsync(default);
        await Task.WhenAll(first.Task, other.Task).WaitAsync(TimeSpan.FromSeconds(3));
        Require(!second.Task.IsCompleted, "same conversation must not run concurrently");
        release.SetResult();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await queue.StopAsync(deadline.Token);
        Require(order.ToArray().SequenceEqual(new[] { "a1-start", "a1-end", "a2" }), "conversation order");
        Require(!queue.TryEnqueue("d", _ => Task.CompletedTask), "stop must close admission");
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
        env.Http.Handler = (_, _, _) => Task.FromResult(++sequence == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("response_format json_object unsupported") }
            : JsonReply("{\"reason\":\"没有接话必要\"}"));
        var unsupported = await structured.GetStructuredOutcomeAsync<ProactiveVerdict>("s", "u", config);
        Require(unsupported.Result is not null && sequence == 2, "explicit JSON mode rejection may fall back");
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
            Require(await repo.SumTokensAsync() == 36 && await repo.CountAiCallsAsync() == 2, "ledger and sent history not double counted");
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
                        "CREATE TABLE \"(?:OutgoingReplies|ActiveMessageAttempts)\" \\([\\s\\S]*?\\);", "");
                    script = System.Text.RegularExpressions.Regex.Replace(script,
                        "CREATE INDEX [^;]*ON \"(?:OutgoingReplies|ActiveMessageAttempts)\"[^;]*;", "");
                    script = System.Text.RegularExpressions.Regex.Replace(script,
                        "^.*\"(?:ContextWindowTokens|MaxInputTokens)\" INTEGER NOT NULL,?\\r?\\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);
                    await db.Database.ExecuteSqlRawAsync(script);
                    db.ChatMessages.Add(new ChatMessage { MessageId = "legacy-row", PlainText = "合成旧数据" });
                    await db.SaveChangesAsync();
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
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailMonthlyReceipt && command.CommandText.Contains("INSERT INTO \"ActiveMessageQuotas\""))
                throw new InvalidOperationException("synthetic monthly receipt failure");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
