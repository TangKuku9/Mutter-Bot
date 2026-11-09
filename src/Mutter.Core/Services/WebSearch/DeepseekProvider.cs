using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services.WebSearch;

/// <summary>
/// DeepSeek 搜索工具 API（POST /v1/tools/search）。Key 必填。
/// 结果同时带 snippet（约 100–200 字）与 summary（约 600–800 字），
/// 优先取信息量更大的 summary；注入层仍会按统一预算截断。
/// </summary>
public sealed class DeepseekProvider(HttpClient httpClient, ILogger<DeepseekProvider> logger)
    : WebSearchProviderBase(httpClient, logger), IWebSearchProvider
{
    public string Id => "deepseek";
    public bool RequiresApiKey => true;
    public string DisplayName => "DeepSeek 搜索";

    private const string DefaultEndpoint = "https://api.deepseek.com/v1/tools/search";

    public async Task<WebSearchOutcome> SearchAsync(
        ExternalSearchConfig config, string query, CancellationToken ct)
    {
        var endpoint = ResolveEndpoint(config.BaseUrl);
        RequireHttpUrl(endpoint);

        var body = new Dictionary<string, object>
        {
            ["query"] = query,
            ["count"] = Math.Clamp(config.MaxResults, 1, ExternalSearchConfig.CeilingMaxResults)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
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
            var snippet = ReadString(item, "summary", "snippet", "content");
            if (title.Length == 0 || url.Length == 0) continue;
            hits.Add(new WebSearchHit { Title = title, Url = url, Snippet = snippet });
            if (hits.Count >= config.MaxResults) break;
        }
        return new WebSearchOutcome { Hits = hits };
    }

    /// <summary>
    /// BaseUrl 可填站点根、/v1、/v1/tools 或完整端点，统一补全到 /v1/tools/search；
    /// 兼容网关给完整端点时原样使用，避免重复拼路径。
    /// </summary>
    private static string ResolveEndpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return DefaultEndpoint;
        var url = baseUrl.Trim().TrimEnd('/');
        if (url.EndsWith("/search", StringComparison.OrdinalIgnoreCase)) return url;
        if (url.EndsWith("/tools", StringComparison.OrdinalIgnoreCase)) return url + "/search";
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return url + "/tools/search";
        return url + "/v1/tools/search";
    }
}
