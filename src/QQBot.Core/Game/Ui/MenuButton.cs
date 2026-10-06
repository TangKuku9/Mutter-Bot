namespace QQBot.Core.Game.Ui;

/// <summary>
/// 菜单按钮的语义描述（与渲染方式无关）。同一份定义可渲染为：
/// 真实键盘按钮（KeyboardSpec）、markdown 文字链（&lt;qqbot-cmd-input&gt;）或纯文本提示。
/// </summary>
/// <param name="Label">按钮文字，官方上限 10 字符。</param>
/// <param name="Command">
/// 点击后交给玩家的指令文本，必须是 <see cref="Services.GameCommandRouter"/> 能命中的**裸关键词**
/// （如 "签到"、"炼丹 凝气丹"、"选 2"）。禁止前导 "/"：那会被 BotCommandService 当作管理员
/// 斜杠命令拦截（群主/管理员点击会得到 "Unknown command"），也禁止全角空格等不可见分隔。
/// </param>
/// <param name="VisitedLabel">点击后按钮文字，缺省与 Label 相同。</param>
/// <param name="Style">0=灰线框 1=蓝线框 3=白底红字 4=蓝底白字。</param>
/// <param name="GroupId">互斥分组：同组内一个按钮被点击后其余置灰（仅回调按钮生效）。</param>
/// <param name="ConfirmText">非空则点击前弹二次确认（官方上限 40 字、禁 URL）。</param>
public sealed record MenuButton(
    string Label,
    string Command,
    string? VisitedLabel = null,
    int Style = 1,
    string? GroupId = null,
    string? ConfirmText = null)
{
    /// <summary>官方按钮文字上限。</summary>
    public const int MaxLabelChars = 10;

    /// <summary>指令文本长度上限（官方 40034108 指令参数长度超限）。</summary>
    public const int MaxCommandChars = 40;
}
