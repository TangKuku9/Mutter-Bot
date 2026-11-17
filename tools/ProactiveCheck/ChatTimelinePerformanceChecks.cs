using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Mutter.Infrastructure.Data;
using Mutter.Web.Services;

internal static partial class RuntimeChecks
{
    private static async Task ChatTimelineHotGroupAsync()
    {
        Console.WriteLine($"      测试进程：{Environment.ProcessorCount} 个逻辑核；GC 可用内存 {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576} MiB");
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var original = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var probe = new TimelineQueryProbe();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(original.Database.GetConnectionString()).AddInterceptors(probe).Options);
        // 单个热群，包含不会在列表显示的原始消息和决策窗口大字段，避免只测空壳行。
        await db.Database.ExecuteSqlRawAsync("""
            PRAGMA cache_size = -8192;
            INSERT INTO "ChatMessages"
                ("MessageId","ChatType","GroupId","UserId","UserName","PlainText","RawSegmentsJson",
                 "HasImage","HasAtBot","IsBotMessage","IsProactive","CreatedAt")
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 100000)
            SELECT 'scale-'||i, 0, 'SCALE', 'user-'||(i%500), '用户'||(i%500), '热群消息'||i,
                   CAST(zeroblob(2048) AS TEXT), 0, i%10=1, i%10=0, i%20=0,
                   datetime('2026-10-01', '+'||(i/4)||' seconds') FROM n;
            INSERT INTO "OutgoingReplies"
                ("Id","GroupId","BotId","Content","IsProactive","PromptTokens","CompletionTokens",
                 "State","PlatformMessageId","CreatedAt")
            SELECT "MessageId", 'SCALE', 'BOT', '主动回复', 1, 10, 10, 'sent', 'receipt-'||"Id", "CreatedAt"
            FROM "ChatMessages" WHERE "IsProactive"=1;
            INSERT INTO "ProactiveChatLogs"
                ("GroupId","EvaluatedAt","MessageCount","DetailVersion","WindowSnapshotJson","ImageCount",
                 "MemoryCount","MemoryPromptChars","Outcome","ShouldSpeak","SentMessageId","SearchUsed",
                 "PromptTokens","CompletionTokens","SpeechPromptTokens","SpeechCompletionTokens")
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 20000)
            SELECT 'SCALE', datetime('2026-10-01', '+'||((i*5)/4)||' seconds'), 10, 2,
                   CAST(zeroblob(4096) AS TEXT), 0, 0, 0, 'skipped_cooldown', 0,
                   CASE WHEN i%4=0 THEN 'scale-'||(i*5)
                        WHEN i%4=1 THEN 'receipt-'||((i-1)*5) ELSE NULL END,
                   0, 0, 0, 0, 0 FROM n;
            """);

        var reader = new ChatTimelineReader(db);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        probe.Statements.Clear();
        var watch = Stopwatch.StartNew();
        var first = await reader.ReadLatestAsync("SCALE", null, 50, cts.Token);
        var firstMs = watch.Elapsed.TotalMilliseconds;
        var statements = probe.Statements.ToArray();
        Require(first.Items.Count == 50 && first.TotalCount is null && first.HasMore,
            "a single 100k-message group opens with just one page and no blocking count");
        Require(firstMs < 1000, $"a 100k-message first page must finish within 1 s (took {firstMs:F0} ms)");
        Require(statements.All(s => !s.Sql.Contains("COUNT(", StringComparison.Ordinal)
            && !s.Sql.Contains("RawSegmentsJson", StringComparison.Ordinal)
            && !s.Sql.Contains("WindowSnapshotJson", StringComparison.Ordinal)),
            "the first page neither counts history nor reads raw messages and decision windows");
        Console.WriteLine($"      单群 10 万消息 + 2 万大窗口留痕：首次 {firstMs:F0} ms");
        await RequireIndexOrderedAsync(db, statements);

        var exact = await reader.ReadAsync("SCALE", null, null, 1, 50, first.Snapshot, cts.Token);
        Require(first.Items.Select(i => $"{i.Kind}:{i.Id}").SequenceEqual(exact.Items.Select(i => $"{i.Kind}:{i.Id}")),
            "bounded candidates preserve tick, kind, ID ordering and exact sent-decision deduplication");
        var samples = new List<double>();
        foreach (var group in new[] { "SCALE", null })
        foreach (var source in ChatTimelineReader.Sources)
        {
            var reference = await reader.ReadAsync(group, source, null, 1, 50, first.Snapshot, cts.Token);
            Require((await reader.CountAsync(group, source, null, first.Snapshot, cts.Token)).TotalCount == reference.TotalCount,
                "the background count and exact pagination retain the same source and snapshot semantics");
            for (var i = 0; i < 4; i++)
            {
                probe.Statements.Clear();
                watch.Restart();
                var page = await reader.ReadLatestAsync(group, source, 50, cts.Token);
                samples.Add(watch.Elapsed.TotalMilliseconds);
                if (i == 0)
                    await RequireIndexOrderedAsync(db, probe.Statements.ToArray());
                Require(page.Items.Select(item => $"{item.Kind}:{item.Id}")
                    .SequenceEqual(reference.Items.Select(item => $"{item.Kind}:{item.Id}")),
                    $"100k-message group '{group}' source '{source}' agrees with the exact endpoint");
            }
        }
        samples.Sort();
        var p50 = samples[(int)Math.Ceiling(samples.Count * .50) - 1];
        var p95 = samples[(int)Math.Ceiling(samples.Count * .95) - 1];
        Require(p95 < 250 && samples[^1] < 1000,
            $"100k-message source switching must stay responsive (p95 {p95:F0} ms, max {samples[^1]:F0} ms)");
        Console.WriteLine($"      单群/全部 × 各来源 {samples.Count} 次打开：p50 {p50:F0} ms；p95 {p95:F0} ms；max {samples[^1]:F0} ms；首屏查询均无临时排序");

        probe.Statements.Clear();
        watch.Restart();
        var last = await reader.ReadAsync("SCALE", null, null, int.MaxValue, 50, first.Snapshot, cts.Token);
        var lastMs = watch.Elapsed.TotalMilliseconds;
        var finalPage = (int)Math.Ceiling(exact.TotalCount!.Value / 50d);
        Require(last.Page == finalPage && last.TotalCount == exact.TotalCount && !last.HasMore
            && last.Items.Count == exact.TotalCount - (finalPage - 1) * 50 && last.Items[^1].Id == 1
            && last.Items[^1].Kind == "message" && lastMs < 3000,
            $"a deep page clamps safely and preserves the oldest row (took {lastMs:F0} ms)");
        await RequireIndexOrderedAsync(db, probe.Statements.ToArray());
        Console.WriteLine($"      最后一页（第 {last.Page} 页，含精确总数）：{lastMs:F0} ms");

        // 所有旧决策都被消息吸收时，不能为了凑足 51 个事件而遍历整段留痕。
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "ProactiveChatLogs" SET "SentMessageId" = 'scale-'||(20*("Id"%5000+1));
            """);
        probe.Statements.Clear();
        watch.Restart();
        var sentOnly = await reader.ReadLatestAsync("SCALE", null, 50, cts.Token);
        var sentOnlyMs = watch.Elapsed.TotalMilliseconds;
        Require(sentOnly.Items.Count == 50 && sentOnly.Items.All(i => i.Kind == "message") && sentOnlyMs < 1000,
            $"a fully linked decision history must not force a full-history scan (took {sentOnlyMs:F0} ms)");
        await RequireIndexOrderedAsync(db, probe.Statements.ToArray());
        Console.WriteLine($"      2 万条留痕全部已关联：首屏 {sentOnlyMs:F0} ms");
        Console.WriteLine($"      测试进程峰值工作集：{Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MiB");
    }

    private static async Task RequireIndexOrderedAsync(AppDbContext db, TimelineStatement[] statements)
    {
        foreach (var statement in statements.Where(s => s.Sql.Contains("ORDER BY", StringComparison.Ordinal)))
        {
            var plan = await ExplainAsync(db, statement);
            Require(plan.All(step => !step.Contains("TEMP B-TREE", StringComparison.Ordinal)),
                "first-page candidates must use index order without a history-sized temporary sort: " + string.Join("; ", plan));
        }
    }

    private static async Task<List<string>> ExplainAsync(AppDbContext db, TimelineStatement statement)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + statement.Sql;
        foreach (var (name, value) in statement.Parameters)
            command.Parameters.Add(new SqliteParameter(name, value));
        var details = new List<string>();
        await using var result = await command.ExecuteReaderAsync();
        while (await result.ReadAsync()) details.Add(result.GetString(3));
        return details;
    }

    private sealed record TimelineStatement(string Sql, (string Name, object Value)[] Parameters);

    private sealed class TimelineQueryProbe : DbCommandInterceptor
    {
        public List<TimelineStatement> Statements { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Statements.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>()
                .Select(p => (p.ParameterName, p.Value ?? DBNull.Value)).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
