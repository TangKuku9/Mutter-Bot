using System.Collections.Concurrent;
using Mutter.Core.Configuration;
using Mutter.Core.Services;

namespace Mutter.Infrastructure.Services;

/// <summary>总结连接各自维护后台健康与探测租约，避免影响聊天或其他总结连接。</summary>
public sealed class GroupMemoryUpstreamHealth(TimeProvider clock, RuntimeOptions runtime)
{
    private readonly ConcurrentDictionary<string, UpstreamHealthTracker> _connections = new(StringComparer.Ordinal);

    public UpstreamHealthTracker ForConnection(string connectionId) =>
        _connections.GetOrAdd(connectionId, _ => new UpstreamHealthTracker(clock, runtime));
}
