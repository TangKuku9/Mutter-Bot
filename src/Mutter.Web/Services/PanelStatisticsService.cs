using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Web.Models;

namespace Mutter.Web.Services;

/// <summary>缓存只保存聚合结果；同一缓存未命中时只执行一轮查询。</summary>
public sealed class PanelStatisticsService(IServiceScopeFactory scopes, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, (DateTimeOffset At, AiUsageReport Value)> _usage = new();
    private (DateTimeOffset At, ChatStatsDto Value)? _overview;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    public async Task<AiUsageReport> UsageAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 90);
        await _gate.WaitAsync(ct);
        try
        {
            if (_usage.TryGetValue(days, out var cached) && clock.GetUtcNow() - cached.At < Ttl) return cached.Value;
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var value = new AiUsageReport(await repo.GetAiUsageSummaryAsync(days, ct),
                await repo.GetDailyTokenStatsAsync(days, ct), await repo.GetGroupTokenStatsAsync(days, 50, ct),
                await repo.GetUserTokenStatsAsync(days, 50, ct));
            _usage[days] = (clock.GetUtcNow(), value);
            return value;
        }
        finally { _gate.Release(); }
    }

    public async Task<ChatStatsDto> OverviewAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_overview is { } cached && clock.GetUtcNow() - cached.At < Ttl) return cached.Value;
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var usage = await repo.GetAiUsageSummaryAsync(ct: ct);
            var today = clock.GetUtcNow().UtcDateTime.AddHours(8).Date.AddHours(-8);
            var value = new ChatStatsDto
            {
                TotalMessages = await repo.CountMessagesAsync(ct: ct), TodayMessages = await repo.CountMessagesAsync(today, ct),
                AiCallCount = usage.CallCount, TotalTokensUsed = usage.PromptTokens + usage.CompletionTokens,
                LegacyReplyCount = usage.LegacyCount, UnknownUsageCount = usage.UnknownUsageCount
            };
            _overview = (clock.GetUtcNow(), value);
            return value;
        }
        finally { _gate.Release(); }
    }
}
