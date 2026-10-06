using System.Text.Json.Serialization;

namespace QQBot.Core.Models.QQOfficial;

// ============================================================================
// 官方 API v2 出站富消息的传输模型（字段与《发送群聊消息》一一对应）。
// 这里只放"线上格式"，不放游戏语义：按钮语义（MenuButton）到本模型的转换
// 由 QQBot.Core.Game.Ui.GameKeyboard 负责，并在那里完成 5×5 / label≤10 钳制。
// ============================================================================

/// <summary>markdown 消息体。msg_type=2 时必填，且此时 content 必须为空。</summary>
public class MessageMarkdownSpec
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>内嵌键盘。id（平台按钮模板）与 content（自定义按钮）互斥。</summary>
public class KeyboardSpec
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeyboardContentSpec? Content { get; set; }
}

public class KeyboardContentSpec
{
    [JsonPropertyName("rows")]
    public List<KeyboardRowSpec> Rows { get; set; } = new();
}

public class KeyboardRowSpec
{
    [JsonPropertyName("buttons")]
    public List<KeyboardButtonSpec> Buttons { get; set; } = new();
}

public class KeyboardButtonSpec
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("render_data")]
    public ButtonRenderDataSpec RenderData { get; set; } = new();

    [JsonPropertyName("action")]
    public ButtonActionSpec Action { get; set; } = new();

    /// <summary>同组按钮点击一个后其余置灰。仅 action.type=1（回调）有效。</summary>
    [JsonPropertyName("group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; set; }
}

public class ButtonRenderDataSpec
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("visited_label")]
    public string VisitedLabel { get; set; } = string.Empty;

    /// <summary>0=灰色线框 1=蓝色线框 3=白底红字 4=蓝底白字。</summary>
    [JsonPropertyName("style")]
    public int Style { get; set; } = 1;
}

public class ButtonActionSpec
{
    /// <summary>0=跳转 1=回调后台 2=指令按钮（输入框插入 @bot data）。</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; } = 2;

    [JsonPropertyName("permission")]
    public ButtonPermissionSpec Permission { get; set; } = new();

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }

    [JsonPropertyName("unsupport_tips")]
    public string UnsupportTips { get; set; } = "请升级QQ版本后使用按钮";

    /// <summary>点击即自动发送 data。官方：仅单聊可用。</summary>
    [JsonPropertyName("enter")]
    public bool Enter { get; set; }

    /// <summary>指令是否带引用回复本消息。</summary>
    [JsonPropertyName("reply")]
    public bool Reply { get; set; }

    [JsonPropertyName("modal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ButtonModalSpec? Modal { get; set; }
}

public class ButtonPermissionSpec
{
    /// <summary>0=指定用户 1=管理员 2=所有人。</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; } = 2;

    [JsonPropertyName("specify_user_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SpecifyUserIds { get; set; }
}

/// <summary>二次确认弹窗。content ≤40 字且不得含 URL。</summary>
public class ButtonModalSpec
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("confirm_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfirmText { get; set; }

    [JsonPropertyName("cancel_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CancelText { get; set; }
}

/// <summary>引用回复。message_id 取消息事件 message_scene.ext 的 msg_idx，或自发消息响应的 ext_info.ref_idx。</summary>
public class MessageReferenceSpec
{
    [JsonPropertyName("message_id")]
    public string MessageId { get; set; } = string.Empty;
}

/// <summary>
/// 出站消息的统一抽象：由调用方（游戏层 / 事件层）构造，API 客户端据此决定 msg_type。
/// MarkdownText 非空即走 msg_type=2（此时 PlainText 被忽略，仅作降级备份随包携带）。
/// </summary>
public sealed record OutboundMessage
{
    /// <summary>纯文本正文（msg_type=0），同时是 markdown 被拒时的降级内容。</summary>
    public string? PlainText { get; init; }

    /// <summary>markdown 正文（msg_type=2）。非空则优先于 PlainText。</summary>
    public string? MarkdownText { get; init; }

    /// <summary>按钮键盘。官方约束：只能挂在 markdown 消息上。</summary>
    public KeyboardSpec? Keyboard { get; init; }

    /// <summary>被动回复的消息 ID（5 分钟有效、每条最多回复 5 次）。</summary>
    public string? MsgId { get; init; }

    /// <summary>被动回复的事件 ID（INTERACTION_CREATE 等），与 MsgId 二选一。</summary>
    public string? EventId { get; init; }

    public int? MsgSeq { get; init; }

    /// <summary>引用回复的消息索引，null 表示不引用。</summary>
    public string? RefMessageId { get; init; }

    /// <summary>是否主动消息（无 msg_id / event_id）。主动消息受月度配额限制。</summary>
    public bool IsActive => MsgId is null && EventId is null;

    public bool UseMarkdown => !string.IsNullOrEmpty(MarkdownText);

    public int MsgType => UseMarkdown ? 2 : 0;

    public static OutboundMessage Text(string plain, string? msgId = null, int? msgSeq = null) =>
        new() { PlainText = plain, MsgId = msgId, MsgSeq = msgSeq };
}
