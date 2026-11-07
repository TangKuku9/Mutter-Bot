using System.Text.Json.Serialization;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services.WebSearch;

/// <summary>一条外部搜索结果。Title/Url/Snippet 均已清洗与限长，可直接进注入模板。</summary>
public sealed record WebSearchHit
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string Snippet { get; init; }
}

/// <summary>一次检索的结果：命中列表 + 附带说明（是否命中缓存/是否兜底等）。</summary>
public sealed record WebSearchOutcome
{
    public static readonly WebSearchOutcome Empty = new() { Hits = [], ServedFromCache = false };

    public IReadOnlyList<WebSearchHit> Hits { get; init; } = [];

    /// <summary>命中 3 分钟缓存。</summary>
    [JsonIgnore]
    public bool ServedFromCache { get; init; }

    public bool IsEmpty => Hits.Count == 0;
}

/// <summary>
/// 外搜失败分类。401/403 不重试、不打兜底；超时/5xx/429 记为 Transient 可按配置兜底。
/// 抽成枚举而不是直接用 HTTP 码，是为了让降级决策与解析逻辑解耦、可被自检覆盖。
/// </summary>
public enum WebSearchFailureKind
{
    Transient,
    Auth,
    InvalidResponse,
    Disabled
}

/// <summary>外搜尝试失败的统一载体。</summary>
public sealed record WebSearchFailure(WebSearchFailureKind Kind, int? HttpStatus, string Reason)
{
    public bool Retryable => Kind == WebSearchFailureKind.Transient;

    public string Describe() =>
        Kind == WebSearchFailureKind.Auth
            ? $"auth({HttpStatus?.ToString() ?? "?"})"
            : $"{Kind.ToString().ToLowerInvariant()}({HttpStatus?.ToString() ?? "?"})";
}

/// <summary>
/// 可插拔搜索服务商。实现只负责「一次 HTTP 检索 + 解析」，缓存/熔断/去重/兜底由
/// <see cref="ExternalSearchService"/> 统一编排，避免每个 Provider 各写一套易漂移的稳定性逻辑。
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>Provider id，与 <c>ExternalSearchConfig.Provider</c> 对应。</summary>
    string Id { get; }

    /// <summary>该服务是否需要 API Key（SearXNG 不需要）。</summary>
    bool RequiresApiKey { get; }

    /// <summary>面板展示名。</summary>
    string DisplayName { get; }

    Task<WebSearchOutcome> SearchAsync(
        ExternalSearchConfig config, string query, CancellationToken ct);
}
