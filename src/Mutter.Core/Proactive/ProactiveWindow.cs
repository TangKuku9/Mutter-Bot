using System.Text.RegularExpressions;

namespace Mutter.Core.Proactive;

/// <summary>观察窗口里的一条群聊消息。ImageUrls 是图片类消息的官方临时下载链，有时效。</summary>
public readonly record struct ObservedMessage(
    string SenderName, string Text, string MessageId, DateTime UtcAt,
    IReadOnlyList<string>? ImageUrls = null);

/// <summary>
/// 一次评估的输入快照：攒到的窗口消息 + 最后一条消息的被动回复槽位。
/// 槽位有时效（官方 5 分钟），故随快照一起带走，避免评估期间被误判为有效。
/// </summary>
public sealed class ProactiveWindow
{
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    public required string GroupId { get; init; }

    public required IReadOnlyList<ObservedMessage> Messages { get; init; }

    public required string LastMessageId { get; init; }

    public required DateTime LastMessageUtcAt { get; init; }

    /// <summary>被动回复槽位是否仍在官方 5 分钟时效内。</summary>
    public bool PassiveSlotAlive(DateTime utcNow)
        => !string.IsNullOrEmpty(LastMessageId) && (utcNow - LastMessageUtcAt) < TimeSpan.FromMinutes(5);

    /// <summary>
    /// 把已经出现在本窗口里的消息从背景历史中剔除。
    /// 两类消息渲染出来是同一行文本（都是「[昵称]: 正文」），不剔就会在同一份 prompt 里
    /// 各出现一次：模型既把它们当"更早的背景"、又把它们当"当前窗口"，既烧 token 又误导判定。
    /// 从后往前扫、按条数逐个抵消，保证留下的仍是最近的一段、也不会多剔真实的历史消息。
    /// </summary>
    public IReadOnlyList<(string Role, string Content)> ExcludeFrom(
        IReadOnlyList<(string Role, string Content)> history)
    {
        if (history.Count == 0) return history;

        var budget = BuildWindowBudget();

        var keep = new List<(string Role, string Content)>(history.Count);
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var key = Normalize(history[i].Content);
            if (key.Length > 0 && budget.TryGetValue(key, out var left) && left > 0)
            {
                budget[key] = left - 1;
                continue;
            }

            keep.Add(history[i]);
        }

        keep.Reverse();
        return keep;
    }

    /// <summary>窗口内每条消息的归一化「昵称+正文」及出现次数。</summary>
    private Dictionary<string, int> BuildWindowBudget()
    {
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in Messages)
        {
            var key = Normalize(Render(m));
            if (key.Length == 0) continue;
            budget[key] = budget.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return budget;
    }

    /// <summary>与 ContextBuilder.BuildConversation 的 user 行格式保持一致。</summary>
    private static string Render(ObservedMessage m) => $"[{m.SenderName}]: {m.Text}";

    /// <summary>
    /// 压掉全部空白再比。窗口消息进窗口前被 Observe 压过空白，而入库那行是上游原文，
    /// 折行清理（CjkTextFixer）又可能改过它——只压空白做模糊匹配，避免该匹配的漏掉。
    /// </summary>
    private static string Normalize(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : MultiSpace.Replace(text, string.Empty).Trim();

    /// <summary>窗口内最近 max 张图片的下载链，按时间正序。取最近的是因为官方 URL 约一小时后失效。</summary>
    public IReadOnlyList<string> CollectImages(int max)
    {
        if (max <= 0) return Array.Empty<string>();

        var all = new List<string>();
        foreach (var m in Messages)
            if (m.ImageUrls is { Count: > 0 })
                all.AddRange(m.ImageUrls);

        return all.Count <= max ? all : all[^max..];
    }

    public int ImageCount
    {
        get
        {
            var n = 0;
            foreach (var m in Messages)
                n += m.ImageUrls?.Count ?? 0;
            return n;
        }
    }
}
