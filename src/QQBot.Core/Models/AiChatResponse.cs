namespace QQBot.Core.Models;

public class AiChatResponse
{
    public string Content { get; set; } = string.Empty;
    public string? Model { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;
}
