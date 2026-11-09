using System.Diagnostics;
using Mutter.Core.Models;

namespace Mutter.Core.Services;

/// <summary>整条生成流程共享截止时间、尝试次数与已知用量；不自动重试未知结果。</summary>
public sealed class AiCallScope : IDisposable
{
    private static readonly AsyncLocal<AiCallScope?> Ambient = new();
    private static TimeSpan _defaultTimeout = TimeSpan.FromSeconds(120);
    private readonly AiCallScope? _parent;
    private readonly CancellationTokenSource? _deadline;
    private readonly CancellationToken _hostToken;
    private readonly AiCallScope _root;
    /// <summary>根期限与起点只取值于根作用域；嵌套作用域共享同一条预算。</summary>
    private readonly TimeSpan _timeout;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private int _attempts;
    private int _promptTokens;
    private int _completionTokens;
    private readonly int _startPromptTokens;
    private readonly int _startCompletionTokens;

    private AiCallScope(CancellationToken ct, string? purpose, AiCallMetadata? metadata)
    {
        _parent = Ambient.Value;
        _root = _parent?._root ?? this;
        _hostToken = _parent?._root._hostToken ?? ct;
        Purpose = purpose ?? _parent?.Purpose ?? "chat";
        OperationId = _parent?.OperationId ?? RuntimeActivity.Current?.Id ?? Guid.NewGuid().ToString("N");
        Metadata = metadata ?? _parent?.Metadata ?? new();
        if (_parent is null)
        {
            _timeout = _defaultTimeout;
            _deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _deadline.CancelAfter(_timeout);
        }
        Token = _root._deadline!.Token;
        _startPromptTokens = _root._promptTokens;
        _startCompletionTokens = _root._completionTokens;
        Ambient.Value = this;
    }

    public static AiCallScope Begin(CancellationToken ct, string? purpose = null, AiCallMetadata? metadata = null) => new(ct, purpose, metadata);
    public static AiCallScope? Current => Ambient.Value?._root;

    /// <summary>宿主启动时从 RuntimeOptions 下发根期限；测试与未装配场景保持 120 秒默认。</summary>
    public static void Configure(TimeSpan rootTimeout) =>
        _defaultTimeout = rootTimeout > TimeSpan.Zero ? rootTimeout : TimeSpan.FromSeconds(120);

    /// <summary>根期限的剩余预算。单次模型尝试取 min(尝试上限, 剩余)，让降级链的后续阶段总有预算可用。</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = _root._timeout - Stopwatch.GetElapsedTime(_root._startedAt);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }
    public CancellationToken Token { get; }
    /// <summary>宿主/队列传入的原始令牌；与 120 秒期限取消区分开。</summary>
    public CancellationToken HostToken => _root._hostToken;
    public string Purpose { get; }
    public string OperationId { get; }
    public AiCallMetadata Metadata { get; }
    public string Stage { get; set; } = "generate";
    public int Attempts => _root._attempts;

    public void BeginAttempt()
    {
        Token.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _root._attempts) > 4)
            throw new InvalidOperationException("AI 调用已达到本轮 4 次尝试上限。");
    }

    public void RecordUsage(int prompt, int completion)
    {
        Interlocked.Add(ref _root._promptTokens, prompt);
        Interlocked.Add(ref _root._completionTokens, completion);
    }

    public AiChatResponse WithUsage(AiChatResponse response)
    {
        response.PromptTokens = _root._promptTokens - _startPromptTokens;
        response.CompletionTokens = _root._completionTokens - _startCompletionTokens;
        return response;
    }

    public void Dispose()
    {
        Ambient.Value = _parent;
        _deadline?.Dispose();
    }
}
