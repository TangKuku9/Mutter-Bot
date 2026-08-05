using QQBot.Core.Models.Config;

namespace QQBot.Core.Models;

public class BuildContextResult
{
    public List<(string Role, string Content)> Messages { get; set; } = new();
    public AiConfig EffectiveConfig { get; set; } = new();
    public int EstimatedTokens { get; set; }
    public int MessageCount { get; set; }
    public bool WasTruncated { get; set; }
    public string? CharacterPrompt { get; set; }
}
