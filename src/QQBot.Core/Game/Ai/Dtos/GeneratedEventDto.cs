namespace QQBot.Core.Game.Ai.Dtos;

/// <summary>AI 事件生成器输出 DTO。AI 只给定性 difficulty/rewardHint，数值由引擎缩放。</summary>
public class GeneratedEventDto
{
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = "opportunity"; // opportunity|tribulation|mystery|competition
    public string Narrative { get; set; } = string.Empty;
    public int Difficulty { get; set; } = 1;          // 1-5
    public List<GeneratedEventOptionDto> Options { get; set; } = new();
    public string? RewardHint { get; set; }
}

public class GeneratedEventOptionDto
{
    public string Label { get; set; } = string.Empty;
    public string Risk { get; set; } = "mid"; // low|mid|high
    public string? Hint { get; set; }
}
