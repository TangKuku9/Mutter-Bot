using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QQBot.Core.Interfaces;

namespace QQBot.Infrastructure.Services;

/// <summary>
/// 修仙消息自动撤回调度器（单例 + 自托管后台循环）。
/// 官方撤回窗口为发送后 2 分钟，此处设 75 秒延迟，留出时钟漂移与排队余量；
/// 每 3 秒扫描一次到期项，逐条撤回（10 QPS 限频远未触顶）。
/// 进程重启丢失待撤回队列只是漏撤几条，无需持久化。
/// </summary>
public class GameRecallService : IHostedService, IGameRecallService
{
    /// <summary>发送到撤回的延迟。上限 120 秒（官方 2 分钟），留 45 秒余量。</summary>
    private static readonly TimeSpan RecallDelay = TimeSpan.FromSeconds(75);

    private const int ScanIntervalMs = 3000;

    private sealed record PendingRecall(string GroupOpenId, string MessageId, DateTime DueAt);

    private readonly IQqBotApiService _apiService;
    private readonly ILogger<GameRecallService> _logger;
    private readonly ConcurrentDictionary<string, PendingRecall> _pending = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _seq;

    public GameRecallService(IQqBotApiService apiService, ILogger<GameRecallService> logger)
    {
        _apiService = apiService;
        _logger = logger;
    }

    public void Schedule(string groupOpenId, string? messageId)
    {
        if (string.IsNullOrEmpty(groupOpenId) || string.IsNullOrEmpty(messageId)) return;

        // messageId 可能重复（平台 id 全局唯一，兜底防撞），拼序号保证 key 唯一
        var key = $"{messageId}#{Interlocked.Increment(ref _seq)}";
        _pending[key] = new PendingRecall(groupOpenId, messageId, DateTime.UtcNow + RecallDelay);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _logger.LogInformation("修仙消息自动撤回调度器已启动（延迟 {Delay} 秒）", RecallDelay.TotalSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
            catch (Exception) { /* 停机路径不抛 */ }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ScanIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RecallDueAsync(ct);
        }
    }

    private async Task RecallDueAsync(CancellationToken ct)
    {
        if (_pending.IsEmpty) return;

        var now = DateTime.UtcNow;
        foreach (var kv in _pending)
        {
            if (kv.Value.DueAt > now) continue;
            if (!_pending.TryRemove(kv.Key, out var item)) continue;

            var ok = await _apiService.DeleteGroupMessageAsync(item.GroupOpenId, item.MessageId, ct);
            if (ok)
                _logger.LogDebug("已自动撤回修仙消息 group={GroupOpenId}, messageId={MessageId}", item.GroupOpenId, item.MessageId);

            if (_pending.Count <= 1) return; // 无积压时让出循环，等下一轮扫描
        }
    }
}
