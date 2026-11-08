namespace Mutter.Core.Interfaces;

/// <summary>入队结果：Accepted 正常排队；Coalesced 已合并进该会话已有的排队回复；Rejected 有界拒绝。</summary>
public enum EnqueueResult
{
    Accepted = 0,
    Coalesced = 1,
    Rejected = 2
}

public interface IConversationWorkQueue
{
    /// <summary>有界接纳；同会话按入队顺序执行，主动工作只在无排队应答时调度。
    /// coalesce=true（AI 回复路径）且会话已有可合并的排队回复时，新条目被吸收：
    /// 刷新那条排队的过期时间即可，不单独排队、不发任何提示——回复执行时构建的上下文会看到这条晚到的消息。</summary>
    EnqueueResult TryEnqueue(string conversationId, Func<CancellationToken, Task> work,
        bool background = false, TimeSpan? maxQueueAge = null,
        Func<CancellationToken, Task>? onExpired = null, string? description = null, bool coalesce = false);

    Mutter.Core.Models.ConversationQueueSnapshot Snapshot();
}
