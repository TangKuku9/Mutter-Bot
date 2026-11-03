using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;

namespace Mutter.Infrastructure.Services;

/// <summary>会话公平调度：每次取一项，再将仍有工作的会话放回队尾。</summary>
public sealed class ConversationWorkQueue(
    ILogger<ConversationWorkQueue> logger, TimeProvider clock,
    int capacity = 256, int parallelism = 3) : BackgroundService, IConversationWorkQueue
{
    private sealed record Work(Func<CancellationToken, Task> Run, bool Background, DateTimeOffset ExpiresAt,
        Func<CancellationToken, Task>? OnExpired);
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<Work>> _sessions = new();
    private readonly Queue<string> _foreground = new();
    private readonly Queue<string> _background = new();
    private readonly Channel<bool> _ready = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false
    });
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _accepting = true;
    private int _outstanding;
    private int _backgroundInFlight;

    public bool TryEnqueue(string conversationId, Func<CancellationToken, Task> work,
        bool background = false, TimeSpan? maxQueueAge = null,
        Func<CancellationToken, Task>? onExpired = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        lock (_gate)
        {
            if (!_accepting || _outstanding >= capacity) return false;
            if (!_sessions.TryGetValue(conversationId, out var queue))
            {
                queue = new Queue<Work>();
                _sessions.Add(conversationId, queue);
                (background ? _background : _foreground).Enqueue(conversationId);
                _ready.Writer.TryWrite(true);
            }
            // 防止一个会话占满全局队列；包含正在执行的一项。
            if (queue.Count >= 16) return false;
            queue.Enqueue(new Work(work, background,
                clock.GetUtcNow() + (maxQueueAge ?? TimeSpan.FromMinutes(2)), onExpired));
            _outstanding++;
            return true;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, parallelism).Select(_ => WorkerAsync(stoppingToken)));

    private async Task WorkerAsync(CancellationToken ct)
    {
        await foreach (var _ in _ready.Reader.ReadAllAsync(ct))
        {
            string id;
            Work work;
            lock (_gate)
            {
                // 主动工作最多占一个执行槽，给直接应答保留容量。唤醒信号允许合并/过期。
                if (_foreground.Count == 0 && (_background.Count == 0 || _backgroundInFlight >= 1)) continue;
                id = (_foreground.Count > 0 ? _foreground : _background).Dequeue();
                work = _sessions[id].Peek();
                if (work.Background) _backgroundInFlight++;
            }
            try
            {
                if (clock.GetUtcNow() >= work.ExpiresAt)
                {
                    logger.LogWarning("Conversation work expired conversation={Conversation}", id);
                    if (work.OnExpired is not null) await work.OnExpired(ct);
                }
                else
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromMinutes(3));
                    await work.Run(deadline.Token);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Conversation work failed conversation={Conversation}", id); }
            finally
            {
                lock (_gate)
                {
                    var queue = _sessions[id];
                    queue.Dequeue();
                    _outstanding--;
                    if (work.Background) _backgroundInFlight--;
                    if (queue.Count == 0) _sessions.Remove(id);
                    else
                    {
                        (queue.Peek().Background ? _background : _foreground).Enqueue(id);
                        _ready.Writer.TryWrite(true);
                    }
                    if (_background.Count > 0 && _backgroundInFlight == 0) _ready.Writer.TryWrite(true);
                    if (!_accepting && _outstanding == 0)
                    {
                        _ready.Writer.TryComplete();
                        _drained.TrySetResult();
                    }
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _accepting = false;
            if (_outstanding == 0)
            {
                _ready.Writer.TryComplete();
                _drained.TrySetResult();
            }
        }
        try { await _drained.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { logger.LogWarning("Conversation drain deadline reached"); }
        finally
        {
            await base.StopAsync(cancellationToken);
            if (ExecuteTask is not null)
            {
                try { await ExecuteTask.WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { logger.LogWarning("Conversation cancellation deadline reached"); }
            }
        }
    }
}
