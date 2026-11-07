using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services.WebSearch;

/// <summary>
/// SearXNG 元搜索（自建/公共实例）。免 Key；实例必须开启 JSON format
///（settings.yml 的 search.formats 含 json），否则返回 403——探测与这里都会
/// 明确提示「未开启 JSON 输出」而不是笼统的鉴权失败。
/// </summary>
public sealed class SearxngProvider(HttpClient httpClient, ILogger<SearxngProvider> logger)
    : WebSearchProviderBase(httpClient, logger), IWebSearchProvider
{
    public string Id => "searxng";
    public bool RequiresApiKey => false;
    public string DisplayName => "SearXNG";

    public async Task<WebSearchOutcome> SearchAsync(
        ExternalSearchConfig config, string query, CancellationToken ct)
    {
        var baseUri = TrimTrailingSlash(config.BaseUrl);
        RequireHttpUrl(baseUri);
        var url = BuildUrl(baseUri, query);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/json");
        // 有些公共实例按 UA 拦截默认 HttpClient，带上常见 UA 降低误伤
        request.Headers.Add("User-Agent", "Mutter-Bot/2 (search; +https://github.com/TangKuku9/Mutter-Bot)");

        using var document = await SendAsync(request, config, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
            throw new WebSearchRequestException(WebSearchFailureKind.InvalidResponse, null, "缺少 results 数组");

        var hits = new List<WebSearchHit>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var hit = ParseHit(item);
            if (hit is not null) hits.Add(hit);
            if (hits.Count >= config.MaxResults) break;
        }
        return new WebSearchOutcome { Hits = hits };
    }

    /// <summary>GET /search?q=…&amp;format=json&amp;safesearch=1；不依赖非标准结果条数参数。</summary>
    private static string BuildUrl(string baseUri, string query)
    {
        var builder = new StringBuilder(baseUri);
        if (!baseUri.EndsWith('/')) builder.Append('/');
        builder.Append("search");
        builder.Append("?format=json&safesearch=1&language=zh-CN");
        builder.Append("&q=").Append(Uri.EscapeDataString(query));
        return builder.ToString();
    }

    private static WebSearchHit? ParseHit(JsonElement item)
    {
        var title = ReadString(item, "title");
        var url = ReadString(item, "url");
        var snippet = ReadString(item, "content");
        if (title.Length == 0 || url.Length == 0) return null;
        return new WebSearchHit { Title = title, Url = url, Snippet = snippet };
    }

    private static string TrimTrailingSlash(string url) => url.TrimEnd('/');
}
