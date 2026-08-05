using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using QQBot.Core.Interfaces;
using QQBot.Web.Hubs;
using QQBot.Web.Services;

namespace QQBot.Web;

public static class WebServiceExtensions
{
    public static IServiceCollection AddQQBotWeb(this IServiceCollection services)
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

        return services;
    }

    public static WebApplication ConfigureQQBotWeb(this WebApplicationBuilder builder)
    {
        builder.Services.AddQQBotWeb();

        var app = builder.Build();

        app.UseCors();
        app.UseStaticFiles();
        app.MapControllers();
        app.MapHub<BotStatusHub>("/hub/botstatus");

        return app;
    }
}
