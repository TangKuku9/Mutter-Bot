using Microsoft.AspNetCore.Mvc;
using QQBot.Core.Interfaces;
using QQBot.Web.Models;

namespace QQBot.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IMessageRepository _messageRepository;
    private readonly IConfigRepository _configRepository;

    public ChatController(IMessageRepository messageRepository, IConfigRepository configRepository)
    {
        _messageRepository = messageRepository;
        _configRepository = configRepository;
    }

    [HttpGet("history")]
    public async Task<ActionResult<PagedResult<ChatMessageDto>>> GetHistory(
        [FromQuery] string? groupId = null,
        [FromQuery] string? source = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (items, totalCount) = await _messageRepository.GetPagedMessagesAsync(groupId, page, pageSize, source);

        var groupNames = (await _configRepository.GetKnownGroupsAsync()).ToDictionary(g => g.GroupId, g => g.GroupName);
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
    public async Task<ActionResult<ChatStatsDto>> GetStats()
    {
        // Use UTC+8 (China Standard Time) for "today" boundary
        var cstZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cstZone).Date;
        var todayUtc = TimeZoneInfo.ConvertTimeToUtc(todayLocal, cstZone);

        var totalMessages = await _messageRepository.CountMessagesAsync();
        var todayMessages = await _messageRepository.CountMessagesAsync(todayUtc);
        var aiCallCount = await _messageRepository.CountAiCallsAsync();
        var totalTokens = await _messageRepository.SumTokensAsync();

        return Ok(new ChatStatsDto
        {
            TotalMessages = totalMessages,
            TodayMessages = todayMessages,
            AiCallCount = aiCallCount,
            TotalTokensUsed = totalTokens
        });
    }

    [HttpGet("token-stats")]
    public async Task<IActionResult> GetTokenStats([FromQuery] int days = 7)
    {
        days = Math.Clamp(days, 1, 90);
        var stats = await _messageRepository.GetDailyTokenStatsAsync(days);
        return Ok(stats);
    }

    [HttpGet("token-stats/groups")]
    public async Task<IActionResult> GetGroupTokenStats([FromQuery] int days = 7, [FromQuery] int top = 10)
    {
        days = Math.Clamp(days, 1, 90);
        top = Math.Clamp(top, 1, 50);
        var stats = await _messageRepository.GetGroupTokenStatsAsync(days, top);
        return Ok(stats);
    }

    [HttpGet("token-stats/users")]
    public async Task<IActionResult> GetUserTokenStats([FromQuery] int days = 7, [FromQuery] int top = 10)
    {
        days = Math.Clamp(days, 1, 90);
        top = Math.Clamp(top, 1, 50);
        var stats = await _messageRepository.GetUserTokenStatsAsync(days, top);
        return Ok(stats);
    }
}
