namespace QQBot.Core.Models;

/// <summary>主动消息配额与授权状态</summary>
public class ActiveMessageQuotaStatus
{
    public int UsedThisMonth { get; set; }
    public int Quota { get; set; }
    public bool Rejected { get; set; }
    public DateTime? LastSentAt { get; set; }
}

/// <summary>主动消息发送结果</summary>
public class ActiveMessageSendResult
{
    public bool Success { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
