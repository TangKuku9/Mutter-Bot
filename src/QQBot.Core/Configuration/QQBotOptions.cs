namespace QQBot.Core.Configuration;

public class QQBotOptions
{
    public string AppId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.bot.qq.com";

    /// <summary>修仙系统富交互（markdown 卡片 / 按钮菜单 / @标签）开关与能力门控配置。</summary>
    public RichUiOptions RichUi { get; set; } = new();
}

/// <summary>消息按钮键盘模式。None=纯文字链或提示；Command=指令按钮(插入输入框)；Callback=回调按钮(需 INTERACTION intent)。</summary>
public enum GameKeyboardMode
{
    None = 0,
    Command = 1,
    Callback = 2
}

/// <summary>
/// 富交互配置。总开关 Enabled 默认关闭，逐群（GroupAllowList）灰度；
/// 各能力（Markdown / 键盘 / @标签）由 RichUiCapability 按平台实际返回错误码动态降级。
/// </summary>
public class RichUiOptions
{
    /// <summary>富交互总开关。关闭时所有面板回落到既有纯文本渲染（零行为变化）。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>允许发送 msg_type=2 的 Markdown 消息。</summary>
    public bool Markdown { get; set; } = true;

    /// <summary>
    /// 按钮键盘模式：None / Command / Callback（Callback 需先获 INTERACTION 事件授权）。
    /// 故意用字符串承载：生产 appsettings 由运维手改，写错值不能导致进程启动即挂，
    /// 解析失败时按 None（纯文本菜单/文字链）兼容处理，见 <see cref="ResolveKeyboardMode"/>。
    /// </summary>
    public string KeyboardMode { get; set; } = "None";

    /// <summary>解析键盘模式；无法识别的值退回 None 而不抛异常。</summary>
    public GameKeyboardMode ResolveKeyboardMode() =>
        Enum.TryParse<GameKeyboardMode>(KeyboardMode, ignoreCase: true, out var mode) ? mode : GameKeyboardMode.None;

    /// <summary>无按钮组件权限时，用 markdown 内嵌文字链（&lt;qqbot-cmd-input&gt;）替代按钮。</summary>
    public bool CmdInputLinks { get; set; } = true;

    /// <summary>使用 &lt;qqbot-at-user&gt; 真实 @ 玩家（关闭则退回文本道号）。</summary>
    public bool AtUserTags { get; set; } = true;

    /// <summary>被动回复带 message_reference 引用原消息（默认关，观察兼容性后再开）。</summary>
    public bool MessageReference { get; set; } = false;

    /// <summary>订阅 INTERACTION_CREATE (1&lt;&lt;26) intent。⚠ 须先获平台授权，误配会导致网关断连。</summary>
    public bool InteractionIntent { get; set; } = false;

    /// <summary>灰度群白名单；空=全部生效。仅影响富交互，不影响纯文本兜底可用性。</summary>
    public List<string> GroupAllowList { get; set; } = new();

    /// <summary>某能力被平台拒绝后，重新探测的间隔分钟数（避免每条消息都撞一次坏权限）。</summary>
    public int CapabilityTtlMinutes { get; set; } = 360;

    /// <summary>键盘最大行数（官方上限 5）。</summary>
    public int MaxRows { get; set; } = 5;

    /// <summary>每行最大按钮数（官方上限 5）。</summary>
    public int MaxButtonsPerRow { get; set; } = 5;

    /// <summary>Markdown 正文最大字符数，超出截断（防 40054007 长度超限）。</summary>
    public int MaxMarkdownChars { get; set; } = 2000;
}
