using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services.WebSearch;

/// <summary>Brave Search API（GET /res/v1/web/search）。Key 必填。</summary>
public sealed class BraveProvider(HttpClient httpClient, ILogger<BraveProvider> logger)
    : WebSearchProviderBase(httpClient, logger), IWebSearchProvider
{
    public string Id => "brave";
    public bool RequiresApiKey => true;
    public string DisplayName => "Brave Search";

    private const string DefaultEndpoint = "https://api.search.brave.com/res/v1/web/search";

    public async Task<WebSearchOutcome> SearchAsync(
        ExternalSearchConfig config, string query, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(config.BaseUrl)
            ? DefaultEndpoint
            : config.BaseUrl.Trim().TrimEnd('/');
        RequireHttpUrl(endpoint);
        // QueryString 走 UriBuilder，避免手拼转义出错
        var uri = new UriBuilder(endpoint)
        {
            Query = $"q={Uri.EscapeDataString(query)}&count={Math.Clamp(config.MaxResults, 1, ExternalSearchConfig.CeilingMaxResults)}"
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Subscription-Token", config.ApiKey);
        request.Headers.Add("Accept", "application/json");

        using var document = await SendAsync(request, config, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("web", out var web)
            || web.ValueKind != JsonValueKind.Object
            || !web.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new WebSearchRequestException(WebSearchFailureKind.InvalidResponse, null, "缺少 web.results 数组");

        var hits = new List<WebSearchHit>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = ReadString(item, "title");
            var url = ReadString(item, "url");
            var snippet = ReadString(item, "description");
            if (title.Length == 0 || url.Length == 0) continue;
            hits.Add(new WebSearchHit { Title = title, Url = url, Snippet = snippet });
            if (hits.Count >= config.MaxResults) break;
        }
        return new WebSearchOutcome { Hits = hits };
    }
}
