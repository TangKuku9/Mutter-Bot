using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;
using Mutter.Infrastructure.Ai;

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
        services.AddHttpClient<HttpModelTransport>(c => c.Timeout = TimeSpan.FromSeconds(120));
        services.AddSingleton<IAiModelExecutor, AiModelExecutor>();
        services.AddSingleton<IOutgoingReplyStore, OutgoingReplyStore>();
        services.AddSingleton<IAiCallStore, AiCallStore>();
        services.AddSingleton<ConversationWorkQueue>();
        services.AddSingleton<IConversationWorkQueue>(sp => sp.GetRequiredService<ConversationWorkQueue>());

        // QQ 官方开放平台通信层
        services.AddSingleton<QqGatewayWebSocketClient>();
        services.AddSingleton<IQqGatewayConnection>(sp => sp.GetRequiredService<QqGatewayWebSocketClient>());
        services.AddHttpClient<QqBotApiService>();
        services.AddSingleton<IQqBotApiService>(sp => sp.GetRequiredService<QqBotApiService>());
        services.AddSingleton<IActiveMessageService, ActiveMessageService>();

        services.AddHostedService(sp => sp.GetRequiredService<ConversationWorkQueue>());
        services.AddSingleton<ProactiveWorkflow>();
        // 择机主动发言评估循环（全局开关关闭时只做空转，不调 AI）
        services.AddHostedService<ProactiveChatHostedService>();

        // Mutter 官方开放平台 access_token
        services.AddOptions<MutterOptions>().Configure<IConfiguration>((o, cfg) => cfg.GetSection("Mutter").Bind(o));
        services.AddHttpClient<AccessTokenManager>();
        services.AddSingleton<AccessTokenManager>();
        services.AddHostedService<GatewayHostedService>();

        return services;
    }
}
