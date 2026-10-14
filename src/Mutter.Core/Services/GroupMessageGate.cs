using System.Collections.Concurrent;

namespace Mutter.Core.Services;

/// <summary>
/// 群消息重复投递闸门。
/// 开启群全量消息后，同一条 @ 机器人的消息会被 GROUP_MESSAGE_CREATE 与 GROUP_AT_MESSAGE_CREATE
/// 各下发一次，且是同一个 msg_id，谁先到不可控。旧的"id 见过就丢"在全量事件先到时会把 @ 事件
/// 一起吞掉，线上表现就是围观档下 bot 对 @ 它的人完全不回话。所以 @ 事件必须有第二次机会。
/// </summary>
public sealed class GroupMessageGate
{
    private const int PruneThreshold = 5000;
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Entry> _seen = new();

    /// <param name="reprocessAsAt">
    /// true 表示这条之前只按普通消息处理过（已落库），本次要补走 @ 回复链路，调用方不得重复落库。
    /// </param>
    public bool ShouldHandle(string msgId, bool isAtBot, out bool reprocessAsAt)
    {
        reprocessAsAt = false;
        var now = DateTime.UtcNow;

        if (_seen.TryAdd(msgId, new Entry(now, isAtBot)))
        {
            Prune(now);
            return true;
        }

        if (!isAtBot) return false;                       // 普通消息的重复投递
        if (!_seen.TryGetValue(msgId, out var prev) || prev.HandledAsAt) return false;

        _seen[msgId] = prev with { HandledAsAt = true };   // 全量先到、@ 事件后到：补走回复
        reprocessAsAt = true;
        return true;
    }

    private void Prune(DateTime now)
    {
        if (_seen.Count <= PruneThreshold) return;
        var cutoff = now - KeepFor;
        foreach (var kv in _seen)
            if (kv.Value.SeenAt < cutoff)
                _seen.TryRemove(kv.Key, out _);
    }

    private readonly record struct Entry(DateTime SeenAt, bool HandledAsAt);
}
