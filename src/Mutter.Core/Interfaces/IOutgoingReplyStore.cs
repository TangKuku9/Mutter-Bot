using Mutter.Core.Models;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Interfaces;

public interface IOutgoingReplyStore
{
    Task<string?> CheckProactiveAsync(string groupId, ProactiveChatConfig limits, CancellationToken ct);
    /// <summary>持久化发送意图。主动发言同时原子预占冷却/日配额，返回拦截原因。</summary>
    Task<string?> PrepareAsync(OutgoingReply reply, ProactiveChatConfig? limits, CancellationToken ct);
    /// <summary>确认发送后才把内容加入 ChatMessages。重复确认不得重复插入。</summary>
    Task CompleteAsync(OutgoingReply reply, DeliveryResult result, CancellationToken ct);
}
