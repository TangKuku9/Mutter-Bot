namespace Mutter.Core.Services;

/// <summary>固定当前轮的历史读取上界，排队后到达的消息留到它自己的轮次。</summary>
public sealed class ConversationTurnScope : IDisposable
{
    private static readonly AsyncLocal<ConversationTurnScope?> Ambient = new();
    private readonly ConversationTurnScope? _previous;
    public static ConversationTurnScope? Current => Ambient.Value;
    public string MessageId { get; }
    public DateTime CreatedAt { get; }
    public long? MessageRowId { get; }

    public ConversationTurnScope(string messageId, DateTime createdAt, long? messageRowId = null)
    {
        MessageId = messageId;
        CreatedAt = createdAt;
        MessageRowId = messageRowId;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }
    public void Dispose() => Ambient.Value = _previous;
}
