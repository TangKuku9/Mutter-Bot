using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;
using QQBot.Core.Game.Services;
using QQBot.Core.Game.Ui;
using QQBot.Core.Interfaces;
using QQBot.Core.Models.QQOfficial;

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
        var cap = scope.ServiceProvider.GetRequiredService<RichUiCapability>();

        var groupIds = await repo.GetActiveGroupIdsAsync(ct);
        var recall = scope.ServiceProvider.GetRequiredService<IGameRecallService>();
        _logger.LogInformation("开始月榜推送，共 {Count} 个群", groupIds.Count);

        var sentCount = 0;
        foreach (var groupId in groupIds)
        {
            try
            {
                // 持久化去重：本月已推送过的群跳过（防止服务重启后重复推送，白占主动消息频控额度）
                var world = await repo.GetOrCreateGroupWorldAsync(groupId, ct);
                if (world.LeaderboardSnapshotAt.Year == now.Year && world.LeaderboardSnapshotAt.Month == now.Month)
                    continue;

                var leaderboard = await repo.GetGroupLeaderboardAsync(groupId, 10, ct);
                if (leaderboard.Count == 0) continue;

                var msg = BuildMonthlyMessage(leaderboard, now, cap, groupId);
                var result = await activeMessage.SendGroupActiveRichAsync(groupId, msg, ct);
                
                if (result.Success)
                {
                    sentCount++;
                    _logger.LogInformation("月榜已推送至群 {GroupId}", groupId);
                    // 月榜同样属于修仙消息，延迟后自动撤回避免刷屏
                    recall.Schedule(groupId, result.MessageId);
                    // 仅在发送成功后记录快照（失败群当日下轮重试）
                    world.LeaderboardSnapshotAt = now;
                    await repo.SaveGroupWorldAsync(world, ct);
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

    /// <summary>
    /// 月榜文本。纯文本与历史一致（同时作为 markdown 被拒时的兜底）；
    /// 开了富交互时额外给出 markdown + 主菜单按钮（主动配额只计 1 条）。
    /// </summary>
    private static OutboundMessage BuildMonthlyMessage(
        List<LeaderboardEntry> entries, DateTime now, RichUiCapability cap, string groupId)
    {
        var plain = FormatMonthlyLeaderboard(entries, now, cap.UseAtTags(groupId));

        if (!cap.UseMarkdown(groupId))
            return OutboundMessage.Text(plain);

        var markdown = MarkdownText.FromPlainText(plain);
        var menu = GameMenuCatalog.For(GameAction.Help, MenuContext.Empty);
        var keyboard = GameKeyboard.From(menu, cap.EffectiveKeyboardMode(groupId), cap.Options);
        if (keyboard is null && cap.UseCmdInputLinks(groupId))
        {
            var links = GameKeyboard.RenderInputLinks(menu);
            if (links.Length > 0) markdown += "\n" + links;
        }

        return new OutboundMessage
        {
            PlainText = plain,
            MarkdownText = markdown,
            Keyboard = keyboard,
        };
    }

    private static string FormatMonthlyLeaderboard(List<LeaderboardEntry> entries, DateTime now, bool atTags = false)
    {
        var monthStr = now.AddHours(8).ToString("yyyy年MM月"); // 转北京时间
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("════ 修仙月榜 ════");
        sb.AppendLine($"　 {monthStr}");

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var medal = i switch { 0 => "①", 1 => "②", 2 => "③", _ => $"{i + 1}." };
            var who = GameKeyboard.AtUser(e.MemberOpenId,
                GameTextFormatter.DisplayName(e.Nickname, e.MemberOpenId), atTags);
            sb.AppendLine($"{medal} {who}　{Realm.NameOf(e.RealmIndex)}");
            sb.AppendLine($"　 修为 {e.CultivationPoints:N0}");
        }

        sb.AppendLine();
        sb.Append("@我 帮助 查看修仙玩法");
        return sb.ToString();
    }
}
