namespace QQBot.Infrastructure.Entities;

public class GroupConfigEntity
{
    public string GroupId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int TriggerMode { get; set; }
    public string? CustomSystemPrompt { get; set; }
    public double? CustomTemperature { get; set; }
    public string? Keywords { get; set; }
    public string? ProactiveKeywords { get; set; }
    public bool ProactiveEnabled { get; set; }
    public string? ActiveCharacter { get; set; }
}
