using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Game.Ai;
using QQBot.Core.Game.Engines;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;

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
    private readonly EventApplier _applier;
    private readonly EventGenerator _eventGenerator;
    private readonly ActionReferee _referee;
    private readonly ILogger<CultivationGameService> _logger;

    /// <summary>
    /// 按 (groupId, memberOpenId) 串行化同一玩家的游戏指令，避免读-改-写 PlayerProfiles
    /// 时的并发整行覆盖（丢失更新，典型表现：突破成功后被并发指令用旧值覆盖回去）。
    /// 不同玩家互不阻塞，仅同一玩家的指令串行执行。
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _playerLocks = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public CultivationGameService(
        IServiceScopeFactory scopeFactory,
        IdleSettlementEngine idleEngine,
        BreakthroughEngine breakthroughEngine,
        DailyTaskEngine dailyEngine,
        AlchemyEngine alchemyEngine,
        ExploreEngine exploreEngine,
        EventApplier applier,
        EventGenerator eventGenerator,
        ActionReferee referee,
        ILogger<CultivationGameService> logger)
    {
        _scopeFactory = scopeFactory;
        _idleEngine = idleEngine;
        _breakthroughEngine = breakthroughEngine;
        _dailyEngine = dailyEngine;
        _alchemyEngine = alchemyEngine;
        _exploreEngine = exploreEngine;
        _applier = applier;
        _eventGenerator = eventGenerator;
        _referee = referee;
        _logger = logger;
    }

    public async Task<string> HandleAsync(
        string groupId, string memberOpenId, string nickname, GameCommand command, CancellationToken ct = default)
    {
        // 帮助：纯静态文本，无需存档与串行化
        if (command.Action == GameAction.Help)
            return GameTextFormatter.FormatHelp();

        // 排行：只读，不修改存档，无需按玩家串行化
        if (command.Action == GameAction.Leaderboard)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
                var entries = await repo.GetGroupLeaderboardAsync(groupId, 10, ct);
                return GameTextFormatter.FormatLeaderboard(entries);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cultivation game handle failed group={GroupId} action={Action}", groupId, command.Action);
                return "天道动荡，操作失败，请稍后再试。";
            }
        }

        // 其余动作会读-改-写 PlayerProfiles，按 (groupId, memberOpenId) 串行化，
        // 防止同一玩家的并发指令互相用旧值整行覆盖（如突破被回退）造成丢失更新。
        var playerKey = $"{groupId}:{memberOpenId}";
        var gate = _playerLocks.GetOrAdd(playerKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();

            // 获取/创建存档
            var existing = await repo.GetPlayerAsync(groupId, memberOpenId, ct);
            var isNew = existing is null;
            var player = isNew
                ? await repo.GetOrCreatePlayerAsync(groupId, memberOpenId, nickname, ct)
                : existing!;
            var world = await repo.GetOrCreateGroupWorldAsync(groupId, ct);
            var now = DateTime.UtcNow;

            // 新玩家首次查看面板 → 欢迎语
            if (isNew && command.Action == GameAction.Status)
            {
                await repo.AppendEventLogAsync(new CultivationEventLog(
                    groupId, memberOpenId, "create",
                    Json("action", "create", "root", player.SpiritRootName, "grade", player.SpiritRootGrade),
                    null, Json("realmIndex", 0)), ct);
                return GameTextFormatter.FormatNewPlayer(player);
            }

            // 每次访问先惰性结算挂机
            var idle = _idleEngine.Settle(player, world, now);
            var idleSummary = _applier.ApplyIdle(player, idle);

            return command.Action switch
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
                GameAction.UseDan => await HandleUseDanAsync(repo, player, now, command.Args, idleSummary, ct),
                _ => GameTextFormatter.FormatHelp()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cultivation game handle failed group={GroupId} action={Action}", groupId, command.Action);
            return "天道动荡，操作失败，请稍后再试。";
        }
        finally
        {
            gate.Release();
        }
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
                return $"突破尚在冷却中，剩余 {FormatRemaining(cd.Value - now)}。\n（{idleSummary}）";

            // 消耗丹药积累的突破加成
            var pillBonus = player.PendingBreakthroughBonus;
            var totalBonus = session.TotalBonus + pillBonus;
            var result = _breakthroughEngine.Attempt(player, now, totalBonus);
            var msg = _applier.ApplyBreakthrough(player, result);

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
                return idleSummary.Length > 0
                    ? $"{idleSummary}\n{session.Nickname}正在渡劫中，道友可 @我 护法 助其一臂之力！"
                    : $"{session.Nickname}正在渡劫中，道友可 @我 护法 助其一臂之力！";
            }
        }

        // 前置检查：修为是否足够（提前提示，避免创建无意义会话）
        if (Realm.IsMax(player.RealmIndex))
            return idleSummary.Length > 0
                ? $"{idleSummary}\n已达渡劫之巅，无更高境界可破。"
                : "已达渡劫之巅，无更高境界可破。";

        if (player.CultivationPoints < BreakthroughEngine.ThresholdFor(player.RealmIndex))
        {
            var need = BreakthroughEngine.ThresholdFor(player.RealmIndex);
            return idleSummary.Length > 0
                ? $"{idleSummary}\n修为不足，突破需 {need:N0} 修为（当前 {player.CultivationPoints:N0}）。"
                : $"修为不足，突破需 {need:N0} 修为（当前 {player.CultivationPoints:N0}）。";
        }

        // 创建新的渡劫会话
        var newSession = new TribulationSession
        {
            MemberOpenId = player.MemberOpenId,
            Nickname = player.Nickname,
            StartedAt = now,
            ExpiresAt = now.AddMinutes(10)
        };
        world.TribulationSessionJson = TribulationSession.Serialize(newSession);
        await repo.SaveGroupWorldAsync(world, ct);

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "tribulation_start",
            Json("realm", player.RealmName, "expiresAt", newSession.ExpiresAt),
            null, Json("cultivationPoints", player.CultivationPoints)), ct);

        var announce = $"{player.Nickname}正在准备突破 {Realm.NameOf(player.RealmIndex + 1)}！"
            + $"\n10分钟内群友可 @我 护法 助阵（每人 +{TribulationSession.ProtectorBonus:P0} 成功率，上限{TribulationSession.MaxProtectors}人）。"
            + $"\n准备就绪后再次 @我 突破 即可渡劫。"
            + (idleSummary.Length > 0 ? $"\n（{idleSummary}）" : "");
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
                ? $"{idleSummary}\n自己渡劫无法为自己护法，请再次 @我 突破 执行渡劫。"
                : "自己渡劫无法为自己护法，请再次 @我 突破 执行渡劫。";

        if (!session.CanAddProtector(player.MemberOpenId))
        {
            if (session.Protectors.Contains(player.MemberOpenId))
                return idleSummary.Length > 0
                    ? $"{idleSummary}\n你已为 {session.Nickname} 护法过了。"
                    : $"你已为 {session.Nickname} 护法过了。";
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
        var msg = $"你为 {session.Nickname} 护法成功！"
            + $"\n当前护法 {session.Protectors.Count}/{TribulationSession.MaxProtectors} 人，成功率加成 +{session.TotalBonus:P0}"
            + $"\n护法奖励：修为 +{reward:N0}"
            + $"\n渡劫剩余时间：{FormatRemaining(remaining)}"
            + (idleSummary.Length > 0 ? $"\n（{idleSummary}）" : "");
        return msg;
    }

    private async Task<string> HandleCheckinAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        var cd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "daily", ct);
        if (cd is not null && cd.Value > now)
            return $"今日已签到，明日0点刷新。\n（{idleSummary}）";

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
                    ? $"{idleSummary}\n灵石不足，重置突破冷却需 {GameConstants.BtCooldownResetPrice} 灵石（当前 {player.SpiritStones:N0}）。"
                    : $"灵石不足，重置突破冷却需 {GameConstants.BtCooldownResetPrice} 灵石（当前 {player.SpiritStones:N0}）。";

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
                ? $"{idleSummary}\n消耗 {GameConstants.BtCooldownResetPrice} 灵石，突破冷却已重置。"
                : $"消耗 {GameConstants.BtCooldownResetPrice} 灵石，突破冷却已重置。";
        }

        // 材料购买
        var (matId, matName, price) = itemName switch
        {
            "灵草" or "lingcao" => ("lingcao", "灵草", GameConstants.ShopPriceLingcao),
            "药草" or "yaocao" => ("yaocao", "药草", GameConstants.ShopPriceYaocao),
            "灵矿" or "lingkuang" => ("lingkuang", "灵矿", GameConstants.ShopPriceLingkuang),
            "玄冰露" or "xuanbing_lu" => ("xuanbing_lu", "玄冰露", GameConstants.ShopPriceXuanbing),
            "火焰晶" or "huoyan_jing" => ("huoyan_jing", "火焰晶", GameConstants.ShopPriceHuoyan),
            _ => ((string?)null, (string?)null, 0L)
        };

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
                ? $"{idleSummary}\n灵石不足，购买 {matName}×{qty} 需 {totalCost} 灵石（当前 {player.SpiritStones:N0}）。"
                : $"灵石不足，购买 {matName}×{qty} 需 {totalCost} 灵石（当前 {player.SpiritStones:N0}）。";

        player.SpiritStones -= totalCost;
        await repo.UpdateInventoryItemAsync(player.GroupId, player.MemberOpenId, matId!, "material",
            matName!, qty, ct);
        await repo.SavePlayerAsync(player, ct);

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "shop",
            Json("item", matId, "qty", qty, "price", totalCost),
            null, Json("spiritStones", player.SpiritStones)), ct);

        return idleSummary.Length > 0
            ? $"{idleSummary}\n购买成功：{matName}×{qty}，消耗灵石 {totalCost}（剩余 {player.SpiritStones:N0}）。"
            : $"购买成功：{matName}×{qty}，消耗灵石 {totalCost}（剩余 {player.SpiritStones:N0}）。";
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
        int lifespanGain = 0;
        var extraMsg = "";

        switch (recipe.ItemId)
        {
            case "huiqi_dan": // 回气丹：即时修为
                cultGain = (long)Math.Round(50 * realmScale);
                break;
            case "zhuji_dan": // 筑基丹：突破成功率加成
                player.PendingBreakthroughBonus += 0.10;
                extraMsg = "，下次突破成功率 +10%";
                break;
            case "pojie_dan": // 破界丹：大幅突破成功率加成
                player.PendingBreakthroughBonus += 0.20;
                extraMsg = "，下次突破成功率 +20%";
                break;
            case "huoyan_dan": // 火焰丹：修为+永久挂机效率
                cultGain = (long)Math.Round(80 * realmScale);
                player.IdleRateMultiplier = Math.Min(2.0, player.IdleRateMultiplier + 0.05);
                extraMsg = "，挂机效率永久+5%";
                break;
            default:
                cultGain = (long)Math.Round(30 * realmScale);
                break;
        }

        player.CultivationPoints += cultGain;
        player.Lifespan += lifespanGain;
        await repo.SavePlayerAsync(player, ct);

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "use_dan",
            Json("item", recipe.ItemId, "cultivation", cultGain, "lifespan", lifespanGain,
                 "breakthroughBonus", player.PendingBreakthroughBonus),
            null, Json("cultivationPoints", player.CultivationPoints, "lifespan", player.Lifespan)), ct);

        var msg = cultGain > 0
            ? $"服用{recipe.Name}，修为 +{cultGain:N0}"
            : $"服用{recipe.Name}";
        if (lifespanGain > 0) msg += $"，寿元 +{lifespanGain}";
        msg += extraMsg;

        return idleSummary.Length > 0 ? $"{idleSummary}\n{msg}" : msg;
    }

    private async Task<string> HandlePracticeAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        // P0.3 寿元归零软惩罚：寿元耗尽时禁止修行
        if (player.Lifespan <= 0)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n寿元耗尽，无力闭关修行。需突破境界恢复寿元。"
                : "寿元耗尽，无力闭关修行。需突破境界恢复寿元。";

        var cd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "practice", ct);
        if (cd is not null && cd.Value > now)
            return $"修行尚在冷却中，剩余 {FormatRemaining(cd.Value - now)}。\n（{idleSummary}）";

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
            return $"炼丹尚在冷却中，剩余 {FormatRemaining(cd.Value - now)}。\n（{idleSummary}）";

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

        return idleSummary.Length > 0 ? $"{idleSummary}\n{result.Message}" : result.Message;
    }

    private async Task<string> HandleExploreAsync(
        ICultivationRepository repo, PlayerProfile player, DateTime now, string idleSummary, CancellationToken ct)
    {
        await repo.SavePlayerAsync(player, ct);

        // P0.3 寿元归零软惩罚：寿元耗尽时禁止探索
        if (player.Lifespan <= 0)
            return idleSummary.Length > 0
                ? $"{idleSummary}\n寿元耗尽，无力外出探索。需突破境界恢复寿元后再行历练。"
                : "寿元耗尽，无力外出探索。需突破境界恢复寿元后再行历练。";

        var cd = await repo.GetCooldownAsync(player.GroupId, player.MemberOpenId, "explore", ct);
        if (cd is not null && cd.Value > now)
            return $"探索尚在冷却中，剩余 {FormatRemaining(cd.Value - now)}。\n（{idleSummary}）";

        var result = _exploreEngine.Explore(player, now);

        // 应用确定性结果
        player.CultivationPoints = Math.Max(0, player.CultivationPoints + result.CultivationDelta);
        player.Lifespan = Math.Max(0, player.Lifespan + result.LifespanDelta);

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
                 "technique", result.TechniqueId ?? ""),
            null, Json("cultivationPoints", player.CultivationPoints, "lifespan", player.Lifespan,
                       "techniqueId", player.TechniqueId ?? "")), ct);

        var fullMsg = result.Message + techniqueMsg;
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
            ? $"{idleSummary}\n天地平静，暂无奇遇。可尝试 @我 探索 外出历练。"
            : "天地平静，暂无奇遇。可尝试 @我 探索 外出历练。";
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
            return $"请输入有效序号（1-{pending.Options.Count}）：@我 选 <序号>";

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

        // 奖励缩放：difficulty × 境界
        var realmScale = player.RealmIndex + 1;
        var cultReward = (long)Math.Round(difficulty * 50L * realmScale * (success ? 1.0 : 0.2));
        var lifespanDelta = success ? 0 : -difficulty;
        long stones = success ? difficulty * 10 : 0;

        player.CultivationPoints += cultReward;
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
            ? $"你选择了「{option.Label}」，历经险阻终得机缘！"
            : $"你选择了「{option.Label}」，可惜功败垂成，损了些修为。";

        await repo.AppendEventLogAsync(new CultivationEventLog(
            player.GroupId, player.MemberOpenId, "event_resolve",
            Json("title", pending.Title, "choice", choice, "outcome", outcome, "difficulty", difficulty),
            consumedEventJson,
            Json("cultivationPoints", player.CultivationPoints, "spiritStones", player.SpiritStones, "lifespan", player.Lifespan)), ct);

        var msg = $"{narr}\n修为 +{cultReward:N0}" + (lifespanDelta < 0 ? $"，寿元 {lifespanDelta}" : "") + (stones > 0 ? $"，灵石 +{stones}" : "");
        return idleSummary.Length > 0 ? $"{idleSummary}\n{msg}" : msg;
    }

    /// <summary>
    /// 处理 @bot 行动/自由行动 前缀触发的自由文本动作（AI 裁判）。
    /// 由 MessageEventHandler 在 GameCommandRouter 命中"行动"前缀时调用。
    /// 返回 null 表示 AI 未配置，调用方应提示不可用。
    /// </summary>
    public async Task<GameReply?> HandleFreeActionAsync(
        string groupId, string memberOpenId, string nickname, string freeText, CancellationToken ct = default)
    {
        // 自由行动同样会读-改-写 PlayerProfiles，与确定性指令共用同一把玩家锁，
        // 避免自由行动与普通指令并发导致整行覆盖回退。
        var playerKey = $"{groupId}:{memberOpenId}";
        var gate = _playerLocks.GetOrAdd(playerKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();

            // 显式前缀触发，无存档时提示而非回退
            var existing = await repo.GetPlayerAsync(groupId, memberOpenId, ct);
            if (existing is null)
                return new GameReply("你尚未踏入修仙之路，发送「@bot 修仙」开始你的旅程。");

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
                return new GameReply($"天道沉思中，{FormatRemaining(cd.Value - now)}后再来尝试自由行动。\n（{idleSummary}）");

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
            return new GameReply(
                prefix + v.Narrative
                + $"\n（修为 {outcome.ActualCultivationDelta:+#;-#;0}）",
                outcome.AiModelUsed,
                outcome.PromptTokens,
                outcome.CompletionTokens);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Free action referee failed group={GroupId}", groupId);
            return new GameReply("天道动荡，判定失败，请稍后再试。");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ApplyExploreResultAsync(PlayerProfile player, ExploreResult result, ICultivationRepository repo, CancellationToken ct)
    {
        player.CultivationPoints = Math.Max(0, player.CultivationPoints + result.CultivationDelta);
        player.Lifespan = Math.Max(0, player.Lifespan + result.LifespanDelta);
        foreach (var (matId, qty) in result.Materials)
            await repo.UpdateInventoryItemAsync(player.GroupId, player.MemberOpenId, matId, "material",
                Models.AlchemyRecipe.MaterialName(matId), qty, ct);
    }

    private static int DetSeed(string memberOpenId, string title, int choice, DateTime now)
    {
        var key = $"{memberOpenId}|{title}|{choice}|{now:yyyy-MM-dd}";
        var hash = 5381;
        foreach (var c in key)
            hash = ((hash << 5) + hash) ^ c;
        return hash & 0x7FFFFFFF;
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
