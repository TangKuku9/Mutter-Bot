using Mutter.Core.Models;

namespace Mutter.Core.Services;

/// <summary>固定当前批次的历史读取上界，批次以外的后续消息留到自己的轮次。</summary>
public sealed class ConversationTurnScope : IDisposable
{
    private static readonly AsyncLocal<ConversationTurnScope?> Ambient = new();
    private readonly ConversationTurnScope? _previous;
    public static ConversationTurnScope? Current => Ambient.Value;
    public string MessageId { get; }
    public DateTime CreatedAt { get; }
    public long? MessageRowId { get; }
    public IReadOnlySet<string> MessageIds { get; }
    public string? CombinedInput { get; }

    public ConversationTurnScope(string messageId, DateTime createdAt, long? messageRowId = null,
        IReadOnlyList<ChatMessage>? batch = null)
    {
        MessageId = messageId;
        CreatedAt = createdAt;
        MessageRowId = messageRowId;
        MessageIds = batch is null ? new HashSet<string> { messageId }
            : batch.Select(m => m.MessageId).ToHashSet();
        // 整批作为当前输入保留，避免上下文条数裁剪再次丢掉已接纳的合并消息。
        CombinedInput = batch is { Count: > 1 }
            ? string.Join("\n", batch.Select(m => $"[{m.UserName}]: {m.PlainText}")) : null;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }
    public void Dispose() => Ambient.Value = _previous;
}
