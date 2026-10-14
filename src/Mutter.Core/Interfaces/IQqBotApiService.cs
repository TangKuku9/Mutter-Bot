using Mutter.Core.Models;
using Mutter.Core.Models.QQOfficial;

namespace Mutter.Core.Interfaces;

public interface IQqBotApiService
{
    string BotId { get; }
    string BotName { get; }

    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>发送群文本消息，返回平台分配的消息 id（用于撤回），解析失败为 null。</summary>
    Task<string?> SendGroupMessageAsync(string groupOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default);
    Task<string?> SendPrivateMessageAsync(string userOpenId, string content, string? msgId = null, int? msgSeq = null, CancellationToken ct = default);

    /// <summary>发送群富消息（文本 / markdown / markdown+按钮 / 引用回复），由 msg 决定 msg_type。返回平台分配的消息 id（用于撤回）。</summary>
    Task<string?> SendGroupRichAsync(string groupOpenId, OutboundMessage msg, CancellationToken ct = default);

    /// <summary>发送单聊富消息。返回平台分配的消息 id（用于撤回）。</summary>
    Task<string?> SendPrivateRichAsync(string userOpenId, OutboundMessage msg, CancellationToken ct = default);

    /// <summary>
    /// 撤回机器人自己在群里发送的消息（DELETE /v2/groups/{group_openid}/messages/{message_id}）。
    /// 官方限制：发送超过 2 分钟的消息不可撤回（40064004）。尽力而为：所有失败仅记日志不抛出。
    /// </summary>
    Task<bool> DeleteGroupMessageAsync(string groupOpenId, string messageId, CancellationToken ct = default);

    /// <summary>回应互动（按钮回调）。官方要求 3 秒内完成，同一 interaction_id 只能回应一次。</summary>
    Task ReplyInteractionAsync(string interactionId, InteractionReply reply, CancellationToken ct = default);

    Task<string?> UploadMediaAsync(string groupOpenId, int fileType, string url, CancellationToken ct = default);
}
