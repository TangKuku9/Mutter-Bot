using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Proactive;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Repositories;

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
        // 上游降级时保留观察窗口，冷却后只放行一个受限探测，避免无人 @ 时永远无法恢复。
        if (health.IsDegraded)
        {
            if (!_lastSeenDegraded)
            {
                _lastSeenDegraded = true;
                logger.LogWarning("上游 AI 降级，暂停常规主动评估（保留观察窗口，冷却后受限探测）");
            }
            if (!health.CanScheduleBackground) return;
        }
        else if (_lastSeenDegraded)
        {
            _lastSeenDegraded = false;
            logger.LogInformation("上游 AI 恢复，继续主动发言评估");
        }
        using var scope = scopes.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var global = await configs.GetProactiveConfigAsync(ct);
        if (!global.Enabled) return;
        var pending = observe.PendingWindows();
        if (pending.Count == 0) return;
        var groupIds = pending.Select(p => p.GroupId).ToArray();
        var groups = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GroupConfigs.AsNoTracking()
            .Where(g => groupIds.Contains(g.GroupId))
            .Select(g => new { g.GroupId, g.IsEnabled, g.AiProactiveEnabled, g.ProactiveOverridesJson })
            .ToDictionaryAsync(g => g.GroupId, ct);
        foreach (var (group, _) in pending)
        {
            if (!groups.TryGetValue(group, out var settings) || !settings.IsEnabled || !settings.AiProactiveEnabled)
            {
                observe.Take(group, 1); // 停用的群丢弃旧窗口，重新参与时从新消息开始。
                continue;
            }
            var config = global.WithOverrides(ProactiveOverridesCodec.Decode(settings.ProactiveOverridesJson));
            if (!observe.IsDue(group, config, clock.GetUtcNow().UtcDateTime) || !_scheduled.TryAdd(group, 0)) continue;
            var result = queue.TryEnqueue($"g:{group}", async token =>
            {
                try
                {
                    // 群设置可能在排队期间改变；取走窗口前重读，避免按旧窗口条数和节奏执行。
                    using var latestScope = scopes.CreateScope();
                    var latestConfigs = latestScope.ServiceProvider.GetRequiredService<IConfigRepository>();
                    var latestGlobal = await latestConfigs.GetProactiveConfigAsync(token);
                    var latestGroup = await latestConfigs.GetGroupConfigAsync(group, token);
                    if (!latestGlobal.Enabled) return;
                    if (latestGroup is not { IsEnabled: true, AiProactiveEnabled: true })
                    {
                        observe.Take(group, 1);
                        return;
                    }
                    var latest = latestGlobal.WithOverrides(latestGroup.ProactiveOverrides);
                    if (!observe.IsDue(group, latest, clock.GetUtcNow().UtcDateTime)) return;
                    // 排队期间健康状态可能已变化，必须在取走观察窗口前再次检查。
                    if (!health.TryBeginBackgroundCall(out var probe)) return;
                    using var lease = probe;
                    var window = observe.Take(group, latest.WindowMaxMessages);
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
            else if (health.IsDegraded) break;
        }
    }
}
