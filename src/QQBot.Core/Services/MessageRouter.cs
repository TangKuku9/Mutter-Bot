using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Interfaces;

namespace QQBot.Core.Services;

public class MessageRouter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessageRouter> _logger;

    public MessageRouter(IServiceScopeFactory scopeFactory, ILogger<MessageRouter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// 群消息路由：一次读配置同时得出「是否走回复链路」「是否进择机发言观察窗口」「这条有没有 @ 到机器人」。
    /// 全量消息模式下每条消息都要过这里，合并查询避免读两遍库。
    /// </summary>
    /// <param name="content">
    /// 消息原文。全量模式下平台不再单独下发 @ 事件，事件类型里的"@ 了 bot"丢了，
    /// 只能从 content 里的 &lt;@识别码&gt; 判回来（识别码在面板按群配置）。
    /// </param>
    public async Task<GroupRoute> RouteGroupAsync(
        string groupOpenId, bool isAtBot, string? content = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var config = await configRepo.GetGroupConfigAsync(groupOpenId, ct);

        if (config == null)
        {
            // 群无配置（全新数据库或自动配置功能上线前加入的群）
            if (isAtBot)
            {
                _logger.LogInformation("群 {GroupId} 无配置，@bot 默认处理", groupOpenId);
                return new GroupRoute(true, false, null);
            }
            _logger.LogDebug("Group {GroupId} not configured, ignoring", groupOpenId);
            return new GroupRoute(false, false, null);
        }

        if (!config.IsEnabled)
        {
            _logger.LogDebug("Group {GroupId} not enabled, ignoring", groupOpenId);
            return new GroupRoute(false, false, null);
        }

        // 识别码配了才认：没配时宁可少回话，也不能把 @ 群友的消息当成 @ 机器人
        var mentionCode = !isAtBot && BotMention.IsMentioned(content, config.BotMentionOpenId)
            ? config.BotMentionOpenId
            : null;

        if (isAtBot || mentionCode is not null) return new GroupRoute(true, false, mentionCode);

        // 非 @ 消息只有两种归宿：围观档落库，以及被择机窗口采集。要不要说话由 AI 判时机，
        // 不再有“命中关键词就接话”这种无判断的旁路（它曾与择机共用同一被动槽位造成二次回复）。
        var global = await configRepo.GetProactiveConfigAsync(ct);
        var shouldObserve = global.Enabled && config.AiProactiveEnabled;

        return new GroupRoute(config.TriggerMode == 2, shouldObserve, null);
    }

    public async Task<bool> ShouldProcessPrivateAsync(string userOpenId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var enabled = await configRepo.IsPrivateChatEnabledAsync(userOpenId, ct);
        if (!enabled)
        {
            _logger.LogDebug("Private chat user {UserId} not enabled, ignoring", userOpenId);
        }
        return enabled;
    }

    /// <param name="ShouldProcess">走既有回复链路：消息落库，被 @ 时才回。</param>
    /// <param name="ShouldObserve">是否送进择机发言的观察窗口。</param>
    /// <param name="BotMentionCode">非空 = 这条按识别码判定为 @ 了机器人，值是那串码，用于把 at 段从喂模型的文本里剥掉。</param>
    public readonly record struct GroupRoute(bool ShouldProcess, bool ShouldObserve, string? BotMentionCode);
}
