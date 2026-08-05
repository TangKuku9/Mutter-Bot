namespace QQBot.Core.Models.Config;

/// <summary>
/// AI 服务商预设，用于前端一键填充 API 地址、模型名等信息。
/// 联网搜索模式说明：
/// <list type="bullet">
/// <item><c>none</c>：不支持联网搜索</item>
/// <item><c>enable_search</c>：通义千问模式，请求体顶层附加 enable_search:true，平台自动检索</item>
/// <item><c>web_search_tool</c>：智谱 GLM 模式，tools 声明 web_search(enable=true)，平台自动执行</item>
/// </list>
/// </summary>
public class AiProviderPreset
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;
    public List<string> Models { get; set; } = new();
    public bool WebSearchSupported { get; set; }
    public string WebSearchMode { get; set; } = "none";
    public string Description { get; set; } = string.Empty;
    public string KeyHelpUrl { get; set; } = string.Empty;
    public string KeyHelpText { get; set; } = string.Empty;

    /// <summary>
    /// 内置服务商预设列表。
    /// </summary>
    public static List<AiProviderPreset> GetDefaults() =>
    [
        new()
        {
            Id = "qwen",
            Name = "通义千问（百炼）",
            ApiBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            DefaultModel = "qwen-plus",
            Models = ["qwen-plus", "qwen-max", "qwen-turbo", "qwen-flash", "qwen3-max", "qwen3.7-max"],
            WebSearchSupported = true,
            WebSearchMode = "enable_search",
            Description = "阿里云百炼平台，OpenAI 兼容接口。联网搜索通过 enable_search 开启，平台自动检索并融入回答，无需额外处理。",
            KeyHelpUrl = "https://bailian.console.aliyun.com/",
            KeyHelpText = "百炼控制台 → API-KEY 管理"
        },
        new()
        {
            Id = "zhipu",
            Name = "智谱 GLM",
            ApiBaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            DefaultModel = "glm-4-flash",
            Models = ["glm-4-flash", "glm-4", "glm-4-plus", "glm-4.5", "glm-4.6"],
            WebSearchSupported = true,
            WebSearchMode = "web_search_tool",
            Description = "智谱 AI 开放平台，OpenAI 兼容接口。联网搜索通过 tools 声明 web_search 工具开启，平台自动执行搜索。",
            KeyHelpUrl = "https://open.bigmodel.cn/usercenter/apikeys",
            KeyHelpText = "智谱开放平台 → API Keys"
        },
        new()
        {
            Id = "kimi",
            Name = "Kimi（Moonshot）",
            ApiBaseUrl = "https://api.moonshot.cn/v1",
            DefaultModel = "kimi-k2.6",
            Models = ["kimi-k2.6", "moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k"],
            WebSearchSupported = false,
            WebSearchMode = "none",
            Description = "Moonshot AI，OpenAI 兼容接口。内置 $web_search 工具需工具调用回环，当前版本暂不支持一键联网，可手动配置。",
            KeyHelpUrl = "https://platform.moonshot.cn/console/api-keys",
            KeyHelpText = "Moonshot 平台 → API Key 管理"
        },
        new()
        {
            Id = "deepseek",
            Name = "DeepSeek",
            ApiBaseUrl = "https://api.deepseek.com/v1",
            DefaultModel = "deepseek-chat",
            Models = ["deepseek-chat", "deepseek-reasoner", "deepseek-coder"],
            WebSearchSupported = false,
            WebSearchMode = "none",
            Description = "DeepSeek 官方 API，OpenAI 兼容接口。API 暂不支持原生联网搜索（网页端可手动开启）。",
            KeyHelpUrl = "https://platform.deepseek.com/api_keys",
            KeyHelpText = "DeepSeek 开放平台 → API Keys"
        },
        new()
        {
            Id = "openai",
            Name = "OpenAI",
            ApiBaseUrl = "https://api.openai.com/v1",
            DefaultModel = "gpt-4o-mini",
            Models = ["gpt-4o-mini", "gpt-4o", "gpt-4-turbo", "gpt-3.5-turbo"],
            WebSearchSupported = false,
            WebSearchMode = "none",
            Description = "OpenAI 官方 API。联网搜索需使用 Responses API（与当前 Chat Completions 接口不同），国内访问需国际网络。",
            KeyHelpUrl = "https://platform.openai.com/api-keys",
            KeyHelpText = "OpenAI Platform → API Keys"
        },
        new()
        {
            Id = "custom",
            Name = "自定义",
            ApiBaseUrl = "",
            DefaultModel = "",
            Models = [],
            WebSearchSupported = false,
            WebSearchMode = "none",
            Description = "手动填写任意 OpenAI 兼容接口地址，适用于自建/代理/其他兼容服务。",
            KeyHelpUrl = "",
            KeyHelpText = ""
        }
    ];

    /// <summary>
    /// 根据 API 地址推断联网搜索模式（用于运行时判断，即使用户手动填 URL 也能生效）。
    /// </summary>
    public static string InferWebSearchMode(string apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            return "none";

        var url = apiBaseUrl.ToLowerInvariant();

        // 通义千问 / 百炼
        if (url.Contains("dashscope") || url.Contains("aliyuncs"))
            return "enable_search";

        // 智谱
        if (url.Contains("bigmodel"))
            return "web_search_tool";

        return "none";
    }
}
