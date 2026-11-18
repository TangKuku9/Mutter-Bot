using System.Text;
using System.Text.RegularExpressions;
using Mutter.Core.Models.QQOfficial;

namespace Mutter.Core.Services;

/// <summary>
/// 把模型回答里附带的来源链接整理为「正文 + 来源行」。
/// 只读取已生成的文本，不访问链接；没有链接时仍返回纯文本消息。
/// </summary>
public static partial class AiReplyMarkdownFormatter
{
    public const int MaxSources = 5;

    public static OutboundMessage Format(string content, string? msgId = null, int? msgSeq = null)
    {
        var body = new StringBuilder(content.Length);
        var sources = new List<Source>();
        var cursor = 0;
        foreach (Match match in MarkdownLinkRegex().Matches(content))
        {
            body.Append(content, cursor, match.Index - cursor);
            var label = match.Groups[1].Value.Trim();
            var url = match.Groups[2].Value.Trim();
            if (AppendSource(label, url, sources)) cursor = match.Index + match.Length;
            else
            {
                // 非 HTTP(S) 或非法链接原样保留，不能因为同一条回复里有合法来源就吞掉它。
                body.Append(match.Value);
                cursor = match.Index + match.Length;
            }
        }
        body.Append(content, cursor, content.Length - cursor);

        var text = body.ToString();
        body.Clear();
        cursor = 0;
        foreach (Match match in BareUrlRegex().Matches(text))
        {
            if (match.Index > 0 && (char.IsLetterOrDigit(text[match.Index - 1]) || text[match.Index - 1] is '_' or '/'))
                continue;
            var url = match.Value.TrimEnd('.', ',', '!', '?', ';', ':', '，', '。', '！', '？', '；', '：');
            body.Append(text, cursor, match.Index - cursor);
            if (!AppendSource(url, url, sources))
            {
                body.Append(match.Value);
                cursor = match.Index + match.Length;
                continue;
            }
            body.Append(match.Value[(match.Value.Length - url.Length)..]);
            cursor = match.Index + match.Length;
        }
        if (sources.Count == 0) return OutboundMessage.Text(content, msgId, msgSeq);
        body.Append(text, cursor, text.Length - cursor);

        var distinct = sources.DistinctBy(s => s.Url, StringComparer.OrdinalIgnoreCase).ToArray();
        // 超过上限时宁可不美化，也不静默丢掉模型列出的来源。
        if (distinct.Length > MaxSources) return OutboundMessage.Text(content, msgId, msgSeq);

        var plain = CleanupBody(body.ToString());
        var markdown = (plain.Length == 0 ? "" : plain + "\n\n")
            + "来源：" + string.Join(" · ", distinct.Select(s => $"[{s.Label}]({s.Url})"));
        return new OutboundMessage { PlainText = content, MarkdownText = markdown, MsgId = msgId, MsgSeq = msgSeq };
    }

    private static bool AppendSource(string label, string url, List<Source> sources)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return false;
        var display = label;
        try { display = Uri.UnescapeDataString(label); }
        catch (UriFormatException) { display = uri.Host; }
        if (string.IsNullOrWhiteSpace(display) || display.Contains("http", StringComparison.OrdinalIgnoreCase)
            || display.Length > 40 || display.Any(char.IsControl)) display = uri.Host;
        sources.Add(new(display.Trim(), url));
        return true;
    }

    private static string CleanupBody(string text)
    {
        var body = EmptyParenRegex().Replace(text, string.Empty);
        body = CitationWrapperRegex().Replace(body, "$1$2");
        body = SpaceBeforePunctuationRegex().Replace(body, "$1");
        // 保留原有段落；只去掉链接被移除后留下的空行与行首尾空白。
        return string.Join('\n', body.Split('\n')
            .Select(line => WhitespaceRunRegex().Replace(line.Trim(), " "))
            .Where(line => line.Length > 0));
    }

    [GeneratedRegex(@"\[([^\]\r\n]{1,500})\]\(\s*(https?://[^\s<>]+?)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"https?://[^\s<>\[\]\(\)]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareUrlRegex();

    [GeneratedRegex(@"[ \t]*\([ \t]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyParenRegex();

    [GeneratedRegex(@"([\s\S]*?)\s*([。！？!?]?)来源：\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CitationWrapperRegex();

    [GeneratedRegex(@"[ \t]+([。！？!?，,；;：:])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforePunctuationRegex();

    [GeneratedRegex(@"[ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRunRegex();

    private sealed record Source(string Label, string Url);
}
