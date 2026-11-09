using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services.WebSearch;

/// <summary>Tavily 检索 API（POST /search）。Key 必填。</summary>
public sealed class TavilyProvider(HttpClient httpClient, ILogger<TavilyProvider> logger)
    : WebSearchProviderBase(httpClient, logger), IWebSearchProvider
{
    public string Id => "tavily";
    public bool RequiresApiKey => true;
    public string DisplayName => "Tavily";

    private const string DefaultEndpoint = "https://api.tavily.com";

    public async Task<WebSearchOutcome> SearchAsync(
        ExternalSearchConfig config, string query, CancellationToken ct)
    {
        // BaseUrl 留空用官方地址；填了则允许指向自建网关（含 /search 后缀的完整端点也收）。
        var baseUri = string.IsNullOrWhiteSpace(config.BaseUrl)
            ? DefaultEndpoint
            : config.BaseUrl.Trim().TrimEnd('/');
        var endpoint = baseUri.EndsWith("/search", StringComparison.OrdinalIgnoreCase)
            ? baseUri
            : baseUri + "/search";
        RequireHttpUrl(endpoint);

        var body = new Dictionary<string, object>
        {
            ["query"] = query,
            ["max_results"] = Math.Clamp(config.MaxResults, 1, ExternalSearchConfig.CeilingMaxResults),
            ["search_depth"] = "basic",
            // 只取摘要，不要求正文抽取：注入预算有限，Snippet 足够
            ["include_answer"] = false,
            ["include_raw_content"] = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        // 官方鉴权为 Authorization: Bearer；自建兼容网关也按这一标准协议接入。
        request.Headers.Authorization = new("Bearer", config.ApiKey);
        request.Headers.Add("Accept", "application/json");

        using var document = await SendAsync(request, config, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
            throw new WebSearchRequestException(WebSearchFailureKind.InvalidResponse, null, "缺少 results 数组");

        var hits = new List<WebSearchHit>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = ReadString(item, "title");
            var url = ReadString(item, "url");
            var snippet = ReadString(item, "content");
            if (title.Length == 0 || url.Length == 0) continue;
            hits.Add(new WebSearchHit { Title = title, Url = url, Snippet = snippet });
            if (hits.Count >= config.MaxResults) break;
        }
        return new WebSearchOutcome { Hits = hits };
    }
}
