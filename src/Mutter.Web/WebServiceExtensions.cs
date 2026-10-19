using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Interfaces;
using Mutter.Web.Hubs;
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

        // 自更新：检查最新正式版、下载校验并替换程序文件。
        // 更新包下载不设总超时（慢链路可能要几十分钟），只靠连接/读取停滞超时与自动重试兜底。
        services.AddHttpClient("update-download", client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mutter-Bot-Update");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
                AutomaticDecompression = DecompressionMethods.None
            });
        services.AddHttpClient();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<UpdateApplier>();

        return services;
    }

    public static WebApplication ConfigureMutterWeb(this WebApplicationBuilder builder)
    {
        builder.Services.AddMutterWeb();

        var app = builder.Build();

        app.UseCors();
        app.UseStaticFiles();
        app.MapControllers();
        app.MapHub<BotStatusHub>("/hub/botstatus");

        return app;
    }
}
