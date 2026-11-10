using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.QQOfficial;
using Mutter.Core.Proactive;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;
using Mutter.Web.Controllers;

internal static partial class RuntimeChecks
{
    private static async Task ReviewBoundariesAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var now = DateTime.UtcNow;
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ProactiveChatLogs.AddRange(
            Log(false, ProactiveOutcomes.FailedAiCall),
            Log(false, ProactiveOutcomes.SkippedDisabled, "stale-model"),
            Log(true, ProactiveOutcomes.SkippedDisabled),
            Log(true, ProactiveOutcomes.SkippedQuietHours),
            Log(null, ProactiveOutcomes.SkippedCooldown, "legacy-model"),
            Log(null, ProactiveOutcomes.SkippedDisabled),
            Log(null, ProactiveOutcomes.SkippedDisabled, ""),
            Log(null, ProactiveOutcomes.SkippedAiDeclined));
        await db.SaveChangesAsync();
        Require(await ReadTodayDecisionsAsync(scope.ServiceProvider) == 4,
            "explicit phases take precedence over final outcomes; old records retain model evidence and compatible counting");

        // Null/空字符串都不是引用；真实 ID 的引用和 pending 意图仍要保留。
        db.ProactiveChatLogs.AddRange(
            new ProactiveChatLogEntity { GroupId = "GROUP", EvaluatedAt = now, SentMessageId = null },
            new ProactiveChatLogEntity { GroupId = "GROUP", EvaluatedAt = now, SentMessageId = "" },
            new ProactiveChatLogEntity { GroupId = "GROUP", EvaluatedAt = now, SentMessageId = "real-receipt" });
        db.OutgoingReplies.AddRange(
            Receipt("null-receipt", null, DeliveryStates.Failed),
            Receipt("empty-receipt", "", DeliveryStates.Unknown),
            Receipt("referenced-receipt", "real-receipt", DeliveryStates.Sent),
            new OutgoingReply { Id = "pending-receipt", GroupId = "GROUP", State = DeliveryStates.Pending, CreatedAt = now.AddDays(-91) });
        await db.SaveChangesAsync();
        var queries = new AiUsageQueries(db);
        var before = JsonSerializer.Serialize(await queries.SummaryAsync(null, default));
        var retention = new DataRetentionService(env.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<DataRetentionService>.Instance);
        await retention.CleanupAsync(default);
        await retention.CleanupAsync(default);
        Require(await db.OutgoingReplies.CountAsync() == 2
            && await db.OutgoingReplies.AnyAsync(r => r.Id == "referenced-receipt")
            && await db.OutgoingReplies.AnyAsync(r => r.Id == "pending-receipt"),
            "only a real nonempty receipt ID or a pending state prevents expiry deletion");
        Require(JsonSerializer.Serialize(await queries.SummaryAsync(null, default)) == before,
            "repeated cleanup neither loses nor duplicates cumulative usage with null/empty references");
        Require(await db.AiUsageArchives.SingleAsync() is { LegacyCount: 2, PromptTokens: 20, CompletionTokens: 4 },
            "expired unreferenced receipts are archived exactly once");

        ProactiveChatLogEntity Log(bool? evaluated, string outcome, string? model = null) => new()
        {
            GroupId = "GROUP", EvaluatedAt = now, AiEvaluated = evaluated, Outcome = outcome, Model = model
        };
        OutgoingReply Receipt(string id, string? platformId, string state) => new()
        {
            Id = id, GroupId = "GROUP", State = state, PlatformMessageId = platformId,
            Model = "synthetic-model", PromptTokens = 10, CompletionTokens = 2, CreatedAt = now.AddDays(-91)
        };
    }

    private static async Task<int> ReadTodayDecisionsAsync(IServiceProvider services)
    {
        var controller = new ProactiveController(services.GetRequiredService<AppDbContext>(),
            services.GetRequiredService<IConfigRepository>(), services.GetRequiredService<IActiveMessageService>(),
            new ProactiveObserveService(), new BoundaryGateway(), null!);
        var result = (OkObjectResult)(await controller.GetStatus(default)).Result!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return json.RootElement.GetProperty("todayDecisions").GetInt32();
    }

    private sealed class BoundaryGateway : IQqGatewayConnection
    {
        public bool IsConnected => false;
        public IReadOnlyDictionary<string, long> EventCounts => new Dictionary<string, long>();
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<bool>? ConnectionStateChanged { add { } remove { } }
        public event Func<QqDispatch, CancellationToken, Task>? DispatchReceived { add { } remove { } }
    }
}
