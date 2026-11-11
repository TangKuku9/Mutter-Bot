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

    private static JsonElement JsonBody(IActionResult result) => JsonSerializer.SerializeToElement(((OkObjectResult)result).Value!);

    /// <summary>P1-5 与部分更新契约：无配置群首次保存、缺省字段不覆盖、记忆 AND、预设上限与更新接口。</summary>
    private static async Task ProactiveGroupContractAsync()
    {
        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var api = new ProactiveParametersController(db, repo);

        // 已知群没有配置行：面板按“参与响应”展示（与 MessageRouter 的默认放行一致），首次保存不再静默停用。
        await repo.AddKnownGroupAsync("FRESH", "无配置群");
        var read = JsonBody(await api.GetGroup("FRESH", default));
        Require(read.GetProperty("IsEnabled").GetBoolean() && !read.GetProperty("Enabled").GetBoolean(),
            "a known group without a config row reads as participating instead of disabled");
        Require(await api.SaveGroup("FRESH", new() { Overrides = new() { CooldownMinutes = 10 } }, default) is OkObjectResult,
            "the first parameter save for a known group succeeds");
        var saved = (await repo.GetGroupConfigAsync("FRESH"))!;
        Require(saved is { IsEnabled: true, AiProactiveEnabled: false } && saved.ProactiveOverrides!.CooldownMinutes == 10,
            "the first save inserts an enabled row without silently opting the group into proactive");

        // 部分更新契约：只传 Enabled 不清空 overrides；只传 Overrides 不改 Enabled。
        Require(await api.SaveGroup("FRESH", new() { Enabled = true }, default) is OkObjectResult, "a partial save with only the switch succeeds");
        saved = (await repo.GetGroupConfigAsync("FRESH"))!;
        Require(saved.AiProactiveEnabled && saved.ProactiveOverrides!.CooldownMinutes == 10, "enabling alone keeps stored overrides");
        Require(await api.SaveGroup("FRESH", new() { Overrides = new() { CooldownMinutes = 20 } }, default) is OkObjectResult, "a partial save with only overrides succeeds");
        saved = (await repo.GetGroupConfigAsync("FRESH"))!;
        Require(saved.AiProactiveEnabled && saved.ProactiveOverrides!.CooldownMinutes == 20, "overrides alone keep the participation switch");

        // 记忆双开关：MemoryAllowed 只反映群管理；EffectiveConfig.MemoryInjectionEnabled 是最终 AND。
        var managed = (await repo.GetGroupConfigAsync("FRESH"))!;
        managed.MemoryInjectionEnabled = false;
        await repo.SaveGroupConfigAsync(managed);
        var blocked = JsonBody(await api.GetGroup("FRESH", default));
        Require(!blocked.GetProperty("MemoryAllowed").GetBoolean()
            && !blocked.GetProperty("EffectiveConfig").GetProperty("MemoryInjectionEnabled").GetBoolean(),
            "group management prohibition shows in both memory fields");
        managed = (await repo.GetGroupConfigAsync("FRESH"))!;
        managed.MemoryInjectionEnabled = true;
        managed.ProactiveOverrides!.MemoryInjectionEnabled = false;
        await repo.SaveGroupConfigAsync(managed);
        var overrideOff = JsonBody(await api.GetGroup("FRESH", default));
        Require(overrideOff.GetProperty("MemoryAllowed").GetBoolean()
            && !overrideOff.GetProperty("EffectiveConfig").GetProperty("MemoryInjectionEnabled").GetBoolean(),
            "the proactive memory override alone turns off effective injection");

        // 预设：控制字符清洗、更新接口、重名校验、数量上限。
        var created = (ProactiveParametersController.PresetDto)((OkObjectResult)await api.CreatePreset(
            new() { Name = "\u0007晚\u000B间 ", Description = " 说明\t", Values = new() { Liveliness = 1 } }, default)).Value!;
        Require(created.Name == "晚间" && created.Description == "说明", "control characters are stripped from preset text");
        Require(((ProactiveParametersController.PresetDto)((OkObjectResult)await api.UpdatePreset(int.Parse(created.Id),
            new() { Name = "晨间", Description = "早鸟", Values = new() { Liveliness = 2, CooldownMinutes = 30 } }, default)).Value!)
            .Values is { Liveliness: 2, CooldownMinutes: 30 }, "preset updates change name and values");
        Require(((ProactiveParametersController.PresetDto)((OkObjectResult)await api.UpdatePreset(int.Parse(created.Id),
            new() { Name = "午后", Description = "保留参数" }, default)).Value!).Values.Liveliness == 2,
            "an update without values keeps the stored parameters");
        var second = (ProactiveParametersController.PresetDto)((OkObjectResult)await api.CreatePreset(
            new() { Name = "第二份", Values = new() { Liveliness = 1 } }, default)).Value!;
        Require(await api.UpdatePreset(int.Parse(second.Id), new() { Name = "午后", Description = "" }, default) is ConflictObjectResult,
            "preset updates cannot take an existing name");
        Require(await api.UpdatePreset(int.Parse(second.Id), new() { Name = "日常接话", Description = "" }, default) is ConflictObjectResult,
            "preset updates cannot shadow built-in names");
        for (var i = 3; i <= 50; i++)
            Require(await api.CreatePreset(new() { Name = $"预设{i:D2}", Values = new() { Liveliness = 1 } }, default) is OkObjectResult,
                "presets up to the cap are accepted");
        Require(await db.ProactivePresets.CountAsync() == 50, "the preset cap is reached");
        Require(await api.CreatePreset(new() { Name = "超出上限", Values = new() { Liveliness = 1 } }, default) is BadRequestObjectResult,
            "the 51st preset is rejected");
    }

    /// <summary>覆盖解码容错：坏字段单独丢弃、未知键透传、半个静默区间清空、仓储写入的预算兜底。</summary>
    private static async Task ProactiveOverridesCodecAsync()
    {
        var decoded = ProactiveOverridesCodec.Decode("""
            {"cooldownMinutes":10,"windowSeconds":1.5,"futureFlag":true,
             "quietStartHour":23,"quietEndHour":7,"maxGlobalPerDay":999,
             "Enabled":false,"MAXGLOBALPERDAY":999}
            """);
        Require(decoded is { CooldownMinutes: 10, WindowSeconds: null, QuietStartHour: 23, QuietEndHour: 7 },
            "one malformed field is dropped without discarding the rest of the overrides");
        Require(decoded.Extra is { Count: 1 } && decoded.Extra.ContainsKey("futureFlag"), "unknown keys land in Extra");
        var encoded = ProactiveOverridesCodec.Encode(decoded);
        Require(encoded.Contains("futureFlag")
            && !(encoded.Contains("maxGlobalPerDay", StringComparison.OrdinalIgnoreCase)
                || encoded.Contains("enabled", StringComparison.OrdinalIgnoreCase)
                || encoded.Contains("activePerAccountPerMinute", StringComparison.OrdinalIgnoreCase)),
            "encode preserves future keys and never stores global constraints, including case variants");
        Require(ProactiveOverridesCodec.Decode(encoded).Extra is { Count: 1 }, "the round trip keeps future keys");
        var half = ProactiveOverridesCodec.Decode("""{"quietEndHour":8,"cooldownMinutes":5}""");
        Require(half is { QuietEndHour: null, QuietStartHour: null, CooldownMinutes: 5 },
            "half a quiet interval is dropped on read instead of reaching the API");

        await using var env = await TestEnvironment.CreateAsync();
        using var scope = env.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await repo.SaveAiConfigAsync(env.Config() with { ContextWindowTokens = 2048 });
        var group = (await repo.GetGroupConfigAsync("GROUP"))!;
        group.ProactiveOverrides = new() { EvalMaxTokens = 8192, CooldownMinutes = 12 };
        await repo.SaveGroupConfigAsync(group);
        Require((await repo.GetGroupConfigAsync("GROUP"))!.ProactiveOverrides!.EvalMaxTokens == 512,
            "the repository clamps eval budgets to the configured context window");
        // partial GroupConfig：ProactiveOverrides 为 null 时保持原值，不清空覆盖。
        await repo.SaveGroupConfigAsync(new GroupConfig { GroupId = "GROUP", IsEnabled = true, AiProactiveEnabled = true });
        Require((await repo.GetGroupConfigAsync("GROUP"))!.ProactiveOverrides is { EvalMaxTokens: 512, CooldownMinutes: 12 },
            "partial saves keep the stored overrides");
        await repo.SaveProactiveConfigAsync(new() { Enabled = true, QuietStartHour = 0, QuietEndHour = 0, EvalMaxTokens = 8192 });
        Require((await repo.GetProactiveConfigAsync()).EvalMaxTokens == 512, "global saves clamp the budget the same way");
    }

    /// <summary>调度门禁：静默期与日限清空缓冲、主动通道开关兜底、状态接口的生效值与待评估过滤。</summary>
    private static async Task ProactiveSchedulerGuardsAsync()
    {
        var clock = new TestClock();
        var quietConfig = new ProactiveChatConfig { Enabled = true, QuietStartHour = 12, QuietEndHour = 13, WindowSeconds = 15 }.Normalize();
        var quietObserve = new ProactiveObserveService(clock);
        quietObserve.Observe("G", "虚拟用户", "深夜话题", "quiet-input");
        var now = clock.GetUtcNow().UtcDateTime;
        Require(quietConfig.IsQuietNow(now), "the synthetic clock sits inside quiet hours");
        Require(!quietObserve.IsDue("G", quietConfig, now) && quietObserve.PendingWindows().Count == 0,
            "quiet hours clear the buffer instead of scheduling an evaluation");
        var cappedObserve = new ProactiveObserveService(clock);
        var capConfig = new ProactiveChatConfig { Enabled = true, QuietStartHour = 0, QuietEndHour = 0, MaxPerGroupPerDay = 1 }.Normalize();
        cappedObserve.Observe("G", "虚拟用户", "新话题", "cap-input");
        cappedObserve.MarkSent("G", clock.GetUtcNow().UtcDateTime);
        Require(!cappedObserve.IsDue("G", capConfig, clock.GetUtcNow().UtcDateTime) && cappedObserve.PendingWindows().Count == 0,
            "a spent daily cap clears the buffer instead of logging skips");

        // 静默期 Tick：不排任务、不调用 AI、缓冲被清空。
        await using (var env = await TestEnvironment.CreateAsync())
        {
            using (var scope = env.Services.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                var global = await repo.GetProactiveConfigAsync();
                global.QuietStartHour = 12; global.QuietEndHour = 13;
                await repo.SaveProactiveConfigAsync(global);
            }
            var observe = env.Services.GetRequiredService<ProactiveObserveService>();
            observe.Observe("GROUP", "虚拟用户", "深夜话题", "quiet-input");
            var service = new ProactiveChatHostedService(env.Services.GetRequiredService<IServiceScopeFactory>(), observe,
                env.Services.GetRequiredService<ProactiveWorkflow>(), env.Services.GetRequiredService<IConversationWorkQueue>(),
                env.Services.GetRequiredService<UpstreamHealthTracker>(), env.Services.GetRequiredService<RuntimeOptions>(), env.Clock,
                NullLogger<ProactiveChatHostedService>.Instance);
            await (Task)typeof(ProactiveChatHostedService).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [CancellationToken.None])!;
            using var check = env.Services.CreateScope();
            Require(env.Http.Requests.Count == 0 && !await check.ServiceProvider.GetRequiredService<AppDbContext>().ProactiveChatLogs.AnyAsync()
                && observe.PendingWindows().Count == 0,
                "a quiet-hour tick schedules nothing, calls no model and discards the buffer");
        }

        // 主动通道服务自身把关 AllowActiveChannel（群与私聊都按生效配置）。
        await using (var env = await TestEnvironment.CreateAsync())
        {
            using (var scope = env.Services.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                var global = await repo.GetProactiveConfigAsync();
                global.AllowActiveChannel = false;
                await repo.SaveProactiveConfigAsync(global);
            }
            var active = env.Services.GetRequiredService<IActiveMessageService>();
            Require((await active.SendGroupActiveAsync("GROUP", "x")).Reason == "active_channel_disabled"
                && (await active.SendPrivateActiveAsync("USER", "x")).Reason == "active_channel_disabled"
                && env.Api.ActiveCalls == 0,
                "the active message service refuses to send when the channel is disabled");
        }

        // 状态接口：PendingWindows 只列真正会评估的群；配额行带生效参数。
        await using (var env = await TestEnvironment.CreateAsync())
        {
            using (var scope = env.Services.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                await repo.SaveProactiveConfigAsync(new() { Enabled = true, QuietStartHour = 0, QuietEndHour = 0 });
                var group = (await repo.GetGroupConfigAsync("GROUP"))!;
                group.ProactiveOverrides = new() { CooldownMinutes = 5, QuietStartHour = 2, QuietEndHour = 3 };
                group.MemoryInjectionEnabled = false;
                await repo.SaveGroupConfigAsync(group);
                await repo.SaveGroupConfigAsync(new GroupConfig { GroupId = "IDLE", IsEnabled = true, AiProactiveEnabled = true });
                await repo.SaveGroupConfigAsync(new GroupConfig { GroupId = "OFF", IsEnabled = false, AiProactiveEnabled = true });
            }
            var observe = new ProactiveObserveService();
            observe.Observe("GROUP", "虚拟用户", "甲", "g1");
            observe.Observe("OFF", "虚拟用户", "乙", "o1");
            observe.Observe("GHOST", "虚拟用户", "丙", "h1");
            using var controllerScope = env.Services.CreateScope();
            var controller = new ProactiveController(controllerScope.ServiceProvider.GetRequiredService<AppDbContext>(),
                controllerScope.ServiceProvider.GetRequiredService<IConfigRepository>(), controllerScope.ServiceProvider.GetRequiredService<IActiveMessageService>(),
                observe, new BoundaryGateway(), null!);
            var status = JsonSerializer.SerializeToElement(((OkObjectResult)(await controller.GetStatus(default)).Result!).Value);
            var pending = status.GetProperty("PendingWindows").EnumerateArray()
                .Select(p => p.GetProperty("GroupId").GetString()).ToArray();
            Require(pending.SequenceEqual(["GROUP"]), "pending windows only list groups that would actually be evaluated");
            var quota = status.GetProperty("Quotas").EnumerateArray().ToList();
            Require(quota.All(q => q.GetProperty("GroupId").GetString() != "OFF"), "disabled groups keep no quota row");
            var effective = quota.Single(q => q.GetProperty("GroupId").GetString() == "GROUP").GetProperty("Effective");
            Require(effective.GetProperty("CooldownMinutes").GetInt32() == 5 && effective.GetProperty("QuietStartHour").GetInt32() == 2
                && effective.GetProperty("QuietEndHour").GetInt32() == 3 && effective.GetProperty("MaxPerGroupPerDay").GetInt32() == 3,
                "quota rows carry the group's effective values next to the global ones");
            Require(!effective.GetProperty("MemoryInjectionEnabled").GetBoolean(),
                "the status effective memory flag also respects the group's memory permission");
            using (var scope = env.Services.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                var global = await repo.GetProactiveConfigAsync();
                global.Enabled = false;
                await repo.SaveProactiveConfigAsync(global);
            }
            // 真实请求各自使用独立作用域；这里手动清跟踪，模拟第二次 GET 看到最新全局开关。
            controllerScope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.Clear();
            status = JsonSerializer.SerializeToElement(((OkObjectResult)(await controller.GetStatus(default)).Result!).Value);
            Require(!status.GetProperty("PendingWindows").EnumerateArray().Any(), "the global switch empties pending windows");
        }
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
