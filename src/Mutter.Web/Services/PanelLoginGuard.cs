namespace Mutter.Web.Services;

/// <summary>
/// 面板登录失败限流：同一 IP 连续失败达到阈值后锁定一段时间，防暴力破解。
/// </summary>
public class PanelLoginGuard
{
    private const int MaxFailures = 5;
    private const int MaximumAddresses = 4096;
    private const int GlobalAttemptsPerMinute = 20;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private sealed record Attempts(int Count, DateTime FirstAt, DateTime LockedUntil, DateTime LastAt);

    private readonly Dictionary<string, Attempts> _byIp = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _hashSlot = new(1, 1);
    private readonly TimeProvider _clock;
    private DateTime _windowStart;
    private int _windowAttempts;
    private DateTime _nextCleanup;

    public PanelLoginGuard(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>在昂贵哈希前原子接纳；限制全局猜测速度、哈希并发及来源表大小。</summary>
    public bool TryBeginAttempt(string ip, out IDisposable? lease, out TimeSpan retryAfter)
    {
        lease = null;
        retryAfter = TimeSpan.FromSeconds(3);
        lock (_sync)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (now >= _nextCleanup)
            {
                foreach (var key in _byIp.Where(pair => now - pair.Value.LastAt > TimeSpan.FromHours(1)
                    && pair.Value.LockedUntil <= now).Select(pair => pair.Key).ToArray()) _byIp.Remove(key);
                _nextCleanup = now.AddMinutes(1);
            }
            if (_byIp.TryGetValue(ip, out var attempts) && attempts.LockedUntil > now)
            {
                retryAfter = attempts.LockedUntil - now;
                return false;
            }
            if (now - _windowStart >= TimeSpan.FromMinutes(1)) { _windowStart = now; _windowAttempts = 0; }
            if (_windowAttempts >= GlobalAttemptsPerMinute)
            {
                retryAfter = _windowStart.AddMinutes(1) - now;
                return false;
            }
            if (!_byIp.ContainsKey(ip) && _byIp.Count >= MaximumAddresses) return false;
            if (!_hashSlot.Wait(0)) return false;
            _byIp.TryAdd(ip, new(0, now, DateTime.MinValue, now));
            _byIp[ip] = _byIp[ip] with { LastAt = now };
            _windowAttempts++;
            lease = new AttemptLease(_hashSlot);
            return true;
        }
    }

    private sealed class AttemptLease(SemaphoreSlim slot) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) slot.Release(); }
    }

    /// <summary>该 IP 是否处于锁定中；是则返回剩余锁定时间。</summary>
    public TimeSpan? GetLockedRemaining(string ip)
    {
        lock (_sync)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            return _byIp.TryGetValue(ip, out var a) && a.LockedUntil > now ? a.LockedUntil - now : null;
        }
    }

    /// <summary>记录一次失败；达到阈值时开始锁定。</summary>
    public void RecordFailure(string ip)
    {
        lock (_sync)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (!_byIp.TryGetValue(ip, out var old))
            {
                if (_byIp.Count < MaximumAddresses) _byIp[ip] = new(1, now, DateTime.MinValue, now);
                return;
            }
            var count = now - old.FirstAt > TimeSpan.FromHours(1) ? 1 : Math.Min(MaxFailures, old.Count + 1);
            _byIp[ip] = new(count, count == 1 ? now : old.FirstAt, count >= MaxFailures ? now.Add(LockDuration) : old.LockedUntil, now);
        }
    }

    /// <summary>登录成功后清除该 IP 的失败计数。</summary>
    public void RecordSuccess(string ip) { lock (_sync) _byIp.Remove(ip); }
}
