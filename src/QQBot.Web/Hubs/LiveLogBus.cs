using System.Collections.Concurrent;

namespace QQBot.Web.Hubs;

/// <summary>
/// 实时日志事件总线：FileLoggerProvider 推送日志，BotStatusHub 订阅转发给前端
/// </summary>
public sealed class LiveLogBus
{
    /// <summary>最近 N 条日志缓冲，供新连接的客户端拉取历史</summary>
    private readonly ConcurrentQueue<LogEntry> _buffer = new();
    private const int MaxBufferCount = 300;

    /// <summary>日志产生事件（订阅者：各 SignalR 连接）</summary>
    public event Action<LogEntry>? LogEmitted;

    public void Emit(LogEntry entry)
    {
        _buffer.Enqueue(entry);
        while (_buffer.Count > MaxBufferCount)
            _buffer.TryDequeue(out _);

        try { LogEmitted?.Invoke(entry); }
        catch { /* 订阅者异常不应影响日志记录 */ }
    }

    /// <summary>获取缓冲区内的历史日志（按时间正序）</summary>
    public IReadOnlyList<LogEntry> GetRecentLogs() => _buffer.ToArray();
}

/// <summary>日志条目</summary>
public sealed class LogEntry
{
    public DateTime Timestamp { get; init; }
    public string Level { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Exception { get; init; }
}
