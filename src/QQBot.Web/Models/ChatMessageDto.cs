namespace QQBot.Web.Models;

public class ChatMessageDto
{
    public long Id { get; set; }
    public int ChatType { get; set; }
    public string? GroupId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string PlainText { get; set; } = string.Empty;
    public bool HasImage { get; set; }
    public bool HasAtBot { get; set; }
    public bool IsBotMessage { get; set; }
    public DateTime CreatedAt { get; set; }
}
