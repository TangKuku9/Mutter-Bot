using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core;
using Mutter.Core.EventHandlers;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Models.QQOfficial;
using Mutter.Core.Services;
using Mutter.Infrastructure;
using Mutter.Infrastructure.Ai;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;

internal static partial class RuntimeChecks
{
    // 长时模拟单独运行，不把真实的 75/90 秒等待放进常规 CI。
    public static async Task<bool> RunMemoryProductionSimulationAsync(bool scenariosOnly = false)
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "group-memory-production-sim-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(directory);
        await using var simulation = new ProductionMemorySimulation(directory, scenariosOnly);
        Console.WriteLine($"SIM artifacts: {directory}");
        var passed = false;
        try
        {
            if (scenariosOnly) await simulation.RunFastScenariosAsync();
            else await simulation.RunAsync();
            passed = true;
        }
        catch (Exception ex)
        {
            simulation.Results.Add(new("未完成的模拟场景", false, ex.ToString()));
            Console.WriteLine($"FAIL simulation: {ex}");
        }
        finally { await simulation.SaveReportAsync(passed); }
        Console.WriteLine($"SIM {(passed ? "PASS" : "FAIL")}: {Path.Combine(directory, "report.md")}");
        return passed;
    }

    private sealed record SimulationResult(string Name, bool Passed, string Detail);

    private sealed class ProductionMemorySimulation(string directory, bool scenariosOnly) : IAsyncDisposable
    {
        private readonly string _database = Path.Combine(directory, "simulation.db");
        private readonly SimulationClock _clock = new();
        private readonly SimulationApi _api = new();
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly SimulatedMemoryUpstream _upstream = new();
        private IHost? _host;
        private IServiceProvider Services => _host!.Services;
        private GroupMemoryLearningService Learner => Services.GetRequiredService<GroupMemoryLearningService>();
        private ConversationWorkQueue Queue => Services.GetRequiredService<ConversationWorkQueue>();
        public List<SimulationResult> Results { get; } = [];
        private int _restarts;

        public async Task RunFastScenariosAsync()
        {
            Console.WriteLine("SIM mode: fault scenarios only; real 75/90-second waits are excluded from this run");
            await _upstream.StartAsync();
            await StartHostAsync(initialize: true);
            await ShortSocketTimeoutAsync();
            await TransientRestartAsync();
            await ConnectionIsolationAsync();
            await PoisonAndClearAsync();
            await QuotasAsync();
            Check("后台并发始终有界", _upstream.MaxMemory == 1, $"max memory in flight={_upstream.MaxMemory}");
        }

        private async Task ShortSocketTimeoutAsync()
        {
            await ConfigureAsync("SIM-SOCKET", "memory-socket", "MEM-B");
            var source = await AddMessageAsync("SIM-SOCKET");
            using (var scope = Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (await db.AiProviderConnections.SingleAsync(c => c.Id == "MEM-B")).TimeoutSeconds = 5;
                await db.SaveChangesAsync();
            }
            _upstream.Plan = (model, _) => model == "memory-socket" ? new(Hang: true) : new();
            await PumpAsync();
            await WaitUntilAsync(() => _upstream.ActiveMemory == 0, TimeSpan.FromSeconds(5));
            var state = await StateAsync("SIM-SOCKET");
            Check("真实 Socket 取消按超时分类", state.LastOutcome == "timeout" && state.LastProcessedRowId < source
                && state.RetryThroughRowId == source && state.CallsToday == 1, "5 秒真实连接超时，多层异常链仍保留 timeout 语义");
            await ConfigureAsync("SIM-SOCKET", "memory-socket", "MEM-B", enabled: false);
            using var restore = Services.CreateScope();
            var database = restore.ServiceProvider.GetRequiredService<AppDbContext>();
            (await database.AiProviderConnections.SingleAsync(c => c.Id == "MEM-B")).TimeoutSeconds = 90;
            await database.SaveChangesAsync();
        }

        private void Check(string name, bool condition, string detail)
        {
            Results.Add(new(name, condition, detail));
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")} [{_elapsed.Elapsed.TotalSeconds:F1}s] {name}: {detail}");
            Require(condition, name + ": " + detail);
        }

        public async Task RunAsync()
        {
            await _upstream.StartAsync();
            await StartHostAsync(initialize: true, automatic: false);
            await Learner.TickAsync();
            Check("旧历史不自动调用模型", _upstream.Requests.IsEmpty, "SQLite 包含 50,000 条旧消息，尚未开启学习");
            using (var scope = Services.CreateScope())
            {
                var config = (await scope.ServiceProvider.GetRequiredService<IConfigRepository>().GetAiConfigAsync())!;
                using var call = AiCallScope.Begin(default, "simulation_probe", maxAttempts: 1);
                var response = await Services.GetRequiredService<IAiChatService>().ChatAsync("仅用于本地模拟连通检查。",
                    [("user", "检查真实 HTTP 管线")], config, call.Token);
                Check("真实 HTTP 管线预检", response.Content.Contains("合成回复"), "生产协议适配器已连接本轮本地上游");
            }
            await ConfigureAsync("SIM-SLOW", "memory-slow");
            var baseline = await StateAsync("SIM-SLOW");
            Check("首次启用建立历史边界", baseline.LastProcessedRowId == 50_000, $"cursor={baseline.LastProcessedRowId}");
            var source = await AddMessageAsync("SIM-SLOW");
            _upstream.Plan = (model, _) => model == "memory-slow" ? new(DelaySeconds: 75) : new();
            // 重新启动实际宿主，让第一批由生产的每分钟 PeriodicTimer 触发。
            await RestartAsync(automatic: true);
            var started = Stopwatch.StartNew();
            Console.WriteLine("SIM waiting: actual one-minute scheduler, followed by a 75-second upstream response");
            await _upstream.Observed("memory-slow").WaitAsync(TimeSpan.FromSeconds(80));
            Check("实际后台定时器触发", started.Elapsed.TotalSeconds >= 55, $"first HTTP after {started.Elapsed.TotalSeconds:F1}s");
            await ForegroundBurstAsync(6);
            Check("慢总结期间前台可用", _upstream.ActiveMemory == 1 && _api.Delivered.Count == 48,
                $"48 条真实 @ 管线回复，p95={Percentile(_api.Latencies, .95):F0}ms");
            await IdleAsync(TimeSpan.FromSeconds(90));
            var slow = await StateAsync("SIM-SLOW");
            using (var scope = Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var call = await db.AiCallRecords.SingleAsync(c => c.Model == "memory-slow");
                var recall = await scope.ServiceProvider.GetRequiredService<IAutomaticMemoryRecall>().RecallAsync("SIM-SLOW", "喝茶");
                Check("75 秒总结成功并可本地召回", slow.LastOutcome == "success" && slow.LastProcessedRowId == source
                    && call.ElapsedMs >= 74_000 && recall.Count > 0,
                    $"elapsed={call.ElapsedMs}ms, calls={slow.CallsToday}, recall={recall.Count}");
            }

            await ConfigureAsync("SIM-SLOW", "memory-timeout");
            var timeoutSource = await AddMessageAsync("SIM-SLOW");
            var priorCursor = (await StateAsync("SIM-SLOW")).LastProcessedRowId;
            _upstream.Plan = (model, _) => model == "memory-timeout" ? new(Hang: true) : new();
            Console.WriteLine("SIM waiting: actual 90-second connection timeout, with foreground replies in parallel");
            await Learner.TickAsync();
            await _upstream.Observed("memory-timeout").WaitAsync(TimeSpan.FromSeconds(5));
            await ForegroundBurstAsync(4);
            await IdleAsync(TimeSpan.FromSeconds(105));
            await WaitUntilAsync(() => _upstream.ActiveMemory == 0, TimeSpan.FromSeconds(5));
            var timeout = await StateAsync("SIM-SLOW");
            using (var scope = Services.CreateScope())
            {
                var call = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AiCallRecords.SingleAsync(c => c.Model == "memory-timeout");
                Check("真实 90 秒超时保留原批次", call.State == "timeout" && call.ElapsedMs is >= 88_000 and < 100_000
                    && timeout.LastOutcome == "timeout" && timeout.LastProcessedRowId == priorCursor
                    && timeout.RetryThroughRowId >= timeoutSource && timeout.ConsecutiveFailures == 0 && timeout.CallsToday == 2,
                    $"elapsed={call.ElapsedMs}ms, cursor={timeout.LastProcessedRowId}, retryThrough={timeout.RetryThroughRowId}, quota={timeout.CallsToday}");
            }
            await RestartAsync(); // 后续宿主只启动队列，通过生产调度入口推进冷却/日界。
            var attempts = _upstream.MemoryRequests;
            await Learner.TickAsync(); await IdleAsync();
            Check("超时后不立即重发", _upstream.MemoryRequests == attempts, "退避期间没有新增 HTTP 请求");
            _upstream.Plan = (_, _) => new();
            _clock.Advance(TimeSpan.FromMinutes(6));
            await PumpAsync();
            Check("超时原批次可恢复完成", (await StateAsync("SIM-SLOW")).LastOutcome == "success", "同游标重试成功，未直接弃批");
            await ConfigureAsync("SIM-SLOW", "memory-timeout", enabled: false);

            await TransientRestartAsync();
            await ConnectionIsolationAsync();
            await PoisonAndClearAsync();
            await QuotasAsync();
            Check("后台并发始终有界", _upstream.MaxMemory == 1, $"max memory in flight={_upstream.MaxMemory}");
            Check("前台回复投递无丢失或重复", _api.Delivered.Count == 80 && _api.Duplicates == 0
                && Percentile(_api.Latencies, .95) < 5_000,
                $"delivered={_api.Delivered.Count}, duplicates={_api.Duplicates}, p50={Percentile(_api.Latencies, .5):F0}ms, p95={Percentile(_api.Latencies, .95):F0}ms");
            Check("会话工作无异常终态", Queue.Snapshot().Tasks.All(t => t.State != "failed"), "共享队列无 failed 任务");
            using (var scope = Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var persisted = await db.ChatMessages.CountAsync(m => m.IsBotMessage && !m.IsProactive);
                var sent = await db.OutgoingReplies.CountAsync(r => r.State == DeliveryStates.Sent);
                Check("前台回复与发送回执均已落库", persisted == 80 && sent == 80, $"bot history={persisted}, confirmed receipts={sent}");
                var calls = await db.AiCallRecords.CountAsync();
                Check("实际 HTTP 与调用账本一一对应", calls == _upstream.Requests.Count,
                    $"HTTP requests={_upstream.Requests.Count}, audit rows={calls}");
            }
        }

        private async Task TransientRestartAsync()
        {
            await ConfigureAsync("SIM-FAIL", "memory-flaky");
            var first = await AddMessageAsync("SIM-FAIL");
            _upstream.Plan = (model, count) => model == "memory-flaky" && count <= 3 ? new(Status: 503) : new();
            await PumpAsync();
            var saved = await StateAsync("SIM-FAIL");
            var later = await AddMessageAsync("SIM-FAIL");
            var attempts = _upstream.MemoryRequests;
            await RestartAsync();
            var restored = await StateAsync("SIM-FAIL");
            await PumpAsync();
            Check("实际宿主重启保留重试状态", restored.LastProcessedRowId == saved.LastProcessedRowId
                && restored.NextAttemptAt == saved.NextAttemptAt && restored.CallsToday == saved.CallsToday
                && restored.RetryThroughRowId == first && _upstream.MemoryRequests == attempts,
                $"cursor={restored.LastProcessedRowId}, quota={restored.CallsToday}, transient={restored.TransientFailures}");
            _clock.Advance(TimeSpan.FromMinutes(6)); await PumpAsync();
            _clock.Advance(TimeSpan.FromMinutes(11)); await PumpAsync();
            var failed = await StateAsync("SIM-FAIL");
            Check("连续三个 503 不弃批", failed.LastProcessedRowId == saved.LastProcessedRowId
                && failed.ConsecutiveFailures == 0 && failed.TransientFailures == 3 && failed.CallsToday == 3,
                $"transient={failed.TransientFailures}, poison={failed.ConsecutiveFailures}, quota={failed.CallsToday}");
            _clock.Advance(TimeSpan.FromMinutes(21)); await PumpAsync();
            var recovered = await StateAsync("SIM-FAIL");
            Check("故障恢复不混入后来消息", recovered.LastProcessedRowId == first && recovered.LastProcessedRowId < later,
                $"completed={recovered.LastProcessedRowId}, later={later}");
            _clock.Advance(TimeSpan.FromMinutes(2)); await PumpAsync();
            Check("后来消息继续学习", (await StateAsync("SIM-FAIL")).LastProcessedRowId == later, "重试完成后下一批正常推进");
            await ConfigureAsync("SIM-FAIL", "memory-flaky", enabled: false);
        }

        private async Task ConnectionIsolationAsync()
        {
            _clock.Advance(TimeSpan.FromMinutes(2));
            await ConfigureAsync("SIM-RATE", "memory-rate");
            await AddMessageAsync("SIM-RATE");
            _upstream.Plan = (model, count) => model == "memory-rate" && count == 1 ? new(Status: 429, RetryAfterSeconds: 600) : new();
            await PumpAsync();
            await ConfigureAsync("SIM-SHARED", "memory-shared");
            await AddMessageAsync("SIM-SHARED");
            await ConfigureAsync("SIM-INDEPENDENT", "memory-independent", "MEM-B");
            var other = await AddMessageAsync("SIM-INDEPENDENT");
            _clock.Advance(TimeSpan.FromMinutes(6));
            await PumpAsync();
            var shared = await StateAsync("SIM-SHARED");
            var independent = await StateAsync("SIM-INDEPENDENT");
            Check("429 按连接隔离且拦截不扣配额", shared.CallsToday == 0 && independent.LastProcessedRowId == other
                && !Services.GetRequiredService<UpstreamHealthTracker>().IsDegraded,
                $"shared quota={shared.CallsToday}, other outcome={independent.LastOutcome}, foreground healthy");
            var attempts = _upstream.MemoryRequests;
            await PumpAsync();
            Check("遵守上游十分钟 Retry-After", _upstream.MemoryRequests == attempts, "群级五分钟退避到期仍被连接闸门拦截");
            _clock.Advance(TimeSpan.FromMinutes(5)); await PumpAsync();
            Check("冷却后恢复受限探测", _upstream.MemoryRequests == attempts + 1, "十分钟后只有一个总结请求放行");
            foreach (var (group, model, connection) in new[]
            {
                ("SIM-RATE", "memory-rate", "MEM-A"), ("SIM-SHARED", "memory-shared", "MEM-A"),
                ("SIM-INDEPENDENT", "memory-independent", "MEM-B")
            }) await ConfigureAsync(group, model, connection, enabled: false);
        }

        private async Task PoisonAndClearAsync()
        {
            await ConfigureAsync("SIM-BAD", "memory-bad", "MEM-B");
            var first = await AddMessageAsync("SIM-BAD");
            _upstream.Plan = (model, count) => model == "memory-bad" && count <= 3
                ? new(Content: "invalid JSON", Truncated: count == 2) : new();
            await PumpAsync();
            var later = await AddMessageAsync("SIM-BAD");
            _clock.Advance(TimeSpan.FromMinutes(6)); await PumpAsync();
            _clock.Advance(TimeSpan.FromMinutes(11)); await PumpAsync();
            var bad = await StateAsync("SIM-BAD");
            var retries = _upstream.Requests.Where(r => r.Model == "memory-bad").ToArray();
            Check("坏输出只弃原批次", bad.LastOutcome == "skipped_after_failures" && bad.LastProcessedRowId == first
                && retries.Length == 3 && retries.All(r => r.SourceIds.SequenceEqual([first])) && first < later,
                $"three attempts through={first}, later={later} remains pending");
            _clock.Advance(TimeSpan.FromMinutes(16)); await PumpAsync();
            Check("坏批之后的新消息可完成", (await StateAsync("SIM-BAD")).LastProcessedRowId == later, "后续合法输出正常推进");
            await ConfigureAsync("SIM-BAD", "memory-bad", "MEM-B", enabled: false);

            await ConfigureAsync("SIM-CLEAR", "memory-clear", "MEM-B");
            var source = await AddMessageAsync("SIM-CLEAR");
            var release = Signal();
            _upstream.Release = release.Task;
            await Learner.TickAsync();
            await _upstream.Observed("memory-clear").WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                using var scope = Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().ClearAsync("SIM-CLEAR", default);
            }
            finally { release.TrySetResult(); }
            await IdleAsync();
            var cleared = await StateAsync("SIM-CLEAR");
            using (var scope = Services.CreateScope())
                Check("在途请求不能复活已清空记忆", cleared.LastProcessedRowId == source && cleared.CallsToday == 1
                    && !(await scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().ItemsAsync("SIM-CLEAR")).Any(),
                    "真实 HTTP 在清空后返回，旧 Revision 被拒绝，已发请求仍记配额");
            await ConfigureAsync("SIM-CLEAR", "memory-clear", "MEM-B", enabled: false);
        }

        private async Task QuotasAsync()
        {
            _clock.NextDay();
            _upstream.Plan = (_, _) => new(Content: "{\"operations\":[]}");
            for (var group = 1; group <= 5; group++)
                await ConfigureAsync("SIM-QUOTA-" + group, "memory-quota-" + group, "MEM-C");
            var before = _upstream.MemoryRequests;
            for (var group = 1; group <= 4; group++)
            {
                for (var request = 0; request < 12; request++)
                {
                    await AddMessageAsync("SIM-QUOTA-" + group);
                    await PumpAsync();
                }
                await AddMessageAsync("SIM-QUOTA-" + group);
                await PumpAsync();
                Check("单群请求硬上限 " + group, (await StateAsync("SIM-QUOTA-" + group)).CallsToday == 12
                    && _upstream.Requests.Count(r => r.Model == "memory-quota-" + group) == 12,
                    "12 次真实 HTTP 后，第 13 批保留待处理且未发请求");
            }
            await AddMessageAsync("SIM-QUOTA-5");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Learner.TickAsync()));
            await IdleAsync();
            Check("全局每日 48 次硬上限", _upstream.MemoryRequests - before == 48
                && (await StateAsync("SIM-QUOTA-5")).CallsToday == 0,
                "48 次真实总结请求后，第五群与并发调度均不能超额");
            await RestartAsync(); await PumpAsync();
            Check("重启不能刷新每日配额", _upstream.MemoryRequests - before == 48, "全新宿主和 DI 容器仍受磁盘配额限制");
            _clock.NextDay();
            _clock.Advance(TimeSpan.FromMinutes(2)); // 让上一测试日的待处理记录确实超过 24 小时。
            await AddMessageAsync("SIM-QUOTA-5");
            await PumpAsync();
            Check("北京时间换日后恢复学习", _upstream.MemoryRequests - before == 49
                && (await StateAsync("SIM-QUOTA-5")).CallsToday == 1,
                "旧积压按 24 小时规则处理，新消息成功使用新一天配额");
        }

        private async Task ForegroundBurstAsync(int rounds)
        {
            var handler = Services.GetRequiredService<MessageEventHandler>();
            for (var round = 0; round < rounds; round++)
            {
                var inputs = Enumerable.Range(0, 8).Select(index =>
                {
                    var group = index == 0 ? "SIM-SLOW" : "SIM-FG-" + index;
                    var id = Guid.NewGuid().ToString("N");
                    var delivery = _api.Expect(id);
                    var dispatch = new QqDispatch("GROUP_AT_MESSAGE_CREATE", id, 1,
                        JsonSerializer.SerializeToElement(new GroupMessageEvent
                        {
                            Id = id, GroupOpenid = group, Content = $"生产模拟接话 {id}，请用一句话回复。",
                            Author = new() { MemberOpenid = "SIM-USER-" + index, Username = "合成成员" + index }
                        }));
                    return (Dispatch: dispatch, Delivery: delivery);
                }).ToArray();
                await Task.WhenAll(inputs.Select(input => handler.HandleDispatchAsync(input.Dispatch)));
                await Task.WhenAll(inputs.Select(input => input.Delivery.WaitAsync(TimeSpan.FromSeconds(10))));
            }
        }

        private async Task StartHostAsync(bool initialize = false, bool automatic = false)
        {
            _host = new HostBuilder().ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mutter:Runtime:Parallelism"] = "3", ["Mutter:Runtime:AiTimeoutSeconds"] = "120",
                ["Mutter:Runtime:AiPerAttemptTimeoutSeconds"] = "90", ["Mutter:Runtime:WorkDeadlineSeconds"] = "240"
            })).ConfigureLogging(logging =>
            {
                logging.ClearProviders(); logging.AddConsole(); logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Critical);
                logging.AddFilter("Microsoft.EntityFrameworkCore.Query", LogLevel.Error);
            })
                .ConfigureServices((_, services) =>
                {
                    services.AddLogging(); services.AddCoreServices().AddInfrastructureServices();
                    services.AddSingleton<TimeProvider>(_clock);
                    services.AddSingleton<IQqBotApiService>(_api);
                    services.AddSingleton<INotificationService, FakeNotifications>();
                    services.AddSingleton<IAiImageResolver, FakeImages>();
                    services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_database};Pooling=False;Default Timeout=30"));
                    // 唯一网络目标为本轮随机端口的本地模拟上游；真实协议、HttpClient 和审计逻辑均保留。
                    services.AddHttpClient<HttpModelTransport>().ConfigurePrimaryHttpMessageHandler(() => new SimulationNetworkGuard(_upstream.Address));
                    services.RemoveAll<IHostedService>();
                    services.AddHostedService(provider => provider.GetRequiredService<ConversationWorkQueue>());
                    if (automatic) services.AddHostedService(provider => provider.GetRequiredService<GroupMemoryLearningService>());
                }).UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; }).Build();
            using (var scope = Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync(); await db.EnsureSchemaUpgradedAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                if (initialize) await InitializeAsync(scope.ServiceProvider, db);
            }
            await _host.StartAsync();
            Console.WriteLine($"SIM host started: queue accepting={Queue.Snapshot().Accepting}, stopping={Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested}");
        }

        private async Task InitializeAsync(IServiceProvider services, AppDbContext db)
        {
            var models = new[] { "memory-slow", "memory-timeout", "memory-socket", "memory-flaky", "memory-rate", "memory-shared", "memory-independent", "memory-bad", "memory-clear" }
                .Concat(Enumerable.Range(1, 5).Select(n => "memory-quota-" + n)).Append("chat").ToArray();
            foreach (var id in new[] { "CHAT", "MEM-A", "MEM-B", "MEM-C" })
                db.AiProviderConnections.Add(new()
                {
                    Id = id, Name = "Synthetic " + id, Protocol = "openai", ApiBaseUrl = _upstream.Address.GetLeftPart(UriPartial.Authority) + "/v1",
                    ApiKey = "synthetic-simulation-key", TimeoutSeconds = 90, DefaultModel = id == "CHAT" ? "chat" : "memory-slow",
                    HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["X-Simulation-Connection"] = id }),
                    ModelsJson = JsonSerializer.Serialize(models.Select(id => new { id, contextWindowTokens = 8192, maxOutputTokens = 1024 }))
                });
            await db.SaveChangesAsync();
            var configs = services.GetRequiredService<IConfigRepository>();
            await configs.SaveAiConfigAsync(new()
            {
                ProviderConnectionId = "CHAT", ApiBaseUrl = _upstream.Address.GetLeftPart(UriPartial.Authority) + "/v1", ApiKey = "synthetic-simulation-key",
                Protocol = "openai", ModelName = "chat", ConnectionTimeoutSeconds = 90,
                MaxTokens = 512, ContextWindowTokens = 8192, MaxInputTokens = 6000, WebSearchEnabled = false
            });
            await configs.SaveProactiveConfigAsync(new() { Enabled = false });
            foreach (var group in new[] { "SIM-SLOW" }.Concat(Enumerable.Range(1, 7).Select(n => "SIM-FG-" + n)))
                await configs.SaveGroupConfigAsync(new() { GroupId = group, IsEnabled = true, TriggerMode = 2 });
            await db.Database.ExecuteSqlRawAsync("""
                WITH RECURSIVE old(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM old WHERE n < 50000)
                INSERT INTO "ChatMessages" ("MessageId","ChatType","GroupId","UserId","UserName","PlainText",
                    "RawSegmentsJson","HasImage","HasAtBot","IsBotMessage","IsProactive","CreatedAt")
                SELECT 'sim-old-' || n,0,'SIM-SLOW','SIM-OLD-USER','合成旧成员','合成历史：我喜欢喝茶。','',0,1,0,0,'2026-01-01 00:00:00' FROM old;
                """);
        }

        private async Task ConfigureAsync(string group, string model, string connection = "MEM-A", bool enabled = true)
        {
            using var scope = Services.CreateScope();
            var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            if (await configs.GetGroupConfigAsync(group) is null)
                await configs.SaveGroupConfigAsync(new() { GroupId = group, IsEnabled = true, TriggerMode = 2 });
            var repo = scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>();
            var state = await repo.StateAsync(group);
            await repo.SaveConfigAsync(group, new()
            {
                Enabled = enabled, CountEnabled = true, Count = 1, IntervalEnabled = false, ScheduleEnabled = false,
                ProviderConnectionId = connection, ModelName = model
            }, state?.Revision ?? 0, default);
        }

        private async Task<long> AddMessageAsync(string group)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var message = new ChatMessage
            {
                GroupId = group, MessageId = Guid.NewGuid().ToString("N"), UserId = "SIM-USER-" + group,
                UserName = "合成测试成员", PlainText = "我喜欢喝茶，不喝咖啡，这是我的长期偏好。 " + Guid.NewGuid().ToString("N"),
                HasAtBot = true, CreatedAt = _clock.GetUtcNow().UtcDateTime
            };
            db.ChatMessages.Add(message); await db.SaveChangesAsync(); return message.Id;
        }

        private async Task<GroupMemoryLearningStateEntity> StateAsync(string group)
        {
            using var scope = Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<AutomaticMemoryRepository>().StateAsync(group))!;
        }

        private async Task PumpAsync()
        {
            Require(ReferenceEquals(Services.GetRequiredService<TimeProvider>(), _clock), "simulation host must use its injected clock");
            await Learner.TickAsync(); await IdleAsync();
        }

        private Task IdleAsync(TimeSpan? timeout = null) => WaitUntilAsync(() => Queue.Snapshot() is { Running: 0, Waiting: 0 }, timeout ?? TimeSpan.FromSeconds(15));

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition())
            {
                if (elapsed.Elapsed > timeout) throw new TimeoutException("simulation condition exceeded " + timeout);
                await Task.Delay(50);
            }
        }

        private async Task RestartAsync(bool automatic = false)
        {
            await StopHostAsync(); _restarts++;
            await StartHostAsync(automatic: automatic);
        }

        private async Task StopHostAsync()
        {
            if (_host is null) return;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _host.StopAsync(stop.Token); }
            finally { _host.Dispose(); _host = null; }
        }

        public async Task SaveReportAsync(bool passed)
        {
            var queue = _host is null ? null : Queue.Snapshot();
            var clockType = _host is null ? null : Services.GetRequiredService<TimeProvider>().GetType().FullName;
            await StopHostAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_database};Pooling=False").Options;
            await using var db = new AppDbContext(options);
            var calls = await db.AiCallRecords.AsNoTracking().OrderBy(c => c.StartedAt).ToListAsync();
            var states = await db.GroupMemoryLearningStates.AsNoTracking().OrderBy(s => s.GroupId).ToListAsync();
            var metrics = new
            {
                Passed = passed, WallSeconds = _elapsed.Elapsed.TotalSeconds, Database = _database, HistoryRows = 50_000,
                SimulatedNow = _clock.GetUtcNow(), ClockType = clockType, Queue = queue,
                HostRestarts = _restarts, FaultScenariosOnly = scenariosOnly,
                RealSlowSeconds = scenariosOnly ? 0 : 75, RealTimeoutSeconds = scenariosOnly ? 0 : 90,
                QueueParallelism = 3, MaxMemoryInFlight = _upstream.MaxMemory, MaxChatInFlight = _upstream.MaxChat,
                ForegroundReplies = _api.Delivered.Count, ForegroundP50Ms = Percentile(_api.Latencies, .5),
                ForegroundP95Ms = Percentile(_api.Latencies, .95), ActualHttpRequests = _upstream.Requests.Count,
                Results, States = states, Calls = calls, HttpRequests = _upstream.Requests.ToArray()
            };
            var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            await File.WriteAllTextAsync(Path.Combine(directory, "metrics.json"), JsonSerializer.Serialize(metrics, json));
            var report = new StringBuilder("# 自动群记忆模拟生产测试\n\n");
            report.AppendLine($"结果：**{(passed ? "通过" : "失败")}**；实际运行 {_elapsed.Elapsed.TotalSeconds:F1} 秒。\n");
            report.AppendLine("使用真实本地 HTTP、生产服务装配、共享队列和磁盘 SQLite（WAL）。仅替换模型端点、QQ 发送出口与面板通知；所有消息、身份和密钥均为合成数据。退避与北京时间日界通过模拟时钟推进。未测试真实模型提炼质量和外部平台网络。\n");
            report.AppendLine(scenariosOnly ? "本轮仅运行故障与配额场景，未执行实际定时器、75 秒成功、90 秒超时和前台消息压力场景。\n"
                : "本轮由实际后台定时器触发首批，通过消息处理入口压测前台；75 秒成功和 90 秒超时使用真实等待。\n");
            report.AppendLine($"50,000 条历史；三执行槽；后台最大并发 {_upstream.MaxMemory}；宿主重建 {_restarts} 次；前台回复 {_api.Delivered.Count} 条，p50 {Percentile(_api.Latencies, .5):F0}ms / p95 {Percentile(_api.Latencies, .95):F0}ms。\n");
            report.AppendLine("| 场景 | 结果 | 证据 |\n| --- | --- | --- |");
            foreach (var result in Results)
                report.AppendLine($"| {result.Name} | {(result.Passed ? "通过" : "失败")} | {result.Detail.Replace("|", "/").Replace("\r", " ").Replace("\n", " ")} |");
            report.AppendLine("\n原始指标、HTTP 元数据、模型调用账本和最终状态见 `metrics.json`；合成数据库为 `simulation.db`。\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "report.md"), report.ToString());
        }

        private static double Percentile(IEnumerable<double> samples, double fraction)
        {
            var values = samples.Order().ToArray();
            return values.Length == 0 ? 0 : values[Math.Max(0, (int)Math.Ceiling(values.Length * fraction) - 1)];
        }

        public async ValueTask DisposeAsync()
        {
            await StopHostAsync(); await _upstream.DisposeAsync();
        }
    }

    private sealed class SimulationClock : TimeProvider
    {
        private TimeSpan _offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;
        public void Advance(TimeSpan elapsed) => _offset += elapsed;
        public void NextDay()
        {
            var next = GetUtcNow().UtcDateTime.AddHours(8).Date.AddDays(1).AddMinutes(1).AddHours(-8);
            Advance(next - GetUtcNow().UtcDateTime);
        }
    }

    private sealed class SimulationApi : IQqBotApiService
    {
        private readonly ConcurrentDictionary<string, (long At, TaskCompletionSource Done)> _pending = new();
        public ConcurrentDictionary<string, double> Delivered { get; } = new();
        public IEnumerable<double> Latencies => Delivered.Values;
        private int _duplicates;
        public int Duplicates => _duplicates;
        public string BotId => "SIM-BOT";
        public string BotName => "Synthetic simulation bot";
        public Task Expect(string id) { var done = Signal(); _pending[id] = (Stopwatch.GetTimestamp(), done); return done.Task; }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> SendGroupMessageAsync(string groupOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default)
        {
            if (msgId is not null && _pending.TryGetValue(msgId, out var pending))
            {
                if (!Delivered.TryAdd(msgId, Stopwatch.GetElapsedTime(pending.At).TotalMilliseconds)) Interlocked.Increment(ref _duplicates);
                pending.Done.TrySetResult();
            }
            return Task.FromResult<string?>("sim-sent-" + msgId);
        }
        public Task<string?> SendPrivateMessageAsync(string id, string text, string? msgId = null, int? msgSeq = null, CancellationToken ct = default) => SendGroupMessageAsync(id, text, msgId, msgSeq, ct);
        public Task<string?> SendGroupRichAsync(string id, OutboundMessage message, CancellationToken ct = default) => SendGroupMessageAsync(id, message.PlainText ?? message.MarkdownText ?? "", message.MsgId, message.MsgSeq, ct);
        public Task<string?> SendPrivateRichAsync(string id, OutboundMessage message, CancellationToken ct = default) => SendGroupRichAsync(id, message, ct);
        public Task<bool> DeleteGroupMessageAsync(string id, string message, CancellationToken ct = default) => Task.FromResult(true);
        public Task ReplyInteractionAsync(string id, InteractionReply reply, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> UploadMediaAsync(string id, int type, string url, CancellationToken ct = default) => throw new NotSupportedException("simulation uses text only");
    }

    private sealed class SimulationNetworkGuard(Uri address) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri || uri.Host != address.Host || uri.Port != address.Port || uri.Scheme != "http")
                throw new InvalidOperationException("simulation may only call its local upstream");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record SimulationResponse(int Status = 200, int DelaySeconds = 0, bool Hang = false,
        string? Content = null, bool Truncated = false, int? RetryAfterSeconds = null);

    private sealed class SimulatedMemoryUpstream : IAsyncDisposable
    {
        public sealed class Request
        {
            public string Model { get; init; } = "";
            public string Connection { get; init; } = "";
            public long[] SourceIds { get; init; } = [];
            public long ElapsedMs { get; set; }
            public int? Status { get; set; }
            public bool ClientCancelled { get; set; }
        }
        private WebApplication? _app;
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _observed = new();
        private readonly ConcurrentDictionary<string, int> _counts = new();
        public ConcurrentQueue<Request> Requests { get; } = new();
        public Uri Address { get; private set; } = null!;
        public Func<string, int, SimulationResponse> Plan { get; set; } = (_, _) => new();
        public Task? Release { get; set; }
        private int _activeMemory, _maxMemory, _activeChat, _maxChat;
        public int ActiveMemory => Volatile.Read(ref _activeMemory);
        public int MaxMemory => _maxMemory;
        public int MaxChat => _maxChat;
        public int MemoryRequests => Requests.Count(r => r.Model.StartsWith("memory-", StringComparison.Ordinal));
        public Task Observed(string model) => _observed.GetOrAdd(model, _ => Signal()).Task;

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path.GetTempPath() });
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            _app = builder.Build(); _app.MapPost("/v1/chat/completions", HandleAsync);
            await _app.StartAsync(); Address = new Uri(_app.Urls.Single());
        }

        private async Task HandleAsync(HttpContext context)
        {
            using var outer = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var model = outer.RootElement.GetProperty("model").GetString()!;
            var memory = model.StartsWith("memory-", StringComparison.Ordinal);
            JsonDocument? payload = null;
            if (memory)
            {
                var user = outer.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "user");
                payload = JsonDocument.Parse(user.GetProperty("content").GetString()!);
            }
            using var parsed = payload;
            var sourceMessages = payload?.RootElement.GetProperty("messages").EnumerateArray().ToArray() ?? [];
            var request = new Request
            {
                Model = model, Connection = context.Request.Headers["X-Simulation-Connection"].ToString(),
                SourceIds = sourceMessages.Select(m => m.GetProperty("id").GetInt64()).ToArray()
            };
            Requests.Enqueue(request);
            var count = _counts.AddOrUpdate(model, 1, (_, previous) => previous + 1);
            if (memory) UpdateMaximum(ref _maxMemory, Interlocked.Increment(ref _activeMemory));
            else UpdateMaximum(ref _maxChat, Interlocked.Increment(ref _activeChat));
            _observed.GetOrAdd(model, _ => Signal()).TrySetResult();
            var elapsed = Stopwatch.StartNew();
            try
            {
                var response = memory ? Plan(model, count) : new SimulationResponse();
                if (response.Hang) await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                if (response.DelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(response.DelaySeconds), context.RequestAborted);
                if (!memory) await Task.Delay(100, context.RequestAborted);
                if (model == "memory-clear" && Release is { } release) await release.WaitAsync(context.RequestAborted);
                request.Status = response.Status;
                context.Response.StatusCode = response.Status;
                if (response.RetryAfterSeconds is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString();
                if (response.Status != 200)
                {
                    await context.Response.WriteAsJsonAsync(new { error = "synthetic upstream failure" }, context.RequestAborted); return;
                }
                var content = response.Content ?? (memory ? JsonSerializer.Serialize(new
                {
                    operations = new[] { new
                    {
                        op = "add", subjectUserId = sourceMessages[0].GetProperty("userId").GetString(),
                        content = "喜欢喝茶，不喝咖啡。", tags = new[] { "喝茶", "咖啡" },
                        sourceRowIds = new[] { sourceMessages[0].GetProperty("id").GetInt64() },
                        evidence = AutomaticMemoryRepository.Cut(sourceMessages[0].GetProperty("text").GetString(), 16), kind = "preference"
                    } }
                }) : $"合成回复 {count}：已收到你的消息。");
                await context.Response.WriteAsJsonAsync(new
                {
                    model, choices = new[] { new { message = new { content }, finish_reason = response.Truncated ? "length" : "stop" } },
                    usage = new { prompt_tokens = 100, completion_tokens = 25 }
                }, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { request.ClientCancelled = true; }
            finally
            {
                request.ElapsedMs = (long)elapsed.Elapsed.TotalMilliseconds;
                if (memory) Interlocked.Decrement(ref _activeMemory); else Interlocked.Decrement(ref _activeChat);
            }
        }

        private static void UpdateMaximum(ref int maximum, int value)
        {
            int previous;
            do { previous = Volatile.Read(ref maximum); if (value <= previous) return; }
            while (Interlocked.CompareExchange(ref maximum, value, previous) != previous);
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is null) return;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(stop.Token); await _app.DisposeAsync();
        }
    }
}
