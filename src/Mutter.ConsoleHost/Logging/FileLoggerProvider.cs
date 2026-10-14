using System.IO;
using Microsoft.Extensions.Logging;
using Mutter.Web.Hubs;

namespace Mutter.ConsoleHost.Logging;

/// <summary>
/// 简易文件日志提供程序：将所有日志写入指定文件；
/// 同时推送到 LiveLogBus 供 TUI 实时显示。
/// 从 Windows 版 FileLoggerProvider 迁移而来，代码完全跨平台。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly LiveLogBus? _liveLogBus;

    public FileLoggerProvider(string filePath, LiveLogBus? liveLogBus = null)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _writer = new StreamWriter(
            new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
        _liveLogBus = liveLogBus;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock, _liveLogBus);

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly StreamWriter _writer;
        private readonly object _lock;
        private readonly LiveLogBus? _liveLogBus;

        public FileLogger(string categoryName, StreamWriter writer, object @lock, LiveLogBus? liveLogBus)
        {
            _categoryName = categoryName;
            _writer = writer;
            _lock = @lock;
            _liveLogBus = liveLogBus;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var shortCat = _categoryName.Length > 40
                ? _categoryName[..37] + "..."
                : _categoryName;
            var now = DateTime.Now;
            var levelStr = logLevel.ToString();
            var messageText = formatter(state, exception);

            var msg = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{levelStr,-5}] [{shortCat}] {messageText}";
            if (exception != null)
                msg += Environment.NewLine + exception;

            lock (_lock)
            {
                try { _writer.WriteLine(msg); }
                catch { }
            }

            if (_liveLogBus is not null)
            {
                try
                {
                    _liveLogBus.Emit(new LogEntry
                    {
                        Timestamp = now,
                        Level = levelStr,
                        Category = shortCat,
                        Message = messageText,
                        Exception = exception?.ToString()
                    });
                }
                catch { }
            }
        }
    }
}
