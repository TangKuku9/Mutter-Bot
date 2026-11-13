namespace Mutter.Infrastructure.Entities;

public class AiConfigEntity
{
    public string ProviderConnectionId { get; set; } = string.Empty;
    public int Id { get; set; }
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 2048;
    public int MaxContextMessages { get; set; } = 20;
    public int ContextWindowTokens { get; set; } = 32768;
    public int MaxInputTokens { get; set; } = Mutter.Core.Services.AiRequestBudget.DefaultInputBudget;
    public bool GroupMemoryInjectionEnabled { get; set; } = true;

    /// <summary>@ 回复 / 私聊最多附带几张图，0=不带图。</summary>
    public int MaxAttachedImages { get; set; } = Mutter.Core.Models.Config.AiConfig.DefaultMaxAttachedImages;

    public bool WebSearchEnabled { get; set; } = false;

    /// <summary>已选用的联网搜索通道：空=按地址推断，openai=兼容接口形状，gemini=原生 :generateContent。</summary>
    public string WebSearchEndpoint { get; set; } = string.Empty;

    /// <summary>已选用的探测形状 id（如 enable_search / web_search_tool / gemini_native）。</summary>
    public string WebSearchShape { get; set; } = string.Empty;

    /// <summary>该策略的验证时间；为空表示尚未实测。</summary>
    public DateTime? WebSearchVerifiedAt { get; set; }

    /// <summary>带图联网策略：auto / two_stage / off。</summary>
    public string WebSearchImageMode { get; set; } = "auto";

    /// <summary>“图片 + 搜索”是否实测可用；null=未测。</summary>
    public bool? WebSearchImageCapable { get; set; }

    /// <summary>图文组合的验证时间。</summary>
    public DateTime? WebSearchImageVerifiedAt { get; set; }

    /// <summary>独立搜索服务整卡配置（JSON 单列）：Enabled/Provider/BaseUrl/Key/…。
    /// Key 存在这个 JSON 里，与主 ApiKey 同为敏感字段，出入面板一律打码。</summary>
    public string ExternalSearchJson { get; set; } = string.Empty;
}
