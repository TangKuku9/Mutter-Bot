using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;

namespace QQBot.Web.Controllers;

[ApiController]
[Route("api/cultivation")]
public class CultivationController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CultivationController(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [HttpGet("leaderboard/{groupId}")]
    public async Task<ActionResult> GetLeaderboard(string groupId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        var entries = await repo.GetGroupLeaderboardAsync(groupId, 20, ct);
        var result = entries.Select(e => new
        {
            e.MemberOpenId,
            e.Nickname,
            e.RealmIndex,
            RealmName = Realm.NameOf(e.RealmIndex),
            e.CultivationPoints
        });
        return Ok(result);
    }

    [HttpGet("events/{groupId}")]
    public async Task<ActionResult> GetEvents(string groupId, CancellationToken ct)
    {
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
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        var groupIds = await repo.GetActiveGroupIdsAsync(ct);
        return Ok(groupIds);
    }

    [HttpGet("world/{groupId}")]
    public async Task<ActionResult> GetWorld(string groupId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        var world = await repo.GetOrCreateGroupWorldAsync(groupId, ct);
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
                tribulation.Nickname,
                tribulation.StartedAt,
                tribulation.ExpiresAt,
                ProtectorCount = tribulation.Protectors.Count,
                Bonus = tribulation.TotalBonus,
                IsExpired = tribulation.IsExpired
            }
        });
    }
}
