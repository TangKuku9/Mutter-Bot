namespace Mutter.Core.Interfaces;

public interface IConversationWorkQueue
{
    /// <summary>有界接纳；同会话按入队顺序执行，主动工作只在无排队应答时调度。</summary>
    bool TryEnqueue(string conversationId, Func<CancellationToken, Task> work,
        bool background = false, TimeSpan? maxQueueAge = null,
        Func<CancellationToken, Task>? onExpired = null);
}
