using System.Collections.Concurrent;
using Terminal.Gui;
using Mutter.Web.Hubs;

namespace Mutter.ConsoleHost.Tui;

/// <summary>
/// 实时日志视图：显示日志条目，支持自动滚动、暂停滚动、清空。
/// </summary>
public sealed class LogsView : View
{
    private readonly ListView _listView;
    private readonly List<string> _lines = new();
    private volatile bool _autoScroll = true;
    private Label _statusLabel;
    private const int MaxLines = 5000;

    public LogsView()
    {
        X = 0; Y = 0;
        Width = Dim.Fill(); Height = Dim.Fill();

        _statusLabel = new Label("  日志自动滚动: 开启  | Ctrl+P 暂停/恢复  | Ctrl+K 清空")
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            ColorScheme = MainTuiApp.YellowScheme
        };

        _listView = new ListView()
        {
            X = 0, Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = MainTuiApp.DarkScheme,
            AllowsMarking = false
        };

        Add(_statusLabel, _listView);
    }

    /// <summary>从 LiveLogBus 接收新日志（由 MainLoop 调用，线程安全）</summary>
    public void AppendLog(LogEntry entry)
    {
        var timeStr = entry.Timestamp.ToString("HH:mm:ss.fff");
        var levelStr = entry.Level switch
        {
            "Debug" => "DBG",
            "Information" => "INF",
            "Warning" => "WRN",
            "Error" => "ERR",
            "Critical" => "CRT",
            "Trace" => "TRC",
            _ => entry.Level.Length > 3 ? entry.Level[..3] : entry.Level
        };

        var line = $"[{timeStr}] [{levelStr}] [{entry.Category}] {entry.Message}";
        if (entry.Exception is not null)
        {
            var firstLine = entry.Exception.Split('\n')[0];
            line += " | " + firstLine;
        }

        _lines.Add(line);

        if (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
        }

        _listView.SetSource(_lines);

        if (_autoScroll && _lines.Count > 0)
        {
            _listView.SelectedItem = _lines.Count - 1;
        }

        _listView.SetNeedsDisplay();
    }

    public void ToggleAutoScroll()
    {
        _autoScroll = !_autoScroll;
        _statusLabel.Text = _autoScroll
            ? "  日志自动滚动: 开启  | Ctrl+P 暂停/恢复  | Ctrl+K 清空"
            : "  日志自动滚动: 暂停  | Ctrl+P 暂停/恢复  | Ctrl+K 清空";
        _statusLabel.ColorScheme = _autoScroll ? MainTuiApp.YellowScheme : MainTuiApp.RedScheme;
        _statusLabel.SetNeedsDisplay();
    }

    public new void Clear()
    {
        _lines.Clear();
        _listView.SetSource(_lines);
        _listView.SetNeedsDisplay();
    }
}
