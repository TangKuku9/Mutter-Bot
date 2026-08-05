using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;
using QQBot.Core.Interfaces;

namespace QQBot.Infrastructure.Services;

public class GameNotificationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GameNotificationHostedService> _logger;
    private string? _lastSentMonth;

    public GameNotificationHostedService(IServiceScopeFactory scopeFactory, ILogger<GameNotificationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("修仙月榜推送服务已启动");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrySendMonthlyLeaderboardAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "月榜推送执行异常");
            }

            // 每小时检查一次
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task TrySendMonthlyLeaderboardAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var currentMonth = now.ToString("yyyy-MM");
        
        // 已发送过本月，跳过
        if (_lastSentMonth == currentMonth) return;
        
        // 仅在每月1号发送（UTC）
        if (now.Day != 1) return;

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        var activeMessage = scope.ServiceProvider.GetRequiredService<IActiveMessageService>();

        var groupIds = await repo.GetActiveGroupIdsAsync(ct);
        _logger.LogInformation("开始月榜推送，共 {Count} 个群", groupIds.Count);

        var sentCount = 0;
        foreach (var groupId in groupIds)
        {
            try
            {
                var leaderboard = await repo.GetGroupLeaderboardAsync(groupId, 10, ct);
                if (leaderboard.Count == 0) continue;

                var msg = FormatMonthlyLeaderboard(leaderboard, now);
                var result = await activeMessage.SendGroupActiveAsync(groupId, msg, ct);
                
                if (result.Success)
                {
                    sentCount++;
                    _logger.LogInformation("月榜已推送至群 {GroupId}", groupId);
                }
                else
                {
                    _logger.LogWarning("月榜推送失败 group={GroupId} reason={Reason}", groupId, result.Reason);
                }

                // 每条间隔5秒，避免触发QQ频率限制
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "群 {GroupId} 月榜推送异常", groupId);
            }
        }

        _lastSentMonth = currentMonth;
        _logger.LogInformation("月榜推送完成，成功 {SentCount}/{TotalCount} 个群", sentCount, groupIds.Count);
    }

    private static string FormatMonthlyLeaderboard(List<LeaderboardEntry> entries, DateTime now)
    {
        var monthStr = now.AddHours(8).ToString("yyyy年MM月"); // 转北京时间
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"══ {monthStr} 修仙排行榜 ══");
        sb.AppendLine();
        
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var medal = i switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"{i + 1}." };
            sb.AppendLine($"{medal} {e.Nickname} — {Realm.NameOf(e.RealmIndex)}（修为 {e.CultivationPoints:N0}）");
        }
        
        sb.AppendLine();
        sb.Append("@我 帮助 查看修仙玩法");
        return sb.ToString();
    }
}
