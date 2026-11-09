using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services.WebSearch;

/// <summary>博查搜索（POST /api/v3/web-search）。Key 必填。</summary>
public sealed class BochaProvider(HttpClient httpClient, ILogger<BochaProvider> logger)
    : WebSearchProviderBase(httpClient, logger), IWebSearchProvider
{
    public string Id => "bocha";
    public bool RequiresApiKey => true;
    public string DisplayName => "博查";

    private const string DefaultEndpoint = "https://api.bochaai.com/v1/web-search";

    public async Task<WebSearchOutcome> SearchAsync(
        ExternalSearchConfig config, string query, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(config.BaseUrl)
            ? DefaultEndpoint
            : config.BaseUrl.Trim().TrimEnd('/');
        RequireHttpUrl(endpoint);

        var body = new Dictionary<string, object>
        {
            ["query"] = query,
            ["count"] = Math.Clamp(config.MaxResults, 1, ExternalSearchConfig.CeilingMaxResults),
            ["summary"] = true
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new("Bearer", config.ApiKey);
        request.Headers.Add("Accept", "application/json");

        using var document = await SendAsync(request, config, ct);
        var root = document.RootElement;
        // {"code":200,"data":{"webPages":{"value":[…]}}}
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("webPages", out var webPages) || webPages.ValueKind != JsonValueKind.Object
            || !webPages.TryGetProperty("value", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new WebSearchRequestException(WebSearchFailureKind.InvalidResponse, null, "缺少 data.webPages.value 数组");

        var hits = new List<WebSearchHit>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = ReadString(item, "name");
            var url = ReadString(item, "url");
            var snippet = ReadString(item, "summary", "snippet");
            if (title.Length == 0 || url.Length == 0) continue;
            hits.Add(new WebSearchHit { Title = title, Url = url, Snippet = snippet });
            if (hits.Count >= config.MaxResults) break;
        }
        return new WebSearchOutcome { Hits = hits };
    }
}
