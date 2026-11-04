using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.EventHandlers;
using Mutter.Core.Interfaces;
using Mutter.Core.Proactive;
using Mutter.Core.Services;

namespace Mutter.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IMessageParser, MessageParser>();
        services.AddSingleton<IAiChatService, AiChatService>();
        services.AddSingleton(TimeProvider.System);
        // 图片下载独立客户端：单张 10s 硬超时在解析器内部，这里只兜底；不占 AI 调用槽
        services.AddHttpClient("image-resolver")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<IAiImageResolver, AiImageResolver>();
        services.AddSingleton<AiSearchCapabilityStore>();
        // 联网搜索探测：独立短超时客户端，与正式 AI 调用互不干扰
        services.AddHttpClient<WebSearchProbeRunner>()
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(25));
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
