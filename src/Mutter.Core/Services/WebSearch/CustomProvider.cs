using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services.WebSearch;

/// <summary>
/// 通用自定义搜索通道：用户填完整端点，按固定契约接入未内置的搜索 API。
/// 请求：POST &lt;BaseUrl&gt;，body 为 {"query":"…","count":N}；
/// 填了 Key 时附带 Authorization: Bearer，留空则匿名请求。
/// 响应：{"results":[{"title":"…","url":"…","snippet":"…"}]}，
/// 解析时兼容 name/link/summary/content/description 等常见别名。
/// 契约之外的任意 schema 不在客户端猜测——需要一层兼容网关。
/// </summary>
public sealed class CustomProvider(HttpClient httpClient, ILogger<CustomProvider> logger)
    : WebSearchProviderBase(httpClient, logger), IWebSearchProvider
{
    public string Id => "custom";
    // Key 选填：内网/自建搜索服务可能完全匿名。
    public bool RequiresApiKey => false;
    public string DisplayName => "自定义（JSON API）";

    public async Task<WebSearchOutcome> SearchAsync(
        ExternalSearchConfig config, string query, CancellationToken ct)
    {
        var endpoint = config.BaseUrl.Trim();
        if (endpoint.Length == 0)
            throw new WebSearchRequestException(WebSearchFailureKind.Disabled, null, "自定义通道未填写服务地址");
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
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
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
            var title = ReadString(item, "title", "name");
            var url = ReadString(item, "url", "link");
            var snippet = ReadString(item, "snippet", "summary", "content", "description");
            if (title.Length == 0 || url.Length == 0) continue;
            hits.Add(new WebSearchHit { Title = title, Url = url, Snippet = snippet });
            if (hits.Count >= config.MaxResults) break;
        }
        return new WebSearchOutcome { Hits = hits };
    }
}
