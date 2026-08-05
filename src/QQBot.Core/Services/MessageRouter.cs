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

    public async Task<bool> ShouldProcessGroupAsync(string groupOpenId, bool isAtBot, string content, CancellationToken ct = default)
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
                return true;
            }
            _logger.LogDebug("Group {GroupId} not configured, ignoring", groupOpenId);
            return false;
        }

        if (!config.IsEnabled)
        {
            _logger.LogDebug("Group {GroupId} not enabled, ignoring", groupOpenId);
            return false;
        }

        if (isAtBot) return true;

        if (config.TriggerMode == 2) return true;

        if (config.Keywords != null && config.Keywords.Count > 0)
        {
            var text = content ?? "";
            if (config.Keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
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

    public async Task<(bool ShouldProcess, string MatchedKeyword)> CheckProactiveTriggerAsync(string groupOpenId, string content, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var config = await configRepo.GetGroupConfigAsync(groupOpenId, ct);
        if (config == null || !config.ProactiveEnabled) return (false, "");

        if (string.IsNullOrWhiteSpace(config.ProactiveKeywords)) return (false, "");

        var text = content ?? "";
        var keywords = config.ProactiveKeywords
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var kw in keywords)
        {
            if (!string.IsNullOrEmpty(kw) && text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return (true, kw);
        }

        return (false, "");
    }
}
