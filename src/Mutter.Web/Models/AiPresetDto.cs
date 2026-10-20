namespace Mutter.Web.Models;

/// <summary>
/// 存进 AiPresets.ConfigJson 的配置快照。Version 用于以后调整结构时兼容旧数据；
/// ApiKey 不在这里，由 AiConfigPresetEntity.ApiKey 单独保存。
/// </summary>
public class AiPresetConfigDto : AiConfigInputDto
{
    public int Version { get; set; } = 1;
}

public class AiPresetSummaryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string PersonalityDisplayName { get; set; } = string.Empty;
    public bool PersonalityMissing { get; set; }
    public bool WebSearchEnabled { get; set; }
    public string WebSearchShape { get; set; } = string.Empty;
    public bool HasApiKey { get; set; }
    public bool IsValid { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AiPresetDetailDto : AiPresetSummaryDto
{
    /// <summary>快照里的可写配置；ApiKey 恒为空，密钥单独在下面的打码字段里。</summary>
    public AiPresetConfigDto Config { get; set; } = new();

    /// <summary>已保存密钥的打码值；空 = 未保存。</summary>
    public string ApiKey { get; set; } = string.Empty;
}

public class AiPresetSaveDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>false = 不保存 API Key，应用时沿用当前配置的 Key。</summary>
    public bool IncludeApiKey { get; set; } = true;

    /// <summary>当前表单快照；不传则由服务端快照当前已保存配置。</summary>
    public AiConfigInputDto? Config { get; set; }
}

public class AiPresetUpdateDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>传了 Config 时：null=保留预设原有 Key，true/false=按表单/清空处理。</summary>
    public bool? IncludeApiKey { get; set; }

    /// <summary>不传 = 只改名称和备注，不动配置快照。</summary>
    public AiConfigInputDto? Config { get; set; }
}
