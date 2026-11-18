using System.Text.RegularExpressions;
using Mutter.Core.Models;

namespace Mutter.Core.Services.WebSearch;

/// <summary>搜索正文允许简单排版。链接和图片由检索结果构造。</summary>
public sealed partial record SearchReplyMarkdown(string PlainText, string Text, string? Image)
{
    public const string PromptSuffix = """

        本轮联网答复的排版可以用简短标题、加粗和列表。段落间留空行。
        不输出 HTML、图片链接、Markdown 链接、表格或代码块。应用会另附可用出处和配图。
        """;

    public static SearchReplyMarkdown? Create(AiChatResponse response, IEnumerable<string> sourceUrls,
        WebSearchImage? image = null)
    {
        var answer = response.RawContent ?? response.Content;
        if (string.IsNullOrWhiteSpace(answer)) return null;

        // 中立化模型输出的链接、图片、自动链接和 QQ 标签，保留标题、加粗、列表与换行。
        var text = MarkdownLink().Replace(answer, match => match.Groups[1].Value);
        text = BareUrl().Replace(text, string.Empty).Trim();
        text = text.Replace('<', '＜').Replace('>', '＞').Replace('[', '［').Replace(']', '］')
            .Replace('`', 'ˋ').Replace('\\', '＼').Replace("~~~", "～～～");
        text = string.Join('\n', text.Split('\n').Select(line => line.Trim()));
        if (text.Length == 0) return null;
        var plainText = Heading().Replace(text, string.Empty).Replace("**", string.Empty).Replace("__", string.Empty);

        var sources = sourceUrls.Select(url => PublicUrl(url)).OfType<string>()
            .Distinct(StringComparer.Ordinal).Take(3).ToArray();
        if (sources.Length > 0)
            text += "\n\n检索来源：" + string.Join(" · ", sources.Select(SourceLink));

        var url = PublicUrl(image?.Url, httpsOnly: true);
        if (url is null) return new(plainText, text, null);

        var label = "检索配图";
        if (image is { Width: > 0, Height: > 0 })
        {
            var scale = Math.Min(1d, 480d / Math.Max(image.Width, image.Height));
            label += $" #{Math.Max(1, (int)(image.Width * scale))}px #{Math.Max(1, (int)(image.Height * scale))}px";
        }
        var picture = $"![{label}]({url})";
        var imageSource = PublicUrl(image?.SourceUrl);
        if (imageSource is not null && !sources.Contains(imageSource))
            picture += $"\n\n图片来源：{SourceLink(imageSource)}";
        return new(plainText, text, picture);
    }

    internal static string? PublicUrl(string? value, bool httpsOnly = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 800
            || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '<' or '>')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && (httpsOnly || uri.Scheme != Uri.UriSchemeHttp))
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback) return null;

        var host = uri.IdnHost.TrimEnd('.');
        if (!host.Contains('.') || host.EndsWith(".localhost") || host.EndsWith(".local")
            || host.EndsWith(".internal") || host.EndsWith(".home.arpa")
            || OutboundAddressPolicy.IsBlockedHost(host)) return null;

        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
            .Replace("(", "%28").Replace(")", "%29").Replace("[", "%5B").Replace("]", "%5D");
    }

    private static string SourceLink(string url) => $"[{new Uri(url).IdnHost}]({url})";

    [GeneratedRegex(@"!?\[([^\]\r\n]*)\]\([^\r\n]*?\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(?:https?|qqbot-media)://[^\s<>\[\]，。！；、）]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"^ {0,3}#{1,6} +", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Heading();
}
