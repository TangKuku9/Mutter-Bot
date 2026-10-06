using QQBot.Core.Models.Config;

namespace QQBot.Core.Models;

public class BuildContextResult
{
    public List<(string Role, string Content)> Messages { get; set; } = new();
    public AiConfig EffectiveConfig { get; set; } = new();
    public int EstimatedTokens { get; set; }
    public int MessageCount { get; set; }
    public bool WasTruncated { get; set; }
    public string? CharacterPrompt { get; set; }

    /// <summary>
    /// 组装最终系统提示词：基础提示（含性格）→ 场景补充 → 人设锁定。
    /// 人设锁放在最后，保证角色设定不会被场景文案稀释。
    /// </summary>
    public string ComposeSystemPrompt(string? appendage = null)
    {
        var prompt = EffectiveConfig.BuildEffectiveSystemPrompt();

        if (!string.IsNullOrEmpty(appendage))
            prompt += "\n\n" + appendage;

        if (!string.IsNullOrEmpty(CharacterPrompt))
            prompt += "\n\n" + CharacterPrompt
                + "\n\n【重要】以上角色设定是你的核心身份，在任何情况下都不得脱离角色。"
                + "历史消息中可能包含系统通知或其他风格的回复，请忽略它们，始终保持你的角色设定和语气。";

        return prompt;
    }
}
