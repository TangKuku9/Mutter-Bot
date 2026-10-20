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
        // 显式超时，别用默认的 100s 让上游抖动长期占满 AI 并发槽（见 MessageEventHandler._aiSemaphore）。
        // 45s 撑不住带图的调用：实测同一模型纯文本 ~10s，附一张图 10~37s，两次带图评估就是在 45s 上
        // 超时被打成 skipped_ai_unavailable 的（见 2026-10-06 11:36 日志）。放到 120s，兼顾读图与快速失败。
        services.AddHttpClient<IAiChatService, AiChatService>()
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(120));
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
        services.AddSingleton<GroupMessageGate>();
        services.AddSingleton<BotCommandService>();
        services.AddSingleton<MessageEventHandler>();

        // 富交互能力门控（markdown / 按钮 / @标签 的配置与降级态）。
        // 它不属于修仙域：主动消息与网关的降级判断都依赖它。
        services.AddSingleton<RichUiCapability>();

        return services;
    }
}
