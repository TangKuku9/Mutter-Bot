using System.Collections.Concurrent;

namespace QQBot.Web.Services;

/// <summary>
/// 管理面板登录会话的内存存储。会话 ID 通过 HttpOnly Cookie 下发。
/// </summary>
public class PanelSessionStore
{
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
    private readonly TimeSpan _timeout;

    public PanelSessionStore(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    /// <summary>创建新会话并返回会话令牌。</summary>
    public string CreateSession()
    {
        Cleanup();
        // 256-bit 随机令牌
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        _sessions[token] = DateTime.UtcNow.Add(_timeout);
        return token;
    }

    /// <summary>校验会话令牌是否有效，有效则滑动续期。</summary>
    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;

        if (_sessions.TryGetValue(token, out var expiry))
        {
            if (expiry > DateTime.UtcNow)
            {
                // 滑动过期：每次访问续期
                _sessions[token] = DateTime.UtcNow.Add(_timeout);
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

    private void Cleanup()
    {
        foreach (var kv in _sessions)
        {
            if (kv.Value <= DateTime.UtcNow)
                _sessions.TryRemove(kv.Key, out _);
        }
    }
}
