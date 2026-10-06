using System.Text.Json.Serialization;

namespace QQBot.Core.Models.QQOfficial;

/// <summary>
/// 《发送群聊消息 / 发送单聊消息》请求体。
/// msg_type：0=纯文本(content)、2=Markdown(markdown.content)、7=富媒体(media)。
/// 传 markdown 时 content 必须为空，故此处由工厂方法保证互斥。
/// </summary>
public class SendMessageRequest
{
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("msg_type")]
    public int MsgType { get; set; }

    [JsonPropertyName("msg_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MsgId { get; set; }

    /// <summary>被动回复事件 ID（INTERACTION_CREATE / GROUP_ADD_ROBOT / GROUP_MSG_RECEIVE），与 msg_id 二选一。</summary>
    [JsonPropertyName("event_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventId { get; set; }

    [JsonPropertyName("msg_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MsgSeq { get; set; }

    [JsonPropertyName("markdown")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageMarkdownSpec? Markdown { get; set; }

    [JsonPropertyName("keyboard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeyboardSpec? Keyboard { get; set; }

    [JsonPropertyName("media")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaInfo? Media { get; set; }

    [JsonPropertyName("file_info")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileInfo { get; set; }

    [JsonPropertyName("message_reference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageReferenceSpec? MessageReference { get; set; }

    /// <summary>纯文本消息（msg_type=0）。签名与历史调用点保持一致。</summary>
    public static SendMessageRequest Text(string content, string? msgId, int? msgSeq) =>
        new() { Content = content, MsgType = 0, MsgId = msgId, MsgSeq = msgSeq };

    /// <summary>由出站抽象构造实际请求：markdown 优先，且此时必须清空 content。</summary>
    public static SendMessageRequest From(OutboundMessage msg)
    {
        if (msg.UseMarkdown)
        {
            return new SendMessageRequest
            {
                MsgType = 2,
                Markdown = new MessageMarkdownSpec { Content = msg.MarkdownText! },
                Keyboard = msg.Keyboard,          // 键盘只能随 markdown 下发
                MsgId = msg.MsgId,
                EventId = msg.EventId,
                MsgSeq = msg.MsgSeq,
                MessageReference = msg.RefMessageId is null
                    ? null
                    : new MessageReferenceSpec { MessageId = msg.RefMessageId },
            };
        }

        return new SendMessageRequest
        {
            MsgType = 0,
            Content = msg.PlainText ?? string.Empty,
            MsgId = msg.MsgId,
            EventId = msg.EventId,
            MsgSeq = msg.MsgSeq,
            MessageReference = msg.RefMessageId is null
                ? null
                : new MessageReferenceSpec { MessageId = msg.RefMessageId },
        };
    }
}

public class MediaInfo
{
    [JsonPropertyName("file_info")]
    public string FileInfo { get; set; } = string.Empty;
}
