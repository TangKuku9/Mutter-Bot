using Microsoft.EntityFrameworkCore;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Infrastructure.Data;

namespace Mutter.Infrastructure.Repositories;

/// <summary>所有聚合都在 SQLite 执行；旧版只有回复级用量，单独标注，不推测用户归属。</summary>
public sealed class AiUsageQueries(AppDbContext db)
{
    public sealed class UsageRow
    {
        public DateTime At { get; set; }
        public string? GroupId { get; set; }
        public string? RequesterId { get; set; }
        public string? RequesterName { get; set; }
        public long? PromptTokens { get; set; }
        public long? CompletionTokens { get; set; }
        public string State { get; set; } = "success";
        public int Legacy { get; set; }
    }

    private IQueryable<UsageRow> Rows(DateTime? since = null)
    {
        var query = db.Database.SqlQueryRaw<UsageRow>("""
            SELECT "StartedAt" AS "At", "GroupId", "RequesterId", "RequesterName",
                "PromptTokens", "CompletionTokens", "State", 0 AS "Legacy" FROM "AiCallRecords"
            UNION ALL
            SELECT "CreatedAt" AS "At", "GroupId", NULL AS "RequesterId", NULL AS "RequesterName",
                "PromptTokens", "CompletionTokens", 'success' AS "State", 1 AS "Legacy"
                FROM "ChatMessages" WHERE "AiModelUsed" IS NOT NULL AND "AiOperationId" IS NULL
            UNION ALL
            SELECT r."CreatedAt" AS "At", r."GroupId", NULL AS "RequesterId", NULL AS "RequesterName",
                r."PromptTokens", r."CompletionTokens", 'success' AS "State", 1 AS "Legacy"
                FROM "OutgoingReplies" r WHERE r."Model" IS NOT NULL AND r."AiOperationId" IS NULL
                AND NOT EXISTS (SELECT 1 FROM "ChatMessages" m WHERE m."MessageId" = r."Id")
            """);
        return since is null ? query : query.Where(r => r.At >= since);
    }

    public async Task<AiUsageSummary> SummaryAsync(DateTime? since, CancellationToken ct)
    {
        return await Rows(since).GroupBy(_ => 1).Select(g => new AiUsageSummary
        {
            CallCount = g.LongCount(r => r.Legacy == 0),
            FailedCount = g.LongCount(r => r.Legacy == 0 && r.State != "success"),
            UnknownUsageCount = g.LongCount(r => r.Legacy == 0 && (r.PromptTokens == null || r.CompletionTokens == null)),
            LegacyCount = g.LongCount(r => r.Legacy != 0),
            PromptTokens = g.Sum(r => r.PromptTokens ?? 0),
            CompletionTokens = g.Sum(r => r.CompletionTokens ?? 0)
        }).SingleOrDefaultAsync(ct) ?? new();
    }

    public async Task<List<DailyTokenStats>> DailyAsync(DateTime since, CancellationToken ct)
    {
        var rows = await Rows(since).GroupBy(r => r.At.AddHours(8).Date).Select(g => new
        {
            Date = g.Key, Prompt = g.Sum(r => r.PromptTokens ?? 0), Output = g.Sum(r => r.CompletionTokens ?? 0),
            Calls = g.LongCount(r => r.Legacy == 0), Legacy = g.LongCount(r => r.Legacy != 0)
        }).OrderBy(r => r.Date).ToListAsync(ct);
        return rows.Select(r => new DailyTokenStats
        {
            Date = r.Date.ToString("yyyy-MM-dd"), PromptTokens = r.Prompt, CompletionTokens = r.Output,
            CallCount = r.Calls, LegacyCount = r.Legacy
        }).ToList();
    }

    public async Task<List<GroupTokenStats>> GroupsAsync(DateTime since, int top, CancellationToken ct)
    {
        var totals = Rows(since).Where(r => r.GroupId != null).GroupBy(r => r.GroupId).Select(g => new
        {
            GroupId = g.Key, Tokens = g.Sum(r => (r.PromptTokens ?? 0) + (r.CompletionTokens ?? 0)),
            Calls = g.LongCount(r => r.Legacy == 0), Legacy = g.LongCount(r => r.Legacy != 0)
        });
        return await (from row in totals
            join name in db.KnownGroups on row.GroupId equals name.GroupOpenId into names
            from name in names.DefaultIfEmpty()
            orderby row.Tokens descending, row.GroupId
            select new GroupTokenStats
            {
                GroupId = row.GroupId!, GroupName = name == null ? "" : name.Name ?? "",
                TotalTokens = row.Tokens, CallCount = row.Calls, LegacyCount = row.Legacy
            }).Take(top).ToListAsync(ct);
    }

    public async Task<List<UserTokenStats>> UsersAsync(DateTime since, int top, CancellationToken ct) =>
        await Rows(since).Where(r => r.Legacy == 0 && r.RequesterId != null)
            .GroupBy(r => r.RequesterId).Select(g => new UserTokenStats
            {
                UserId = g.Key!, UserName = g.Max(r => r.RequesterName) ?? "",
                TotalTokens = g.Sum(r => (r.PromptTokens ?? 0) + (r.CompletionTokens ?? 0)), CallCount = g.LongCount()
            }).OrderByDescending(r => r.TotalTokens).ThenBy(r => r.UserId).Take(top).ToListAsync(ct);
}
