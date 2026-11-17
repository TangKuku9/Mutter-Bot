using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RegistrationMarker))) return services;
        services.AddSingleton(new RegistrationMarker());
        services.AddControllers()
            .AddApplicationPart(typeof(WebServiceExtensions).Assembly);
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 16 * 1024;
            options.MaximumParallelInvocationsPerClient = 1;
            options.EnableDetailedErrors = false;
        });
        services.AddAuthentication(PanelAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PanelAuthenticationHandler>(PanelAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization();
        services.TryAddSingleton(sp =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var timeout = TimeSpan.FromMinutes(Math.Clamp(config.GetValue("PanelAuth:SessionTimeoutMinutes", 120), 5, 720));
            var sessions = new PanelSessionStore(timeout, sp.GetService<TimeProvider>());
            sessions.ObserveCredentials(config);
            return sessions;
        });
        services.TryAddSingleton<PanelLoginGuard>();
        services.TryAddSingleton<LiveLogBus>();
        services.AddOptions<ForwardedHeadersOptions>().Configure<Microsoft.Extensions.Configuration.IConfiguration>((options, config) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            var proxies = config.GetSection("PanelSecurity:TrustedProxies").Get<string[]>() ?? ["127.0.0.1", "::1"];
            foreach (var proxy in proxies)
            {
                if (!IPAddress.TryParse(proxy, out var address)) throw new InvalidOperationException("PanelSecurity:TrustedProxies 必须是明确的 IP 地址");
                options.KnownProxies.Add(address);
            }
        });
        services.Configure<KestrelServerOptions>(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 1024 * 1024;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxRequestHeaderCount = 64;
            options.Limits.MaxRequestLineSize = 4096;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Limits.MaxConcurrentConnections = 256;
            options.Limits.MaxConcurrentUpgradedConnections = 64;
            options.Limits.Http2.MaxStreamsPerConnection = 16;
            options.Limits.Http2.MaxRequestHeaderFieldSize = 4096;
            options.Limits.Http2.InitialConnectionWindowSize = 64 * 1024;
            options.Limits.Http2.InitialStreamWindowSize = 64 * 1024;
        });

        // 注册 SignalR 通知服务
        services.AddSingleton<INotificationService, SignalRNotificationService>();
        services.AddSingleton<PanelStatisticsService>();
        services.AddScoped<ChatTimelineReader>();
        services.AddSingleton<PanelPasswordStore>();
        services.AddSingleton<PanelBootstrapStore>();
        services.AddHostedService(sp => sp.GetRequiredService<PanelBootstrapStore>());

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
        var app = builder.Build();
        app.UseMutterWeb();
        return app;
    }

    /// <summary>独立 Web、TUI/无头宿主及公开扩展入口共用，防止安全策略随入口漂移。</summary>
    public static WebApplication UseMutterWeb(this WebApplication app)
    {
        app.UseExceptionHandler(errors => errors.Run(async context =>
        {
            var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            context.Response.StatusCode = exception is Microsoft.AspNetCore.Http.BadHttpRequestException badRequest
                ? badRequest.StatusCode : StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                error = context.Response.StatusCode < 500 ? "请求数据超出限制或格式无效" : "请求处理失败，请查看服务器日志"
            });
        }));
        app.UseForwardedHeaders();
        app.UseMiddleware<PanelSecurityMiddleware>();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<ApiAuthMiddleware>();
        app.UseAuthorization();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapControllers().RequireAuthorization();
        app.MapHub<BotStatusHub>("/hub/botstatus", options =>
        {
            options.CloseOnAuthenticationExpiration = true;
            options.ApplicationMaxBufferSize = 64 * 1024;
            options.TransportMaxBufferSize = 64 * 1024;
            options.TransportSendTimeout = TimeSpan.FromSeconds(10);
            options.LongPolling.PollTimeout = TimeSpan.FromSeconds(30);
        }).RequireAuthorization();

        return app;
    }

    private sealed class RegistrationMarker { }
}
