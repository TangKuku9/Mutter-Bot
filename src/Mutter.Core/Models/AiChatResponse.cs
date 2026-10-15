namespace Mutter.Core.Models;

public class AiChatResponse
{
    public string Content { get; set; } = string.Empty;
    public string? RawContent { get; set; }
    public string? Model { get; set; }
    public string? FinishReason { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;
}
