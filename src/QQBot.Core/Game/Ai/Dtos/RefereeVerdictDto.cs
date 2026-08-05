namespace QQBot.Core.Game.Ai.Dtos;

/// <summary>
/// AI 自由动作裁判输出 DTO。
/// 约束：cultivationDelta 绝对值 ≤ 0.3（引擎二次钳制）；禁止指定具体数值。
/// </summary>
public class RefereeVerdictDto
{
    public bool Feasible { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = "none"; // none|low|mid|high|lethal
    public string OutcomeType { get; set; } = "success"; // success|partial|failure|backfire
    public string Narrative { get; set; } = string.Empty;
    public RefereeModifiersDto? SuggestedModifiers { get; set; }
}

public class RefereeModifiersDto
{
    /// <summary>修为变化比例（相对当前），引擎钳制到 [-0.3, 0.3]。</summary>
    public double CultivationDelta { get; set; }
    public string? ItemConsumeHint { get; set; }
    public string? StatusEffect { get; set; } // none|injured|enlightened|poisoned
}
