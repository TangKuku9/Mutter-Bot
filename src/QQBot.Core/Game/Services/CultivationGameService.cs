using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Game.Ai;
using QQBot.Core.Game.Engines;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;
using QQBot.Core.Game.Ui;

namespace QQBot.Core.Game.Services;

/// <summary>
/// 修仙RPG 游戏门面。Singleton，通过 IServiceScopeFactory 访问 Scoped 仓储。
/// 编排：获取存档 → 惰性结算挂机 → 执行动作 → 持久化 → 审计日志。
/// </summary>
public class CultivationGameService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IdleSettlementEngine _idleEngine;
    private readonly BreakthroughEngine _breakthroughEngine;
    private readonly DailyTaskEngine _dailyEngine;
    private readonly AlchemyEngine _alchemyEngine;
    private readonly ExploreEngine _exploreEngine;
    private readonly SparEngine _sparEngine;
    private readonly EventApplier _applier;
    private readonly EventGenerator _eventGenerator;
    private readonly ActionReferee _referee;
    private readonly IStructuredAiService _structured;
    private readonly GameUiPresenter _ui;
    private readonly ILogger<CultivationGameService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public CultivationGameService(
        IServiceScopeFactory scopeFactory,
        IdleSettlementEngine idleEngine,
        BreakthroughEngine breakthroughEngine,
        DailyTaskEngine dailyEngine,
        AlchemyEngine alchemyEngine,
        ExploreEngine exploreEngine,
        SparEngine sparEngine,
        EventApplier applier,
        EventGenerator eventGenerator,
        ActionReferee referee,
        IStructuredAiService structured,
        GameUiPresenter ui,
        ILogger<CultivationGameService> logger)
    {
        _scopeFactory = scopeFactory;
        _idleEngine = idleEngine;
        _breakthroughEngine = breakthroughEngine;
        _dailyEngine = dailyEngine;
        _alchemyEngine = alchemyEngine;
        _exploreEngine = exploreEngine;
        _sparEngine = sparEngine;
        _applier = applier;
        _eventGenerator = eventGenerator;
        _referee = referee;
        _structured = structured;
        _ui = ui;
        _logger = logger;

        // 启动自检：内容池配置错误快速失败，不静默上线
        FlavorText.Validate();
    }

    /// <summary>群级串行锁：同群所有游戏操作排队执行，消除并发指令造成的读改写竞态
    /// （存档丢更新、护法互相覆盖、渡劫会话冲突、丹药重复消耗等）。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _groupLocks = new();

    private static SemaphoreSlim GroupLock(string groupId) =>
        _groupLocks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));

    public async Task<GameUiReply> HandleAsync(
        string groupId, string memberOpenId, string nickname, GameCommand command, CancellationToken ct = default)
    {
        var groupLock = GroupLock(groupId);
        await groupLock.WaitAsync(ct);
        try
        {
            return await HandleCoreAsync(groupId, memberOpenId, nickname, command, ct);
        }
        finally
        {
            groupLock.Release();
        }
    }

    private async Task<GameUiReply> HandleCoreAsync(
        string groupId, string memberOpenId, string nickname, GameCommand command, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();

            // 帮助与排行不需要存档
            if (command.Action == GameAction.Help)
                return await ComposeAsync(repo, groupId, command, GameTextFormatter.FormatHelp(),
                    null, null, string.Empty, GameTextFormatter.BuildHelp(), nickname, ct);

            if (command.Action == GameAction.Leaderboard)
            {
                var entries = await repo.GetGroupLeaderboardAsync(groupId, 10, ct);
                var lbPanel = entries.Count > 0
                    ? GameTextFormatter.BuildLeaderboard(entries, _ui.Capability.UseAtTags(groupId))
                    : null;
                return await ComposeAsync(repo, groupId, command, GameTextFormatter.FormatLeaderboard(entries),
                    null, null, string.Empty, lbPanel, nickname, ct);
            }

            // 获取/创建存档
            var existing = await repo.GetPlayerAsync(groupId, memberOpenId, ct);
            var isNew = existing is null;
            var player = isNew
                ? await repo.GetOrCreatePlayerAsync(groupId, memberOpenId, nickname, ct)
                : existing!;
            var world = await repo.GetOrCreateGroupWorldAsync(groupId, ct);
            var now = DateTime.UtcNow;

            // 昵称双向对齐：平台下发了真实昵称就回填存档（历史只拿到 OpenId，旧档只能显示“道友XXXX”）；
            // 拿不到昵称的场景（如按钮回调事件）则用存档里的道号补位，保证面板与首行宽度估算不失真。
            if (!string.IsNullOrWhiteSpace(nickname) && nickname != memberOpenId)
            {
                if (player.Nickname != nickname) player.Nickname = nickname;
            }
            else if (!string.IsNullOrWhiteSpace(player.Nickname))
            {
                nickname = player.Nickname;
            }

            // 新玩家创角审计日志（任意首条指令均写入）；首次查看面板 → 欢迎语
            if (isNew)
            {
                await repo.AppendEventLogAsync(new CultivationEventLog(
                    groupId, memberOpenId, "create",
                    Json("action", "create", "root", player.SpiritRootName, "grade", player.SpiritRootGrade),
                    null, Json("realmIndex", 0)), ct);
                if (command.Action == GameAction.Status)
                    return await ComposeAsync(repo, groupId, command, GameTextFormatter.FormatNewPlayer(player),
                        player, world, string.Empty, null, nickname, ct);
            }

            // 每次访问先惰性结算挂机
            var idle = _idleEngine.Settle(player, world, now);
            var idleSummary = _applier.ApplyIdle(player, idle);

            var reply = command.Action switch
            {
                GameAction.Status => await HandleStatusAsync(repo, player, idleSummary, idle, ct),
                GameAction.Breakthrough => await HandleBreakthroughAsync(repo, player, world, now, idleSummary, ct),
                GameAction.Checkin => await HandleCheckinAsync(repo, player, now, idleSummary, ct),
                GameAction.Practice => await HandlePracticeAsync(repo, player, now, idleSummary, ct),
                GameAction.Inventory => await HandleInventoryAsync(repo, player, idleSummary, ct),
                GameAction.Alchemy => await HandleAlchemyAsync(repo, player, now, command.Args, idleSummary, ct),
                GameAction.Explore => await HandleExploreAsync(repo, player, now, idleSummary, ct),
                GameAction.Event => await HandleEventAsync(repo, player, world, now, idleSummary, ct),
                GameAction.Choose => await HandleChooseAsync(repo, player, world, now, command.Args, idleSummary, ct),
                GameAction.Protect => await HandleProtectAsync(repo, player, world, now, idleSummary, ct),
                GameAction.Shop => await HandleShopAsync(repo, player, now, command.Args, idleSummary, ct),
                GameAction.Sell => await HandleSellAsync(repo, player, command.Args, idleSummary, ct),
                GameAction.UseDan => await HandleUseDanAsync(repo, player, now, command.Args, idleSummary, ct),
                GameAction.Spar => await HandleSparAsync(repo, player, now, command.Args, idleSummary, ct),
                GameAction.Quest => await HandleQuestAsync(repo, player, now, idleSummary, ct),
                _ => GameTextFormatter.FormatHelp()
            };

            // 出口统一交给呈现器：纯文本永远折行兜底（同时用于落库与降级），开了富交互的群额外生成 markdown 与菜单
            var exitPanel = command.Action == GameAction.Status
                ? GameTextFormatter.BuildStatus(player, idleSummary)
                : command.Action == GameAction.Shop && string.IsNullOrWhiteSpace(command.Args)
                    ? GameTextFormatter.BuildShop(player.SpiritStones)
                    : command.Action == GameAction.Alchemy && string.IsNullOrWhiteSpace(command.Args)
                        ? GameTextFormatter.BuildRecipes()
                        : null;

            return await ComposeAsync(repo, groupId, command, reply,
                player, world, idleSummary, exitPanel, nickname, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cultivation game handle failed group={GroupId} action={Action}", groupId, command.Action);
            return GameUiReply.Text("天道动荡，操作失败。\n请稍后再试。");
        }
    }

    /// <summary>
    /// 结单出口组装。纯文本与面板正文不一致时（理论上不应发生）自动退回纯文本转换，
    /// 保证降级内容与实际发送内容同源。
    /// </summary>
    private async Task<GameUiReply> ComposeAsync(
        ICultivationRepository repo, string groupId, GameCommand command, string plain,
        PlayerProfile? player, GroupWorld? world, string idleSummary,
        GamePanel? panel, string nickname, CancellationToken ct)
    {
        var cap = _ui.Capability;
        if (!cap.UseMarkdown(groupId))
            return GameUiReply.Text(TextLayout.Flow(plain, GameTextFormatter.MentionReserve(nickname)));

        if (panel is not null && panel.RenderPlain() != plain)
        {
            _logger.LogWarning("RichUi 面板与正文不一致，退回纯文本转换 group={GroupId} action={Action}",
                groupId, command.Action);
            panel = null;
        }

        var ctx = await BuildMenuContextAsync(repo, groupId, command, player, world, ct);
        return _ui.Compose(groupId, command.Action, plain, ctx, nickname, panel);
    }

    /// <summary>菜单需要标注冷却的动作（与 SetCooldownAsync 的键一致）。</summary>
    private static readonly string[] MenuCooldownKeys =
        { "practice", "explore", "alchemy", "breakthrough", "daily", "spar", "referee" };

    /// <summary>按当前界面所需的一点点动态数据组装菜单上下文（仅在富交互开启时调用）。</summary>
    private static async Task<MenuContext> BuildMenuContextAsync(
        ICultivationRepository repo, string groupId, GameCommand command,
        PlayerProfile? player, GroupWorld? world, CancellationToken ct)
    {
        // 一次 IN 查询拿到全部相关冷却，菜单据此把“现在点只会吃冷却”的动作标成灰框并后置
        var cooldowns = player is null
            ? null
            : await LoadMenuCooldownsAsync(repo, groupId, player.MemberOpenId, ct);

        switch (command.Action)
        {
            case GameAction.Alchemy when string.IsNullOrWhiteSpace(command.Args):
                return MenuContext.Empty with
                {
                    RecipeNames = Models.AlchemyRecipe.Recipes.Select(r => r.Name).ToList(),
                    Cooldowns = cooldowns
                };

            case GameAction.Inventory or GameAction.UseDan:
            {
                if (player is null) return MenuContext.Empty with { Cooldowns = cooldowns };
                var inv = await repo.GetInventoryAsync(groupId, player.MemberOpenId, ct);
                return MenuContext.Empty with
                {
                    UsableDanNames = inv
                        .Where(i => i.ItemType == "dan" && i.Quantity > 0)
                        .Select(i => i.ItemName)
                        .ToList(),
                    Cooldowns = cooldowns
                };
            }

            case GameAction.Event or GameAction.Choose:
            {
                var pending = world is null ? null : EventGenerator.Deserialize(world.PendingEventJson);
                var live = pending is not null
                    && (world!.PendingEventExpiresAt is null || world.PendingEventExpiresAt > DateTime.UtcNow);
                return MenuContext.Empty with
                {
                    EventOptionCount = live ? pending!.Options.Count : 0,
                    Cooldowns = cooldowns
                };
            }

            case GameAction.Breakthrough or GameAction.Protect:
            {
                var session = world is null ? null : TribulationSession.Deserialize(world.TribulationSessionJson);
                var open = session is not null && !session.IsExpired;
                return MenuContext.Empty with
                {
                    TribulationOpen = open,
                    IsOwnTribulation = open && session!.MemberOpenId == player?.MemberOpenId,
                    Cooldowns = cooldowns
                };
            }

            default:
                return MenuContext.Empty with { Cooldowns = cooldowns };
        }
    }

    private static async Task<Dictionary<string, TimeSpan>?> LoadMenuCooldownsAsync(
        ICultivationRepository repo, string groupId, string memberOpenId, CancellationToken ct)
    {
        var active = await repo.GetActiveCooldownsAsync(groupId, memberOpenId, MenuCooldownKeys, ct);
        if (active.Count == 0) return null;

        var now = DateTime.UtcNow;
        return active.ToDictionary(kv => kv.Key, kv => kv.Value - now, StringComparer.Ordinal);
    }

    private async Task<string> HandleStatusAsync(
        ICultivationRepository repo, PlayerProfile player, string idleSummary, IdleSettlementResult idle, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);
        if (idle.GainedCultivation > 0)
        {
            await repo.AppendEventLogAsync(new CultivationEventLog(
                player.GroupId, player.MemberOpenId, "idle",
                Json("gained", idle.GainedCultivation, "hours", Math.Round(idle.EffectiveHours, 2)),
                null, Json("cultivationPoints", player.CultivationPoints)), ct);
        }
        return GameTextFormatter.FormatStatus(player, idleSummary);
    }

    private async Task<string> HandleBreakthroughAsync(
        ICultivationRepository repo, PlayerProfile player, GroupWorld world, DateTime now, string idleSummary, CancellationToken ct)
    {
        // 先持久化挂机结算
        await repo.SavePlayerAsync(player, ct);

        // 检查是否有进行中的渡劫会话
        var session = TribulationSession.Deserialize(world.TribulationSessionJson);

        // 会话属于当前玩家且未过期 → 执行真正的突破
        if (session is not null && !session.IsExpired && session.MemberOpenId == player.MemberOpenId)
        {
            var cd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "breakthrough", ct);
            if (cd is not null && cd.Value > now)
                return (idleSummary.Length > 0 ? idleSummary + "\n" : "")
                    + $"突破冷却中，剩 {FormatRemaining(cd.Value - now)}";

            // 消耗丹药积累的突破加成
            var pillBonus = player.PendingBreakthroughBonus;
            var totalBonus = session.TotalBonus + pillBonus;
            var result = _breakthroughEngine.Attempt(player, now, totalBonus);
            var msg = _applier.ApplyBreakthrough(player, result);

            // 表现层装饰：风味句（确定性）+ 成功时 AI 批语（失败静默回退）
            if (result.Outcome is "success" or "failure")
            {
                var flavorKey = result.Outcome == "success" ? "breakthrough_success" : "breakthrough_failure";
                msg = FlavorText.Decorate(msg, flavorKey,
                    $"{player.MemberOpenId}|bt|{now:yyyyMMddHHmm}|{player.CultivationPoints}");
                if (result.Outcome == "success")
                {
                    var blessing = await TryGetAiBlessingAsync(player, ct);
                    if (blessing is not null) msg += $"\n{blessing}";
                }
            }

            if (result.Outcome is "success" or "failure")
                await repo.SetCooldownAsync(player.GroupId, player.MemberOpenId, "breakthrough",
                    TimeSpan.FromHours(GameConstants.BreakthroughCooldownHours), ct);

            // 清除渡劫会话与丹药加成
            world.TribulationSessionJson = null;
            player.PendingBreakthroughBonus = 0;
            await repo.SaveGroupWorldAsync(world, ct);
            await repo.SavePlayerAsync(player, ct);

            var pillMsg = pillBonus > 0 ? $"，丹药加成 +{pillBonus:P0}" : "";
            var protectorInfo = session.Protectors.Count > 0
                ? $"（{session.Protectors.Count}位道友护法 +{session.TotalBonus:P0}{pillMsg}）"
                : pillBonus > 0 ? $"（丹药加成 +{pillBonus:P0}）" : "";

            await repo.AppendEventLogAsync(new CultivationEventLog(
                player.GroupId, player.MemberOpenId, "breakthrough",
                Json("outcome", result.Outcome, "chance", Math.Round(result.RolledChance, 3),
                     "protectors", session.Protectors.Count, "bonus", Math.Round(session.TotalBonus, 3),
                     "pillBonus", Math.Round(pillBonus, 3)),
                null,
                Json("realmIndex", player.RealmIndex, "cultivationPoints", player.CultivationPoints,
                     "spiritStones", player.SpiritStones)), ct);

            return idleSummary.Length > 0
                ? $"{idleSummary}\n{msg}{protectorInfo}"
                : $"{msg}{protectorInfo}";
        }

        // 清除过期会话
        if (session is not null && (session.IsExpired || session.MemberOpenId != player.MemberOpenId))
        {
            if (session.IsExpired)
            {
                world.TribulationSessionJson = null;
                await repo.SaveGroupWorldAsync(world, ct);
            }
            else if (session.MemberOpenId != player.MemberOpenId)
            {
                var who = AtMember(session.MemberOpenId, session.Nickname, player.GroupId);
                return idleSummary.Length > 0
                    ? $"{idleSummary}\n{who} 正在渡劫中\n道友可 @我 护法相助"
                    : $"{who} 正在渡劫中\n道友可 @我 护法相助";
            }
        }

        // 冷却检查：创建新会话前同样校验，防止冷却中的玩家反复创建会话阻塞全群突破
        var cdCreate = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "breakthrough", ct);
        if (cdCreate is not null && cdCreate.Value > now)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n突破冷却中，剩 {FormatRemaining(cdCreate.Value - now)}"
                : $"突破冷却中，剩 {FormatRemaining(cdCreate.Value - now)}";

        // 前置检查：修为是否足够（提前提示，避免创建无意义会话）
        if (Realm.IsMax(player.RealmIndex))
            return idleSummary.Length > 0
                ? $"{idleSummary}\n已达渡劫之巅，无更高境界可破。"
                : "已达渡劫之巅，无更高境界可破。";

        if (player.CultivationPoints < BreakthroughEngine.ThresholdFor(player.RealmIndex))
        {
            var need = BreakthroughEngine.ThresholdFor(player.RealmIndex);
            return idleSummary.Length > 0
                ? $"{idleSummary}\n修为不足，无法突破。\n需 {need:N0}（当前 {player.CultivationPoints:N0}）"
                : $"修为不足，无法突破。\n需 {need:N0}（当前 {player.CultivationPoints:N0}）";
        }

        // 创建新的渡劫会话
        var newSession = new TribulationSession
        {
            MemberOpenId = player.MemberOpenId,
            Nickname = GameTextFormatter.DisplayName(player),
            StartedAt = now,
            ExpiresAt = now.AddMinutes(10)
        };
        world.TribulationSessionJson = TribulationSession.Serialize(newSession);
        await repo.SaveGroupWorldAsync(world, ct);

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "tribulation_start",
            Json("realm", player.RealmName, "expiresAt", newSession.ExpiresAt),
            null, Json("cultivationPoints", player.CultivationPoints)), ct);

        var announce = $"{AtMember(player.MemberOpenId, player.Nickname, player.GroupId)} 正在准备突破\n"
            + $"{Realm.NameOf(player.RealmIndex + 1)}！\n"
            + $"10分钟内群友可 @我 护法\n"
            + $"（每人 +{TribulationSession.ProtectorBonus:P0}，上限{TribulationSession.MaxProtectors}人）\n"
            + $"准备就绪后再次 @我 突破\n执行渡劫"
            + (idleSummary.Length > 0 ? $"\n{idleSummary}" : "");
        return announce;
    }

    private async Task<string> HandleProtectAsync(
        ICultivationRepository repo, PlayerProfile player, GroupWorld world, DateTime now, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        var session = TribulationSession.Deserialize(world.TribulationSessionJson);

        if (session is null || session.IsExpired)
        {
            // 清除过期会话
            if (session is not null)
            {
                world.TribulationSessionJson = null;
                await repo.SaveGroupWorldAsync(world, ct);
            }
            return idleSummary.Length > 0
                ? $"{idleSummary}\n当前无人渡劫，无法护法。"
                : "当前无人渡劫，无法护法。";
        }

        if (session.MemberOpenId == player.MemberOpenId)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n自己无法为自己护法\n再次 @我 突破 执行渡劫"
                : "自己无法为自己护法\n再次 @我 突破 执行渡劫";

        if (!session.CanAddProtector(player.MemberOpenId))
        {
            var protectorName = GameTextFormatter.DisplayName(session.Nickname, session.MemberOpenId);
            if (session.Protectors.Contains(player.MemberOpenId))
                return idleSummary.Length > 0
                    ? $"{idleSummary}\n你已为 {protectorName} 护法过了。"
                    : $"你已为 {protectorName} 护法过了。";
            if (session.Protectors.Count >= TribulationSession.MaxProtectors)
                return idleSummary.Length > 0
                    ? $"{idleSummary}\n护法名额已满（{TribulationSession.MaxProtectors}人）。"
                    : $"护法名额已满（{TribulationSession.MaxProtectors}人）。";
        }

        session.Protectors.Add(player.MemberOpenId);
        world.TribulationSessionJson = TribulationSession.Serialize(session);
        await repo.SaveGroupWorldAsync(world, ct);

        // 护法奖励：少量修为
        var reward = 50L * (player.RealmIndex + 1);
        player.CultivationPoints += reward;
        await repo.SavePlayerAsync(player, ct);

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "protect",
            Json("target", session.MemberOpenId, "totalProtectors", session.Protectors.Count),
            null, Json("cultivationPoints", player.CultivationPoints, "reward", reward)), ct);

        var remaining = session.ExpiresAt - now;
        var msg = $"你为 {AtMember(session.MemberOpenId, session.Nickname, player.GroupId)} 护法成功！"
            + $"\n当前护法 {session.Protectors.Count}/{TribulationSession.MaxProtectors} 人"
            + $"\n成功率加成 +{session.TotalBonus:P0}"
            + $"\n护法奖励：修为 +{reward:N0}"
            + $"\n渡劫剩余：{FormatRemaining(remaining)}"
            + (idleSummary.Length > 0 ? $"\n{idleSummary}" : "");
        return msg;
    }

    private async Task<string> HandleCheckinAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        var cd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "daily", ct);
        if (cd is not null && cd.Value > now)
            return (idleSummary.Length > 0 ? idleSummary + "\n" : "")
                + "今日已签到，明日0点刷新";

        var result = _dailyEngine.Checkin(player, now);
        var msg = _applier.ApplyCheckin(player, result, now);

        // 冷却设为 UTC+8 次日0点
        var nextMidnightUtc = now.AddHours(8).Date.AddDays(1).AddHours(-8);
        await repo.SetCooldownAsync(player.GroupId, player.MemberOpenId, "daily",
            nextMidnightUtc - now, ct);

        await repo.SavePlayerAsync(player, ct);
        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "daily",
            Json("streak", result.NewStreak),
            null,
            Json("spiritStones", player.SpiritStones, "cultivationPoints", player.CultivationPoints)), ct);

        msg = FlavorText.Decorate(msg, "checkin",
            $"{player.MemberOpenId}|{now:yyyyMMdd}|{result.NewStreak}");
        return idleSummary.Length > 0 ? $"{idleSummary}\n{msg}" : msg;
    }

    private async Task<string> HandleShopAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string args, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        // 无参数：展示商店
        if (string.IsNullOrWhiteSpace(args))
            return idleSummary.Length > 0
                ? $"{idleSummary}\n{GameTextFormatter.FormatShop(player.SpiritStones)}"
                : GameTextFormatter.FormatShop(player.SpiritStones);

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var itemName = parts[0];

        // 特殊指令：重置突破冷却
        if (itemName is "重置冷却" or "冷却")
        {
            if (player.SpiritStones < GameConstants.BtCooldownResetPrice)
                return idleSummary.Length > 0
                    ? $"{idleSummary}\n灵石不足\n重置突破冷却需 {GameConstants.BtCooldownResetPrice} 灵石\n当前持有 {player.SpiritStones:N0}"
                    : $"灵石不足\n重置突破冷却需 {GameConstants.BtCooldownResetPrice} 灵石\n当前持有 {player.SpiritStones:N0}";

            var cd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "breakthrough", ct);
            if (cd is null || cd.Value <= now)
                return idleSummary.Length > 0
                    ? $"{idleSummary}\n突破冷却已结束，无需重置。"
                    : "突破冷却已结束，无需重置。";

            player.SpiritStones -= GameConstants.BtCooldownResetPrice;
            await repo.SetCooldownAsync(player.GroupId, player.MemberOpenId, "breakthrough",
                TimeSpan.Zero, ct);
            await repo.SavePlayerAsync(player, ct);

            await repo.AppendEventLogAsync(new CultivationEventLog(
                player.GroupId, player.MemberOpenId, "shop",
                Json("item", "bt_cooldown_reset", "price", GameConstants.BtCooldownResetPrice),
                null, Json("spiritStones", player.SpiritStones)), ct);

            return idleSummary.Length > 0
                ? $"{idleSummary}\n已消耗 {GameConstants.BtCooldownResetPrice} 灵石\n突破冷却已重置"
                : $"已消耗 {GameConstants.BtCooldownResetPrice} 灵石\n突破冷却已重置";
        }

        // 材料购买（价格映射与出售共用）
        var (matId, matName, price) = ResolveShopMaterial(itemName);
        if (matId is null)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n未知商品「{itemName}」。@我 商店 查看可购买列表。"
                : $"未知商品「{itemName}」。@我 商店 查看可购买列表。";

        var qty = 1;
        if (parts.Length >= 2 && int.TryParse(parts[1], out var parsed) && parsed > 0)
            qty = parsed;

        var totalCost = price * qty;
        if (player.SpiritStones < totalCost)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n灵石不足\n购买 {matName}×{qty} 需 {totalCost} 灵石\n当前持有 {player.SpiritStones:N0}"
                : $"灵石不足\n购买 {matName}×{qty} 需 {totalCost} 灵石\n当前持有 {player.SpiritStones:N0}";

        player.SpiritStones -= totalCost;
        await repo.UpdateInventoryItemAsync(player.GroupId, player.MemberOpenId, matId!, "material",
            matName!, qty, ct);
        await repo.SavePlayerAsync(player, ct);

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "shop",
            Json("item", matId, "qty", qty, "price", totalCost),
            null, Json("spiritStones", player.SpiritStones)), ct);

        return idleSummary.Length > 0
            ? $"{idleSummary}\n购买成功：{matName} ×{qty}\n灵石 -{totalCost}，余 {player.SpiritStones:N0}"
            : $"购买成功：{matName} ×{qty}\n灵石 -{totalCost}，余 {player.SpiritStones:N0}";
    }

    /// <summary>商店材料价格映射，购买与出售共用。</summary>
    private static (string? MatId, string? MatName, long BuyPrice) ResolveShopMaterial(string name) => name switch
    {
        "灵草" or "lingcao" => ("lingcao", "灵草", GameConstants.ShopPriceLingcao),
        "药草" or "yaocao" => ("yaocao", "药草", GameConstants.ShopPriceYaocao),
        "灵矿" or "lingkuang" => ("lingkuang", "灵矿", GameConstants.ShopPriceLingkuang),
        "玄冰露" or "xuanbing_lu" => ("xuanbing_lu", "玄冰露", GameConstants.ShopPriceXuanbing),
        "火焰晶" or "huoyan_jing" => ("huoyan_jing", "火焰晶", GameConstants.ShopPriceHuoyan),
        _ => ((string?)null, (string?)null, 0L)
    };

    /// <summary>出售材料：回收价 = 购入价 × ShopSellPriceRatio，向下取整，无冷却（回收亏损，无刷取空间）。</summary>
    private async Task<string> HandleSellAsync(
        ICultivationRepository repo, PlayerProfile player, string args, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        if (string.IsNullOrWhiteSpace(args))
            return idleSummary.Length > 0
                ? $"{idleSummary}\n请指定材料名\n如：@我 出售 灵草 5\n@我 商店 查看回收价"
                : "请指定材料名\n如：@我 出售 灵草 5\n@我 商店 查看回收价";

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (matId, matName, buyPrice) = ResolveShopMaterial(parts[0]);

        if (matId is null)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n无法出售「{parts[0]}」\n@我 商店 查看可回收列表"
                : $"无法出售「{parts[0]}」\n@我 商店 查看可回收列表";

        var qty = 1;
        if (parts.Length >= 2 && int.TryParse(parts[1], out var parsed) && parsed > 0)
            qty = parsed;

        // 校验背包持有量，防止扣减被 Math.Max(0,…) 静默吞掉差额
        var inventory = await repo.GetInventoryAsync(player.GroupId, player.MemberOpenId, ct);
        var owned = inventory.FirstOrDefault(i => i.ItemId == matId)?.Quantity ?? 0;
        if (owned <= 0)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n你的背包中没有{matName}。"
                : $"你的背包中没有{matName}。";
        if (owned < qty)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n{matName}数量不足，持有 {owned}\n无法出售 {qty}"
                : $"{matName}数量不足，持有 {owned}\n无法出售 {qty}";

        var unitPrice = (long)Math.Floor(buyPrice * GameConstants.ShopSellPriceRatio);
        var totalGain = unitPrice * qty;

        player.SpiritStones += totalGain;
        await repo.UpdateInventoryItemAsync(player.GroupId, player.MemberOpenId, matId!, "material",
            matName!, -qty, ct);
        await repo.SavePlayerAsync(player, ct);

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "sell",
            Json("item", matId, "qty", qty, "price", totalGain, "unitPrice", unitPrice),
            null, Json("spiritStones", player.SpiritStones)), ct);

        return idleSummary.Length > 0
            ? $"{idleSummary}\n出售成功：{matName} ×{qty}\n灵石 +{totalGain}，余 {player.SpiritStones:N0}"
            : $"出售成功：{matName} ×{qty}\n灵石 +{totalGain}，余 {player.SpiritStones:N0}";
    }

    private async Task<string> HandleUseDanAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string args, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        if (string.IsNullOrWhiteSpace(args))
            return idleSummary.Length > 0
                ? $"{idleSummary}\n请指定丹药名，如：@我 使用 回气丹"
                : "请指定丹药名，如：@我 使用 回气丹";

        var recipe = Models.AlchemyRecipe.Find(args.Trim());
        if (recipe is null)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n未找到丹药：{args}"
                : $"未找到丹药：{args}";

        var inventory = await repo.GetInventoryAsync(player.GroupId, player.MemberOpenId, ct);
        var item = inventory.FirstOrDefault(i => i.ItemId == recipe.ItemId && i.ItemType == "dan");
        if (item is null || item.Quantity <= 0)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n背包中没有{recipe.Name}。"
                : $"背包中没有{recipe.Name}。";

        // 消耗丹药
        await repo.UpdateInventoryItemAsync(player.GroupId, player.MemberOpenId,
            recipe.ItemId, "dan", recipe.Name, -1, ct);

        // 应用效果
        var realmScale = Math.Pow(3.0, player.RealmIndex);
        long cultGain = 0;
        var extraMsg = "";

        switch (recipe.ItemId)
        {
            case "huiqi_dan": // 回气丹：即时修为
                cultGain = (long)Math.Round(50 * realmScale);
                break;
            case "zhuji_dan": // 筑基丹：突破成功率加成
                player.PendingBreakthroughBonus += 0.10;
                extraMsg = "下次突破成功率 +10%";
                break;
            case "pojie_dan": // 破界丹：大幅突破成功率加成
                player.PendingBreakthroughBonus += 0.20;
                extraMsg = "下次突破成功率 +20%";
                break;
            case "huoyan_dan": // 火焰丹：修为+永久挂机效率
                cultGain = (long)Math.Round(80 * realmScale);
                player.IdleRateMultiplier = Math.Min(2.0, player.IdleRateMultiplier + 0.05);
                extraMsg = "挂机效率永久 +5%";
                break;
            case "huichun_dan": // 回春丹：延寿（不额外加修为）
                player.Lifespan += 5;
                extraMsg = "寿元 +5 载";
                break;
            default:
                cultGain = (long)Math.Round(30 * realmScale);
                break;
        }

        player.CultivationPoints += cultGain;
        await repo.SavePlayerAsync(player, ct);

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "use_dan",
            Json("item", recipe.ItemId, "cultivation", cultGain,
                 "breakthroughBonus", player.PendingBreakthroughBonus),
            null, Json("cultivationPoints", player.CultivationPoints, "lifespan", player.Lifespan)), ct);

        var msg = cultGain > 0
            ? $"服用{recipe.Name}，修为 +{cultGain:N0}"
            : $"服用{recipe.Name}";
        if (extraMsg.Length > 0) msg += $"\n{extraMsg}";

        return idleSummary.Length > 0 ? $"{idleSummary}\n{msg}" : msg;
    }

    private async Task<string> HandlePracticeAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        // P0.3 寿元归零软惩罚：寿元耗尽时禁止修行
        if (player.Lifespan <= 0)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n寿元耗尽，无力闭关。\n需突破境界恢复寿元"
                : "寿元耗尽，无力闭关。\n需突破境界恢复寿元";

        var cd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "practice", ct);
        if (cd is not null && cd.Value > now)
            return (idleSummary.Length > 0 ? idleSummary + "\n" : "")
                + $"修行冷却中，剩 {FormatRemaining(cd.Value - now)}";

        var result = _dailyEngine.Practice(player, now);
        var msg = _applier.ApplyPractice(player, result);

        await repo.SetCooldownAsync(player.GroupId, player.MemberOpenId, "practice",
            TimeSpan.FromHours(GameConstants.PracticeCooldownHours), ct);

        await repo.SavePlayerAsync(player, ct);
        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "practice",
            Json("gained", result.GainedCultivation),
            null,
            Json("cultivationPoints", player.CultivationPoints)), ct);

        msg = FlavorText.Decorate(msg, "practice",
            $"{player.MemberOpenId}|{now:yyyyMMddHH}|{player.CultivationPoints}");
        return idleSummary.Length > 0 ? $"{idleSummary}\n{msg}" : msg;
    }

    private async Task<string> HandleInventoryAsync(
        ICultivationRepository repo, PlayerProfile player, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);
        var inv = await repo.GetInventoryAsync(player.GroupId, player.MemberOpenId, ct);
        var body = GameTextFormatter.FormatInventory(inv);
        return idleSummary.Length > 0 ? $"{idleSummary}\n{body}" : body;
    }

    private async Task<string> HandleAlchemyAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string args, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        // 无参数：列出可炼丹方
        if (string.IsNullOrWhiteSpace(args))
            return idleSummary.Length > 0 ? $"{idleSummary}\n{GameTextFormatter.FormatRecipes()}" : GameTextFormatter.FormatRecipes();

        var cd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "alchemy", ct);
        if (cd is not null && cd.Value > now)
            return (idleSummary.Length > 0 ? idleSummary + "\n" : "")
                + $"炼丹冷却中，剩 {FormatRemaining(cd.Value - now)}";

        var inv = await repo.GetInventoryAsync(player.GroupId, player.MemberOpenId, ct);
        var result = _alchemyEngine.Brew(player, inv, args, now);

        // success/failure 都消耗材料；insufficient/unknown 不消耗、不计冷却
        if (result.Outcome is "success" or "failure" && result.ItemId is not null)
        {
            var recipe = Models.AlchemyRecipe.Find(result.ItemId);
            if (recipe is not null)
            {
                foreach (var (matId, need) in recipe.Materials)
                    await repo.UpdateInventoryItemAsync(player.GroupId, player.MemberOpenId, matId, "material",
                        Models.AlchemyRecipe.MaterialName(matId), -need, ct);
            }

            if (result.Outcome == "success" && result.ItemId is not null)
                await repo.UpdateInventoryItemAsync(player.GroupId, player.MemberOpenId, result.ItemId, "dan",
                    result.ItemName ?? result.ItemId, result.OutputCount, ct);

            await repo.SetCooldownAsync(player.GroupId, player.MemberOpenId, "alchemy",
                TimeSpan.FromMinutes(GameConstants.AlchemyCooldownMinutes), ct);
        }

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "alchemy",
            Json("outcome", result.Outcome, "item", result.ItemId ?? "", "quality", result.Quality),
            null, Json("rolledChance", Math.Round(result.RolledChance, 3))), ct);

        var alchemyMsg = result.Message;
        if (result.Outcome is "success" or "failure")
            alchemyMsg = FlavorText.Decorate(alchemyMsg,
                result.Outcome == "success" ? "alchemy_success" : "alchemy_failure",
                $"{player.MemberOpenId}|{result.ItemId}|{now:yyyyMMddHHmm}");
        return idleSummary.Length > 0 ? $"{idleSummary}\n{alchemyMsg}" : alchemyMsg;
    }

    private async Task<string> HandleExploreAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        // P0.3 寿元归零软惩罚：寿元耗尽时禁止探索
        if (player.Lifespan <= 0)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n寿元耗尽，无力外出。\n需突破境界恢复寿元"
                : "寿元耗尽，无力外出。\n需突破境界恢复寿元";

        var cd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "explore", ct);
        if (cd is not null && cd.Value > now)
            return (idleSummary.Length > 0 ? idleSummary + "\n" : "")
                + $"探索冷却中，剩 {FormatRemaining(cd.Value - now)}";

        var result = _exploreEngine.Explore(player, now);

        // 应用确定性结果
        player.CultivationPoints = Math.Max(0, player.CultivationPoints + result.CultivationDelta);
        player.Lifespan = Math.Max(0, player.Lifespan + result.LifespanDelta);
        player.SpiritStones += result.StonesDelta;

        foreach (var (matId, qty) in result.Materials)
            await repo.UpdateInventoryItemAsync(player.GroupId, player.MemberOpenId, matId, "material",
                Models.AlchemyRecipe.MaterialName(matId), qty, ct);

        // 功法掉落：自动比较并更换
        var techniqueMsg = "";
        if (result.TechniqueId is not null)
        {
            if (Technique.IsBetter(result.TechniqueId, player.TechniqueId, player.SpiritRootType))
            {
                var oldName = player.TechniqueName;
                player.TechniqueId = result.TechniqueId;
                techniqueMsg = $"\n习得新功法：{Technique.NameOf(result.TechniqueId)}（替换{oldName}）";
            }
            else
            {
                techniqueMsg = $"\n发现{Technique.NameOf(result.TechniqueId)}，但不及当前功法，未更换。";
            }
        }

        await repo.SetCooldownAsync(player.GroupId, player.MemberOpenId, "explore",
            TimeSpan.FromHours(GameConstants.ExploreCooldownHours), ct);

        await repo.SavePlayerAsync(player, ct);
        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "explore",
            Json("title", result.Title, "type", result.Type, "cultivation", result.CultivationDelta, "lifespan", result.LifespanDelta,
                 "stones", result.StonesDelta, "technique", result.TechniqueId ?? ""),
            null, Json("cultivationPoints", player.CultivationPoints, "lifespan", player.Lifespan,
                       "techniqueId", player.TechniqueId ?? "")), ct);

        var fullMsg = result.Message + techniqueMsg;
        fullMsg = FlavorText.Decorate(fullMsg,
            result.Type == "opportunity" ? "explore_opportunity" : "explore_tribulation",
            $"{player.MemberOpenId}|{result.Title}|{now:yyyyMMddHH}");
        return idleSummary.Length > 0 ? $"{idleSummary}\n{fullMsg}" : fullMsg;
    }

    private async Task<string> HandleEventAsync(
        ICultivationRepository repo, PlayerProfile player, GroupWorld world, DateTime now, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        // 1. 已有未过期群事件 → 直接展示
        var pending = EventGenerator.Deserialize(world.PendingEventJson);
        if (pending is not null && (world.PendingEventExpiresAt is null || world.PendingEventExpiresAt > now))
        {
            var body = AiGamePrompts.RenderGeneratedEvent(pending);
            return idleSummary.Length > 0 ? $"{idleSummary}\n{body}" : body;
        }

        // 2. AI 已配置且满足最小间隔 → 生成新事件
        var sinceLast = now - world.LastEventGeneratedAt;
        if (sinceLast.TotalHours >= GameConstants.AiEventMinIntervalHours)
        {
            var generated = await _eventGenerator.GenerateAsync(player.GroupId, ct);
            if (generated is not null)
            {
                world.PendingEventJson = EventGenerator.Serialize(generated);
                world.PendingEventExpiresAt = now.AddHours(6);
                world.PendingEventParticipants = 0;
                world.LastEventGeneratedAt = now;
                await repo.SaveGroupWorldAsync(world, ct);

                await repo.AppendEventLogAsync(new CultivationEventLog(
                    player.GroupId, null, "event_generate",
                    Json("title", generated.Title, "difficulty", generated.Difficulty),
                    world.PendingEventJson, Json("source", "ai")), ct);

                var body = AiGamePrompts.RenderGeneratedEvent(generated);
                return idleSummary.Length > 0 ? $"{idleSummary}\n{body}" : body;
            }
        }

        // 3. 兜底：无事件可看，不触发探索（避免"事件"指令意外修改游戏数据）
        return idleSummary.Length > 0
            ? $"{idleSummary}\n天地平静，暂无奇遇。\n可尝试 @我 探索 外出历练"
            : "天地平静，暂无奇遇。\n可尝试 @我 探索 外出历练";
    }

    private async Task<string> HandleChooseAsync(
        ICultivationRepository repo, PlayerProfile player, GroupWorld world, DateTime now, string args, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        var pending = EventGenerator.Deserialize(world.PendingEventJson);
        if (pending is null || (world.PendingEventExpiresAt is not null && world.PendingEventExpiresAt <= now))
            return idleSummary.Length > 0 ? $"{idleSummary}\n当前无奇遇可供抉择。" : "当前无奇遇可供抉择。";

        // 解析选项序号
        if (!int.TryParse(args.Trim(), out var choice) || choice < 1 || choice > pending.Options.Count)
            return $"序号无效，可选 1-{pending.Options.Count}\n回复：@我 选 <序号>";

        var option = pending.Options[choice - 1];
        var difficulty = AiGamePrompts.ClampDifficulty(pending.Difficulty);

        // 确定性结算：基础成功率由难度+风险决定，种子化RNG
        var baseSuccess = option.Risk switch
        {
            "low" => 0.85,
            "high" => 0.35,
            "lethal" => 0.20,
            _ => 0.60
        };
        baseSuccess -= (difficulty - 1) * 0.08; // 难度越高越险
        baseSuccess += player.SpiritRootGrade <= 2 ? 0.05 : 0;
        baseSuccess = Math.Clamp(baseSuccess, 0.05, 0.95);

        var seed = DetSeed(player.MemberOpenId, pending.Title, choice, now);
        var rng = new Random(seed);
        var success = rng.NextDouble() < baseSuccess;

        // 奖励缩放：difficulty × 境界；失败时扣减少量修为（与失败叙事一致）
        var realmScale = player.RealmIndex + 1;
        var lifespanDelta = success ? 0 : -difficulty;
        long cultDelta;
        long stones;
        if (success)
        {
            cultDelta = difficulty * 50L * realmScale;
            stones = difficulty * 10;
        }
        else
        {
            cultDelta = -difficulty * 10L * realmScale;
            stones = 0;
        }

        player.CultivationPoints = Math.Max(0, player.CultivationPoints + cultDelta);
        player.Lifespan = Math.Max(0, player.Lifespan + lifespanDelta);
        player.SpiritStones += stones;

        // 偶发材料（成功时）
        if (success && rng.NextDouble() < 0.5)
        {
            var mats = new[] { "lingcao", "yaocao", "lingkuang" };
            var matId = mats[rng.Next(mats.Length)];
            var qty = rng.Next(1, 3);
            await repo.UpdateInventoryItemAsync(player.GroupId, player.MemberOpenId, matId, "material",
                Models.AlchemyRecipe.MaterialName(matId), qty, ct);
        }

        // 事件消费后清空（单次抉择），先保存原始 JSON 供审计
        var consumedEventJson = world.PendingEventJson;
        world.PendingEventJson = null;
        world.PendingEventExpiresAt = null;
        world.PendingEventParticipants++;
        await repo.SaveGroupWorldAsync(world, ct);
        await repo.SavePlayerAsync(player, ct);

        var outcome = success ? "成功" : "失败";
        var narr = success
            ? $"你选择了「{option.Label}」\n历经险阻，终得机缘！"
            : $"你选择了「{option.Label}」\n功败垂成，损了些修为。";

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "event_resolve",
            Json("title", pending.Title, "choice", choice, "outcome", outcome, "difficulty", difficulty),
            consumedEventJson,
            Json("cultivationPoints", player.CultivationPoints, "spiritStones", player.SpiritStones, "lifespan", player.Lifespan)), ct);

        var msg = $"{narr}\n修为 {(cultDelta >= 0 ? "+" : "")}{cultDelta:N0}" + (lifespanDelta < 0 ? $"，寿元 {lifespanDelta}" : "") + (stones > 0 ? $"，灵石 +{stones}" : "");
        return idleSummary.Length > 0 ? $"{idleSummary}\n{msg}" : msg;
    }

    /// <summary>
    /// 处理 @bot 行动/自由行动 前缀触发的自由文本动作（AI 裁判）。
    /// 由 MessageEventHandler 在 GameCommandRouter 命中"行动"前缀时调用。
    /// 返回 null 表示 AI 未配置，调用方应提示不可用。
    /// </summary>
    public async Task<GameUiReply?> HandleFreeActionAsync(
        string groupId, string memberOpenId, string nickname, string freeText, CancellationToken ct = default)
    {
        var groupLock = GroupLock(groupId);
        await groupLock.WaitAsync(ct);
        try
        {
            return await HandleFreeActionCoreAsync(groupId, memberOpenId, nickname, freeText, ct);
        }
        finally
        {
            groupLock.Release();
        }
    }

    private async Task<GameUiReply?> HandleFreeActionCoreAsync(
        string groupId, string memberOpenId, string nickname, string freeText, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();

            // 显式前缀触发，无存档时提示而非回退
            var existing = await repo.GetPlayerAsync(groupId, memberOpenId, ct);
            if (existing is null)
                return ComposeText(groupId, GameAction.FreeAction,
                    "你尚未踏入修仙之路\n发送「@bot 修仙」启程", nickname);

            var player = existing;
            var world = await repo.GetOrCreateGroupWorldAsync(groupId, ct);
            var now = DateTime.UtcNow;

            // 先惰性结算挂机
            var idle = _idleEngine.Settle(player, world, now);
            var idleSummary = _applier.ApplyIdle(player, idle);
            await repo.SavePlayerAsync(player, ct);

            // 裁判冷却防刷
            var cd = await repo.GetCooldownAsync(groupId, memberOpenId, "referee", ct);
            if (cd is not null && cd.Value > now)
                return ComposeText(groupId, GameAction.FreeAction,
                    (idleSummary.Length > 0 ? idleSummary + "\n" : "")
                    + $"天道沉思中，{FormatRemaining(cd.Value - now)}后再试", nickname);

            var sceneCtx = EventGenerator.Deserialize(world.PendingEventJson)?.Title;
            var outcome = await _referee.JudgeAsync(player, freeText, sceneCtx, ct);

            if (outcome is null)
            {
                // AI 未配置或失败 → 返回 null，调用方回退到普通 AI 聊天
                return null;
            }

            var v = outcome.Verdict;
            // 应用钳制后的修为变化
            player.CultivationPoints = Math.Max(0, player.CultivationPoints + outcome.ActualCultivationDelta);

            // statusEffect 简化处理
            var effect = v.SuggestedModifiers?.StatusEffect;
            if (effect == "injured") player.Lifespan = Math.Max(0, player.Lifespan - 1);

            await repo.SetCooldownAsync(groupId, memberOpenId, "referee",
                TimeSpan.FromMinutes(GameConstants.RefereeCooldownMinutes), ct);
            await repo.SavePlayerAsync(player, ct);

            // 审计日志：记录 AI 原始输出 + 实际应用变更（防作弊可回溯）
            await repo.AppendEventLogAsync(new CultivationEventLog(
                groupId, memberOpenId, "ai_referee",
                Json("action", freeText, "feasible", v.Feasible, "outcome", v.OutcomeType, "risk", v.RiskLevel),
                System.Text.Json.JsonSerializer.Serialize(v, JsonOpts), // AiRawOutput
                Json("rawDelta", Math.Round(outcome.RawDelta, 4),
                     "clampedDelta", Math.Round(outcome.ClampedDelta, 4),
                     "actualCultivationDelta", outcome.ActualCultivationDelta,
                     "cultivationPoints", player.CultivationPoints)), ct);

            var prefix = idleSummary.Length > 0 ? idleSummary + "\n" : "";
            var body = v.Narrative
                + $"\n（修为 {outcome.ActualCultivationDelta:+#;-#;0}）";
            return ComposeText(groupId, GameAction.FreeAction, prefix + body, nickname) with
            {
                AiModelUsed = outcome.AiModelUsed,
                PromptTokens = outcome.PromptTokens,
                CompletionTokens = outcome.CompletionTokens,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Free action referee failed group={GroupId}", groupId);
            return GameUiReply.Text("天道动荡，判定失败，请稍后再试。");
        }
    }

    /// <summary>无面板的短回复（冷却提示、AI 叙事等）统一走呈现器，保证折行与菜单行为一致。</summary>
    private GameUiReply ComposeText(string groupId, GameAction action, string plain, string nickname) =>
        _ui.Compose(groupId, action, plain, MenuContext.Empty, nickname);

    /// <summary>
    /// 把玩家渲染为可点 @ 标签（富交互关闭时退回文本道号）。
    /// 用于需要“看到就是在叫你”的场景：渡劫公告、护法回报、切磋对阵。
    /// </summary>
    private string AtMember(string memberOpenId, string nickname, string groupId) =>
        GameKeyboard.AtUser(memberOpenId, GameTextFormatter.DisplayName(nickname, memberOpenId),
            _ui.Capability.UseAtTags(groupId));

    private static int DetSeed(string memberOpenId, string title, int choice, DateTime now)
    {
        var key = $"{memberOpenId}|{title}|{choice}|{now:yyyy-MM-dd}";
        var hash = 5381;
        foreach (var c in key)
            hash = ((hash << 5) + hash) ^ c;
        return hash & 0x7FFFFFFF;
    }

    /// <summary>
    /// 切磋：发起即结算的纯计算对决。对手解析降级链：昵称匹配 → 随机群友 → 幻影NPC。
    /// 败者零损失；彩头小额且随境界缩放；30min 冷却防刷。
    /// </summary>
    private async Task<string> HandleSparAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string args, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        var cd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "spar", ct);
        if (cd is not null && cd.Value > now)
            return (idleSummary.Length > 0 ? idleSummary + "\n" : "")
                + $"切磋冷却中，剩 {FormatRemaining(cd.Value - now)}";

        // 对手解析：昵称前缀/包含匹配 → 随机群友 → 幻影（独苗群保底可玩）
        var members = await repo.GetGroupPlayersAsync(player.GroupId, ct);
        var others = members.Where(m => m.MemberOpenId != player.MemberOpenId).ToList();
        var targetName = args.Trim();
        PlayerProfile opponent;
        var isPhantom = false;

        if (!string.IsNullOrEmpty(targetName))
        {
            var match = others.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.Nickname) && m.Nickname.StartsWith(targetName, StringComparison.Ordinal))
                ?? others.FirstOrDefault(m =>
                    !string.IsNullOrEmpty(m.Nickname) && m.Nickname.Contains(targetName));
            if (match is null)
                return idleSummary.Length > 0
                    ? $"{idleSummary}\n未找到名为「{targetName}」的道友。"
                    : $"未找到名为「{targetName}」的道友。";
            opponent = (await repo.GetPlayerAsync(player.GroupId, match.MemberOpenId, ct))!;
        }
        else if (others.Count > 0)
        {
            // 确定性挑人：同小时同一对手，与切磋掷点种子对齐避免刷对手挑软柿子…实际上掷点已含双方 id，随机挑人仅影响对手选择，用确定性种子保证可复现。
            var pick = new Random(DetSeed(player.MemberOpenId, "spar_pick", 0, now)).Next(others.Count);
            opponent = (await repo.GetPlayerAsync(player.GroupId, others[pick].MemberOpenId, ct))!;
        }
        else
        {
            // 幻影：镜像自身面板的修行幻象，单人也可玩；MemberOpenId 用固定串参与种子（可复现）
            opponent = new PlayerProfile
            {
                GroupId = player.GroupId,
                MemberOpenId = "phantom",
                Nickname = "幻影道人",
                RealmIndex = player.RealmIndex,
                SpiritRootType = player.SpiritRootType,
                SpiritRootGrade = player.SpiritRootGrade,
                TechniqueId = player.TechniqueId
            };
            isPhantom = true;
        }

        var result = _sparEngine.Duel(player, opponent, player.GroupId, now);

        // 应用彩头：胜者加修为（幻影不落库）
        if (result.ChallengerWins)
        {
            player.CultivationPoints += result.CultDelta;
        }
        else if (!isPhantom)
        {
            opponent.CultivationPoints += result.CultDelta;
            await repo.SavePlayerAsync(opponent, ct);
        }

        await repo.SetCooldownAsync(player.GroupId, player.MemberOpenId, "spar",
            TimeSpan.FromMinutes(SparEngine.CooldownMinutes), ct);
        await repo.SavePlayerAsync(player, ct);

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "spar",
            Json("opponent", opponent.MemberOpenId, "phantom", isPhantom,
                 "winner", result.WinnerOpenId, "cultDelta", result.CultDelta),
            null, Json("cultivationPoints", player.CultivationPoints)), ct);

        var opponentDisplay = isPhantom ? opponent.Nickname
            : GameKeyboard.AtUser(opponent.MemberOpenId, GameTextFormatter.DisplayName(opponent),
                _ui.Capability.UseAtTags(player.GroupId));
        var msg = $"【切磋】{GameTextFormatter.DisplayName(player)} 挑战 {opponentDisplay}\n"
            + result.Narrative
            + $"\n胜者：{result.WinnerName}"
            + $"\n彩头：修为 +{result.CultDelta:N0}"
            + $"\n{result.Consolation}";
        return idleSummary.Length > 0 ? $"{idleSummary}\n{msg}" : msg;
    }

    /// <summary>
    /// 每日任务：进度借审计日志当日计数推断，全部完成后首次进入自动领奖；
    /// 领奖防重借通用冷却表（当日键），零新表零新列。
    /// </summary>
    private async Task<string> HandleQuestAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        var quests = Models.DailyQuests.GetDaily(player.GroupId, now);
        var counts = await repo.GetPlayerActionCountsAsync(
            player.GroupId, player.MemberOpenId, Models.DailyQuests.DayStartUtc(now), ct);

        var states = quests
            .Select(q => (Quest: q, Progress: counts.TryGetValue(q.EventType, out var n) ? n : 0))
            .ToList();
        var allDone = states.All(s => s.Progress >= s.Quest.Target);

        var claimKey = Models.DailyQuests.ClaimCooldownKey(now);
        var claimedCd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, claimKey, ct);
        var claimed = claimedCd is not null && claimedCd.Value > now;
        var justClaimed = false;

        if (allDone && !claimed)
        {
            player.SpiritStones += Models.DailyQuests.RewardStones;
            var nextMidnightUtc = now.AddHours(8).Date.AddDays(1).AddHours(-8);
            await repo.SetCooldownAsync(player.GroupId, player.MemberOpenId, claimKey,
                nextMidnightUtc - now, ct);
            await repo.SavePlayerAsync(player, ct);

            await repo.AppendEventLogAsync(new CultivationEventLog(
                player.GroupId, player.MemberOpenId, "quest_claim",
                Json("day", now.AddHours(8).ToString("yyyyMMdd"), "stones", Models.DailyQuests.RewardStones),
                null, Json("spiritStones", player.SpiritStones)), ct);

            claimed = true;
            justClaimed = true;
        }

        var body = GameTextFormatter.FormatQuest(
            states.Select(s => (s.Quest, s.Progress)).ToList(),
            allDone, claimed, justClaimed, Models.DailyQuests.RewardStones);
        if (justClaimed)
            body = FlavorText.Decorate(body, "quest_done",
                $"{player.MemberOpenId}|quest|{now.AddHours(8):yyyyMMdd}");
        return idleSummary.Length > 0 ? $"{idleSummary}\n{body}" : body;
    }

    /// <summary>
    /// AI 突破批语：仅突破成功时调用，15s 硬超时，任何失败/未配置静默回退静态风味池。
    /// </summary>
    private async Task<string?> TryGetAiBlessingAsync(PlayerProfile player, CancellationToken ct)
    {
        try
        {
            if (!await _structured.IsAiConfiguredAsync(ct)) return null;

            using var cfgScope = _scopeFactory.CreateScope();
            var cfgRepo = cfgScope.ServiceProvider.GetRequiredService<QQBot.Core.Interfaces.IConfigRepository>();
            var aiConfig = await cfgRepo.GetAiConfigAsync(ct);
            if (aiConfig is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var systemPrompt = "你是修仙世界的天道。为刚刚突破成功的修士写一句批语或预言，"
                + "不超过20字，古风凝练，不要引号与解释。仅输出 JSON：{\"blessing\": \"...\"}";
            var userPrompt = $"修士突破至 {Realm.NameOf(player.RealmIndex)} 境界，道号 {GameTextFormatter.DisplayName(player)}。";

            var result = await _structured.GetStructuredAsync<BlessingDto>(systemPrompt, userPrompt, aiConfig, cts.Token);
            if (result is null) return null;

            var blessing = (result.Data.Blessing ?? string.Empty).Trim().Trim('"', '“', '”');
            if (blessing.Length == 0) return null;
            return blessing.Length > 30 ? blessing[..30] : blessing; // 硬截断防长输出刷屏
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AI blessing failed, fallback to flavor pool");
            return null;
        }
    }

    private class BlessingDto
    {
        public string Blessing { get; set; } = string.Empty;
    }

    private static string FormatRemaining(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}时{ts.Minutes}分";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}分";
        return $"{(int)ts.TotalSeconds}秒";
    }

    private static string Json(params object[] kv)
    {
        var dict = new Dictionary<string, object?>();
        for (var i = 0; i + 1 < kv.Length; i += 2)
            dict[kv[i].ToString() ?? $"k{i}"] = kv[i + 1];
        return JsonSerializer.Serialize(dict, JsonOpts);
    }
}
