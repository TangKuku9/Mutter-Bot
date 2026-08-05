using System.Text.Json.Serialization;

namespace QQBot.Core.Models.QQOfficial;

public class AuthorUser
{
    [JsonPropertyName("user_openid")]
    public string UserOpenid { get; set; } = string.Empty;
}

public class AuthorMember
{
    [JsonPropertyName("member_openid")]
    public string MemberOpenid { get; set; } = string.Empty;

    [JsonPropertyName("member_role")]
    public string? MemberRole { get; set; }

    [JsonPropertyName("bot")]
    public bool Bot { get; set; }
}

public class C2cMessageEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public AuthorUser Author { get; set; } = new();

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("attachments")]
    public List<Attachment>? Attachments { get; set; }
}

// Serves both GROUP_AT_MESSAGE_CREATE and GROUP_MESSAGE_CREATE; the handler distinguishes by event name (t).
public class GroupMessageEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public AuthorMember Author { get; set; } = new();

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("group_openid")]
    public string GroupOpenid { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("attachments")]
    public List<Attachment>? Attachments { get; set; }
}
