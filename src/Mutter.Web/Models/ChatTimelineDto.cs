using Mutter.Infrastructure.Entities;

namespace Mutter.Web.Models;

public sealed class ChatTimelinePageDto
{
    public List<ChatTimelineItemDto> Items { get; set; } = [];
    /// <summary>精确总数；轻量首屏尚未计数时为 null，不能当作 0。</summary>
    public int? TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasMore { get; set; }
    public string Snapshot { get; set; } = string.Empty;
}

public sealed class ChatTimelineCountDto
{
    public int TotalCount { get; set; }
    public string Snapshot { get; set; } = string.Empty;
}

public sealed class ChatTimelineItemDto
{
    public string Kind { get; set; } = "message";
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public int ChatType { get; set; }
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string PlainText { get; set; } = string.Empty;
    public bool HasImage { get; set; }
    public bool HasAtBot { get; set; }
    public bool IsBotMessage { get; set; }
    public bool IsProactive { get; set; }
    public long? DecisionId { get; set; }
    public string? Outcome { get; set; }
    public string? Topic { get; set; }
    public string? Reason { get; set; }
}

public sealed class ChatDecisionDto
{
    public long Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string? GroupName { get; set; }
    public DateTime EvaluatedAt { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? Topic { get; set; }
    public string? Line { get; set; }
    public string? Reason { get; set; }
    public string? Detail { get; set; }
    public int MessageCount { get; set; }
    public int? ImageCount { get; set; }
    public int? MemoryCount { get; set; }
    public int? MemoryPromptChars { get; set; }
    public bool? SearchUsed { get; set; }
    public string? SearchQuery { get; set; }
    public string? Model { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? SpeechPromptTokens { get; set; }
    public int? SpeechCompletionTokens { get; set; }
    public ProactiveLogSnapshot? Window { get; set; }
}
