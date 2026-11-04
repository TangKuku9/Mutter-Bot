namespace Mutter.Core.Models;

public static class DeliveryStates
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
    public const string Skipped = "skipped";
}

/// <summary>生成结果与平台投递分开存储。Pending/Unknown 绝不自动重放。</summary>
public sealed class OutgoingReply
{
    public string Id { get; set; } = $"bot-{Guid.NewGuid():N}";
    public string? GroupId { get; set; }
    public string? PeerUserId { get; set; }
    public string BotId { get; set; } = string.Empty;
    public string? ReplyToMessageId { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool IsProactive { get; set; }
    public string? Model { get; set; }
    public string? AiOperationId { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public string State { get; set; } = DeliveryStates.Pending;
    public string? PlatformMessageId { get; set; }
    public string? Channel { get; set; }
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed record DeliveryResult(string State, string? MessageId = null, string? Channel = null, string? Detail = null)
{
    public bool Sent => State == DeliveryStates.Sent;
}
