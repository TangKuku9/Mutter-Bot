using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Proactive;

namespace Mutter.Infrastructure.Services;

/// <summary>只负责定时调度，单次执行由 ProactiveWorkflow 完成。</summary>
public sealed class ProactiveChatHostedService(
    IServiceScopeFactory scopes, ProactiveObserveService observe,
    ProactiveWorkflow workflow, IConversationWorkQueue queue,
    TimeProvider clock, ILogger<ProactiveChatHostedService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, byte> _scheduled = new();

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
        using var scope = scopes.CreateScope();
        var config = await scope.ServiceProvider.GetRequiredService<IConfigRepository>().GetProactiveConfigAsync(ct);
        if (!config.Enabled) return;
        foreach (var (group, _) in observe.PendingWindows())
        {
            if (!observe.IsDue(group, config, clock.GetUtcNow().UtcDateTime) || !_scheduled.TryAdd(group, 0)) continue;
            var accepted = queue.TryEnqueue($"g:{group}", async token =>
            {
                try
                {
                    var window = observe.Take(group, config.WindowMaxMessages);
                    if (window is null) return;
                    observe.MarkEvaluated(group, clock.GetUtcNow().UtcDateTime);
                    await workflow.RunAsync(window, token);
                }
                finally { _scheduled.TryRemove(group, out _); }
            }, background: true, maxQueueAge: TimeSpan.FromSeconds(45), onExpired: expiryToken =>
            {
                _scheduled.TryRemove(group, out _);
                return Task.CompletedTask;
            });
            if (!accepted) _scheduled.TryRemove(group, out _);
        }
    }
}
