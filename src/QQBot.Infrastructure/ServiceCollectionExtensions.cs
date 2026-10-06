using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QQBot.Core.Configuration;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Infrastructure.Data;
using QQBot.Infrastructure.Repositories;
using QQBot.Infrastructure.Services;

namespace QQBot.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IConfigRepository, ConfigRepository>();
        services.AddScoped<ICultivationRepository, CultivationRepository>();
        services.AddSingleton<IStructuredAiService, StructuredAiService>();

        // QQ 官方开放平台通信层
        services.AddSingleton<QqGatewayWebSocketClient>();
        services.AddSingleton<IQqGatewayConnection>(sp => sp.GetRequiredService<QqGatewayWebSocketClient>());
        services.AddHttpClient<QqBotApiService>();
        services.AddSingleton<IQqBotApiService>(sp => sp.GetRequiredService<QqBotApiService>());
        services.AddSingleton<IActiveMessageService, ActiveMessageService>();
        // 修仙消息延迟撤回调度器（单例注册同时作为 HostedService 启动，避免双实例）
        services.AddSingleton<IGameRecallService, GameRecallService>();
        services.AddSingleton<IHostedService>(sp => (GameRecallService)sp.GetRequiredService<IGameRecallService>());

        // Channel 异步消息缓冲
        var channel = Channel.CreateBounded<ChatMessage>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        services.AddSingleton(channel.Reader);
        services.AddSingleton(channel.Writer);

        // 后台消息写入服务
        services.AddHostedService<MessageWriterService>();
        // 修仙月榜主动推送服务（每月1号自动发送排行榜，受主动消息频控约束）
        services.AddHostedService<GameNotificationHostedService>();
        // 择机主动发言评估循环（全局开关关闭时只做空转，不调 AI）
        services.AddHostedService<ProactiveChatHostedService>();

        // QQBot 官方开放平台 access_token
        services.AddOptions<QQBotOptions>().Configure<IConfiguration>((o, cfg) => cfg.GetSection("QQBot").Bind(o));
        services.AddHttpClient<AccessTokenManager>();
        services.AddSingleton<AccessTokenManager>();

        return services;
    }
}
