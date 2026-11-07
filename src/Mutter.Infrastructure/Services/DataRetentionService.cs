using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models;
using Mutter.Infrastructure.Data;

namespace Mutter.Infrastructure.Services;

/// <summary>
/// 本地运行账本的保留策略：到期删除调用记录、主动尝试和终态发送回执。
/// pending 意图代表未确认的发送，仍需人工核查，不自动删除。
/// </summary>
public sealed class DataRetentionService(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<DataRetentionService> logger) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), clock, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), clock);
        while (true)
        {
            try { await CleanupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "运行账本清理失败"); }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>供定时任务与回归测试共用；只删除终态且超过保留期的记录。</summary>
    public async Task CleanupAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - Retention;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // 汇总与删除在同一事务内：重跑不会重复累计，中途失败也不会丢失已知用量。
        // 旧版无聊天历史的回复也需保留汇总；仍在 ChatMessages 中的旧回复继续由原查询统计。
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiUsageArchives"
                ("Id", "CallCount", "FailedCount", "UnknownUsageCount", "LegacyCount", "PromptTokens", "CompletionTokens")
            SELECT 1, COALESCE(SUM("Calls"), 0), COALESCE(SUM("Failed"), 0),
                COALESCE(SUM("UnknownUsage"), 0), COALESCE(SUM("Legacy"), 0),
                COALESCE(SUM("Input"), 0), COALESCE(SUM("Output"), 0)
            FROM (
                SELECT 1 AS "Calls", CASE WHEN "State" <> 'success' THEN 1 ELSE 0 END AS "Failed",
                    CASE WHEN "PromptTokens" IS NULL OR "CompletionTokens" IS NULL THEN 1 ELSE 0 END AS "UnknownUsage",
                    0 AS "Legacy", "PromptTokens" AS "Input", "CompletionTokens" AS "Output"
                FROM "AiCallRecords" WHERE "StartedAt" < {cutoff}
                UNION ALL
                SELECT 0, 0, 0, 1, r."PromptTokens", r."CompletionTokens"
                FROM "OutgoingReplies" r WHERE r."CreatedAt" < {cutoff} AND r."State" <> 'pending'
                    AND r."Model" IS NOT NULL AND r."AiOperationId" IS NULL
                    AND NOT EXISTS (SELECT 1 FROM "ChatMessages" m WHERE m."MessageId" = r."Id")
            ) WHERE 1 = 1
            ON CONFLICT ("Id") DO UPDATE SET
                "CallCount" = "CallCount" + excluded."CallCount",
                "FailedCount" = "FailedCount" + excluded."FailedCount",
                "UnknownUsageCount" = "UnknownUsageCount" + excluded."UnknownUsageCount",
                "LegacyCount" = "LegacyCount" + excluded."LegacyCount",
                "PromptTokens" = "PromptTokens" + excluded."PromptTokens",
                "CompletionTokens" = "CompletionTokens" + excluded."CompletionTokens"
            """, ct);
        var calls = await db.AiCallRecords.Where(r => r.StartedAt < cutoff).ExecuteDeleteAsync(ct);
        var attempts = await db.ActiveMessageAttempts.Where(a => a.AttemptedAt < cutoff).ExecuteDeleteAsync(ct);
        var replies = await db.OutgoingReplies
            .Where(r => r.State != DeliveryStates.Pending && r.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        if (calls + attempts + replies > 0)
            logger.LogInformation("运行账本清理完成 aiCalls={Calls} activeAttempts={Attempts} replies={Replies}",
                calls, attempts, replies);
    }
}
