using System.Text.Json.Serialization;

namespace QQBot.Core.Models.QQOfficial;

/// <summary>
/// INTERACTION_CREATE 事件体（Intent = INTERACTION 1&lt;&lt;26）。
/// type=11 消息按钮点击、12 单聊快捷菜单，这两类必须在 3 秒内回应；
/// 13 点赞点踩 / 14 清空会话 / 18~20 授权等无需回应。
/// </summary>
public class InteractionCreateEvent
{
    /// <summary>事件 ID，可用于被动消息发送（event_id）与互动回调。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; set; }

    /// <summary>c2c / group / guild</summary>
    [JsonPropertyName("scene")]
    public string? Scene { get; set; }

    /// <summary>0=频道 1=群聊 2=单聊</summary>
    [JsonPropertyName("chat_type")]
    public int ChatType { get; set; }

    [JsonPropertyName("group_openid")]
    public string? GroupOpenid { get; set; }

    [JsonPropertyName("group_member_openid")]
    public string? GroupMemberOpenid { get; set; }

    [JsonPropertyName("user_openid")]
    public string? UserOpenid { get; set; }

    [JsonPropertyName("application_id")]
    public string? ApplicationId { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("data")]
    public InteractionData? Data { get; set; }

    /// <summary>消息按钮点击（需回应）。</summary>
    public const int TypeInlineKeyboard = 11;

    /// <summary>单聊快捷菜单点击（需回应）。</summary>
    public const int TypeCallbackCommand = 12;

    public bool NeedsResponse => Type is TypeInlineKeyboard or TypeCallbackCommand;

    /// <summary>按钮点击时携带的 data（发送消息按钮时设置）。</summary>
    public string? ButtonData => Data?.Resolved?.ButtonData;

    public string? ButtonId => Data?.Resolved?.ButtonId;

    /// <summary>群成员 openid：群聊取 group_member_openid，单聊取 user_openid。</summary>
    public string? ActorOpenid => GroupMemberOpenid ?? UserOpenid;

    /// <summary>
    /// 被动回复消息用的 event_id：官方要求带 "INTERACTION_CREATE:" 前缀（发送群/单聊消息的 event_id 字段），
    /// 与 PUT /interactions/{id} 用的裸 <see cref="Id"/> 相反——回应接口文档明确提示该 ID 不带此前缀。
    /// 用裸 Id 当 event_id 会被平台判为 40034025（event_id 无效）。
    /// </summary>
    public string PassiveReplyEventId => $"INTERACTION_CREATE:{Id}";
}

public class InteractionData
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("resolved")]
    public InteractionResolved? Resolved { get; set; }
}

public class InteractionResolved
{
    [JsonPropertyName("button_data")]
    public string? ButtonData { get; set; }

    [JsonPropertyName("button_id")]
    public string? ButtonId { get; set; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("feature_id")]
    public string? FeatureId { get; set; }
}

/// <summary>
/// 互动回应（PUT /interactions/{interaction_id}）。
/// 已核实线上格式：顶层 { "code": 0, "data": {..} }，code=0 为正常回应（不传 data 仅解除客户端 loading）。
/// 官方要求 3 秒内完成，同一 interaction_id 只能回应一次。
/// </summary>
public sealed class InteractionReply
{
    public int Code { get; init; } = 0;

    /// <summary>可选：回应同时携带卡片更新内容（未验证字段集，默认不用）。</summary>
    public string? MarkdownText { get; init; }

    public KeyboardSpec? Keyboard { get; init; }

    /// <summary>仅确认收到，不做卡片更新（重活留给异步结算 + event_id 被动回复）。</summary>
    public static InteractionReply Ok() => new();

    /// <summary>构造 PUT 请求体：code 在顶层，不在 result 里。</summary>
    public object ToWire() => new InteractionReplyWire
    {
        Code = Code,
        Data = MarkdownText is null && Keyboard is null
            ? null
            : new InteractionResultWire
            {
                Markdown = MarkdownText is null ? null : new MessageMarkdownSpec { Content = MarkdownText },
                Keyboard = Keyboard,
            }
    };
}

public class InteractionReplyWire
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InteractionResultWire? Data { get; set; }
}

public class InteractionResultWire
{
    [JsonPropertyName("markdown")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageMarkdownSpec? Markdown { get; set; }

    [JsonPropertyName("keyboard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeyboardSpec? Keyboard { get; set; }
}
