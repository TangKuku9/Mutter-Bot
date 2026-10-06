using System.Text.Json.Serialization;

namespace QQBot.Core.Models.QQOfficial;

public class AuthorUser
{
    [JsonPropertyName("user_openid")]
    public string UserOpenid { get; set; } = string.Empty;

    /// <summary>用户昵称。官方事件体已提供，用于面板展示真实道号与首行 @ 占宽估算。</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

public class AuthorMember
{
    [JsonPropertyName("member_openid")]
    public string MemberOpenid { get; set; } = string.Empty;

    [JsonPropertyName("member_role")]
    public string? MemberRole { get; set; }

    [JsonPropertyName("bot")]
    public bool Bot { get; set; }

    /// <summary>群成员昵称（历史上未解析，导致面板只能显示“道友XXXX”）。</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

/// <summary>消息场景上下文。ext 里的 msg_idx=REFIDX_xxx 用于引用回复。</summary>
public class MessageScene
{
    /// <summary>default=普通聊天窗口</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("ext")]
    public List<string>? Ext { get; set; }

    /// <summary>取引用回复用的消息索引（msg_idx=REFIDX_…）。</summary>
    public string? MsgIdx
    {
        get
        {
            if (Ext is null) return null;
            foreach (var kv in Ext)
            {
                if (kv.StartsWith("msg_idx=", StringComparison.Ordinal))
                    return kv["msg_idx=".Length..];
            }
            return null;
        }
    }
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

    [JsonPropertyName("message_scene")]
    public MessageScene? MessageScene { get; set; }

    [JsonPropertyName("attachments")]
    public List<Attachment>? Attachments { get; set; }
}
