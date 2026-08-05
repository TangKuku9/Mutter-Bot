namespace QQBot.Core.Game.Models;

/// <summary>
/// 事件/奇遇领域模型。
/// </summary>
public class CultivationEvent
{
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = "opportunity"; // opportunity|tribulation|mystery|competition
    public string Narrative { get; set; } = string.Empty;
    public int Difficulty { get; set; } = 1;          // 1-5
    public List<EventOption> Options { get; set; } = new();
    public string? RewardHint { get; set; }
}

public class EventOption
{
    public string Label { get; set; } = string.Empty;
    public string Risk { get; set; } = "mid"; // low|mid|high
    public string? Hint { get; set; }
}
