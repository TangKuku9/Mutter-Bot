using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models.QQOfficial;

namespace Mutter.Infrastructure.Services;

/// <summary>跨 WebSocket 重连保留的有界事件队列；只有整个网关关闭时才限时排空。</summary>
public sealed class GatewayDispatchQueue : IAsyncDisposable
{
    private readonly Channel<QqDispatch> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private readonly TimeSpan _drainTimeout;
    private readonly Task _loop;
    private int _disposed;

    public GatewayDispatchQueue(Func<QqDispatch, CancellationToken, Task> handler, ILogger logger,
        int capacity = 1024, TimeSpan? drainTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _logger = logger;
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5);
        _channel = Channel.CreateBounded<QqDispatch>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        _loop = RunAsync(handler);
    }

    public ValueTask EnqueueAsync(QqDispatch dispatch, CancellationToken ct) => _channel.Writer.WriteAsync(dispatch, ct);

    private async Task RunAsync(Func<QqDispatch, CancellationToken, Task> handler)
    {
        try
        {
            await foreach (var dispatch in _channel.Reader.ReadAllAsync(_stop.Token))
            {
                try { await handler(dispatch, _stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Dispatch 处理异常，eventType={EventType}", dispatch.EventType); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        try { await _loop.WaitAsync(_drainTimeout); }
        catch (TimeoutException)
        {
            _logger.LogWarning("网关关闭时事件队列排空超时，尚有 {Count} 条缓冲事件", _channel.Reader.Count);
            _stop.Cancel();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { _logger.LogWarning("事件处理未响应停机取消"); }
        }
        finally { _stop.Dispose(); }
    }
}
