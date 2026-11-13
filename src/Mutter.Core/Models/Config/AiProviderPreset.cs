namespace Mutter.Core.Models.Config;

/// <summary>创建连接时使用的内置目录；账号实际可用模型由服务商返回。</summary>
public class AiProviderPreset
{
    public const string CatalogDate = "2026-11-13";
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "china";
    public string Protocol { get; set; } = "openai";
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;
    public List<string> Models { get; set; } = [];
    public string ModelsUpdatedAt { get; set; } = CatalogDate;
    public string DocumentationUrl { get; set; } = string.Empty;
    public bool WebSearchSupported { get; set; }
    public string WebSearchMode { get; set; } = "none";
    public string Description { get; set; } = string.Empty;
    public string KeyHelpUrl { get; set; } = string.Empty;
    public string KeyHelpText { get; set; } = string.Empty;

    /// <summary>只收录聊天模型；不混入图像生成、向量与音频专用模型。</summary>
    public static List<AiProviderPreset> GetDefaults() =>
    [
        Create("qwen", "通义千问（百炼）", "china", "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "qwen3.8-flash", ["qwen3.8-flash", "qwen3.8-max", "qwen3.7-plus"],
            "https://help.aliyun.com/zh/model-studio/models", "https://bailian.console.aliyun.com/",
            "百炼中国站；模型需在对应地域开通。国际站密钥与地址应分别配置。", "enable_search"),
        Create("deepseek", "DeepSeek", "china", "https://api.deepseek.com/v1",
            "deepseek-flash", ["deepseek-flash", "deepseek-v4-pro"],
            "https://api-docs.deepseek.com/quick_start/pricing/", "https://platform.deepseek.com/",
            "DeepSeek 官方当前模型；Flash 别名跟随官方版本，联网可使用独立搜索服务。"),
        Create("zhipu", "智谱 GLM", "china", "https://open.bigmodel.cn/api/paas/v4",
            "glm-5.3-flash", ["glm-5.3-flash", "glm-5.3-flashx", "glm-5.3"],
            "https://docs.bigmodel.cn/cn/guide/models/vlm/glm-5.3-flash", "https://bigmodel.cn/",
            "智谱按量接口；Coding Plan 密钥与专属地址需单独配置。", "web_search_tool"),
        Create("kimi", "Kimi（Moonshot）", "china", "https://api.moonshot.cn/v1",
            "kimi-k2.6", ["kimi-k3.1-pro", "kimi-k3", "kimi-k2.6"],
            "https://platform.kimi.com/docs/api/models-overview", "https://platform.kimi.com/console/api-keys",
            "Kimi 中国站按量 API；K3 系列始终思考。Kimi Code 订阅与国际站密钥不能混用。"),
        Create("minimax", "MiniMax（中国站）", "china", "https://api.minimax.cn/v1",
            "MiniMax-M3.1-Flash", ["MiniMax-M3.1-Flash", "MiniMax-M3"],
            "https://platform.minimax.cn/docs/api-reference/text-openai-api", "https://platform.minimax.cn/",
            "MiniMax 官方 OpenAI 兼容接口；M3 系列的采样参数由服务商固定。"),
        Create("stepfun", "阶跃星辰（StepFun）", "china", "https://api.stepfun.com/v1",
            "step-3.7-flash", ["step-3.7-flash", "step-3.5-flash"],
            "https://www.stepfun.com/step-3.7-flash", "https://platform.stepfun.com/",
            "阶跃中国站按量接口；全球站使用独立地址及对应站点密钥。"),
        Create("volcengine", "火山方舟（豆包）", "china", "https://ark.cn-beijing.volces.com/api/v3",
            "doubao-seed-2-1-lite-260915", ["doubao-seed-2-1-lite-260915", "doubao-seed-2-1-pro-260915", "doubao-seed-evolving"],
            "https://ark.volcengine.com/region:cn-beijing/docs/model-list", "https://ark.volcengine.com/",
            "火山方舟按量接口；先开通模型服务，也可手动添加控制台提供的推理接入点 ID。"),
        Create("tencent", "腾讯 TokenHub（混元）", "china", "https://tokenhub.tencentmaas.com/v1",
            "hy4", ["hy4", "hy3", "hunyuan-role-latest", "deepseek/deepseek-flash"],
            "https://cloud.tencent.com/document/product/1823/130079", "https://console.cloud.tencent.com/tokenhub",
            "腾讯 TokenHub 广州站；使用 TokenHub API Key，海外站密钥与地址独立。"),
        Create("siliconflow", "硅基流动（SiliconFlow）", "china", "https://api.siliconflow.cn/v1",
            "deepseek-ai/DeepSeek-V4-Flash", ["deepseek-ai/DeepSeek-V4-Flash", "deepseek-ai/DeepSeek-V4-Pro", "moonshotai/Kimi-K2.7-Code"],
            "https://docs.siliconflow.cn/docs/api/chat-completions-post", "https://cloud.siliconflow.cn/",
            "硅基流动中国站；同名模型在不同平台的 ID 和可用权限可能不同。"),
        Create("openai", "OpenAI", "international", "https://api.openai.com/v1",
            "gpt-6-luna", ["gpt-6-luna", "gpt-6.1-terra", "gpt-6.1-sol", "gpt-6-astra", "gpt-4.1-mini", "gpt-4.1"],
            "https://developers.openai.com/api/docs/models", "https://platform.openai.com/api-keys",
            "OpenAI API 按量计费；使用 Chat Completions，ChatGPT 订阅不包含 API 额度。"),
        Create("anthropic", "Anthropic（Claude）", "international", "https://api.anthropic.com/v1",
            "claude-haiku-5-5", ["claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-2"],
            "https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk", "https://platform.claude.com/",
            "使用 Anthropic 官方 OpenAI 兼容层；不含原生缓存、文件等高级能力，结构化结果采用提示词约束。"),
        Create("gemini", "Google Gemini", "international", "https://generativelanguage.googleapis.com/v1beta",
            "gemini-4-hydrogen", ["gemini-4-hydrogen", "gemini-4-argon", "gemini-4-carbon"],
            "https://aistudio.google.com/docs/models", "https://aistudio.google.com/api-keys",
            "Google AI Studio 原生 generateContent 接口；使用 Gemini API Key。", "none", "gemini"),
        Create("xai", "xAI（Grok）", "international", "https://api.x.ai/v1",
            "grok-4.7", ["grok-4.7"],
            "https://docs.x.ai/developers/models", "https://console.x.ai/",
            "Grok 官方聊天接口；需要实时信息时配置独立搜索服务。"),
        Create("mistral", "Mistral AI", "international", "https://api.mistral.ai/v1",
            "mistral-small-2603", ["mistral-small-2603", "mistral-large-4", "mistral-large-latest"],
            "https://docs.mistral.ai/models", "https://console.mistral.ai/",
            "Mistral 官方聊天接口；提供固定版本与跟随官方更新的 latest 别名。"),
        Create("groq", "Groq", "international", "https://api.groq.com/openai/v1",
            "openai/gpt-oss-120b", ["openai/gpt-oss-120b", "openai/gpt-oss-20b"],
            "https://console.groq.com/docs/models", "https://console.groq.com/keys",
            "GroqCloud 当前生产聊天模型；模型权限与速率以账号控制台为准。"),
        Create("openrouter", "OpenRouter", "international", "https://openrouter.ai/api/v1",
            "google/gemini-4-hydrogen", ["google/gemini-4-hydrogen", "openai/gpt-6-luna", "anthropic/claude-sonnet-5.5", "qwen/qwen3.8-flash"],
            "https://openrouter.ai/models", "https://openrouter.ai/settings/keys",
            "多厂商模型聚合平台；模型 ID 含厂商前缀，可获取模型列表扩充目录。"),
        Create("together", "Together AI", "international", "https://api.together.ai/v1",
            "Qwen/Qwen3.8-Flash", ["Qwen/Qwen3.8-Flash", "deepseek-ai/DeepSeek-V4.1-Flash-1017", "zai-org/GLM-5.3-Flash", "MiniMaxAI/MiniMax-M3.1-Flash"],
            "https://docs.together.ai/docs/serverless/models", "https://api.together.ai/",
            "Together 无服务器推理接口；使用服务商目录里的完整模型 ID。"),
        new()
        {
            Id = "custom", Name = "自定义服务商", Category = "custom",
            Description = "填写其他 OpenAI 兼容或 Gemini 原生接口，并获取或手动添加模型。"
        }
    ];

    private static AiProviderPreset Create(string id, string name, string category, string url,
        string defaultModel, List<string> models, string documentationUrl, string keyHelpUrl,
        string description, string webSearchMode = "none", string protocol = "openai") => new()
    {
        Id = id, Name = name, Category = category, Protocol = protocol,
        ApiBaseUrl = url, DefaultModel = defaultModel, Models = models,
        DocumentationUrl = documentationUrl, KeyHelpUrl = keyHelpUrl,
        KeyHelpText = "在对应服务商控制台创建 API Key", Description = description,
        WebSearchSupported = webSearchMode != "none", WebSearchMode = webSearchMode
    };

    /// <summary>旧配置的联网模式推断；已验证的搜索策略优先。</summary>
    public static string InferWebSearchMode(string apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl)) return "none";
        var url = apiBaseUrl.ToLowerInvariant();
        if (url.Contains("dashscope") || url.Contains("aliyuncs")) return "enable_search";
        if (url.Contains("bigmodel")) return "web_search_tool";
        return "none";
    }
}
