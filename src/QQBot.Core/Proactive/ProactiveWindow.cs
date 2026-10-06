namespace QQBot.Core.Proactive;

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
    public required string GroupId { get; init; }

    public required IReadOnlyList<ObservedMessage> Messages { get; init; }

    public required string LastMessageId { get; init; }

    public required DateTime LastMessageUtcAt { get; init; }

    /// <summary>被动回复槽位是否仍在官方 5 分钟时效内。</summary>
    public bool PassiveSlotAlive(DateTime utcNow)
        => !string.IsNullOrEmpty(LastMessageId) && (utcNow - LastMessageUtcAt) < TimeSpan.FromMinutes(5);

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
