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

    private readonly object _rateGate = new();
    private readonly Dictionary<string, Queue<DateTime>> _minuteWindows = new();
    private readonly Dictionary<string, (string Day, int Count)> _dailyCounters = new();

    public ActiveMessageService(
        IQqBotApiService apiService,
        IServiceScopeFactory scopeFactory,
        RichUiCapability richUi,
        ILogger<ActiveMessageService> logger)
    {
        _apiService = apiService;
        _scopeFactory = scopeFactory;
        _richUi = richUi;
        _logger = logger;
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
        var month = DateTime.UtcNow.ToString("yyyy-MM");

        using var serviceScope = _scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await LoadLimitsAsync(serviceScope, ct);

        var entity = await db.ActiveMessageQuotas
            .FirstOrDefaultAsync(q => q.Key == key && q.Scope == scope && q.Month == month, ct);

        if (entity?.Rejected == true)
        {
            _logger.LogWarning("主动消息已被接收方关闭，跳过发送 subject={Scope} key={Key}", scope, key);
            return new ActiveMessageSendResult { Success = false, Reason = "rejected" };
        }

        var limits = ResolveLimits(scope, config);
        if (!TryAcquire(key, scope, limits, ct))
        {
            _logger.LogWarning("主动消息触发自设频控，跳过发送 subject={Scope} key={Key}", scope, key);
            return new ActiveMessageSendResult { Success = false, Reason = "rate_limited" };
        }

        var attempt = msg;
        string? sentId = null;
        for (var send = 0; ; send++)
        {
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
                // 富能力被拒：记降级态并退回纯文本重发，仍只计 1 条主动配额
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
                    await MarkRejectedAsync(key, scope, ct);
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
                return new ActiveMessageSendResult { Success = false, Reason = "error", ErrorMessage = ex.Message };
            }
        }

        await UpsertQuotaAsync(db, key, scope, month, e => { e.Count++; e.LastSentAt = DateTime.UtcNow; }, ct);
        return new ActiveMessageSendResult { Success = true, Reason = "ok", MessageId = sentId };
    }

    private async Task<ActiveMessageQuotaStatus> GetQuotaStatusAsync(string key, string scope, CancellationToken ct)
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        using var serviceScope = _scopeFactory.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await LoadLimitsAsync(serviceScope, ct);
        var limits = ResolveLimits(scope, config);

        var entity = await db.ActiveMessageQuotas
            .FirstOrDefaultAsync(q => q.Key == key && q.Scope == scope && q.Month == month, ct);

        var (usedToday, sentLastMinute) = PeekCounters(key, scope);

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

    private static async Task<ProactiveChatConfig> LoadLimitsAsync(IServiceScope serviceScope, CancellationToken ct)
    {
        var repo = serviceScope.ServiceProvider.GetRequiredService<IConfigRepository>();
        return await repo.GetProactiveConfigAsync(ct);
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
    private bool TryAcquire(
        string key, string scope, (int PerMinute, int PerDay, int AccountPerMinute) limits, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var target = $"{scope}:{key}";
        var day = now.AddHours(8).ToString("yyyy-MM-dd"); // 官方日界按北京时间的自然日

        lock (_rateGate)
        {
            if (!Allow(target, limits.PerMinute, limits.PerDay, now, day)) return false;
            // 账号整体分钟限：群与私聊共用同一条发送链路，用同一计数器
            if (!Allow("account", limits.AccountPerMinute, int.MaxValue, now, day)) return false;

            Touch(target, now, day);
            Touch("account", now, day);
            return true;
        }
    }

    private bool Allow(string counterKey, int perMinute, int perDay, DateTime now, string day)
    {
        if (_minuteWindows.TryGetValue(counterKey, out var window))
        {
            while (window.Count > 0 && (now - window.Peek()) > TimeSpan.FromMinutes(1))
                window.Dequeue();
            if (window.Count >= perMinute) return false;
        }

        if (perDay < int.MaxValue && _dailyCounters.TryGetValue(counterKey, out var daily)
            && daily.Day == day && daily.Count >= perDay)
            return false;

        return true;
    }

    private void Touch(string counterKey, DateTime now, string day)
    {
        if (!_minuteWindows.TryGetValue(counterKey, out var window))
        {
            window = new Queue<DateTime>();
            _minuteWindows[counterKey] = window;
        }
        window.Enqueue(now);

        var daily = _dailyCounters.TryGetValue(counterKey, out var existing) && existing.Day == day
            ? existing.Count + 1
            : 1;
        _dailyCounters[counterKey] = (day, daily);
    }

    private (int UsedToday, int SentLastMinute) PeekCounters(string key, string scope)
    {
        var counterKey = $"{scope}:{key}";
        var now = DateTime.UtcNow;
        var day = now.AddHours(8).ToString("yyyy-MM-dd");

        lock (_rateGate)
        {
            var usedToday = _dailyCounters.TryGetValue(counterKey, out var daily) && daily.Day == day
                ? daily.Count : 0;

            var sentLastMinute = 0;
            if (_minuteWindows.TryGetValue(counterKey, out var window))
            {
                while (window.Count > 0 && (now - window.Peek()) > TimeSpan.FromMinutes(1))
                    window.Dequeue();
                sentLastMinute = window.Count;
            }

            return (usedToday, sentLastMinute);
        }
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
