using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Mutter.Web.Services;

/// <summary>
/// 管理面板登录会话的内存存储。会话 ID 通过 HttpOnly Cookie 下发。
/// </summary>
public class PanelSessionStore : IDisposable
{
    /// <summary>会话元数据（创建时间/最后活跃/来源 IP），用于面板会话管理页。</summary>
    public sealed record SessionInfo(string TokenPrefix, DateTime CreatedAt, DateTime LastSeenAt, DateTime ExpiresAt, string? RemoteIp);

    private sealed class SessionRecord
    {
        public DateTime CreatedAt { get; init; }
        public DateTime LastSeenAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string? RemoteIp { get; init; }
        public CancellationTokenSource Revoked { get; } = new();
    }

    public sealed record Validation(DateTimeOffset ExpiresAt, CancellationToken Revocation);
    private const int MaximumSessions = 32;
    private readonly Dictionary<string, SessionRecord> _sessions = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;
    private string? _credentialVersion;
    private IDisposable? _credentialSubscription;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(12);

    public PanelSessionStore(TimeSpan timeout, TimeProvider? clock = null)
    {
        _timeout = TimeSpan.FromMinutes(Math.Clamp(timeout.TotalMinutes, 5, 720));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>创建新会话并返回会话令牌。</summary>
    public string CreateSession(string? remoteIp = null)
    {
        Cleanup();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = _clock.GetUtcNow().UtcDateTime;
        SessionRecord? evicted = null;
        lock (_sync)
        {
            if (_sessions.Count >= MaximumSessions)
            {
                var oldest = _sessions.MinBy(pair => pair.Value.CreatedAt);
                evicted = oldest.Value;
                _sessions.Remove(oldest.Key);
            }
            _sessions[token] = new SessionRecord
            {
                CreatedAt = now, LastSeenAt = now, ExpiresAt = now.Add(_timeout), RemoteIp = remoteIp
            };
        }
        evicted?.Revoked.Cancel();
        return token;
    }

    /// <summary>校验会话令牌是否有效，有效则滑动续期。</summary>
    public bool IsValid(string? token) => TryValidate(token, out _);

    public bool TryValidate(string? token, out Validation session, bool renew = true)
    {
        session = null!;
        if (token?.Length != 64) return false;
        SessionRecord? expired = null;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(token, out var rec)) return false;
            var now = _clock.GetUtcNow().UtcDateTime;
            if (rec.ExpiresAt > now && rec.CreatedAt.Add(MaximumLifetime) > now)
            {
                if (renew)
                {
                    rec.LastSeenAt = now;
                    rec.ExpiresAt = new[] { now.Add(_timeout), rec.CreatedAt.Add(MaximumLifetime) }.Min();
                }
                session = new(new DateTimeOffset(rec.ExpiresAt, TimeSpan.Zero), rec.Revoked.Token);
                return true;
            }
            _sessions.Remove(token);
            expired = rec;
        }
        expired.Revoked.Cancel();
        return false;
    }

    /// <summary>注销指定会话。</summary>
    public void Remove(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        SessionRecord? removed;
        lock (_sync) _sessions.Remove(token, out removed);
        removed?.Revoked.Cancel();
    }

    /// <summary>按令牌前缀注销会话（面板会话管理页仅展示前缀）。返回是否命中。</summary>
    public bool RemoveByPrefix(string tokenPrefix)
    {
        if (tokenPrefix?.Length != 8 || !tokenPrefix.All(Uri.IsHexDigit)) return false;
        List<SessionRecord> removed = [];
        lock (_sync)
        {
            foreach (var key in _sessions.Keys.Where(key => key.StartsWith(tokenPrefix, StringComparison.Ordinal)).ToArray())
            {
                removed.Add(_sessions[key]);
                _sessions.Remove(key);
            }
        }
        foreach (var rec in removed) rec.Revoked.Cancel();
        return removed.Count > 0;
    }

    /// <summary>列出当前全部有效会话（令牌仅返回前 8 位前缀）。</summary>
    public List<SessionInfo> GetSessions()
    {
        Cleanup();
        lock (_sync)
            return _sessions.Select(kv => new SessionInfo(kv.Key[..8], kv.Value.CreatedAt, kv.Value.LastSeenAt,
                kv.Value.ExpiresAt, kv.Value.RemoteIp)).OrderByDescending(s => s.LastSeenAt).ToList();
    }

    /// <summary>清除所有会话（修改密码后强制全部重新登录）。</summary>
    public void ClearAll()
    {
        SessionRecord[] removed;
        lock (_sync) { removed = _sessions.Values.ToArray(); _sessions.Clear(); }
        foreach (var rec in removed) rec.Revoked.Cancel();
    }

    /// <summary>配置中的账号、密码或 API 凭据变化时，现有 Cookie 和实时连接同时撤销。</summary>
    public void SynchronizeCredentials(string username, string password, string apiToken)
    {
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username + "\0" + password + "\0" + apiToken)));
        SessionRecord[] removed = [];
        lock (_sync)
        {
            if (_credentialVersion is not null && _credentialVersion != version)
            {
                removed = _sessions.Values.ToArray();
                _sessions.Clear();
            }
            _credentialVersion = version;
        }
        foreach (var rec in removed) rec.Revoked.Cancel();
    }

    public void ObserveCredentials(IConfiguration configuration)
    {
        void Reload() => SynchronizeCredentials(configuration["PanelAuth:Username"] ?? "",
            configuration["PanelAuth:Password"] ?? "", configuration["ApiAuth:Token"] ?? "");
        Reload();
        _credentialSubscription?.Dispose();
        _credentialSubscription = ChangeToken.OnChange(configuration.GetReloadToken, Reload);
    }

    public void Dispose()
    {
        _credentialSubscription?.Dispose();
        ClearAll();
        GC.SuppressFinalize(this);
    }

    private void Cleanup()
    {
        List<SessionRecord> removed = [];
        lock (_sync)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var key in _sessions.Where(pair => pair.Value.ExpiresAt <= now
                || pair.Value.CreatedAt.Add(MaximumLifetime) <= now).Select(pair => pair.Key).ToArray())
            {
                removed.Add(_sessions[key]);
                _sessions.Remove(key);
            }
        }
        foreach (var rec in removed) rec.Revoked.Cancel();
    }
}
