using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Infrastructure.Data;
using QQBot.Infrastructure.Entities;

namespace QQBot.Infrastructure.Services;

public class ActiveMessageService : IActiveMessageService
{
    private const int MaxQuotaPerMonth = 4;
    private const string ScopeGroup = "group";
    private const string ScopeUser = "user";

    private static readonly HashSet<int> RejectErrCodes = new() { 304031, 304032, 304049, 304050 };

    private readonly IQqBotApiService _apiService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ActiveMessageService> _logger;

    public ActiveMessageService(IQqBotApiService apiService, IServiceScopeFactory scopeFactory, ILogger<ActiveMessageService> logger)
    {
        _apiService = apiService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ActiveMessageSendResult> SendGroupActiveAsync(string groupOpenId, string content, CancellationToken ct = default)
    {
        return await SendActiveAsync(groupOpenId, content, ScopeGroup, ct);
    }

    public async Task<ActiveMessageSendResult> SendPrivateActiveAsync(string userOpenId, string content, CancellationToken ct = default)
    {
        return await SendActiveAsync(userOpenId, content, ScopeUser, ct);
    }

    public async Task<ActiveMessageQuotaStatus> GetGroupQuotaStatusAsync(string groupOpenId, CancellationToken ct = default)
    {
        return await GetQuotaStatusAsync(groupOpenId, ScopeGroup, ct);
    }

    public async Task<ActiveMessageQuotaStatus> GetUserQuotaStatusAsync(string userOpenId, CancellationToken ct = default)
    {
        return await GetQuotaStatusAsync(userOpenId, ScopeUser, ct);
    }

    public async Task MarkGroupRejectedAsync(string groupOpenId, CancellationToken ct = default)
    {
        await MarkRejectedAsync(groupOpenId, ScopeGroup, ct);
    }

    public async Task MarkUserRejectedAsync(string userOpenId, CancellationToken ct = default)
    {
        await MarkRejectedAsync(userOpenId, ScopeUser, ct);
    }

    public async Task ClearGroupRejectedAsync(string groupOpenId, CancellationToken ct = default)
    {
        await ClearRejectedAsync(groupOpenId, ScopeGroup, ct);
    }

    public async Task ClearUserRejectedAsync(string userOpenId, CancellationToken ct = default)
    {
        await ClearRejectedAsync(userOpenId, ScopeUser, ct);
    }

    private async Task<ActiveMessageSendResult> SendActiveAsync(string key, string content, string scope, CancellationToken ct)
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM");

        using var serviceScope = _scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entity = await db.ActiveMessageQuotas
            .FirstOrDefaultAsync(q => q.Key == key && q.Scope == scope && q.Month == month, ct);

        if (entity?.Rejected == true)
        {
            _logger.LogWarning("主动消息已被接收方关闭，跳过发送 subject={Scope} key={Key}", scope, key);
            return new ActiveMessageSendResult { Success = false, Reason = "rejected" };
        }

        if (entity != null && entity.Count >= MaxQuotaPerMonth)
        {
            _logger.LogWarning("主动消息月度配额已用尽，跳过发送 subject={Scope} key={Key} count={Count}", scope, key, entity.Count);
            return new ActiveMessageSendResult { Success = false, Reason = "quota_exhausted" };
        }

        try
        {
            if (scope == ScopeGroup)
                await _apiService.SendGroupMessageAsync(key, content, null, null, ct);
            else
                await _apiService.SendPrivateMessageAsync(key, content, null, null, ct);
        }
        catch (QqApiException ex)
        {
            _logger.LogWarning(ex, "发送主动消息失败 subject={Scope} key={Key} errCode={ErrCode}", scope, key, ex.ErrCode);
            if (RejectErrCodes.Contains(ex.ErrCode))
            {
                await MarkRejectedAsync(key, scope, ct);
                return new ActiveMessageSendResult { Success = false, Reason = "rejected" };
            }
            return new ActiveMessageSendResult { Success = false, Reason = "error", ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送主动消息异常 subject={Scope} key={Key}", scope, key);
            return new ActiveMessageSendResult { Success = false, Reason = "error", ErrorMessage = ex.Message };
        }

        await UpsertQuotaAsync(db, key, scope, month, e => { e.Count++; e.LastSentAt = DateTime.UtcNow; }, ct);
        return new ActiveMessageSendResult { Success = true, Reason = "ok" };
    }

    private async Task<ActiveMessageQuotaStatus> GetQuotaStatusAsync(string key, string scope, CancellationToken ct)
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        using var serviceScope = _scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.ActiveMessageQuotas
            .FirstOrDefaultAsync(q => q.Key == key && q.Scope == scope && q.Month == month, ct);
        return new ActiveMessageQuotaStatus
        {
            UsedThisMonth = entity?.Count ?? 0,
            Quota = MaxQuotaPerMonth,
            Rejected = entity?.Rejected ?? false,
            LastSentAt = entity?.LastSentAt
        };
    }

    private async Task MarkRejectedAsync(string key, string scope, CancellationToken ct)
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        using var serviceScope = _scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await UpsertQuotaAsync(db, key, scope, month, e => e.Rejected = true, ct);
        _logger.LogWarning("已标记主动消息被拒 subject={Scope} key={Key}", scope, key);
    }

    private async Task ClearRejectedAsync(string key, string scope, CancellationToken ct)
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        using var serviceScope = _scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.ActiveMessageQuotas
            .FirstOrDefaultAsync(q => q.Key == key && q.Scope == scope && q.Month == month, ct);
        if (entity == null) return;
        entity.Rejected = false;
        await db.SaveChangesAsync(ct);
    }

    private static async Task UpsertQuotaAsync(
        AppDbContext db, string key, string scope, string month,
        Action<ActiveMessageQuotaEntity> apply, CancellationToken ct)
    {
        var entity = await db.ActiveMessageQuotas
            .FirstOrDefaultAsync(q => q.Key == key && q.Scope == scope && q.Month == month, ct);
        if (entity == null)
        {
            entity = new ActiveMessageQuotaEntity { Key = key, Scope = scope, Month = month };
            db.ActiveMessageQuotas.Add(entity);
        }
        apply(entity);
        await db.SaveChangesAsync(ct);
    }
}
