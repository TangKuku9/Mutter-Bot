using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Services;
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
        services.AddScoped<AutomaticMemoryRepository>();
        services.AddScoped<IAutomaticMemoryRecall>(sp => sp.GetRequiredService<AutomaticMemoryRepository>());
        services.AddSingleton<GroupMemoryLearningService>();
        services.AddHostedService(sp => sp.GetRequiredService<GroupMemoryLearningService>());
        services.AddSingleton<IStructuredAiService, StructuredAiService>();
        // HttpClient.Timeout 只是兜底；真正的预算在每次尝试内（min(尝试上限, 根期限剩余)）。
        // 不跟随重定向：上游 302 会把带密钥的 POST 改写成发往任意地址的 GET（SSRF 放大面）。
        // ConnectCallback 在建连时校验解析结果，防止 DNS rebinding 把请求引向云元数据地址。
        services.AddHttpClient<HttpModelTransport>()
            .ConfigureHttpClient((sp, c) =>
            {
                c.Timeout = TimeSpan.FromSeconds(
                    sp.GetRequiredService<IOptions<MutterOptions>>().Value.Runtime.AiTimeoutSeconds + 5);
                c.MaxResponseContentBufferSize = 4 * 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var settings = sp.GetService<OutboundSettingsStore>();
                return OutboundAddressPolicy.CreateHandler(
                    allowRedirect: false, allowSystemProxy: () => settings?.AllowSystemProxy == true);
            });
        services.AddSingleton<IAiModelExecutor, AiModelExecutor>();
        services.AddSingleton<IOutgoingReplyStore, OutgoingReplyStore>();
        services.AddSingleton<IAiCallStore, AiCallStore>();
        services.AddSingleton<ConversationWorkQueue>(sp => new ConversationWorkQueue(
            sp.GetRequiredService<ILogger<ConversationWorkQueue>>(), sp.GetRequiredService<TimeProvider>(),
            runtime: sp.GetRequiredService<RuntimeOptions>(),
            lifetime: sp.GetService<IHostApplicationLifetime>()));
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
        // 运行账本保留策略：终态记录超过 90 天后清理，pending 意图保留供核查
        services.AddHostedService<DataRetentionService>();

        // Mutter 官方开放平台 access_token
        services.AddOptions<MutterOptions>().Configure<IConfiguration>((o, cfg) =>
        {
            cfg.GetSection("Mutter").Bind(o);
            o.Runtime.Normalize();
            AiCallScope.Configure(TimeSpan.FromSeconds(o.Runtime.AiTimeoutSeconds));
        });
        // 运行时参数以普通单例下发，Core 侧服务（如 MessageEventHandler）不必依赖 IOptions。
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MutterOptions>>().Value.Runtime);
        // 上游健康追踪：失败驱动的降级信号源，队列、择机调度与模型传输都读它
        services.AddSingleton<UpstreamHealthTracker>();
        services.AddHttpClient<AccessTokenManager>();
        services.AddSingleton<AccessTokenManager>();
        services.AddHostedService<GatewayHostedService>();

        return services;
    }
}
