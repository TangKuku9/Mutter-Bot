using System.Collections.Concurrent;

namespace QQBot.Web.Services;

/// <summary>
/// 面板登录失败限流：同一 IP 连续失败达到阈值后锁定一段时间，防暴力破解。
/// </summary>
public class PanelLoginGuard
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private sealed record Attempts(int Count, DateTime FirstAt, DateTime LockedUntil);

    private readonly ConcurrentDictionary<string, Attempts> _byIp = new();

    /// <summary>该 IP 是否处于锁定中；是则返回剩余锁定时间。</summary>
    public TimeSpan? GetLockedRemaining(string ip)
    {
        if (_byIp.TryGetValue(ip, out var a) && a.LockedUntil > DateTime.UtcNow)
            return a.LockedUntil - DateTime.UtcNow;
        return null;
    }

    /// <summary>记录一次失败；达到阈值时开始锁定。</summary>
    public void RecordFailure(string ip)
    {
        var now = DateTime.UtcNow;
        _byIp.AddOrUpdate(ip,
            _ => new Attempts(1, now, DateTime.MinValue),
            (_, old) =>
            {
                // 超过1小时的失败计数作废，重新开始累计
                var count = (now - old.FirstAt).TotalHours > 1 ? 1 : old.Count + 1;
                var lockedUntil = count >= MaxFailures ? now.Add(LockDuration) : old.LockedUntil;
                return new Attempts(count, count == 1 ? now : old.FirstAt, lockedUntil);
            });
    }

    /// <summary>登录成功后清除该 IP 的失败计数。</summary>
    public void RecordSuccess(string ip) => _byIp.TryRemove(ip, out _);
}
