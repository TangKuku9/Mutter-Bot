using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Proactive;
using Mutter.Core.Services;

namespace Mutter.Infrastructure.Services;

/// <summary>只负责定时调度，单次执行由 ProactiveWorkflow 完成。</summary>
public sealed class ProactiveChatHostedService(
    IServiceScopeFactory scopes, ProactiveObserveService observe,
    ProactiveWorkflow workflow, IConversationWorkQueue queue,
    UpstreamHealthTracker health, RuntimeOptions runtime,
    TimeProvider clock, ILogger<ProactiveChatHostedService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, byte> _scheduled = new();
    private bool _lastSeenDegraded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), clock);
        while (true)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "择机调度失败"); }

            // 等待本身也必须包在停机分支里；放在 do-while 条件中会把退出异常抛到 ExecuteAsync 外。
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // 上游降级时暂停主动评估：可丢弃的锦上添花让路给用户应答，也停止给坏掉的上游添负载。
        // 观察窗口原样保留，恢复后的下一个 tick 照常评估。
        if (health.IsDegraded)
        {
            if (!_lastSeenDegraded)
            {
                _lastSeenDegraded = true;
                logger.LogWarning("上游 AI 降级，暂停主动发言评估（观察窗口保留，恢复后继续）");
            }
            return;
        }
        if (_lastSeenDegraded)
        {
            _lastSeenDegraded = false;
            logger.LogInformation("上游 AI 恢复，继续主动发言评估");
        }
        using var scope = scopes.CreateScope();
        var config = await scope.ServiceProvider.GetRequiredService<IConfigRepository>().GetProactiveConfigAsync(ct);
        if (!config.Enabled) return;
        foreach (var (group, _) in observe.PendingWindows())
        {
            if (!observe.IsDue(group, config, clock.GetUtcNow().UtcDateTime) || !_scheduled.TryAdd(group, 0)) continue;
            var result = queue.TryEnqueue($"g:{group}", async token =>
            {
                try
                {
                    var window = observe.Take(group, config.WindowMaxMessages);
                    if (window is null) return;
                    observe.MarkEvaluated(group, clock.GetUtcNow().UtcDateTime);
                    await workflow.RunAsync(window, token);
                }
                finally { _scheduled.TryRemove(group, out _); }
            }, background: true, maxQueueAge: TimeSpan.FromSeconds(runtime.BackgroundQueueAgeSeconds), onExpired: expiryToken =>
            {
                _scheduled.TryRemove(group, out _);
                return Task.CompletedTask;
            });
            if (result == EnqueueResult.Rejected) _scheduled.TryRemove(group, out _);
        }
    }
}
