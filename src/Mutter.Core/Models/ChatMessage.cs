namespace Mutter.Core.Models;

public class ChatMessage
{
    public long Id { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public int ChatType { get; set; }
    public string? GroupId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string PlainText { get; set; } = string.Empty;
    public string RawSegmentsJson { get; set; } = string.Empty;
    public bool HasImage { get; set; }
    public bool HasAtBot { get; set; }
    public bool IsBotMessage { get; set; }

    /// <summary>Bot 没被 @ 而自己接的话（择机发言），用于区分“应答”与“插话”。</summary>
    public bool IsProactive { get; set; }

    /// <summary>
    /// 私聊里这条 bot 消息是对谁说的。私聊的 bot 行 UserId 存的是 bot 自己，
    /// 没有这个字段就无从知道某条回复属于哪段对话 —— 多个私聊对象时会互相串味。
    /// </summary>
    public string? PeerUserId { get; set; }
    public string? AiModelUsed { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
