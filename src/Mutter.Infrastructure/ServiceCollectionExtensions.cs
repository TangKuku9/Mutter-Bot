using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;

namespace Mutter.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IConfigRepository, ConfigRepository>();
        services.AddScoped<IGroupMemoryRepository, GroupMemoryRepository>();
        services.AddSingleton<IStructuredAiService, StructuredAiService>();

        // QQ 官方开放平台通信层
        services.AddSingleton<QqGatewayWebSocketClient>();
        services.AddSingleton<IQqGatewayConnection>(sp => sp.GetRequiredService<QqGatewayWebSocketClient>());
        services.AddHttpClient<QqBotApiService>();
        services.AddSingleton<IQqBotApiService>(sp => sp.GetRequiredService<QqBotApiService>());
        services.AddSingleton<IActiveMessageService, ActiveMessageService>();

        // Channel 异步消息缓冲
        var channel = Channel.CreateBounded<ChatMessage>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        services.AddSingleton(channel.Reader);
        services.AddSingleton(channel.Writer);

        // 后台消息写入服务
        services.AddHostedService<MessageWriterService>();
        // 择机主动发言评估循环（全局开关关闭时只做空转，不调 AI）
        services.AddHostedService<ProactiveChatHostedService>();

        // Mutter 官方开放平台 access_token
        services.AddOptions<MutterOptions>().Configure<IConfiguration>((o, cfg) => cfg.GetSection("Mutter").Bind(o));
        services.AddHttpClient<AccessTokenManager>();
        services.AddSingleton<AccessTokenManager>();

        return services;
    }
}
