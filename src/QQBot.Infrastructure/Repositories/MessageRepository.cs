using Microsoft.EntityFrameworkCore;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Infrastructure.Data;

namespace QQBot.Infrastructure.Repositories;

public class MessageRepository : IMessageRepository
{
    private readonly AppDbContext _dbContext;

    public MessageRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task SaveMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        _dbContext.ChatMessages.Add(message);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task SaveMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken ct = default)
    {
        _dbContext.ChatMessages.AddRange(messages);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<List<ChatMessage>> GetGroupHistoryAsync(string groupId, int limit = 20, CancellationToken ct = default)
    {
        return await _dbContext.ChatMessages
            .Where(m => m.ChatType == 0 && m.GroupId == groupId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<ChatMessage>> GetPrivateHistoryAsync(string userId, int limit = 20, CancellationToken ct = default)
    {
        return await _dbContext.ChatMessages
            .Where(m => m.ChatType == 1 && m.UserId == userId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> IsMessageIdExistsAsync(string messageId, CancellationToken ct = default)
    {
        return await _dbContext.ChatMessages
            .AnyAsync(m => m.MessageId == messageId, ct);
    }

    public async Task<(List<ChatMessage> Items, int TotalCount)> GetPagedMessagesAsync(string? groupId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _dbContext.ChatMessages.AsQueryable();

        if (!string.IsNullOrEmpty(groupId))
            query = query.Where(m => m.GroupId == groupId);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<long> CountMessagesAsync(DateTime? since = null, CancellationToken ct = default)
    {
        var query = _dbContext.ChatMessages.AsQueryable();
        if (since.HasValue)
            query = query.Where(m => m.CreatedAt >= since.Value);
        return await query.LongCountAsync(ct);
    }

    public async Task<long> CountAiCallsAsync(CancellationToken ct = default)
    {
        return await _dbContext.ChatMessages
            .LongCountAsync(m => m.AiModelUsed != null, ct);
    }

    public async Task<long> SumTokensAsync(CancellationToken ct = default)
    {
        return await _dbContext.ChatMessages
            .Where(m => m.PromptTokens != null)
            .SumAsync(m => (long)(m.PromptTokens!.Value + (m.CompletionTokens ?? 0)), ct);
    }

    public async Task<List<DailyTokenStats>> GetDailyTokenStatsAsync(int days, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var aiMessages = await _dbContext.ChatMessages
            .Where(m => m.PromptTokens != null && m.CreatedAt >= since)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        return aiMessages
            .GroupBy(m => m.CreatedAt.ToString("yyyy-MM-dd"))
            .Select(g => new DailyTokenStats
            {
                Date = g.Key,
                PromptTokens = g.Sum(m => m.PromptTokens ?? 0),
                CompletionTokens = g.Sum(m => m.CompletionTokens ?? 0),
                CallCount = g.Count()
            })
            .OrderBy(x => x.Date)
            .ToList();
    }

    public async Task<List<GroupTokenStats>> GetGroupTokenStatsAsync(int days, int top, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var aiMessages = await _dbContext.ChatMessages
            .Where(m => m.PromptTokens != null && m.ChatType == 0 && m.GroupId != null && m.CreatedAt >= since)
            .ToListAsync(ct);

        return aiMessages
            .GroupBy(m => m.GroupId)
            .Select(g => new GroupTokenStats
            {
                GroupId = g.Key!,
                TotalTokens = g.Sum(m => (m.PromptTokens ?? 0) + (m.CompletionTokens ?? 0)),
                CallCount = g.Count()
            })
            .OrderByDescending(x => x.TotalTokens)
            .Take(top)
            .ToList();
    }

    public async Task<List<UserTokenStats>> GetUserTokenStatsAsync(int days, int top, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var aiMessages = await _dbContext.ChatMessages
            .Where(m => m.PromptTokens != null && m.CreatedAt >= since)
            .ToListAsync(ct);

        return aiMessages
            .GroupBy(m => new { m.UserId, m.UserName })
            .Select(g => new UserTokenStats
            {
                UserId = g.Key.UserId,
                UserName = g.Key.UserName,
                TotalTokens = g.Sum(m => (m.PromptTokens ?? 0) + (m.CompletionTokens ?? 0)),
                CallCount = g.Count()
            })
            .OrderByDescending(x => x.TotalTokens)
            .Take(top)
            .ToList();
    }
}
