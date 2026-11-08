namespace Mutter.Core.Models;

public sealed record RuntimeStep(DateTimeOffset At, string Stage, string? Detail);
public sealed record ConversationTaskSnapshot(string Id, string ConversationId, string Description,
    bool Background, DateTimeOffset EnqueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    string State, string? Outcome, IReadOnlyList<RuntimeStep> Steps);
public sealed record ConversationQueueSnapshot(int Capacity, int Parallelism, int Waiting, int Running,
    long Rejected, long Expired, long Coalesced, bool Accepting, IReadOnlyList<ConversationTaskSnapshot> Tasks);

/// <summary>仅保留最近任务的有限步骤；调用与发送回执另行持久化。</summary>
public sealed class ConversationTaskTrace(string conversationId, string description, bool background,
    DateTimeOffset enqueuedAt, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly List<RuntimeStep> _steps = [new(enqueuedAt, "queued", null)];
    public string Id { get; } = Guid.NewGuid().ToString("N");
    /// <summary>该项是否已开始执行。合并判定用它区分「正在执行的一项」（不能并进去）和真正在排队的项。</summary>
    public bool Started { get; private set; }
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private string _state = "queued";
    private string? _outcome;

    public void Start()
    {
        lock (_gate) { Started = true; _startedAt = clock.GetUtcNow(); _state = "running"; Mark("started"); }
    }
    public void Mark(string stage, string? detail = null)
    {
        lock (_gate)
        {
            if (_steps.Count >= 32) _steps.RemoveAt(1);
            _steps.Add(new(clock.GetUtcNow(), stage, detail));
        }
    }
    public void Outcome(string outcome, string? detail = null)
    {
        lock (_gate) { _outcome = outcome; Mark(outcome, detail); }
    }
    public void Finish(string state)
    {
        lock (_gate) { _state = state; _completedAt = clock.GetUtcNow(); Mark(state); }
    }
    public ConversationTaskSnapshot Snapshot()
    {
        lock (_gate) return new(Id, conversationId, description, background, enqueuedAt,
            _startedAt, _completedAt, _state, _outcome, _steps.ToArray());
    }
}
