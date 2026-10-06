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
    public bool IsProactive { get; set; }

    /// <summary>群名。跨群列表里光看 openid 前几位认不出是哪个群。</summary>
    public string? GroupName { get; set; }
    public DateTime CreatedAt { get; set; }
}
