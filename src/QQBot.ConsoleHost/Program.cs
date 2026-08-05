using QQBot.ConsoleHost.Services;
using QQBot.ConsoleHost.Tui;

namespace QQBot.ConsoleHost;

/// <summary>
/// Linux 控制台入口：启动后台服务（Web Host + QQ Gateway），然后运行 TUI 界面。
/// 也可通过 --headless 参数以无 TUI 模式运行（适合 systemd 服务）。
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var headless = Array.Exists(args, a => a is "--headless" or "-h");

        var serviceManager = new HostedServiceManager();
        await serviceManager.StartAllAsync();

        if (headless)
        {
            // 无 TUI 模式：等待关闭信号
            Console.WriteLine("QQBot running in headless mode. Press Ctrl+C to stop.");
            var tcs = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                tcs.TrySetResult();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => tcs.TrySetResult();
            await tcs.Task;
        }
        else
        {
            // TUI 模式
            var tuiApp = new MainTuiApp(serviceManager);
            tuiApp.Run();
        }

        await serviceManager.StopAllAsync();
    }
}
