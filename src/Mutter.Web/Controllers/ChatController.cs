using Microsoft.AspNetCore.Mvc;
using Mutter.Core.Interfaces;
using Mutter.Web.Models;
using Mutter.Web.Services;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IMessageRepository _messageRepository;
    private readonly IConfigRepository _configRepository;
    private readonly PanelStatisticsService _statistics;

    public ChatController(IMessageRepository messageRepository, IConfigRepository configRepository, PanelStatisticsService statistics)
    {
        _messageRepository = messageRepository;
        _configRepository = configRepository;
        _statistics = statistics;
    }

    [HttpGet("history")]
    public async Task<ActionResult<PagedResult<ChatMessageDto>>> GetHistory(
        [FromQuery] string? groupId = null,
        [FromQuery] string? source = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (items, totalCount) = await _messageRepository.GetPagedMessagesAsync(groupId, page, pageSize, source, ct);

        var groupNames = (await _configRepository.GetKnownGroupsAsync(ct)).ToDictionary(g => g.GroupId, g => g.GroupName);
        string? NameOf(string? id) => id is not null && groupNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : null;

        var dtos = items.Select(m => new ChatMessageDto
        {
            GroupName = NameOf(m.GroupId),
            Id = m.Id,
            ChatType = m.ChatType,
            GroupId = m.GroupId,
            UserId = m.UserId,
            UserName = m.UserName,
            PlainText = m.PlainText,
            HasImage = m.HasImage,
            HasAtBot = m.HasAtBot,
            IsBotMessage = m.IsBotMessage,
            IsProactive = m.IsProactive,
            CreatedAt = m.CreatedAt
        }).ToList();

        return Ok(new PagedResult<ChatMessageDto>
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpGet("stats")]
    public async Task<ActionResult<ChatStatsDto>> GetStats(CancellationToken ct)
    {
        return Ok(await _statistics.OverviewAsync(ct));
    }

    [HttpGet("usage")]
    public async Task<IActionResult> GetUsage([FromQuery] int days = 7, CancellationToken ct = default) =>
        Ok(await _statistics.UsageAsync(days, ct));

    [HttpGet("token-stats")]
    public async Task<IActionResult> GetTokenStats([FromQuery] int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        var stats = (await _statistics.UsageAsync(days, ct)).Daily;
        return Ok(stats);
    }

    [HttpGet("token-stats/groups")]
    public async Task<IActionResult> GetGroupTokenStats([FromQuery] int days = 7, [FromQuery] int top = 10, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        top = Math.Clamp(top, 1, 50);
        var stats = (await _statistics.UsageAsync(days, ct)).Groups.Take(top);
        return Ok(stats);
    }

    [HttpGet("token-stats/users")]
    public async Task<IActionResult> GetUserTokenStats([FromQuery] int days = 7, [FromQuery] int top = 10, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        top = Math.Clamp(top, 1, 50);
        var stats = (await _statistics.UsageAsync(days, ct)).Users.Take(top);
        return Ok(stats);
    }
}
