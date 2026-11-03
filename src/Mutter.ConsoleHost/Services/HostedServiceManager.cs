using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.ConsoleHost.Logging;
using Mutter.Core;
using Mutter.Core.EventHandlers;
using Mutter.Core.Interfaces;
using Mutter.Infrastructure;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Services;
using Mutter.Web;
using Mutter.Web.Hubs;
using Mutter.Web.Middleware;
using Mutter.Web.Services;

namespace Mutter.ConsoleHost.Services;

/// <summary>
/// 后台服务管理器：启动 Web Host + QQ Gateway，提供状态查询和实时日志。
/// 从 Windows 版 HostedServiceManager 迁移而来，移除了 WPF 依赖。
/// </summary>
public sealed class HostedServiceManager
{
    private WebApplication? _webApp;
    private LiveLogBus? _liveLogBus;
    private UpdateApplier? _updateApplier;

    /// <summary>实时日志总线，供 TUI 订阅显示</summary>
    public LiveLogBus? LiveLogBus => _liveLogBus;

    /// <summary>QQ 网关连接状态</summary>
    public bool IsGatewayConnected =>
        _webApp?.Services.GetService<IQqGatewayConnection>()?.IsConnected ?? false;

    /// <summary>Bot 信息</summary>
    public (string BotId, string BotName) BotInfo =>
        _webApp?.Services.GetService<IQqBotApiService>() is { } api
            ? (api.BotId, api.BotName)
            : (string.Empty, string.Empty);

    /// <summary>启动时间</summary>
    public DateTime StartTime { get; } = DateTime.UtcNow;

    public async Task StartAllAsync()
    {
        await StartWebHostAsync();
    }

    private async Task StartWebHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseUrls("http://0.0.0.0:5188");

        // 实时日志总线
        _liveLogBus = new LiveLogBus();
        builder.Services.AddSingleton(_liveLogBus);

        // 文件日志 + 实时推送
        var logFilePath = Path.Combine(AppContext.BaseDirectory, "logs", "bot.log");
        builder.Logging.AddProvider(new FileLoggerProvider(logFilePath, _liveLogBus));

        // 统一注册所有层的服务
        builder.Services.AddCoreServices();
        builder.Services.AddInfrastructureServices();
        builder.Services.AddMutterWeb();

        // 管理面板会话存储（与 Mutter.Web/Program.cs 保持一致）
        builder.Services.AddSingleton(new PanelSessionStore(TimeSpan.FromMinutes(120)));
        builder.Services.AddSingleton<PanelLoginGuard>();

        _webApp = builder.Build();
        _updateApplier = _webApp.Services.GetService<UpdateApplier>();
        _webApp.UseMiddleware<ApiAuthMiddleware>();
        _webApp.UseCors();
        _webApp.UseDefaultFiles();
        _webApp.UseStaticFiles();
        _webApp.MapControllers();
        _webApp.MapHub<BotStatusHub>("/hub/botstatus");

        using (var scope = _webApp.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            await db.EnsureSchemaUpgradedAsync();
        }

        await _webApp.StartAsync();
        Console.WriteLine("[Host] Web host started on http://0.0.0.0:5188");
    }

    public async Task StopAllAsync()
    {
        if (_webApp != null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await _webApp.StopAsync(deadline.Token);
            await _webApp.DisposeAsync();
        }

        Console.WriteLine("[Host] All services stopped");
    }

    /// <summary>在 Web 宿主停止后、进程退出前调用；仅 Linux 会实际替换文件。</summary>
    public void ApplyPendingUpdateAfterShutdown()
    {
        _updateApplier?.ApplyPendingAfterShutdown(AppContext.BaseDirectory);
    }
}
