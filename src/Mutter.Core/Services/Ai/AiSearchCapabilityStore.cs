using System.Collections.Concurrent;

namespace Mutter.Core.Services;

/// <summary>
/// 联网能力的进程内账本：
/// - 探测阶段分别记录文字验证与明确的图文结论，供「选用」落库与运行时决策；
/// - 运行时被上游确定性拒绝后记一个短 TTL 的负缓存，避免每条带图消息都先撞一次 400。
/// </summary>
public sealed class AiSearchCapabilityStore
{
    private static readonly TimeSpan RuntimeNegativeTtl = TimeSpan.FromMinutes(30);
    /// <summary>探测结论也有保鲜期：模型/网关行为会变，超过一天就当没测过，重新探测一次。</summary>
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromHours(24);

    private sealed record ProbeEntry(DateTimeOffset TextVerifiedAt, bool? ImageCapable, DateTimeOffset ImageCheckedAt);
    private readonly record struct ProbeKey(string ConnectionId, string Version, string Url, string Model, string Shape);

    private readonly ConcurrentDictionary<ProbeKey, ProbeEntry> _probe = new();
    private readonly ConcurrentDictionary<ProbeKey, DateTimeOffset> _runtimeUnsupported = new();

    private static ProbeKey Key(string apiBaseUrl, string modelName, string shape, string connectionId, string connectionVersion) =>
        new(connectionId, connectionVersion, apiBaseUrl.Trim().TrimEnd('/'), modelName.Trim(), shape);

    public void RecordProbe(string apiBaseUrl, string modelName, string shape, bool? imageCapable,
        string connectionId = "", string connectionVersion = "")
    {
        var key = Key(apiBaseUrl, modelName, shape, connectionId, connectionVersion);
        var now = DateTimeOffset.UtcNow;
        if (imageCapable is bool known)
        {
            _probe[key] = new ProbeEntry(now, known, now);
            _runtimeUnsupported.TryRemove(key, out _);
        }
        else
        {
            _probe.AddOrUpdate(key, new ProbeEntry(now, null, default),
                (_, entry) => entry with { TextVerifiedAt = now });
        }
    }

    /// <summary>null = 没探测过 / 结论已过期 / 该形状没有结论；true/false = 实测结论。</summary>
    public bool? GetProbeImageCapability(string apiBaseUrl, string modelName, string shape,
        string connectionId = "", string connectionVersion = "") =>
        GetProbeResult(apiBaseUrl, modelName, shape, connectionId, connectionVersion).ImageCapable;

    public (bool TextVerified, bool? ImageCapable) GetProbeResult(
        string apiBaseUrl, string modelName, string shape, string connectionId = "", string connectionVersion = "")
    {
        var key = Key(apiBaseUrl, modelName, shape, connectionId, connectionVersion);
        if (!_probe.TryGetValue(key, out var entry)) return default;
        var now = DateTimeOffset.UtcNow;
        return (entry.TextVerifiedAt + ProbeTtl > now,
            entry.ImageCheckedAt + ProbeTtl > now ? entry.ImageCapable : null);
    }

    public void MarkRuntimeUnsupported(string apiBaseUrl, string modelName, string shape,
        string connectionId = "", string connectionVersion = "")
    {
        _runtimeUnsupported[Key(apiBaseUrl, modelName, shape, connectionId, connectionVersion)] =
            DateTimeOffset.UtcNow + RuntimeNegativeTtl;
    }

    public bool IsRuntimeUnsupported(string apiBaseUrl, string modelName, string shape,
        string connectionId = "", string connectionVersion = "")
    {
        var key = Key(apiBaseUrl, modelName, shape, connectionId, connectionVersion);
        if (!_runtimeUnsupported.TryGetValue(key, out var until)) return false;
        if (until > DateTimeOffset.UtcNow) return true;
        _runtimeUnsupported.TryRemove(new KeyValuePair<ProbeKey, DateTimeOffset>(key, until));
        return false;
    }

    public void ClearConnection(string connectionId)
    {
        foreach (var key in _probe.Keys.Where(key => key.ConnectionId == connectionId)) _probe.TryRemove(key, out _);
        foreach (var key in _runtimeUnsupported.Keys.Where(key => key.ConnectionId == connectionId)) _runtimeUnsupported.TryRemove(key, out _);
    }

    public void Clear(string apiBaseUrl, string modelName, string shape, string connectionId = "", string connectionVersion = "")
    {
        var key = Key(apiBaseUrl, modelName, shape, connectionId, connectionVersion);
        _probe.TryRemove(key, out _);
        _runtimeUnsupported.TryRemove(key, out _);
    }
}
