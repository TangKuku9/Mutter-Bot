using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Mutter.Core.Services.WebSearch;

/// <summary>从内建搜索实际返回的来源页读取公开预览图。</summary>
public sealed partial class GroundedPreviewImages(HttpClient client)
{
    private const int MaxHeadBytes = 64 * 1024;
    private const int MaxRedirects = 2;
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromMilliseconds(1500);

    public async Task<WebSearchImage?> FindAsync(IReadOnlyList<string> sources, CancellationToken ct)
    {
        foreach (var source in sources.Take(2))
        {
            if (ct.IsCancellationRequested) break;
            if (SearchReplyMarkdown.PublicUrl(source, httpsOnly: true) is not { } url) continue;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(SourceTimeout);
            try
            {
                if (await ReadAsync(new Uri(url), deadline.Token) is { } image)
                    return image;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                or OperationCanceledException or RegexMatchTimeoutException)
            {
                // 配图失败不影响已经生成的答案。来源也可能在请求过程中失效。
            }
        }
        return null;
    }

    private async Task<WebSearchImage?> ReadAsync(Uri page, CancellationToken ct)
    {
        for (var hops = 0; ; hops++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, page);
            request.Headers.Accept.ParseAdd("text/html, application/xhtml+xml, image/*;q=0.5");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (hops == MaxRedirects || location is null || !Uri.TryCreate(page, location, out var next)
                    || SearchReplyMarkdown.PublicUrl(next.AbsoluteUri, httpsOnly: true) is not { } safeNext)
                    return null;
                page = new Uri(safeNext);
                continue;
            }
            if (!response.IsSuccessStatusCode) return null;

            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (type is "image/png" or "image/jpeg" or "image/webp" or "image/gif")
                return new WebSearchImage(page.AbsoluteUri, page.AbsoluteUri);
            if (type is not ("text/html" or "application/xhtml+xml")) return null;

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxHeadBytes];
            var length = await body.ReadAtLeastAsync(buffer, buffer.Length, false, ct);
            return FindInHtml(Encoding.UTF8.GetString(buffer, 0, length), page);
        }
    }

    private static WebSearchImage? FindInHtml(string html, Uri page)
    {
        string? twitterImage = null;
        foreach (Match tag in HeadTags().Matches(html))
        {
            if (tag.Groups["end"].Success) break;
            var meta = tag.Groups["meta"];
            if (!meta.Success) continue;

            string? name = null;
            string? content = null;
            foreach (Match attribute in Attributes().Matches(meta.Value))
            {
                var key = attribute.Groups["key"].Value;
                var value = attribute.Groups["value"].Value;
                if (key.Equals("property", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("name", StringComparison.OrdinalIgnoreCase))
                    name ??= value;
                else if (key.Equals("content", StringComparison.OrdinalIgnoreCase))
                    content ??= value;
            }
            var imageValue = WebUtility.HtmlDecode(content)?.Trim();
            if (name is null || string.IsNullOrEmpty(imageValue)
                || !Uri.TryCreate(page, imageValue, out var uri)
                || SearchReplyMarkdown.PublicUrl(uri.AbsoluteUri, httpsOnly: true) is not { } url
                || url == page.AbsoluteUri)
                continue;

            switch (name.ToLowerInvariant())
            {
                case "og:image":
                case "og:image:url":
                case "og:image:secure_url":
                    return new WebSearchImage(url, page.AbsoluteUri);
                case "twitter:image":
                    twitterImage ??= url;
                    break;
            }
        }
        return twitterImage is null ? null : new WebSearchImage(twitterImage, page.AbsoluteUri);
    }

    // 跳过注释和脚本内容，只读 head 结束前的 meta。属性分隔允许单双引号和无引号。
    [GeneratedRegex("""<!--[\s\S]*?(?:-->|$)|<(?:script|style)(?=[\s/>])(?:"[^"]*"|'[^']*'|[^'"<>])*?>[\s\S]*?(?:</(?:script|style)\s*>|$)|(?<end></head\s*>|<body(?=[\s/>]))|<meta(?=[\s/>])(?<meta>(?:"[^"]*"|'[^']*'|[^'"<>])*)>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex HeadTags();

    [GeneratedRegex("""(?<key>[\w:-]+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s"'=<>]+))""",
        RegexOptions.CultureInvariant, 100)]
    private static partial Regex Attributes();
}
