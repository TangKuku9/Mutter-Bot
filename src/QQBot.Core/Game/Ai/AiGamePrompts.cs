using System.Text;
using QQBot.Core.Game.Ai.Dtos;
using QQBot.Core.Game.Models;

namespace QQBot.Core.Game.Ai;

/// <summary>修仙RPG 的 AI prompt 模板库。强调结构化 JSON 输出与防作弊约束。</summary>
public static class AiGamePrompts
{
    /// <summary>AI 事件生成器 system prompt（"天道意志"）。</summary>
    public static string EventGeneratorSystem(int worldQiLevel, string realmDistribution)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是修仙世界的天道意志，负责为群聊修仙游戏生成动态事件。");
        sb.AppendLine($"当前群世界状态: 灵气浓度={worldQiLevel}，玩家境界分布: {realmDistribution}");
        sb.AppendLine();
        sb.AppendLine("生成一个适合当前群状态的奇遇或劫难事件。输出严格JSON:");
        sb.AppendLine("{");
        sb.AppendLine("  \"title\": \"事件标题(8字内)\",");
        sb.AppendLine("  \"type\": \"opportunity|tribulation|mystery|competition\",");
        sb.AppendLine("  \"narrative\": \"事件描述(50字内,纯文本无markdown)\",");
        sb.AppendLine("  \"difficulty\": 1到5的整数,");
        sb.AppendLine("  \"options\": [");
        sb.AppendLine("    {\"label\":\"选项名(6字内)\",\"risk\":\"low|mid|high\",\"hint\":\"简短提示\"}");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"rewardHint\": \"奖励方向描述(不写具体数值)\"");
        sb.AppendLine("}");
        sb.AppendLine("约束: difficulty 必须匹配平均境界; 不许输出具体修为/灵石数字; 选项2到4个;");
        sb.Append("只输出JSON对象本身，不要任何额外文字或Markdown标记。");
        return sb.ToString();
    }

    /// <summary>AI 自由动作裁判 system prompt。</summary>
    public static string RefereeSystem(
        string realmName, string spiritRootName, string techniqueName, string? sceneContext)
    {
        var scene = string.IsNullOrEmpty(sceneContext) ? "无特殊事件" : sceneContext;
        var sb = new StringBuilder();
        sb.AppendLine("你是修仙世界的规则裁判。判定玩家的自由动作是否合理及后果。");
        sb.AppendLine($"玩家状态: 境界={realmName}, 灵根={spiritRootName}, 功法={techniqueName}");
        sb.AppendLine($"当前场景: {scene}");
        sb.AppendLine();
        sb.AppendLine("cultivationDelta 是修为变化比例(相对当前修为)，正值=获得修为，负值=损失修为。");
        sb.AppendLine("根据动作的创意、风险和境界合理性给出有区分度的数值:");
        sb.AppendLine("  0.01~0.05: 微小收获(日常闲逛、简单观察)");
        sb.AppendLine("  0.05~0.15: 适度收获(认真修炼、探索灵脉)");
        sb.AppendLine("  0.15~0.30: 显著收获(冒险探索、以战养功，需匹配风险)");
        sb.AppendLine("  负值: 失败/反噬的修为损失，同理按严重程度取值。");
        sb.AppendLine();
        sb.AppendLine("输出严格JSON:");
        sb.AppendLine("{");
        sb.AppendLine("  \"feasible\": true或false,");
        sb.AppendLine("  \"reason\": \"判定理由(30字内,纯文本)\",");
        sb.AppendLine("  \"riskLevel\": \"none|low|mid|high|lethal\",");
        sb.AppendLine("  \"outcomeType\": \"success|partial|failure|backfire\",");
        sb.AppendLine("  \"narrative\": \"后果叙事(80字内,纯文本无markdown)\",");
        sb.AppendLine("  \"suggestedModifiers\": {");
        sb.AppendLine("    \"cultivationDelta\": -0.3到0.3的小数,");
        sb.AppendLine("    \"itemConsumeHint\": \"可能消耗的物品类型(可空)\",");
        sb.AppendLine("    \"statusEffect\": \"none|injured|enlightened|poisoned\"");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("约束: cultivationDelta 绝对值不得超过0.3; 禁止直接指定具体修为/灵石数值;");
        sb.AppendLine("outcomeType 必须与 feasible 一致; 不得给玩家凭空创造高阶物品;");
        sb.Append("只输出JSON对象本身，不要任何额外文字或Markdown标记。");
        return sb.ToString();
    }

    /// <summary>玩家可见的探索事件渲染。</summary>
    public static string RenderGeneratedEvent(GeneratedEventDto e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【{e.Title}】{TypeText(e.Type)}·难度{ClampDifficulty(e.Difficulty)}");
        sb.AppendLine(TextLayout.Flow(e.Narrative));
        for (var i = 0; i < e.Options.Count; i++)
        {
            var o = e.Options[i];
            sb.AppendLine($"{i + 1}. {o.Label}（{RiskText(o.Risk)}）");
            if (!string.IsNullOrEmpty(o.Hint))
                sb.AppendLine($"　 {o.Hint}");
        }
        if (!string.IsNullOrEmpty(e.RewardHint))
            sb.AppendLine($"提示：{e.RewardHint}");
        sb.Append("回复 @我 选 <序号> 参与抉择");
        return sb.ToString();
    }

    public static int ClampDifficulty(int d) => Math.Clamp(d, 1, 5);
    public static double ClampDelta(double v) => Math.Clamp(v, -0.3, 0.3);

    private static string TypeText(string t) => t switch
    {
        "tribulation" => "劫难",
        "mystery" => "秘境",
        "competition" => "争锋",
        _ => "奇遇"
    };

    private static string RiskText(string r) => r switch
    {
        "low" => "低险",
        "high" => "高危",
        "lethal" => "凶险",
        _ => "中险"
    };
}
