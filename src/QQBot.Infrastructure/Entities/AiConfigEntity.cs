namespace QQBot.Infrastructure.Entities;

public class AiConfigEntity
{
    public int Id { get; set; }
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 2048;
    public int MaxContextMessages { get; set; } = 20;

    /// <summary>@ 回复 / 私聊最多附带几张图，0=不带图。</summary>
    public int MaxAttachedImages { get; set; } = QQBot.Core.Models.Config.AiConfig.DefaultMaxAttachedImages;

    public bool WebSearchEnabled { get; set; } = false;
}
