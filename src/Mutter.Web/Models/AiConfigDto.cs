namespace Mutter.Web.Models;

/// <summary>
/// AI 配置的可写字段。面板保存、配置预设的创建/覆盖/应用共用这一份契约，
/// 避免几处各写一套字段后逐渐漂移。
/// </summary>
public class AiConfigInputDto
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public int MaxTokens { get; set; }
    public int MaxContextMessages { get; set; }

    /// <summary>@ 回复 / 私聊最多附带几张图，0=不带图。</summary>
    public int MaxAttachedImages { get; set; }

    public bool WebSearchEnabled { get; set; }

    /// <summary>已选用的联网搜索通道：空=按地址推断，openai=兼容接口形状，gemini=原生通道。</summary>
    public string WebSearchEndpoint { get; set; } = string.Empty;

    /// <summary>已选用的探测形状 id。</summary>
    public string WebSearchShape { get; set; } = string.Empty;

    /// <summary>带图联网策略：auto / two_stage / off。</summary>
    public string WebSearchImageMode { get; set; } = "auto";
}

/// <summary>
/// 面板读取的完整 AI 配置：可写字段 + 只读状态与可选项。
/// </summary>
public class AiConfigDto : AiConfigInputDto
{
    /// <summary>附图张数上限，供面板对齐输入框。</summary>
    public int CeilingAttachedImages { get; set; } = Mutter.Core.Models.Config.AiConfig.CeilingAttachedImages;

    /// <summary>该策略的验证时间（只读，由服务端在选用时写入）。</summary>
    public DateTime? WebSearchVerifiedAt { get; set; }

    /// <summary>“图片 + 搜索”是否实测可用（只读，由探测/运行结果写入）。</summary>
    public bool? WebSearchImageCapable { get; set; }

    /// <summary>图文组合的验证时间（只读）。</summary>
    public DateTime? WebSearchImageVerifiedAt { get; set; }

    public List<CharacterDto> AvailableCharacters { get; set; } = new();
    public List<ProviderPresetDto> AvailableProviders { get; set; } = new();
}

public class CharacterDto
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public bool IsCustom { get; set; }
}

public class ProviderPresetDto
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
}
