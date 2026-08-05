namespace QQBot.Core.Models;

public class ChatMessage
{
    public long Id { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public int ChatType { get; set; }
    public string? GroupId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string PlainText { get; set; } = string.Empty;
    public string RawSegmentsJson { get; set; } = string.Empty;
    public bool HasImage { get; set; }
    public bool HasAtBot { get; set; }
    public bool IsBotMessage { get; set; }
    public string? AiModelUsed { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
