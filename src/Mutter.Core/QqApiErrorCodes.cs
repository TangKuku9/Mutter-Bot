namespace Mutter.Core;

/// <summary>
/// QQ 开放接口错误码分类。仅用于「能力降级」判定，不改变业务错误处理语义。
/// 分类依据官方文档《发送群聊消息》《API 调用指南》错误码表。
/// </summary>
public static class QqApiErrorCodes
{
    /// <summary>无 Markdown 能力（权限缺失或语法被拒）→ 降级纯文本重发。</summary>
    public static bool IsMarkdownDenied(int errCode) => MarkdownDenied.Contains(errCode);

    /// <summary>无消息按钮组件能力或键盘参数被拒 → 降级为无按钮（文字链/纯文本）。</summary>
    public static bool IsKeyboardDenied(int errCode) => KeyboardDenied.Contains(errCode);

    /// <summary>被动回复槽位失效（过期/超次数/去重）→ 走既有降级主动消息分支。</summary>
    public static bool IsPassiveExpired(int errCode) => PassiveExpired.Contains(errCode);

    private static readonly HashSet<int> MarkdownDenied = new()
    {
        304036,   // 无 Markdown 模板权限
        40034127, // 无 markdown 模板权限
        40034011, // 无效的 markdown 内容
        40034124, // markdown 消息参数错误
        40034008, // markdown 参数有空值
        40034009, // markdown 参数有换行符
        40034010, // 模版参数中不能含有 markdown 语法
        50056,    // 不允许发送 markdown content
        50057,    // markdown 参数只支持原生语法或模版二选一
        22006,    // 消息类型与内容不匹配
        340069,   // 消息类型无效
    };

    private static readonly HashSet<int> KeyboardDenied = new()
    {
        304037,   // 没有发消息按钮组件的权限
        304038,   // 消息按钮组件不存在
        304039,   // 消息按钮组件解析错误
        305007,   // 键盘样式参数错误
        40034029, // 内联键盘行/列超限
        40034106, // 消息不支持该指令类型
        40034108, // 指令参数长度超限
        40034109, // 指令参数解析失败
    };

    private static readonly HashSet<int> PassiveExpired = new()
    {
        3040103,  // 消息 ID 已过期，不能回复
        40034005, // 回复消息 msg_id 已过期
        40034024, // 请求参数 msg_id 无效或越权
        40034025, // 请求参数 event_id 无效
        40034026, // 请求参数 event_id 已过期
        40034027, // 该事件不支持回复消息
        40034028, // 被动回复时间或次数超限
        40034100, // 主动消息发送超过频控限制
        40054005, // 消息被去重
    };
}
