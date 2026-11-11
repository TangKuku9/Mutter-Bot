using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Proactive;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Repositories;
using Mutter.Infrastructure.Services;
using Mutter.Web.Controllers;

internal static partial class RuntimeChecks
{
    private static async Task ProactiveParametersAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var api = new ProactiveParametersController(db, repo);
        await repo.AddKnownGroupAsync("GROUP", "合成群");
        var group = (await repo.GetGroupConfigAsync("GROUP"))!;
        group.CustomSystemPrompt = "保留原提示词"; group.CustomTemperature = 0.7; group.TriggerMode = 2;
        group.MemoryInjectionEnabled = false;
        await repo.SaveGroupConfigAsync(group);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"GroupConfigs\" DROP COLUMN \"ProactiveOverridesJson\"");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"ProactivePresets\"");
        await db.EnsureSchemaUpgradedAsync(); await db.EnsureSchemaUpgradedAsync(); db.ChangeTracker.Clear();
        Require(!(await repo.GetGroupConfigAsync("GROUP"))!.ProactiveOverrides!.HasValues(), "old groups inherit all proactive defaults after an idempotent upgrade");
        var global = await repo.GetProactiveConfigAsync();
        global.ActivePerAccountPerMinute = 2; global.MaxGlobalPerDay = 7;
        await repo.SaveProactiveConfigAsync(global);
        var overrides = JsonSerializer.Deserialize<ProactiveOverrides>("""
            {"windowSeconds":3,"cooldownMinutes":10,"quietStartHour":0,"quietEndHour":0,
             "maxAttachedImages":0,"webSearchEnabled":false,"enabled":true,
             "maxGlobalPerDay":9999,"activePerAccountPerMinute":9999}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Require(await api.SaveGroup("GROUP", new() { Enabled = true, Overrides = overrides }, default) is OkObjectResult, "group parameters save through the real API");
        group = (await repo.GetGroupConfigAsync("GROUP"))!;
        Require(group is { IsEnabled: true, TriggerMode: 2, CustomSystemPrompt: "保留原提示词", CustomTemperature: 0.7, MemoryInjectionEnabled: false }, "proactive-only saving preserves the rest of group configuration");
        Require(group.ProactiveOverrides is { WindowSeconds: 15, MaxAttachedImages: 0, WebSearchEnabled: false, HistoryMaxMessages: null }, "clamping preserves explicit zero/false and sparse inheritance");
        var effective = global.WithOverrides(group.ProactiveOverrides);
        Require(effective.CooldownMinutes == 10 && global.CooldownMinutes == 45
            && effective.MaxGlobalPerDay == 7 && effective.ActivePerAccountPerMinute == 2, "group merging never mutates global defaults or replaces account/global limits");
        Require(!ProactiveOverridesCodec.Encode(group.ProactiveOverrides).Contains("maxGlobalPerDay"), "presets and group overrides cannot store global constraints");
        await new GroupsController(db).UpdateGroupConfig("GROUP", new() { Name = "新群名", IsEnabled = true, AiProactiveEnabled = true, TriggerMode = 2 }, default);
        Require((await repo.GetGroupConfigAsync("GROUP"))!.ProactiveOverrides!.CooldownMinutes == 10,
            "saving the existing general group editor cannot erase proactive overrides");
        global.HistoryMaxMessages = 19; await repo.SaveProactiveConfigAsync(global);
        Require((await repo.GetProactiveConfigAsync()).WithOverrides(group.ProactiveOverrides).HistoryMaxMessages == 19, "an inherited field follows later global edits");
        Require(await api.SaveGroup("GROUP", new() { Overrides = new() { QuietStartHour = 23 } }, default) is BadRequestObjectResult, "half a quiet interval is rejected without saving");
        Require(await api.SaveGroup("MISSING", new(), default) is NotFoundObjectResult, "unknown groups cannot be created by a stale parameter editor");

        var created = (OkObjectResult)await api.CreatePreset(new() { Name = "我的节奏", Values = group.ProactiveOverrides! }, default);
        var preset = (ProactiveParametersController.PresetDto)created.Value!;
        Require(!preset.IsBuiltIn && preset.Values.MaxAttachedImages == 0, "saved presets retain parameter values without switches or account quotas");
        Require(await api.CreatePreset(new() { Name = "日常接话", Values = new() { Liveliness = 2 } }, default) is ConflictObjectResult, "built-in preset names cannot be shadowed");
        Require(await api.CreatePreset(new() { Name = "空白", Values = new() }, default) is BadRequestObjectResult, "empty parameter presets are rejected");
        Require(await api.CreatePreset(new() { Name = "半个区间", Values = new() { QuietEndHour = 8 } }, default) is BadRequestObjectResult, "preset quiet intervals stay atomic");
        await db.EnsureSchemaUpgradedAsync(); db.ChangeTracker.Clear();
        Require(await db.ProactivePresets.CountAsync() == 1 && (await repo.GetGroupConfigAsync("GROUP"))!.ProactiveOverrides!.CooldownMinutes == 10, "later schema upgrades preserve saved presets and group overrides");
        Require(await api.DeletePreset(int.Parse(preset.Id), default) is OkObjectResult
            && (await repo.GetGroupConfigAsync("GROUP"))!.ProactiveOverrides!.CooldownMinutes == 10, "deleting a preset cannot change a group which copied its values");
        await api.CreatePreset(new() { Name = "Evening", Values = new() { Liveliness = 0 } }, default);
        Require(await api.CreatePreset(new() { Name = "evening", Values = new() { Liveliness = 1 } }, default) is ConflictObjectResult, "database uniqueness handles case-insensitive duplicate preset writes");
        db.ChangeTracker.Clear();
        Require(await api.SaveGroup("GROUP", new() { Enabled = true, Overrides = new() }, default) is OkObjectResult
            && !(await repo.GetGroupConfigAsync("GROUP"))!.ProactiveOverrides!.HasValues(), "restoring inheritance removes overrides rather than copying today's defaults");
    }

    private static async Task ProactiveGroupRuntimeAsync()
    {
        await using (var env = await TestEnvironment.CreateAsync())
        {
            using (var scope = env.Services.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                var global = await repo.GetProactiveConfigAsync(); global.WindowSeconds = 600; await repo.SaveProactiveConfigAsync(global);
                var group = (await repo.GetGroupConfigAsync("GROUP"))!;
                group.ProactiveOverrides = new() { WindowSeconds = 15, WindowMaxMessages = 2, EvalMaxTokens = 768, Liveliness = 2, MaxAttachedImages = 0 };
                await repo.SaveGroupConfigAsync(group);
            }
            env.Http.Handler = (_, _, _) => Task.FromResult(JsonReply("{\"reason\":\"合成判定不发言\"}"));
            var observe = env.Services.GetRequiredService<ProactiveObserveService>();
            for (var i = 0; i < 4; i++) observe.Observe("GROUP", "合成用户", $"合成消息{i}", $"input-{i}");
            env.Clock.Advance(TimeSpan.FromSeconds(16));
            var service = new ProactiveChatHostedService(env.Services.GetRequiredService<IServiceScopeFactory>(), observe,
                env.Services.GetRequiredService<ProactiveWorkflow>(), env.Services.GetRequiredService<IConversationWorkQueue>(),
                env.Services.GetRequiredService<UpstreamHealthTracker>(), env.Services.GetRequiredService<RuntimeOptions>(), env.Clock,
                NullLogger<ProactiveChatHostedService>.Instance);
            await (Task)typeof(ProactiveChatHostedService).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [CancellationToken.None])!;
            using (var scope = env.Services.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                var group = (await repo.GetGroupConfigAsync("GROUP"))!; group.ProactiveOverrides!.WindowMaxMessages = 3;
                await repo.SaveGroupConfigAsync(group);
            }
            var queue = env.Services.GetRequiredService<ConversationWorkQueue>();
            await queue.StartAsync(default); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await queue.StopAsync(deadline.Token);
            using var check = env.Services.CreateScope();
            var log = await check.ServiceProvider.GetRequiredService<AppDbContext>().ProactiveChatLogs.SingleAsync();
            Require(log.MessageCount == 3 && env.Http.Requests.Count == 1, "the scheduler uses group timing and rereads window size after queueing");
            using var body = JsonDocument.Parse(env.Http.Requests[0].Body);
            Require(body.RootElement.GetProperty("max_tokens").GetInt32() == 768, "group output budgets reach the real model request");
            var configs = check.ServiceProvider.GetRequiredService<IConfigRepository>();
            var disabledGroup = (await configs.GetGroupConfigAsync("GROUP"))!;
            disabledGroup.AiProactiveEnabled = false; await configs.SaveGroupConfigAsync(disabledGroup);
            observe.Observe("GROUP", "合成用户", "停用前的旧话题", "old-input");
            await (Task)typeof(ProactiveChatHostedService).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [CancellationToken.None])!;
            Require(observe.PendingWindows().Count == 0, "disabling group participation discards old windows before a later re-enable");
        }
        await using (var env = await TestEnvironment.CreateAsync())
        {
            using (var scope = env.Services.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                await repo.SaveProactiveConfigAsync(new() { Enabled = true, QuietStartHour = 0, QuietEndHour = 0, ActivePerGroupPerMinute = 3, ActivePerGroupPerDay = 20, ActivePerAccountPerMinute = 2 });
                var group = (await repo.GetGroupConfigAsync("GROUP"))!;
                group.ProactiveOverrides = new() { ActivePerGroupPerMinute = 1, ActivePerGroupPerDay = 2 };
                await repo.SaveGroupConfigAsync(group);
            }
            var active = env.Services.GetRequiredService<IActiveMessageService>();
            Require((await active.SendGroupActiveAsync("GROUP", "first")).Success
                && (await active.SendGroupActiveAsync("GROUP", "second")).Reason == "rate_limited", "group active-message minute overrides are enforced");
            Require((await active.SendGroupActiveAsync("OTHER", "first")).Success
                && (await active.SendGroupActiveAsync("THIRD", "first")).Reason == "rate_limited", "all groups still share the global account limit");
            var quota = await active.GetGroupQuotaStatusAsync("GROUP");
            Require(quota.MinuteQuota == 1 && quota.Quota == 2, "the overview quota API reports the group's effective limits");
            env.Clock.Advance(TimeSpan.FromSeconds(61));
            Require((await active.SendGroupActiveAsync("GROUP", "second")).Success, "group quota survives service recreation and minute rollover");
            env.Clock.Advance(TimeSpan.FromSeconds(61));
            Require((await active.SendGroupActiveAsync("GROUP", "third")).Reason == "rate_limited", "group daily active-message overrides are enforced");
        }
        await using (var env = await TestEnvironment.CreateAsync())
        {
            env.Http.Handler = async (_, _, ct) =>
            {
                using var scope = env.Services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                var group = (await repo.GetGroupConfigAsync("GROUP", ct))!;
                group.ProactiveOverrides = new() { QuietStartHour = 12, QuietEndHour = 13 };
                await repo.SaveGroupConfigAsync(group, ct);
                return JsonReply("{\"line\":\"合成回复\"}");
            };
            var now = env.Clock.GetUtcNow().UtcDateTime;
            await env.Services.GetRequiredService<ProactiveWorkflow>().RunAsync(new()
            { GroupId = "GROUP", LastMessageId = "input", LastMessageUtcAt = now, Messages = [new("合成用户", "合成话题", "input", now)] }, default);
            using var scope = env.Services.CreateScope();
            var log = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProactiveChatLogs.SingleAsync();
            Require(log.Outcome == ProactiveOutcomes.SkippedQuietHours && log.AiEvaluated == true
                && env.Api.PassiveCalls == 0 && env.Api.ActiveCalls == 0, "final delivery rereads group quiet hours changed while the model was running");
        }
    }
}
