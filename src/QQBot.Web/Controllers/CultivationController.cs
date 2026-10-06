using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;
using QQBot.Core.Game.Services;
using QQBot.Core.Game.Ui;
using QQBot.Web.Services;

namespace QQBot.Web.Controllers;

[ApiController]
[Route("api/cultivation")]
public class CultivationController : ControllerBase
{
    private const string SessionCookieName = "qqbot_panel_session";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PanelSessionStore _sessions;

    public CultivationController(IServiceScopeFactory scopeFactory, PanelSessionStore sessions)
    {
        _scopeFactory = scopeFactory;
        _sessions = sessions;
    }

    /// <summary>与管理面板共用会话 Cookie 鉴权，防止匿名拉取玩家 OpenId、昵称与 AI 原始输出。</summary>
    private bool IsAuthenticated()
    {
        var token = Request.Cookies[SessionCookieName];
        return !string.IsNullOrEmpty(token) && _sessions.IsValid(token);
    }

    /// <summary>富交互（markdown / 按钮 / @标签）的配置与运行态，只读。</summary>
    [HttpGet("ui/capabilities")]
    public ActionResult GetUiCapabilities()
    {
        if (!IsAuthenticated()) return Unauthorized(new { error = "未登录或会话已过期" });

        using var scope = _scopeFactory.CreateScope();
        var cap = scope.ServiceProvider.GetRequiredService<RichUiCapability>();
        var snap = cap.Snapshot();
        var opt = cap.Options;

        return Ok(new
        {
            configEnabled = snap.ConfigEnabled,
            markdownAllowedByConfig = snap.MarkdownAllowedByConfig,
            configuredKeyboardMode = snap.ConfiguredKeyboardMode.ToString(),
            markdownState = snap.MarkdownState,
            keyboardState = snap.KeyboardState,
            markdownDeniedCode = snap.MarkdownDeniedCode,
            keyboardDeniedCode = snap.KeyboardDeniedCode,
            markdownDeniedAt = snap.MarkdownDeniedAt,
            keyboardDeniedAt = snap.KeyboardDeniedAt,
            interactionIntent = opt.InteractionIntent,
            cmdInputLinks = opt.CmdInputLinks,
            atUserTags = opt.AtUserTags,
            messageReference = opt.MessageReference,
            capabilityTtlMinutes = opt.CapabilityTtlMinutes,
            groupAllowList = opt.GroupAllowList,
            counters = new
            {
                rich = snap.RichSent,
                markdown = snap.MarkdownSent,
                keyboard = snap.KeyboardSent,
                links = snap.LinkOnlySent,
                plainFallback = snap.PlainFallback,
                degradeRetries = snap.DegradeRetries,
            },
            hint = "面板实探：在群里由群主/管理员发送 @我 /ui，会本条发回 markdown 与按钮两条探测消息并回报结果。",
        });
    }

    [HttpGet("leaderboard/{groupId}")]
    public async Task<ActionResult> GetLeaderboard(string groupId, CancellationToken ct)
    {
        if (!IsAuthenticated()) return Unauthorized(new { error = "未登录或会话已过期" });

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        var entries = await repo.GetGroupLeaderboardAsync(groupId, 20, ct);
        var result = entries.Select(e => new
        {
            e.MemberOpenId,
            Nickname = GameTextFormatter.DisplayName(e.Nickname, e.MemberOpenId),
            e.RealmIndex,
            RealmName = Realm.NameOf(e.RealmIndex),
            e.CultivationPoints
        });
        return Ok(result);
    }

    [HttpGet("events/{groupId}")]
    public async Task<ActionResult> GetEvents(string groupId, CancellationToken ct)
    {
        if (!IsAuthenticated()) return Unauthorized(new { error = "未登录或会话已过期" });

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        var logs = await repo.GetEventLogsAsync(groupId, 50, ct);
        var result = logs.Select(l => new
        {
            l.Id,
            l.GroupId,
            l.MemberOpenId,
            l.EventType,
            l.DetailJson,
            l.AiRawOutput,
            l.AppliedChangesJson,
            l.CreatedAt
        });
        return Ok(result);
    }

    [HttpGet("groups")]
    public async Task<ActionResult> GetActiveGroups(CancellationToken ct)
    {
        if (!IsAuthenticated()) return Unauthorized(new { error = "未登录或会话已过期" });

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        var groupIds = await repo.GetActiveGroupIdsAsync(ct);
        return Ok(groupIds);
    }

    [HttpGet("world/{groupId}")]
    public async Task<ActionResult> GetWorld(string groupId, CancellationToken ct)
    {
        if (!IsAuthenticated()) return Unauthorized(new { error = "未登录或会话已过期" });

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        // 只读获取，避免 GET 请求产生创建存档的副作用
        var world = await repo.GetGroupWorldAsync(groupId, ct);
        if (world is null)
            return NotFound(new { error = "该群尚无修仙世界存档" });
        var tribulation = TribulationSession.Deserialize(world.TribulationSessionJson);
        return Ok(new
        {
            world.GroupId,
            world.WorldQiLevel,
            WorldQiFactor = world.WorldQiFactor,
            world.PendingEventJson,
            world.PendingEventExpiresAt,
            world.PendingEventParticipants,
            world.LastEventGeneratedAt,
            world.LeaderboardSnapshotAt,
            TribulationSession = tribulation is null ? null : new
            {
                tribulation.MemberOpenId,
                Nickname = GameTextFormatter.DisplayName(tribulation.Nickname, tribulation.MemberOpenId),
                tribulation.StartedAt,
                tribulation.ExpiresAt,
                ProtectorCount = tribulation.Protectors.Count,
                Bonus = tribulation.TotalBonus,
                IsExpired = tribulation.IsExpired
            }
        });
    }

    // ═══════ 运维操作接口 ═══════

    /// <summary>群内全部玩家存档摘要（运维用）。</summary>
    [HttpGet("players/{groupId}")]
    public async Task<ActionResult> GetPlayers(string groupId, CancellationToken ct)
    {
        if (!IsAuthenticated()) return Unauthorized(new { error = "未登录或会话已过期" });
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        var players = await repo.GetGroupPlayersAsync(groupId, ct);
        return Ok(players.Select(p => new
        {
            p.MemberOpenId,
            Nickname = GameTextFormatter.DisplayName(p.Nickname, p.MemberOpenId),
            p.RealmIndex,
            RealmName = Realm.NameOf(p.RealmIndex),
            p.CultivationPoints,
            p.SpiritStones,
            p.Lifespan,
            p.UpdatedAt
        }));
    }

    /// <summary>清理指定群的悬挂渡劫会话（运维）。</summary>
    [HttpPost("world/{groupId}/clear-tribulation")]
    public async Task<ActionResult> ClearTribulation(string groupId, CancellationToken ct)
    {
        if (!IsAuthenticated()) return Unauthorized(new { error = "未登录或会话已过期" });
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        var world = await repo.GetGroupWorldAsync(groupId, ct);
        if (world is null)
            return NotFound(new { error = "该群尚无修仙世界存档" });
        if (string.IsNullOrEmpty(world.TribulationSessionJson))
            return Ok(new { message = "当前无渡劫会话，无需清理" });

        world.TribulationSessionJson = null;
        await repo.SaveGroupWorldAsync(world, ct);
        return Ok(new { message = "渡劫会话已清理" });
    }

    public class ResetCooldownRequest
    {
        public string GroupId { get; set; } = "";
        public string MemberOpenId { get; set; } = "";
        /// <summary>为空时重置全部已知冷却类型。</summary>
        public string? ActionType { get; set; }
    }

    private static readonly string[] KnownCooldownTypes =
        { "breakthrough", "practice", "alchemy", "explore", "daily", "referee" };

    /// <summary>重置指定玩家的冷却（运维）。</summary>
    [HttpPost("cooldowns/reset")]
    public async Task<ActionResult> ResetCooldown([FromBody] ResetCooldownRequest req, CancellationToken ct)
    {
        if (!IsAuthenticated()) return Unauthorized(new { error = "未登录或会话已过期" });
        if (string.IsNullOrWhiteSpace(req.GroupId) || string.IsNullOrWhiteSpace(req.MemberOpenId))
            return BadRequest(new { error = "缺少群或玩家标识" });

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();

        var types = string.IsNullOrWhiteSpace(req.ActionType)
            ? KnownCooldownTypes
            : new[] { req.ActionType.Trim() };

        foreach (var t in types)
            await repo.SetCooldownAsync(req.GroupId, req.MemberOpenId, t, TimeSpan.Zero, ct);

        return Ok(new { message = $"已重置 {types.Length} 项冷却" });
    }

    /// <summary>手动备份数据库（复制到同目录时间戳文件）。</summary>
    [HttpPost("backup")]
    public ActionResult CreateBackup()
    {
        if (!IsAuthenticated()) return Unauthorized(new { error = "未登录或会话已过期" });
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dbPath = Path.Combine(baseDir, "qqbot.db");
            if (!System.IO.File.Exists(dbPath))
                return NotFound(new { error = "数据库文件不存在" });
            var bakPath = Path.Combine(baseDir, $"qqbot.db.bak.{DateTime.Now:yyyyMMdd-HHmmss}");
            System.IO.File.Copy(dbPath, bakPath);
            return Ok(new { message = "备份完成", file = Path.GetFileName(bakPath) });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"备份失败：{ex.Message}" });
        }
    }

    /// <summary>列出已有备份文件。</summary>
    [HttpGet("backups")]
    public ActionResult ListBackups()
    {
        if (!IsAuthenticated()) return Unauthorized(new { error = "未登录或会话已过期" });
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var files = Directory.EnumerateFiles(baseDir, "qqbot.db.bak.*")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(50)
            .Select(f => new
            {
                Name = f.Name,
                SizeMb = Math.Round(f.Length / 1024.0 / 1024.0, 2),
                LastWriteTime = f.LastWriteTime
            });
        return Ok(files);
    }
}
