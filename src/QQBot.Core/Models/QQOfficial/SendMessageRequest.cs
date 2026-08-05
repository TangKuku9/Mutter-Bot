using System.Text.Json.Serialization;

namespace QQBot.Core.Models.QQOfficial;

public class SendMessageRequest
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("msg_type")]
    public int MsgType { get; set; }

    [JsonPropertyName("msg_id")]
    public string? MsgId { get; set; }

    [JsonPropertyName("msg_seq")]
    public int? MsgSeq { get; set; }

    [JsonPropertyName("markdown")]
    public object? Markdown { get; set; }

    [JsonPropertyName("keyboard")]
    public object? Keyboard { get; set; }

    [JsonPropertyName("media")]
    public MediaInfo? Media { get; set; }

    [JsonPropertyName("file_info")]
    public string? FileInfo { get; set; }

    public static SendMessageRequest Text(string content, string? msgId, int? msgSeq) =>
        new() { Content = content, MsgType = 0, MsgId = msgId, MsgSeq = msgSeq };
}

public class MediaInfo
{
    [JsonPropertyName("file_info")]
    public string FileInfo { get; set; } = string.Empty;
}
