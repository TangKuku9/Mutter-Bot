using System.Text;
using Microsoft.Extensions.Logging;
using QQBot.Core.Configuration;
using QQBot.Core.Models.QQOfficial;

namespace QQBot.Core.Game.Ui;

/// <summary>
/// 菜单语义 → 传输层键盘 / markdown 文字链。
/// 官方硬约束集中在此钳制，越界一律裁剪而非报错（消息可达优先于菜单完整）：
/// 最多 5 行、每行 5 个按钮、label ≤10 字符、指令 data ≤40 字符。
/// </summary>
public static class GameKeyboard
{
    private const int HardMaxRows = 5;
    private const int HardMaxPerRow = 5;

    /// <summary>构造内嵌键盘；模式为 None 或无按钮时返回 null（null 表示这条消息不带键盘）。</summary>
    public static KeyboardSpec? From(
        IReadOnlyList<MenuButton> buttons,
        GameKeyboardMode mode,
        RichUiOptions opt,
        ILogger? log = null)
    {
        if (mode == GameKeyboardMode.None || buttons.Count == 0) return null;

        var perRow = Math.Clamp(opt.MaxButtonsPerRow <= 0 ? HardMaxPerRow : opt.MaxButtonsPerRow, 1, HardMaxPerRow);
        var maxRows = Math.Clamp(opt.MaxRows <= 0 ? HardMaxRows : opt.MaxRows, 1, HardMaxRows);
        var capacity = perRow * maxRows;

        var usable = buttons.Where(b => !string.IsNullOrWhiteSpace(b.Command)).ToList();
        if (usable.Count > capacity)
        {
            log?.LogWarning("RichUi 按钮超限，截断 {Total}→{Cap}（rows≤{Rows}×{PerRow}）",
                usable.Count, capacity, maxRows, perRow);
            usable = usable.Take(capacity).ToList();
        }

        // 指令 data 长度钳制（40034108）；被截断的按钮宁可不显示，也不发出会被拒的消息
        for (var i = usable.Count - 1; i >= 0; i--)
        {
            if (usable[i].Command.Length > MenuButton.MaxCommandChars)
            {
                log?.LogWarning("RichUi 按钮指令超长被丢弃 label={Label} len={Len}", usable[i].Label, usable[i].Command.Length);
                usable.RemoveAt(i);
            }
        }
        if (usable.Count == 0) return null;

        var actionType = mode == GameKeyboardMode.Callback ? 1 : 2;
        var rows = new List<KeyboardRowSpec>();
        var index = 0;
        foreach (var chunk in usable.Chunk(perRow))
        {
            var row = new KeyboardRowSpec();
            foreach (var b in chunk)
            {
                row.Buttons.Add(new KeyboardButtonSpec
                {
                    Id = $"btn_{index++}",
                    RenderData = new ButtonRenderDataSpec
                    {
                        Label = ClampLabel(b.Label, log),
                        VisitedLabel = ClampLabel(b.VisitedLabel ?? b.Label, log),
                        Style = b.Style is 0 or 1 or 3 or 4 ? b.Style : 1,
                    },
                    Action = new ButtonActionSpec
                    {
                        Type = actionType,
                        Data = b.Command,
                        Permission = new ButtonPermissionSpec { Type = 2 }, // 所有人可操作
                        Enter = false,   // 官方：点击即发送仅单聊可用
                        Reply = false,
                        UnsupportTips = "请升级QQ版本后使用按钮",
                        Modal = b.ConfirmText is null
                            ? null
                            : new ButtonModalSpec
                            {
                                Content = Trunc(b.ConfirmText, 40),
                                ConfirmText = "确认",
                                CancelText = "取消",
                            },
                    },
                    // 互斥置灰仅对回调按钮有效
                    GroupId = actionType == 1 ? b.GroupId : null,
                });
            }
            rows.Add(row);
        }

        return new KeyboardSpec { Content = new KeyboardContentSpec { Rows = rows } };
    }

    /// <summary>
    /// markdown 内嵌文字链（无按钮组件权限时的可点替身）：点击把指令插入输入框，由玩家发送。
    /// 每行 4 条，链间以全角空格分隔；text/show 官方要求 urlencode 且 ≤100 字符。
    /// </summary>
    public static string RenderInputLinks(IReadOnlyList<MenuButton> buttons, int perLine = 4)
    {
        if (buttons.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        var usable = buttons.Where(b => !string.IsNullOrWhiteSpace(b.Command)
                                        && b.Command.Length <= MenuButton.MaxCommandChars)
                            .ToList();
        for (var i = 0; i < usable.Count; i += Math.Max(1, perLine))
        {
            if (i > 0) sb.Append('\n');
            var line = usable.Skip(i).Take(Math.Max(1, perLine));
            sb.Append(string.Join("　", line.Select(b =>
                $"<qqbot-cmd-input text=\"{UrlEncode(b.Command)}\" show=\"{UrlEncode(ClampLabelPlain(b.Label))}\" reference=\"false\" />")));
        }
        return sb.ToString();
    }

    /// <summary>官方要求：仅单聊可用（群聊/文字子频道不支持），故群聊一律不下发 enter。</summary>
    public static string EnterLink(string command) =>
        $"<qqbot-cmd-enter text=\"{UrlEncode(command)}\" />";

    /// <summary>真实 @ 标签（文本与 markdown 消息均可用），关闭时退回纯文本道号。</summary>
    public static string AtUser(string memberOpenId, string fallbackName, bool enabled) =>
        enabled && !string.IsNullOrEmpty(memberOpenId)
            ? $"<qqbot-at-user id=\"{memberOpenId}\" />"
            : fallbackName;

    private static string ClampLabel(string label, ILogger? log)
    {
        if (label.Length <= MenuButton.MaxLabelChars) return label;
        log?.LogWarning("RichUi 按钮文字超限被截断 {Label} → {Max}", label, MenuButton.MaxLabelChars);
        return Trunc(label, MenuButton.MaxLabelChars);
    }

    private static string ClampLabelPlain(string label) =>
        label.Length <= MenuButton.MaxLabelChars ? label : Trunc(label, MenuButton.MaxLabelChars);

    /// <summary>截断到 max 字符，末位以 … 标记。</summary>
    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string UrlEncode(string s) => Uri.EscapeDataString(s);
}
