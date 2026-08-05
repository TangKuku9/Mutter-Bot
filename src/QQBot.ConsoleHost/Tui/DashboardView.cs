using Terminal.Gui;
using QQBot.ConsoleHost.Services;

namespace QQBot.ConsoleHost.Tui;

/// <summary>
/// 仪表盘视图：显示 Bot 连接状态、Bot 信息、运行时间、Web 面板地址。
/// </summary>
public sealed class DashboardView : View
{
    private readonly HostedServiceManager _serviceManager;
    private readonly Label _connStatus;
    private readonly Label _botId;
    private readonly Label _botName;
    private readonly Label _uptime;
    private readonly Label _webPanel;
    private readonly Label _dbPath;
    private readonly Label _logPath;

    public DashboardView(HostedServiceManager serviceManager)
    {
        _serviceManager = serviceManager;
        X = 0; Y = 0;
        Width = Dim.Fill(); Height = Dim.Fill();

        var title = new Label("  QQBot 控制面板")
        {
            X = 0, Y = 0,
            ColorScheme = MainTuiApp.GreenScheme
        };
        Add(title);

        var separator = new Label(new string('-', 80))
        {
            X = 0, Y = 1,
            Width = Dim.Fill()
        };
        Add(separator);

        // 连接状态框
        var connFrame = new FrameView(" 网关连接 ")
        {
            X = 1, Y = 3,
            Width = Dim.Fill() - 1,
            Height = 5
        };
        _connStatus = new Label("  状态: 检查中...")
        {
            X = 0, Y = 0,
            Width = Dim.Fill()
        };
        connFrame.Add(_connStatus);
        Add(connFrame);

        // Bot 信息框
        var botFrame = new FrameView(" Bot 信息 ")
        {
            X = 1, Y = 8,
            Width = Dim.Fill() - 1,
            Height = 7
        };
        _botId = new Label("  Bot ID:   ---") { X = 0, Y = 0, Width = Dim.Fill() };
        _botName = new Label("  Bot 名称: ---") { X = 0, Y = 1, Width = Dim.Fill() };
        _uptime = new Label("  运行时间: ---") { X = 0, Y = 2, Width = Dim.Fill() };
        botFrame.Add(_botId, _botName, _uptime);
        Add(botFrame);

        // 服务信息框
        var svcFrame = new FrameView(" 服务信息 ")
        {
            X = 1, Y = 15,
            Width = Dim.Fill() - 1,
            Height = 8
        };
        _webPanel = new Label("  Web 面板: http://localhost:5188")
        {
            X = 0, Y = 0, Width = Dim.Fill(),
            ColorScheme = MainTuiApp.YellowScheme
        };
        _dbPath = new Label("  数据库:   ---") { X = 0, Y = 1, Width = Dim.Fill() };
        _logPath = new Label("  日志文件: ---") { X = 0, Y = 2, Width = Dim.Fill() };
        var hint = new Label("  提示: 浏览器访问 Web 面板进行详细管理")
        {
            X = 0, Y = 4, Width = Dim.Fill(),
            ColorScheme = MainTuiApp.DarkScheme
        };
        svcFrame.Add(_webPanel, _dbPath, _logPath, hint);
        Add(svcFrame);

        // 快捷键提示
        var keys = new Label(
            "  快捷键:  Ctrl+D=仪表盘  Ctrl+L=日志  Ctrl+C=配置  Ctrl+Q=退出")
        {
            X = 1, Y = 24,
            Width = Dim.Fill(),
            ColorScheme = MainTuiApp.DarkScheme
        };
        Add(keys);
    }

    public void UpdateStatus()
    {
        var connected = _serviceManager.IsGatewayConnected;
        _connStatus.Text = connected
            ? "  状态: ● 已连接  (QQ Gateway 正常运行)"
            : "  状态: ○ 未连接  (正在重连...)";
        _connStatus.ColorScheme = connected ? MainTuiApp.GreenScheme : MainTuiApp.RedScheme;

        var (botId, botName) = _serviceManager.BotInfo;
        _botId.Text = $"  Bot ID:   {botId}";
        _botName.Text = $"  Bot 名称: {botName}";

        var uptime = DateTime.UtcNow - _serviceManager.StartTime;
        _uptime.Text = $"  运行时间: {(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";

        _dbPath.Text = $"  数据库:   {Path.Combine(AppContext.BaseDirectory, "qqbot.db")}";
        _logPath.Text = $"  日志文件: {Path.Combine(AppContext.BaseDirectory, "logs", "bot.log")}";

        SetNeedsDisplay();
    }

    public override void Redraw(Rect region)
    {
        UpdateStatus();
        base.Redraw(region);
    }
}
