namespace QQBot.Core.Game.Services;

public enum GameAction
{
    Status,
    Breakthrough,
    Checkin,
    Practice,
    Leaderboard,
    Help,
    Alchemy,
    Inventory,
    Explore,
    Event,
    Choose,
    Protect,
    Shop,
    FreeAction,
    UseDan
}

public record GameCommand(GameAction Action, string Args);

/// <summary>游戏回复结果，包含回复文本与 AI 用量信息（用于 Dashboard 统计）。</summary>
public record GameReply(
    string Reply,
    string? AiModelUsed = null,
    int? PromptTokens = null,
    int? CompletionTokens = null);

/// <summary>
/// 游戏指令路由。普通群员通过 @bot + 关键词触发，绕开现有 admin 指令门控。
/// 未命中返回 null，继续走 AI 回复。
/// </summary>
public class GameCommandRouter
{
    private static readonly Dictionary<string, GameAction> KeywordMap = new()
    {
        ["修炼"] = GameAction.Status,
        ["状态"] = GameAction.Status,
        ["面板"] = GameAction.Status,
        ["我的"] = GameAction.Status,
        ["突破"] = GameAction.Breakthrough,
        ["渡劫"] = GameAction.Breakthrough,
        ["签到"] = GameAction.Checkin,
        ["打卡"] = GameAction.Checkin,
        ["修行"] = GameAction.Practice,
        ["闭关"] = GameAction.Practice,
        ["排行"] = GameAction.Leaderboard,
        ["排行榜"] = GameAction.Leaderboard,
        ["排名"] = GameAction.Leaderboard,
        ["帮助"] = GameAction.Help,
        ["修仙"] = GameAction.Help,
        ["玩法"] = GameAction.Help,
        ["炼丹"] = GameAction.Alchemy,
        ["丹药"] = GameAction.Alchemy,
        ["背包"] = GameAction.Inventory,
        ["物品"] = GameAction.Inventory,
        ["探索"] = GameAction.Explore,
        ["历练"] = GameAction.Explore,
        ["事件"] = GameAction.Event,
        ["奇遇"] = GameAction.Event,
        ["天机"] = GameAction.Event,
        ["选"] = GameAction.Choose,
        ["抉择"] = GameAction.Choose,
        ["护法"] = GameAction.Protect,
        ["助阵"] = GameAction.Protect,
        ["商店"] = GameAction.Shop,
        ["购买"] = GameAction.Shop,
        ["买"] = GameAction.Shop,
        ["行动"] = GameAction.FreeAction,
        ["自由行动"] = GameAction.FreeAction,
        ["使用"] = GameAction.UseDan,
        ["服用"] = GameAction.UseDan,
    };

    /// <summary>从消息纯文本中识别游戏指令。返回 null 表示未命中。</summary>
    public GameCommand? TryMatch(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return null;

        var text = StripMentions(plainText).Trim();
        if (string.IsNullOrEmpty(text)) return null;

        // 取首个词（按空格切分，兼容 "签到 1" 这类带参数）
        var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Trim('/', '＃', '#', '：', ':');

        return KeywordMap.TryGetValue(first, out var action)
            ? new GameCommand(action, text.Substring(first.Length).Trim())
            : null;
    }

    /// <summary>剥离 QQ @ 提及段（如 &lt;@!12345&gt; / &lt;@12345&gt;）。</summary>
    private static string StripMentions(string text)
    {
        var result = text;
        // 反复剥离开头的 @ 提及
        while (result.Length > 0 && result.StartsWith('<'))
        {
            var end = result.IndexOf('>');
            if (end < 0) break;
            result = result[(end + 1)..];
        }
        return result;
    }
}
