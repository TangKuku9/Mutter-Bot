namespace Mutter.Core.Models.Config;

/// <summary>
/// 独立搜索服务（预检索注入）配置。与「服务商内建联网」互补：
/// 这条通道不依赖模型服务商支持搜索——先自己检索，把资料以纯文本注入对话，
/// 任何 OpenAI 兼容端点（包括中转站）都能用。
/// 主开关仍是 <see cref="AiConfig.WebSearchEnabled"/>，两者同时为真才生效；
/// 配置项默认 Enabled=false，升级后现有行为零变化。
/// </summary>
public record ExternalSearchConfig
{
    /// <summary>本卡片自身开关；null 表示从未配置过（区别于显式关闭）。</summary>
    public bool? Enabled { get; set; }

    /// <summary>搜索服务商 id：searxng / tavily / bocha / brave。</summary>
    public string Provider { get; set; } = "searxng";

    /// <summary>搜索服务 API 地址。SearXNG 必须是开了 JSON format 的实例地址。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>单次检索最多取回的结果条数。</summary>
    public int MaxResults { get; set; } = DefaultMaxResults;

    public const int DefaultMaxResults = 5;
    public const int CeilingMaxResults = 10;

    /// <summary>单次检索超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 8;
    public const int CeilingTimeoutSeconds = 30;

    /// <summary>外搜失败/无结果时是否兜底到服务商内建搜索通道。</summary>
    public bool FallbackToBuiltin { get; set; }

    /// <summary>「测试搜索」成功时间；为空表示未测/未通过。</summary>
    public DateTime? VerifiedAt { get; set; }

    public static readonly string[] ProviderIds = ["searxng", "tavily", "bocha", "brave"];

    public static bool IsValidProvider(string? provider) =>
        provider is not null && ProviderIds.Contains(provider);

    /// <summary>
    /// SearXNG 必须填实例地址且免 Key；其余服务可使用官方默认端点，但必须有 Key。
    /// 抽成纯函数供自检、面板校验与运行时共用，避免三处判据漂移。
    /// </summary>
    public static bool IsConfigured(ExternalSearchConfig config) =>
        (config.Provider != "searxng" || !string.IsNullOrWhiteSpace(config.BaseUrl))
        && (config.Provider == "searxng" || !string.IsNullOrWhiteSpace(config.ApiKey));

    public ExternalSearchConfig Normalized() => this with
    {
        Provider = IsValidProvider(Provider) ? Provider : "searxng",
        BaseUrl = BaseUrl.Trim(),
        ApiKey = ApiKey.Trim(),
        MaxResults = Math.Clamp(MaxResults, 1, CeilingMaxResults),
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, CeilingTimeoutSeconds)
    };
}

public static class ExternalSearchConfigExtensions
{
    /// <summary>
    /// 拼独立搜索服务的公开描述（日志/自检用）：含 Provider 但不含 BaseUrl、Key 等敏感段。
    /// </summary>
    public static string Describe(this ExternalSearchConfig config) =>
        $"{config.Provider} enabled={(config.Enabled ?? false).ToString().ToLowerInvariant()}";
}
