using System.Collections.Concurrent;

namespace QQBot.Core.Services;

/// <summary>
/// 被动回复的 msg_seq 计数器。官方按 msg_id+msg_seq 去重，同一 msg_id 重复用同一 seq 会被拒
///（40054005）。被动回复与 AI 择机插话都从这里取号，故计数器必须集中一处共享。
/// </summary>
public sealed class ReplySeqTracker
{
    private const int PruneThreshold = 2000;

    private readonly ConcurrentDictionary<string, (int Seq, DateTime SeenAt)> _counters = new();

    /// <summary>取该 msg_id 的下一个回复序号；主动消息（msgId 为 null）返回 null。</summary>
    public int? Next(string? msgId)
    {
        if (string.IsNullOrEmpty(msgId)) return null;

        var now = DateTime.UtcNow;
        var seq = _counters.AddOrUpdate(msgId, _ => (1, now), (_, v) => (v.Seq + 1, now)).Seq;

        if (_counters.Count > PruneThreshold)
        {
            var cutoff = now.AddMinutes(-10);
            foreach (var kv in _counters)
            {
                if (kv.Value.SeenAt < cutoff) _counters.TryRemove(kv.Key, out _);
            }
        }

        return seq;
    }
}
