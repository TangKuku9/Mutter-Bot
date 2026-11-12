using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mutter.Core;
using Mutter.Core.Interfaces;
using Mutter.Core.Services;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Models.QQOfficial;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;

namespace Mutter.Infrastructure.Services;

/// <summary>
/// 主动消息（msg_id 传空）发送与频控。
/// 旧实现按「每群每月 4 条」拦，那是主动推送全量开放前的规则；2026-06-22 起官方改为
/// 账号 60/30 qpm + 单群 20 qpm + 1000 条/日/群，故这里改成分钟滑窗 + 日计数，
/// 上限值由管理面板配置并钳制在官方 ceiling 内。被拒状态（Rejected）仍持久化在库里。
/// </summary>
public class ActiveMessageService : IActiveMessageService
{
    private const string ScopeGroup = "group";
    private const string ScopeUser = "user";

    private static readonly HashSet<int> RejectErrCodes = new() { 304031, 304032, 304049, 304050 };

    private readonly IQqBotApiService _apiService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RichUiCapability _richUi;
    private readonly ILogger<ActiveMessageService> _logger;

    private readonly TimeProvider _clock;

    public ActiveMessageService(
        IQqBotApiService apiService,
        IServiceScopeFactory scopeFactory,
        RichUiCapability richUi,
        ILogger<ActiveMessageService> logger, TimeProvider? clock = null)
    {
        _apiService = apiService;
        _scopeFactory = scopeFactory;
        _richUi = richUi;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ActiveMessageSendResult> SendGroupActiveAsync(string groupOpenId, string content, CancellationToken ct = default)
    {
        return await SendActiveAsync(groupOpenId, OutboundMessage.Text(content), ScopeGroup, ct);
    }

    public async Task<ActiveMessageSendResult> SendGroupActiveRichAsync(string groupOpenId, OutboundMessage msg, CancellationToken ct = default)
    {
        return await SendActiveAsync(groupOpenId, msg, ScopeGroup, ct);
    }

    public async Task<ActiveMessageSendResult> SendPrivateActiveAsync(string userOpenId, string content, CancellationToken ct = default)
    {
        return await SendActiveAsync(userOpenId, OutboundMessage.Text(content), ScopeUser, ct);
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

    private async Task<ActiveMessageSendResult> SendActiveAsync(string key, OutboundMessage msg, string scope, CancellationToken ct)
    {
        var month = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM");

        using var serviceScope = _scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await LoadLimitsAsync(serviceScope, key, scope, ct);

        // 主动通道总开关按生效配置判定（群已带 overrides，私聊用全局）；
        // 服务自身把关，避免依赖调用方逐一自查。
        if (!config.AllowActiveChannel)
            return new ActiveMessageSendResult { Success = false, Reason = "active_channel_disabled" };

        var entity = await db.ActiveMessageQuotas
            .FirstOrDefaultAsync(q => q.Key == key && q.Scope == scope && q.Month == month, ct);

        if (entity?.Rejected == true)
        {
            _logger.LogWarning("主动消息已被接收方关闭，跳过发送 subject={Scope} key={Key}", scope, key);
            return new ActiveMessageSendResult { Success = false, Reason = "rejected" };
        }

        var limits = ResolveLimits(scope, config);
        var attempt = msg;
        string? sentId = null;
        for (var send = 0; ; send++)
        {
            if (!await TryAcquireAsync(db, key, scope, limits, ct))
                return new ActiveMessageSendResult { Success = false, Reason = "rate_limited" };
            ct.ThrowIfCancellationRequested();
            try
            {
                if (scope == ScopeGroup)
                    sentId = await _apiService.SendGroupRichAsync(key, attempt, ct);
                else
                    sentId = await _apiService.SendPrivateRichAsync(key, attempt, ct);

                if (attempt.UseMarkdown) _richUi.CountMarkdownSent();
                break;
            }
            catch (QqApiException ex) when (attempt.UseMarkdown && send == 0
                                            && (QqApiErrorCodes.IsMarkdownDenied(ex.ErrCode)
                                                || QqApiErrorCodes.IsKeyboardDenied(ex.ErrCode)))
            {
                // 富能力被拒：退回纯文本；每次真实 HTTP 尝试均先持久化占用频控
                var markdownBroken = QqApiErrorCodes.IsMarkdownDenied(ex.ErrCode);
                _richUi.ReportDenied(markdownBroken ? RichUiCapability.CapMarkdown : RichUiCapability.CapKeyboard, ex.ErrCode);
                _richUi.CountDegradeRetry();
                _richUi.CountPlainFallback();
                _logger.LogWarning("主动富消息被拒，降级重发 subject={Scope} key={Key} errCode={ErrCode}", scope, key, ex.ErrCode);
                attempt = markdownBroken
                    ? attempt with { MarkdownText = null, Keyboard = null }
                    : attempt with { Keyboard = null };
                continue;
            }
            catch (QqApiException ex)
            {
                _logger.LogWarning(ex, "发送主动消息失败 subject={Scope} key={Key} errCode={ErrCode}", scope, key, ex.ErrCode);
                if (RejectErrCodes.Contains(ex.ErrCode))
                {
                    try { await MarkRejectedAsync(key, scope, ct); }
                    catch (Exception persistError) { _logger.LogError(persistError, "接收方拒绝状态写入失败"); }
                    return new ActiveMessageSendResult { Success = false, Reason = "rejected" };
                }
                // 40034100 主动消息超频控：官方侧限流，记下来便于面板区分「被平台拦」与「被自己拦」
                if (ex.ErrCode == 40034100)
                    return new ActiveMessageSendResult { Success = false, Reason = "rate_limited", ErrorMessage = ex.Message };

                return new ActiveMessageSendResult { Success = false, Reason = "error", ErrorMessage = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送主动消息异常 subject={Scope} key={Key}", scope, key);
                return new ActiveMessageSendResult { Success = false, Reason = "unknown", ErrorMessage = ex.Message };
            }
        }

        // 平台已确认发送；月度展示计数失败不能反转发送结果。
        try
        {
            using var receiptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var now = _clock.GetUtcNow().UtcDateTime;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ActiveMessageQuotas" ("Key", "Scope", "Month", "Count", "Rejected", "LastSentAt")
                VALUES ({key}, {scope}, {month}, 1, 0, {now})
                ON CONFLICT ("Key", "Scope", "Month") DO UPDATE SET
                    "Count" = "Count" + 1, "LastSentAt" = excluded."LastSentAt"
                """, receiptDeadline.Token);
        }
        catch (Exception ex) { _logger.LogError(ex, "主动消息已发送，月累计写入失败 subject={Scope} key={Key}", scope, key); }
        return new ActiveMessageSendResult { Success = true, Reason = "ok", MessageId = sentId };
    }

    private async Task<ActiveMessageQuotaStatus> GetQuotaStatusAsync(string key, string scope, CancellationToken ct)
    {
        var month = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM");
        using var serviceScope = _scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await LoadLimitsAsync(serviceScope, key, scope, ct);
        var limits = ResolveLimits(scope, config);

        var entity = await db.ActiveMessageQuotas
            .FirstOrDefaultAsync(q => q.Key == key && q.Scope == scope && q.Month == month, ct);

        var (usedToday, sentLastMinute) = await PeekCountersAsync(db, key, scope, ct);

        return new ActiveMessageQuotaStatus
        {
            UsedThisMonth = entity?.Count ?? 0,
            Quota = limits.PerDay,
            UsedToday = usedToday,
            SentLastMinute = sentLastMinute,
            MinuteQuota = limits.PerMinute,
            Rejected = entity?.Rejected ?? false,
            LastSentAt = entity?.LastSentAt
        };
    }

    private async Task MarkRejectedAsync(string key, string scope, CancellationToken ct)
    {
        var month = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM");
        using var serviceScope = _scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await UpsertQuotaAsync(db, key, scope, month, e => e.Rejected = true, ct);
        _logger.LogWarning("已标记主动消息被拒 subject={Scope} key={Key}", scope, key);
    }

    private async Task ClearRejectedAsync(string key, string scope, CancellationToken ct)
    {
        var month = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM");
        using var serviceScope = _scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.ActiveMessageQuotas
            .FirstOrDefaultAsync(q => q.Key == key && q.Scope == scope && q.Month == month, ct);
        if (entity == null) return;
        entity.Rejected = false;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<ProactiveChatConfig> LoadLimitsAsync(IServiceScope serviceScope, string key, string scope, CancellationToken ct)
    {
        var repo = serviceScope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var global = await repo.GetProactiveConfigAsync(ct);
        return scope == ScopeGroup
            ? global.WithOverrides((await repo.GetGroupConfigAsync(key, ct))?.ProactiveOverrides)
            : global;
    }

    private static (int PerMinute, int PerDay, int AccountPerMinute) ResolveLimits(
        string scope, ProactiveChatConfig config) =>
        scope == ScopeGroup
            ? (config.ActivePerGroupPerMinute, config.ActivePerGroupPerDay, config.ActivePerAccountPerMinute)
            // 私聊没有官方单人群频控数字，沿用账号维度分钟上限，日上限放宽
            : (config.ActivePerAccountPerMinute, config.ActivePerGroupPerDay * 4, config.ActivePerAccountPerMinute);

    /// <summary>
    /// 取令牌：先查目标（群/用户）的分钟与日限，再查账号整体分钟限。
    /// 按「尝试」而非「成功」计数——官方频控同样按请求计，成功计数会让重试把额度打满后仍被平台拒。
    /// </summary>
    private async Task<bool> TryAcquireAsync(
        AppDbContext db, string key, string scope,
        (int PerMinute, int PerDay, int AccountPerMinute) limits, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var minute = now.AddMinutes(-1);
        var day = now.AddHours(8).Date.AddHours(-8);
        var id = Guid.NewGuid().ToString("N");
        // 一条 SQLite 写语句原子完成检查与占用；多个服务实例也不会越过共享配额。
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ActiveMessageAttempts" ("Id", "Key", "Scope", "AttemptedAt")
            SELECT {id}, {key}, {scope}, {now}
            WHERE (SELECT COUNT(*) FROM "ActiveMessageAttempts"
                   WHERE "Key" = {key} AND "Scope" = {scope} AND "AttemptedAt" >= {minute}) < {limits.PerMinute}
              AND (SELECT COUNT(*) FROM "ActiveMessageAttempts"
                   WHERE "Key" = {key} AND "Scope" = {scope} AND "AttemptedAt" >= {day}) < {limits.PerDay}
              AND (SELECT COUNT(*) FROM "ActiveMessageAttempts"
                   WHERE "AttemptedAt" >= {minute}) < {limits.AccountPerMinute}
            """, ct);
        return inserted == 1;
    }

    private async Task<(int UsedToday, int SentLastMinute)> PeekCountersAsync(
        AppDbContext db, string key, string scope, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var day = now.AddHours(8).Date.AddHours(-8);
        var minute = now.AddMinutes(-1);
        var query = db.ActiveMessageAttempts.Where(a => a.Key == key && a.Scope == scope);
        return (await query.CountAsync(a => a.AttemptedAt >= day, ct),
            await query.CountAsync(a => a.AttemptedAt >= minute, ct));
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
