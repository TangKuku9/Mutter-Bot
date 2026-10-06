using System.Text.RegularExpressions;
using QQBot.Core.Models.QQOfficial;

namespace QQBot.Core.Game.Ui;

/// <summary>
/// 游戏回复：纯文本正文（永远可用，兼作落库内容与降级兜底）+ 可选的富呈现载荷。
/// 与历史 GameReply 字段同名，调用点无需改动。
/// </summary>
public sealed record GameUiReply(
    string Reply,
    PanelPayload? Panel = null,
    string? AiModelUsed = null,
    int? PromptTokens = null,
    int? CompletionTokens = null)
{
    public static GameUiReply Text(string reply) => new(reply);

    public static GameUiReply Text(string reply, string? model, int? promptTokens, int? completionTokens) =>
        new(reply, null, model, promptTokens, completionTokens);

    public static GameUiReply Rich(PanelPayload payload) => new(payload.Plain, payload);

    /// <summary>是否携带可发送的 markdown 正文。</summary>
    public bool HasMarkdown => Panel is not null && Panel.Markdown.Length > 0;
}

/// <summary>
/// 纯文本 → markdown 的**保守**结构转换。只识别渲染层自己产出的确定符号
/// （══ 标题 ══、──── 小标题 ────、纯 ═/─ 装饰线），不做通用 Markdown 猜测，
/// 因此引擎叙事等自由文本原样透传，降级时也不会露出格式符号。
/// </summary>
public static class MarkdownText
{
    // ══ 修仙面板 ══ / ═══ 背包 ═══
    private static readonly Regex TitleLine = new(@"^═{2,}\s*(.+?)\s*═{2,}$", RegexOptions.Compiled);

    // ──── 挂机 ────
    private static readonly Regex SubTitleLine = new(@"^─{2,}\s*(.+?)\s*─{2,}$", RegexOptions.Compiled);

    // ══════════════ / ──────────（纯装饰线）
    private static readonly Regex BareRule = new(@"^[═─]{4,}$", RegexOptions.Compiled);

    public static string FromPlainText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 只做确定性的符号转换（标题/小标题/装饰线），不猜测行与行的关系：
        // 续行合并由 GamePanel.Item 在数据源建模，避开“全角空格开头就合并”这类误伤。
        var lines = Convert(text);
        return string.Join("\n", lines);
    }

    private static List<string> Convert(string text)
    {
        var lines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();

            // 必须先判纯装饰线：否则 14 个 ═ 会被标题正则切成“### ═”这种垃圾行
            if (BareRule.IsMatch(trimmed))
                continue;                                   // 装饰线在卡片里没有价值，直接丢

            var title = TitleLine.Match(trimmed);
            if (title.Success)
            {
                lines.Add($"### {title.Groups[1].Value}");
                continue;
            }

            var sub = SubTitleLine.Match(trimmed);
            if (sub.Success)
            {
                lines.Add($"**{sub.Groups[1].Value}**");
                continue;
            }

            lines.Add(line);
        }
        return lines;
    }
}
