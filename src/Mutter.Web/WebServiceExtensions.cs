using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Interfaces;
using Mutter.Core.Services;
using Mutter.Web.Hubs;
using Mutter.Web.Middleware;
using Mutter.Web.Services;

namespace Mutter.Web;

public static class WebServiceExtensions
{
    public static IServiceCollection AddMutterWeb(this IServiceCollection services)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(WebServiceExtensions).Assembly);
        services.AddSignalR();
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins("http://localhost:5188", "http://127.0.0.1:5188")
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        // 注册 SignalR 通知服务
        services.AddSingleton<INotificationService, SignalRNotificationService>();
        services.AddSingleton<PanelStatisticsService>();
        services.AddScoped<ChatTimelineReader>();
        services.AddSingleton<PanelPasswordStore>();

        // 自更新：检查最新正式版、下载校验并替换程序文件。
        // 更新包下载不设总超时（慢链路可能要几十分钟），只靠连接/读取停滞超时与自动重试兜底。
        services.AddHttpClient("update-download", client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mutter-Bot-Update");
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = OutboundAddressPolicy.CreateHandler(
                    allowSystemProxy: () => sp.GetService<OutboundSettingsStore>()?.AllowSystemProxy == true);
                handler.ConnectTimeout = TimeSpan.FromSeconds(30);
                handler.AutomaticDecompression = DecompressionMethods.None;
                return handler;
            });
        // 清单地址可由面板改成镜像源；默认客户端也要做建连校验，避免它成为内网探测通道。
        // 空名字就是 CreateClient() 用的默认客户端，这里显式取回它的 builder 以便挂校验。
        services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(sp => OutboundAddressPolicy.CreateHandler(
                allowSystemProxy: () => sp.GetService<OutboundSettingsStore>()?.AllowSystemProxy == true));
        services.AddHttpClient("ai-connections", client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(sp => OutboundAddressPolicy.CreateHandler(
                allowRedirect: false, allowSystemProxy: () => sp.GetService<OutboundSettingsStore>()?.AllowSystemProxy == true));
        services.AddSingleton<UpdateSettingsStore>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<UpdateApplier>();

        return services;
    }

    public static WebApplication ConfigureMutterWeb(this WebApplicationBuilder builder)
    {
        builder.Services.AddMutterWeb();
        // 鉴权中间件依赖会话存储与登录限流；这个扩展是独立入口，缺省时补上，避免一启动就 500。
        if (builder.Services.All(descriptor => descriptor.ServiceType != typeof(PanelSessionStore)))
        {
            var timeout = TimeSpan.FromMinutes(builder.Configuration.GetValue("PanelAuth:SessionTimeoutMinutes", 120));
            builder.Services.AddSingleton(new PanelSessionStore(timeout));
        }
        if (builder.Services.All(descriptor => descriptor.ServiceType != typeof(PanelLoginGuard)))
            builder.Services.AddSingleton<PanelLoginGuard>();

        var app = builder.Build();

        // 与 Program.cs / HostedServiceManager 保持同一套管道：这个扩展是公开入口，
        // 少挂鉴权会让所有 API 与 Hub 在无会话时也能访问。
        app.UseMiddleware<ApiAuthMiddleware>();
        app.UseCors();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapControllers();
        app.MapHub<BotStatusHub>("/hub/botstatus");

        return app;
    }
}
