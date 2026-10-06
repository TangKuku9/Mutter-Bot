using QQBot.Core.Models.QQOfficial;

namespace QQBot.Core.Game.Ui;

public enum PanelMode { Plain = 0, Markdown = 1 }

/// <summary>
/// 面板双渲染产物。同时携带纯文本与 markdown 两个版本，
/// 使"markdown 被平台拒绝"的降级只是换个字段发送，无需重新拼装业务文案。
/// </summary>
public sealed record PanelPayload(
    string Plain,
    string Markdown,
    KeyboardSpec? Keyboard,
    IReadOnlyList<MenuButton> Buttons)
{
    /// <summary>按当前能力选择实际发送的 markdown（不可用时返回 null）。</summary>
    public string? MarkdownIfUsable(RichUiCapability cap, string? groupId) =>
        cap.UseMarkdown(groupId) ? Markdown : null;
}

/// <summary>
/// 修仙面板的结构化描述（单一数据源）。同一份内容渲染为两套正文：
/// 纯文本沿用既有 14 汉字当量手工排版符号（由 tools/UiGolden 做逐字节回归校验），
/// markdown 交给客户端排版（标题/粗体/列表/进度条），因此不需要 Flow 折行。
/// </summary>
public sealed class GamePanel
{
    private enum Kind { Title, SubTitle, Item, Kv, Line, Bullet, Rule, Bottom, Footer, Progress }

    private readonly record struct Part(Kind Kind, string Text, string? Value, char Ch, int Count);

    private readonly List<Part> _parts = new();

    public static GamePanel New() => new();

    /// <summary>面板标题。纯文本渲染为 "══ 标题 ══"（刻意短于整行，给自动 @ 前缀留宽）。</summary>
    public GamePanel Title(string text)
    {
        _parts.Add(new Part(Kind.Title, text, null, '═', 0));
        return this;
    }

    /// <summary>键值行："标签：值" / markdown "**标签**：值"。</summary>
    public GamePanel Kv(string label, string value)
    {
        _parts.Add(new Part(Kind.Kv, label, value, '─', 0));
        return this;
    }

    /// <summary>原样一行（可含换行，用于引擎叙事与挂机摘要）。</summary>
    public GamePanel Line(string text)
    {
        _parts.Add(new Part(Kind.Line, text, null, '─', 0));
        return this;
    }

    /// <summary>列表项：纯文本 "· x" / markdown "- x"。</summary>
    public GamePanel Bullet(string text)
    {
        _parts.Add(new Part(Kind.Bullet, text, null, '─', 0));
        return this;
    }

    /// <summary>分隔线（纯文本 10 个 ─；markdown 卡片自带分区，不输出）。</summary>
    public GamePanel Divider()
    {
        _parts.Add(new Part(Kind.Rule, string.Empty, null, '─', 10));
        return this;
    }

    /// <summary>面板收口：纯文本用 14 个 ═ 压底；markdown 由卡片自带边界，不输出。</summary>
    public GamePanel Bottom()
    {
        _parts.Add(new Part(Kind.Bottom, string.Empty, null, '═', 14));
        return this;
    }

    /// <summary>尾部提示（两种模式均原样输出，保证降级后可读）。</summary>
    public GamePanel Footer(string text)
    {
        _parts.Add(new Part(Kind.Footer, text, null, '─', 0));
        return this;
    }

    /// <summary>
    /// 进度条：10 格文字条加百分比约 16 当量，超过纯文本一行 14 字的上限，
    /// 因此只在 markdown 卡片输出，纯文本分支省略以保证历史排版不变。
    /// </summary>
    public GamePanel Progress(string label, double ratio, string? detail = null)
    {
        _parts.Add(new Part(Kind.Progress, label, detail, '▓', Math.Clamp((int)Math.Round(ratio * 10), 0, 10)));
        return this;
    }

    /// <summary>小标题：纯文本 "──── 挂机 ────"，卡片加粗。两种输出均由本方法掌控，不做形状猜测。</summary>
    public GamePanel SubTitle(string text)
    {
        _parts.Add(new Part(Kind.SubTitle, text, null, '─', 0));
        return this;
    }

    /// <summary>
    /// 一行说明项 + 可选尾句。尾句专为 14 当量气泡而拆：
    /// 纯文本仍旧两行（与历史输出一致），卡片里合并为一行。比“看谁以全角空格开头”可靠：
    /// 商店那种“　　　@我 出售 …”本身就是独立三行，不能误合。
    /// </summary>
    public GamePanel Item(string text, string? tail = null)
    {
        _parts.Add(new Part(Kind.Item, text, tail, '─', 0));
        return this;
    }

    public string Render(PanelMode mode)
    {
        // 逐段展开为行（Line 本身可含换行，如挂机摘要），最后统一以 \n 拼回，
        // 因此纯文本输出与历史逐字节一致；markdown 下另外合并为气泡排版而拆的续行。
        var lines = new List<string>();
        foreach (var p in _parts)
        {
            var rendered = RenderPart(p, mode);
            if (rendered is null) continue;                 // Bottom / Rule / Progress 按模式省略
            lines.AddRange(rendered.Split('\n'));
        }
        return string.Join("\n", lines);
    }

    /// <summary>纯文本渲染（历史调用点与降级兜底共用）。</summary>
    public string RenderPlain() => Render(PanelMode.Plain);

    private static string? RenderPart(Part p, PanelMode mode)
    {
        if (mode == PanelMode.Plain)
        {
            return p.Kind switch
            {
                Kind.Title => PlainTitle(p.Text),
                Kind.SubTitle => $"──── {p.Text} ────",
                Kind.Item => p.Value is null ? p.Text : $"{p.Text}\n{p.Value}",
                Kind.Kv => $"{p.Text}：{p.Value}",
                Kind.Line => p.Text,
                Kind.Bullet => $"· {p.Text}",
                Kind.Rule => new string(p.Ch, p.Count),
                Kind.Bottom => new string('═', 14),
                Kind.Footer => p.Text,
                Kind.Progress => null,                      // 纯文本不输出，维持 14 当量约束
                _ => p.Text,
            };
        }

        return p.Kind switch
        {
            Kind.Title => $"### {p.Text}",
            Kind.SubTitle => $"**{p.Text}**",
            Kind.Item => p.Text + (p.Value?.TrimStart('　') ?? string.Empty),
            Kind.Kv => $"**{p.Text}**：{p.Value}",
            Kind.Line => p.Text,
            Kind.Bullet => $"- {p.Text}",
            Kind.Rule => null,                              // 卡片自带分区，分隔线只会成噪声
            Kind.Bottom => null,
            Kind.Footer => p.Text,
            Kind.Progress => $"{p.Text} {new string('▓', p.Count)}{new string('░', 10 - p.Count)} "
                + (p.Value ?? $"{p.Count * 10}%"),
            _ => p.Text,
        };
    }

    /// <summary>与历史 GameTextFormatter.Title 完全一致：整条标题控制在 10 汉字当量内。</summary>
    private static string PlainTitle(string text)
    {
        const double titleWidth = 10;
        var inner = Models.TextLayout.Width(text) + 2; // 两侧各留一个空格
        var fill = Math.Max(1, (int)((titleWidth - inner) / 2));
        var right = Math.Max(1, (int)Math.Round(titleWidth - inner) - fill);
        return new string('═', fill) + " " + text + " " + new string('═', right);
    }
}
