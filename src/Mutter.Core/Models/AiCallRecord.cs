using Mutter.Core.Interfaces;

namespace Mutter.Core.Models;

/// <summary>一次实际 HTTP 请求的元数据；不存密钥、提示词或原始响应。</summary>
public sealed class AiCallRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OperationId { get; set; } = string.Empty;
    public string? TriggerMessageId { get; set; }
    public string? GroupId { get; set; }
    public string? RequesterId { get; set; }
    public string? RequesterName { get; set; }
    public string Purpose { get; set; } = "chat";
    public string Stage { get; set; } = "generate";
    public string Model { get; set; } = string.Empty;
    public string Transport { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public long ElapsedMs { get; set; }
    public string State { get; set; } = "success";
    public int? HttpStatus { get; set; }
    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }
}

public sealed record AiCallMetadata(string? GroupId = null, string? RequesterId = null,
    string? RequesterName = null, string? TriggerMessageId = null);

public sealed class AiUsageSummary
{
    public long CallCount { get; set; }
    public long FailedCount { get; set; }
    public long UnknownUsageCount { get; set; }
    public long LegacyCount { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
}

public sealed record AiUsageReport(AiUsageSummary Summary, List<DailyTokenStats> Daily,
    List<GroupTokenStats> Groups, List<UserTokenStats> Users);
