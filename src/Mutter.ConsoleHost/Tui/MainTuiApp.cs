using System.Collections.Concurrent;
using Terminal.Gui;
using Mutter.ConsoleHost.Services;
using Mutter.Web.Hubs;

namespace Mutter.ConsoleHost.Tui;

/// <summary>
/// TUI 主应用：基于 Terminal.Gui 的终端界面。
/// 提供仪表盘、实时日志、配置管理三个视图，底部状态栏实时显示连接状态。
/// </summary>
public sealed class MainTuiApp
{
    private readonly HostedServiceManager _serviceManager;
    private Toplevel? _top;
    private StatusBar? _statusBar;
    private StatusItem? _connStatusItem;
    private StatusItem? _botInfoItem;
    private StatusItem? _uptimeItem;

    private readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private LogsView? _logsView;
    private DashboardView? _dashboardView;
    private FrameView? _contentFrame;

    // 主题色方案（延迟初始化，确保 Application.Driver 已就绪）
    private static ColorScheme? _darkScheme;
    public static ColorScheme DarkScheme =>
        _darkScheme ??= new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.White, Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.Black, Color.Cyan),
            HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black),
            HotFocus = Application.Driver.MakeAttribute(Color.Blue, Color.Cyan),
            Disabled = Application.Driver.MakeAttribute(Color.DarkGray, Color.Black)
        };

    private static ColorScheme? _greenScheme;
    public static ColorScheme GreenScheme =>
        _greenScheme ??= new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.Black, Color.BrightGreen),
            HotNormal = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
            HotFocus = Application.Driver.MakeAttribute(Color.Blue, Color.BrightGreen),
            Disabled = Application.Driver.MakeAttribute(Color.DarkGray, Color.Black)
        };

    private static ColorScheme? _redScheme;
    public static ColorScheme RedScheme =>
        _redScheme ??= new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.BrightRed, Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.Black, Color.BrightRed),
            HotNormal = Application.Driver.MakeAttribute(Color.BrightRed, Color.Black),
            HotFocus = Application.Driver.MakeAttribute(Color.Blue, Color.BrightRed),
            Disabled = Application.Driver.MakeAttribute(Color.DarkGray, Color.Black)
        };

    private static ColorScheme? _yellowScheme;
    public static ColorScheme YellowScheme =>
        _yellowScheme ??= new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.BrightYellow, Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.Black, Color.BrightYellow),
            HotNormal = Application.Driver.MakeAttribute(Color.BrightYellow, Color.Black),
            HotFocus = Application.Driver.MakeAttribute(Color.Blue, Color.BrightYellow),
            Disabled = Application.Driver.MakeAttribute(Color.DarkGray, Color.Black)
        };

    public MainTuiApp(HostedServiceManager serviceManager)
    {
        _serviceManager = serviceManager;
    }

    public void Run()
    {
        Application.Init();

        // 初始化主题（必须在 Application.Init() 之后）
        _ = DarkScheme;
        _ = GreenScheme;
        _ = RedScheme;
        _ = YellowScheme;

        _top = new Toplevel
        {
            X = 0, Y = 0,
            Width = Dim.Fill(), Height = Dim.Fill(),
            ColorScheme = DarkScheme
        };

        // 订阅实时日志
        if (_serviceManager.LiveLogBus is not null)
        {
            _serviceManager.LiveLogBus.LogEmitted += OnLogEmitted;
        }

        BuildMenuBar();
        BuildContent();
        BuildStatusBar();

        // 定时刷新状态栏和消费日志队列
        Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(500), _ =>
        {
            UpdateStatusBar();
            DrainLogQueue();
            return true;
        });

        Application.Run(_top);
        Application.Shutdown();

        if (_serviceManager.LiveLogBus is not null)
            _serviceManager.LiveLogBus.LogEmitted -= OnLogEmitted;
    }

    /// <summary>面板触发自更新重启时，从后台线程安全地结束 TUI 主循环。</summary>
    public void RequestShutdown()
    {
        try
        {
            Application.MainLoop?.Invoke(() => Application.RequestStop());
        }
        catch
        {
            // TUI 已退出或尚未初始化
        }
    }

    private void OnLogEmitted(LogEntry entry)
    {
        _logQueue.Enqueue(entry);
    }

    private void DrainLogQueue()
    {
        while (_logQueue.TryDequeue(out var entry))
        {
            _logsView?.AppendLog(entry);
        }
    }

    private void BuildMenuBar()
    {
        var menuBar = new MenuBar(new MenuBarItem[]
        {
            new MenuBarItem("_视图", new MenuItem[]
            {
                new MenuItem("_仪表盘", "显示 Bot 状态概览", () => SwitchToView("dashboard")),
                new MenuItem("_日志", "实时日志查看", () => SwitchToView("logs")),
                new MenuItem("_配置", "查看运行配置", () => SwitchToView("config")),
                null!, // Terminal.Gui 约定：null 项渲染为菜单分隔符
                new MenuItem("_退出", "关闭 Mutter", () => RequestQuit())
            }),
            new MenuBarItem("_操作", new MenuItem[]
            {
                new MenuItem("_清空日志", "清除日志视图", () => _logsView?.Clear()),
                new MenuItem("_暂停滚动", "暂停/恢复日志自动滚动", () => _logsView?.ToggleAutoScroll()),
            }),
            new MenuBarItem("_帮助", new MenuItem[]
            {
                new MenuItem("_关于", "Mutter", () => ShowAbout())
            })
        });
        _top!.Add(menuBar);
    }

    private void BuildContent()
    {
        _contentFrame = new FrameView("")
        {
            X = 0, Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };

        _dashboardView = new DashboardView(_serviceManager);
        _logsView = new LogsView();
        var configView = new ConfigView(_serviceManager);

        _contentFrame.Add(_dashboardView, _logsView, configView);
        _dashboardView.Visible = true;
        _logsView.Visible = false;
        configView.Visible = false;

        _top!.Add(_contentFrame);
    }

    private void BuildStatusBar()
    {
        _connStatusItem = new StatusItem(Key.CtrlMask | Key.D, " Gateway: ---", null);
        _botInfoItem = new StatusItem(Key.Null, " Bot: ---", null);
        _uptimeItem = new StatusItem(Key.Null, " Uptime: ---", null);

        _statusBar = new StatusBar(new[]
        {
            _connStatusItem,
            _botInfoItem,
            _uptimeItem,
            new StatusItem(Key.CtrlMask | Key.Q, "~^Q~ 退出", () => RequestQuit()),
            new StatusItem(Key.CtrlMask | Key.T, "~^T~ 日志", () => SwitchToView("logs"))
        });

        _top!.Add(_statusBar);
    }

    private void SwitchToView(string view)
    {
        if (_contentFrame == null) return;
        foreach (var sub in _contentFrame.Subviews)
        {
            sub.Visible = false;
        }
        switch (view)
        {
            case "dashboard":
                _dashboardView!.Visible = true;
                _dashboardView.UpdateStatus();
                _contentFrame.Title = " 仪表盘 ";
                break;
            case "logs":
                _logsView!.Visible = true;
                _contentFrame.Title = " 实时日志 ";
                break;
            case "config":
                _contentFrame.Subviews.OfType<ConfigView>().FirstOrDefault()!.Visible = true;
                _contentFrame.Title = " 运行配置 ";
                break;
        }
        _contentFrame.SetNeedsDisplay();
    }

    private void UpdateStatusBar()
    {
        var connected = _serviceManager.IsGatewayConnected;
        _connStatusItem!.Title = connected
            ? " Gateway: ● 已连接 "
            : " Gateway: ○ 未连接 ";

        var (botId, botName) = _serviceManager.BotInfo;
        _botInfoItem!.Title = string.IsNullOrEmpty(botId)
            ? " Bot: --- "
            : $" Bot: {botName}({botId}) ";

        var uptime = DateTime.UtcNow - _serviceManager.StartTime;
        _uptimeItem!.Title = $" Uptime: {(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s ";

        _statusBar?.SetNeedsDisplay();
    }

    private void RequestQuit()
    {
        var result = MessageBox.Query("退出", "确定要关闭 Mutter 吗？", "确定", "取消");
        if (result == 0)
        {
            Application.RequestStop();
        }
    }

    private void ShowAbout()
    {
        MessageBox.Query("关于",
            "Mutter\n\n" +
            "基于 .NET 9 + Terminal.Gui\n" +
            "QQ 官方 API + WebSocket Gateway\n\n" +
            "Web 面板: http://localhost:5188",
            "确定");
    }
}
