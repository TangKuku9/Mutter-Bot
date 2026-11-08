using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Services;

namespace Mutter.Infrastructure.Services;

/// <summary>会话公平调度：每次取一项，再将仍有工作的会话放回队尾。</summary>
public sealed class ConversationWorkQueue(
    ILogger<ConversationWorkQueue> logger, TimeProvider clock,
    int capacity = 256, int parallelism = 3, IHostApplicationLifetime? lifetime = null,
    RuntimeOptions? runtime = null) : BackgroundService, IConversationWorkQueue
{
    // runtime（来自 Mutter:Runtime 配置）优先于旧的位置参数；不传 runtime 的直构（自检工具）保持今日默认值。
    private readonly int _capacity = runtime?.Capacity ?? capacity;
    private readonly int _parallelism = runtime?.Parallelism ?? parallelism;
    private readonly int _perConversationCap = runtime?.PerConversationCap ?? 16;
    private readonly TimeSpan _defaultQueueAge =
        runtime is null ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(runtime.QueueAgeSeconds);
    private readonly TimeSpan _workDeadline =
        runtime is null ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(runtime.WorkDeadlineSeconds);

    private sealed class Work(Func<CancellationToken, Task> Run, bool Background, bool Coalesceable,
        Func<CancellationToken, Task>? OnExpired, ConversationTaskTrace Trace, ConversationReplyBatch? ReplyBatch)
    {
        public Func<CancellationToken, Task> Run { get; } = Run;
        public bool Background { get; } = Background;
        public bool Coalesceable { get; } = Coalesceable;
        public bool Claimed { get; set; }
        public ConversationReplyBatch? ReplyBatch { get; } = ReplyBatch;
        /// <summary>可变：同会话的后续 @ 合并进来时刷新，给这条回复多争取一次执行机会。</summary>
        public DateTimeOffset ExpiresAt { get; set; }
        public Func<CancellationToken, Task>? OnExpired { get; } = OnExpired;
        public ConversationTaskTrace Trace { get; } = Trace;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<Work>> _sessions = new();
    private readonly Queue<string> _foreground = new();
    private readonly Queue<string> _background = new();
    private readonly Channel<bool> _ready = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false
    });
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>工作令牌由队列自己持有。宿主 ApplicationStopping 只关闭入口，不能把在途工作一起取消。</summary>
    private readonly CancellationTokenSource _workCts = new();
    private bool _accepting = true;
    private int _outstanding;
    private int _backgroundInFlight;
    private int _running;
    private long _rejected;
    private long _expired;
    private long _coalesced;
    private readonly Queue<ConversationTaskTrace> _recent = new();

    public EnqueueResult TryEnqueue(string conversationId, Func<CancellationToken, Task> work,
        bool background = false, TimeSpan? maxQueueAge = null,
        Func<CancellationToken, Task>? onExpired = null, string? description = null, bool coalesce = false,
        ConversationReplyBatch? replyBatch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        lock (_gate)
        {
            if (!_accepting) { _rejected++; return EnqueueResult.Rejected; }
            _sessions.TryGetValue(conversationId, out var queue);
            if (coalesce && !background && queue is not null)
            {
                // 热点群收敛：同会话已有可合并的排队回复时，这条 @ 不再单独排队。
                // 上下文是执行时构建的，那条回复会看到本次 @ 并一并回应；刷新过期时间多给它争取一次机会。
                // 领取与合并使用同一把锁；过期提示仍在发送时也不能再合并。
                Work? newestPending = null;
                foreach (var pending in queue)
                    if (!pending.Claimed) newestPending = pending;
                if (newestPending is { Coalesceable: true, Background: false }
                    && (replyBatch is null ? newestPending.ReplyBatch is null
                        : newestPending.ReplyBatch?.TryMerge(replyBatch) == true))
                {
                    newestPending.ExpiresAt = clock.GetUtcNow() + (maxQueueAge ?? _defaultQueueAge);
                    _coalesced++;
                    return EnqueueResult.Coalesced;
                }
            }
            // 合并不增加容量占用，满队列时仍可吸收同会话输入。
            if (_outstanding >= _capacity || queue?.Count >= _perConversationCap)
            { _rejected++; return EnqueueResult.Rejected; }
            if (queue is null)
            {
                queue = new Queue<Work>();
                _sessions.Add(conversationId, queue);
                (background ? _background : _foreground).Enqueue(conversationId);
                _ready.Writer.TryWrite(true);
            }
            queue.Enqueue(new Work(work, background, coalesce, onExpired,
                new(conversationId, description ?? (background ? "主动评估" : "会话任务"), background, clock.GetUtcNow(), clock), replyBatch)
            {
                ExpiresAt = clock.GetUtcNow() + (maxQueueAge ?? _defaultQueueAge)
            });
            _outstanding++;
            return EnqueueResult.Accepted;
        }
    }

    public ConversationQueueSnapshot Snapshot()
    {
        lock (_gate) return new(_capacity, _parallelism, _outstanding - _running, _running, _rejected, _expired,
            _coalesced, _accepting, _sessions.Values.SelectMany(q => q).Select(w => w.Trace.Snapshot())
                .Concat(_recent.Reverse().Select(t => t.Snapshot())).ToArray());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 宿主停机时 ApplicationStopping 早于 StopAsync 触发。这里只停止接纳新工作，
        // worker 继续使用队列自己的令牌，把已接纳的任务排空。
        using var registration = stoppingToken.Register(StopAccepting);
        using var hostRegistration = lifetime?.ApplicationStopping.Register(StopAccepting) ?? default;
        await Task.WhenAll(Enumerable.Range(0, _parallelism).Select(_ => WorkerAsync(_workCts.Token)));
    }

    private void StopAccepting()
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
    }

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
                work.Claimed = true;
                _running++;
                if (work.Background) _backgroundInFlight++;
            }
            using var activity = new RuntimeActivity(work.Trace);
            var state = "completed";
            try
            {
                if (clock.GetUtcNow() >= work.ExpiresAt)
                {
                    state = "expired";
                    lock (_gate) _expired++;
                    logger.LogWarning("Conversation work expired conversation={Conversation}", id);
                    if (work.OnExpired is not null) await work.OnExpired(ct);
                    work.Trace.Outcome("expired");
                }
                else
                {
                    work.Trace.Start();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(_workDeadline);
                    await work.Run(deadline.Token);
                }
            }
            catch (OperationCanceledException)
            {
                state = ct.IsCancellationRequested ? "cancelled" : "timeout";
                work.Trace.Outcome(state);
            }
            catch (Exception ex)
            {
                state = "failed";
                work.Trace.Outcome("failed", ex.GetType().Name);
                logger.LogError(ex, "Conversation work failed conversation={Conversation}", id);
            }
            finally
            {
                work.Trace.Finish(state);
                lock (_gate)
                {
                    _running--;
                    _recent.Enqueue(work.Trace);
                    while (_recent.Count > 100) _recent.Dequeue();
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
        StopAccepting();
        var drained = false;
        try
        {
            await _drained.Task.WaitAsync(cancellationToken);
            drained = true;
        }
        catch (OperationCanceledException) { logger.LogWarning("Conversation drain deadline reached"); }
        finally
        {
            // 只有排空期限已到才取消剩余工作；正常停机时让已接纳的任务跑完。
            if (!drained) _workCts.Cancel();
            await base.StopAsync(cancellationToken);
            if (ExecuteTask is not null)
            {
                try { await ExecuteTask.WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (OperationCanceledException) { }
                catch (TimeoutException)
                {
                    _workCts.Cancel();
                    logger.LogWarning("Conversation cancellation deadline reached");
                }
            }
        }
    }

    public override void Dispose()
    {
        _workCts.Cancel();
        base.Dispose();
    }
}
