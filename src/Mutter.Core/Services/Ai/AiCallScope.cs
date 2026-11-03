using Mutter.Core.Models;

namespace Mutter.Core.Services;

/// <summary>整条生成流程共享截止时间、尝试次数与已知用量；不自动重试未知结果。</summary>
public sealed class AiCallScope : IDisposable
{
    private static readonly AsyncLocal<AiCallScope?> Ambient = new();
    private readonly AiCallScope? _parent;
    private readonly CancellationTokenSource? _deadline;
    private readonly AiCallScope _root;
    private int _attempts;
    private int _promptTokens;
    private int _completionTokens;
    private readonly int _startPromptTokens;
    private readonly int _startCompletionTokens;

    private AiCallScope(CancellationToken ct, string? purpose)
    {
        _parent = Ambient.Value;
        _root = _parent?._root ?? this;
        Purpose = purpose ?? _parent?.Purpose ?? "chat";
        if (_parent is null)
        {
            _deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _deadline.CancelAfter(TimeSpan.FromSeconds(120));
        }
        Token = _root._deadline!.Token;
        _startPromptTokens = _root._promptTokens;
        _startCompletionTokens = _root._completionTokens;
        Ambient.Value = this;
    }

    public static AiCallScope Begin(CancellationToken ct, string? purpose = null) => new(ct, purpose);
    public static AiCallScope? Current => Ambient.Value?._root;
    public CancellationToken Token { get; }
    public string Purpose { get; }
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
