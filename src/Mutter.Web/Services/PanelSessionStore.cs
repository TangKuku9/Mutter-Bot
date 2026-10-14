using System.Collections.Concurrent;

namespace Mutter.Web.Services;

/// <summary>
/// 管理面板登录会话的内存存储。会话 ID 通过 HttpOnly Cookie 下发。
/// </summary>
public class PanelSessionStore
{
    /// <summary>会话元数据（创建时间/最后活跃/来源 IP），用于面板会话管理页。</summary>
    public sealed record SessionInfo(string TokenPrefix, DateTime CreatedAt, DateTime LastSeenAt, DateTime ExpiresAt, string? RemoteIp);

    private sealed class SessionRecord
    {
        public DateTime CreatedAt { get; init; }
        public DateTime LastSeenAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string? RemoteIp { get; init; }
    }

    private readonly ConcurrentDictionary<string, SessionRecord> _sessions = new();
    private readonly TimeSpan _timeout;

    public PanelSessionStore(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    /// <summary>创建新会话并返回会话令牌。</summary>
    public string CreateSession(string? remoteIp = null)
    {
        Cleanup();
        // 256-bit 随机令牌
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        _sessions[token] = new SessionRecord
        {
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.Add(_timeout),
            RemoteIp = remoteIp
        };
        return token;
    }

    /// <summary>校验会话令牌是否有效，有效则滑动续期。</summary>
    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;

        if (_sessions.TryGetValue(token, out var rec))
        {
            var now = DateTime.UtcNow;
            if (rec.ExpiresAt > now)
            {
                // 滑动过期：每次访问续期
                rec.LastSeenAt = now;
                rec.ExpiresAt = now.Add(_timeout);
                return true;
            }
            _sessions.TryRemove(token, out _);
        }
        return false;
    }

    /// <summary>注销指定会话。</summary>
    public void Remove(string? token)
    {
        if (!string.IsNullOrEmpty(token))
            _sessions.TryRemove(token, out _);
    }

    /// <summary>按令牌前缀注销会话（面板会话管理页仅展示前缀）。返回是否命中。</summary>
    public bool RemoveByPrefix(string tokenPrefix)
    {
        if (string.IsNullOrEmpty(tokenPrefix)) return false;
        var hit = false;
        foreach (var kv in _sessions)
        {
            if (kv.Key.StartsWith(tokenPrefix, StringComparison.Ordinal))
            {
                _sessions.TryRemove(kv.Key, out _);
                hit = true;
            }
        }
        return hit;
    }

    /// <summary>列出当前全部有效会话（令牌仅返回前 8 位前缀）。</summary>
    public List<SessionInfo> GetSessions()
    {
        Cleanup();
        return _sessions
            .Select(kv => new SessionInfo(
                TokenPrefix: kv.Key[..Math.Min(8, kv.Key.Length)],
                CreatedAt: kv.Value.CreatedAt,
                LastSeenAt: kv.Value.LastSeenAt,
                ExpiresAt: kv.Value.ExpiresAt,
                RemoteIp: kv.Value.RemoteIp))
            .OrderByDescending(s => s.LastSeenAt)
            .ToList();
    }

    /// <summary>清除所有会话（修改密码后强制全部重新登录）。</summary>
    public void ClearAll()
    {
        _sessions.Clear();
    }

    private void Cleanup()
    {
        foreach (var kv in _sessions)
        {
            if (kv.Value.ExpiresAt <= DateTime.UtcNow)
                _sessions.TryRemove(kv.Key, out _);
        }
    }
}
