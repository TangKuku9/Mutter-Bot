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
