using System.Collections.Concurrent;

namespace Mutter.Core.Services;

/// <summary>
/// 图文联网能力的进程内账本：
/// - 探测阶段记录每个形状“图片 + 搜索”是否真的生效，供「选用」落库与运行时决策；
/// - 运行时被上游确定性拒绝后记一个短 TTL 的负缓存，避免每条带图消息都先撞一次 400。
/// </summary>
public sealed class AiSearchCapabilityStore
{
    private static readonly TimeSpan RuntimeNegativeTtl = TimeSpan.FromMinutes(30);

    private sealed record ProbeEntry(bool ImageCapable, DateTimeOffset RecordedAt);

    private readonly ConcurrentDictionary<string, ProbeEntry> _probe = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _runtimeUnsupported = new(StringComparer.Ordinal);

    public static string Key(string apiBaseUrl, string modelName, string shape) =>
        $"{apiBaseUrl.Trim().ToLowerInvariant()}|{modelName.Trim().ToLowerInvariant()}|{shape}";

    public void RecordProbe(string apiBaseUrl, string modelName, string shape, bool imageCapable)
    {
        _probe[Key(apiBaseUrl, modelName, shape)] = new ProbeEntry(imageCapable, DateTimeOffset.UtcNow);
        _runtimeUnsupported.TryRemove(Key(apiBaseUrl, modelName, shape), out _);
    }

    /// <summary>null = 没探测过 / 该形状没有结论；true/false = 实测结论。</summary>
    public bool? GetProbeImageCapability(string apiBaseUrl, string modelName, string shape)
    {
        return _probe.TryGetValue(Key(apiBaseUrl, modelName, shape), out var entry)
            ? entry.ImageCapable
            : null;
    }

    public void MarkRuntimeUnsupported(string apiBaseUrl, string modelName, string shape)
    {
        _runtimeUnsupported[Key(apiBaseUrl, modelName, shape)] = DateTimeOffset.UtcNow + RuntimeNegativeTtl;
    }

    public bool IsRuntimeUnsupported(string apiBaseUrl, string modelName, string shape)
    {
        var key = Key(apiBaseUrl, modelName, shape);
        if (!_runtimeUnsupported.TryGetValue(key, out var until)) return false;
        if (until > DateTimeOffset.UtcNow) return true;
        _runtimeUnsupported.TryRemove(key, out _);
        return false;
    }

    public void Clear(string apiBaseUrl, string modelName, string shape)
    {
        var key = Key(apiBaseUrl, modelName, shape);
        _probe.TryRemove(key, out _);
        _runtimeUnsupported.TryRemove(key, out _);
    }
}
