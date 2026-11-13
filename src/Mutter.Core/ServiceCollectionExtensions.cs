using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using Mutter.Core.EventHandlers;
using Mutter.Core.Interfaces;
using Mutter.Core.Proactive;
using Mutter.Core.Services;
using Mutter.Core.Services.WebSearch;

namespace Mutter.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IMessageParser, MessageParser>();
        services.AddSingleton<IAiChatService, AiChatService>();
        services.AddSingleton(TimeProvider.System);
        // 出站建连统一做地址校验：目标域名解析到链路本地/云元数据时直接拒绝（防 DNS rebinding）。
        static IHttpClientBuilder Guarded(IHttpClientBuilder builder, bool allowRedirect = true) =>
            builder.ConfigurePrimaryHttpMessageHandler(() => OutboundAddressPolicy.CreateHandler(allowRedirect));
        // 图片下载独立客户端：单张 10s 硬超时在解析器内部，这里只兜底；不占 AI 调用槽
        Guarded(services.AddHttpClient("image-resolver"))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<IAiImageResolver, AiImageResolver>();
        services.AddSingleton<AiSearchCapabilityStore>();
        // 联网搜索探测：独立短超时客户端，与正式 AI 调用互不干扰；探测带密钥，不跟随重定向
        Guarded(services.AddHttpClient<WebSearchProbeRunner>(), allowRedirect: false)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(25));
        // 独立搜索服务（预检索注入）：typed client 独立成池，超时钳制在 Provider 基座内按配置生效。
        // AddHttpClient<T> 只注册具体类型，这里再补 IWebSearchProvider 转发，
        // ExternalSearchService 才能通过 IEnumerable<IWebSearchProvider> 收齐全部 Provider。
        Guarded(services.AddHttpClient<SearxngProvider>());
        Guarded(services.AddHttpClient<TavilyProvider>());
        Guarded(services.AddHttpClient<BochaProvider>());
        Guarded(services.AddHttpClient<BraveProvider>());
        Guarded(services.AddHttpClient<DeepseekProvider>());
        Guarded(services.AddHttpClient<CustomProvider>());
        services.AddTransient<IWebSearchProvider>(sp => sp.GetRequiredService<SearxngProvider>());
        services.AddTransient<IWebSearchProvider>(sp => sp.GetRequiredService<TavilyProvider>());
        services.AddTransient<IWebSearchProvider>(sp => sp.GetRequiredService<BochaProvider>());
        services.AddTransient<IWebSearchProvider>(sp => sp.GetRequiredService<BraveProvider>());
        services.AddTransient<IWebSearchProvider>(sp => sp.GetRequiredService<DeepseekProvider>());
        services.AddTransient<IWebSearchProvider>(sp => sp.GetRequiredService<CustomProvider>());
        services.AddSingleton<ExternalSearchService>();
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        services.AddSingleton<MessageRouter>();
        // 择机主动发言：观察窗口 + msg_seq 共享计数 + 发言出口
        services.AddSingleton<ProactiveObserveService>();
        services.AddSingleton<ProactiveSpeaker>();
        services.AddSingleton<ReplySeqTracker>();
        services.AddSingleton<ReplyDispatcher>();
        services.AddSingleton<GroupMessageGate>();
        services.AddSingleton<BotCommandService>();
        services.AddSingleton<MessageEventHandler>();

        // 富交互能力门控（markdown / 按钮 / @标签 的配置与降级态）。
        // 它不属于修仙域：主动消息与网关的降级判断都依赖它。
        services.AddSingleton<RichUiCapability>();

        return services;
    }
}
