namespace QQBot.Web.Models;

public class AiConfigDto
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

    /// <summary>附图张数上限，供面板对齐输入框。</summary>
    public int CeilingAttachedImages { get; set; } = QQBot.Core.Models.Config.AiConfig.CeilingAttachedImages;

    public bool WebSearchEnabled { get; set; }
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
