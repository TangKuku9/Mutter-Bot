using Terminal.Gui;
using QQBot.ConsoleHost.Services;

namespace QQBot.ConsoleHost.Tui;

/// <summary>
/// 配置视图：显示当前运行配置（QQBot 配置、Web 端口、数据库路径等）。
/// </summary>
public sealed class ConfigView : View
{
    public ConfigView(HostedServiceManager serviceManager)
    {
        X = 0; Y = 0;
        Width = Dim.Fill(); Height = Dim.Fill();

        var title = new Label("  运行配置")
        {
            X = 0, Y = 0,
            ColorScheme = MainTuiApp.GreenScheme
        };
        Add(title);

        var sep = new Label(new string('-', 80))
        {
            X = 0, Y = 1,
            Width = Dim.Fill()
        };
        Add(sep);

        // 读取 appsettings.json
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configContent = File.Exists(configPath)
            ? File.ReadAllText(configPath)
            : "(配置文件未找到)";

        // QQ 配置框
        var qqFrame = new FrameView(" QQ Bot 配置 (appsettings.json) ")
        {
            X = 1, Y = 3,
            Width = Dim.Fill() - 1,
            Height = 10
        };

        string appId = "---", apiBaseUrl = "---";
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(configContent);
            if (json.RootElement.TryGetProperty("QQBot", out var qqbot))
            {
                appId = qqbot.GetProperty("AppId").GetString() ?? "---";
                apiBaseUrl = qqbot.GetProperty("ApiBaseUrl").GetString() ?? "---";
            }
        }
        catch { }

        qqFrame.Add(
            new Label($"  AppId:         {appId}") { X = 0, Y = 0, Width = Dim.Fill() },
            new Label($"  ApiBaseUrl:    {apiBaseUrl}") { X = 0, Y = 1, Width = Dim.Fill() },
            new Label($"  ClientSecret:  ****** (已隐藏)") { X = 0, Y = 2, Width = Dim.Fill(), ColorScheme = MainTuiApp.YellowScheme }
        );
        Add(qqFrame);

        // 服务配置框
        var svcFrame = new FrameView(" 服务配置 ")
        {
            X = 1, Y = 13,
            Width = Dim.Fill() - 1,
            Height = 8
        };
        svcFrame.Add(
            new Label($"  Web 面板端口:  5188 (http://0.0.0.0:5188)") { X = 0, Y = 0, Width = Dim.Fill() },
            new Label($"  数据库路径:    {Path.Combine(AppContext.BaseDirectory, "qqbot.db")}") { X = 0, Y = 1, Width = Dim.Fill() },
            new Label($"  日志文件:      {Path.Combine(AppContext.BaseDirectory, "logs", "bot.log")}") { X = 0, Y = 2, Width = Dim.Fill() },
            new Label($"  SignalR Hub:   /hub/botstatus") { X = 0, Y = 3, Width = Dim.Fill() }
        );
        Add(svcFrame);

        // 修改提示
        var hint = new Label(
            "  提示: 修改 appsettings.json 后需重启服务生效\n" +
            "         配置文件位于程序同目录下")
        {
            X = 1, Y = 22,
            Width = Dim.Fill(),
            ColorScheme = MainTuiApp.DarkScheme
        };
        Add(hint);
    }
}
