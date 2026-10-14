using System.Globalization;
using Mutter.ConsoleHost.Services;
using Mutter.ConsoleHost.Tui;

namespace Mutter.ConsoleHost;

/// <summary>
/// Linux 控制台入口：启动后台服务（Web Host + QQ Gateway），然后运行 TUI 界面。
/// 也可通过 --headless 参数以无 TUI 模式运行（适合 systemd 服务）。
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        // 固定运行时环境，避免不同机器上的格式化与日界统计出现差异。
        // 1) 文化固定为 zh-CN——保证百分比/数字等格式化稳定
        //    （否则 invariant/其他 locale 会把 "12%" 渲染成 "12 %"）。
        // 2) 时区固定为 Asia/Shanghai——保证依赖本地日界的逻辑（如"今日消息数"统计
        //    的 DateTime.Today）按北京时间零点计算，而不是服务器时区（可能是 UTC）。
        try
        {
            var zhCn = new CultureInfo("zh-CN");
            CultureInfo.DefaultThreadCurrentCulture = zhCn;
            CultureInfo.DefaultThreadCurrentUICulture = zhCn;
            CultureInfo.CurrentCulture = zhCn;
            CultureInfo.CurrentUICulture = zhCn;
        }
        catch (CultureNotFoundException)
        {
            // 缺少 ICU 时退回系统默认（见 deploy 说明：需安装 libicu）
        }
        TimeZoneInfo.ClearCachedData();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TZ")))
        {
            // 仅在未显式指定时区时兜底为北京时间；Linux 下 TimeZoneInfo.Local 读取 TZ 变量
            Environment.SetEnvironmentVariable("TZ", "Asia/Shanghai");
            TimeZoneInfo.ClearCachedData();
        }

        var headless = Array.Exists(args, a => a is "--headless" or "-h");

        var serviceManager = new HostedServiceManager();
        await serviceManager.StartAllAsync();

        if (headless)
        {
            // 无 TUI 模式：等待关闭信号
            Console.WriteLine("Mutter running in headless mode. Press Ctrl+C to stop.");
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
