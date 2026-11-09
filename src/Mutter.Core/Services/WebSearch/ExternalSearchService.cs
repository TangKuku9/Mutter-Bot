using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models.Config;

namespace Mutter.Core.Services.WebSearch;

/// <summary>
/// 独立搜索服务编排器：Provider 路由、3 分钟同 query 缓存、并发去重（同 query 合流）、
/// 熔断（连续 3 次失败歇 5 分钟）。它不关心注入与降级——那是
/// <see cref="ExternalSearchInjector"/> 与 AiChatService 的职责，失败结果以
/// <see cref="WebSearchFailure"/> 交由上层决策，本层任何异常都不会打断聊天回复。
/// </summary>
public sealed class ExternalSearchService
{
    /// <summary>同 query 结果缓存时长。</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);

    /// <summary>连续失败多少次进入熔断。</summary>
    public const int BreakerThreshold = 3;

    /// <summary>熔断冷却时长。</summary>
    public static readonly TimeSpan BreakerCooldown = TimeSpan.FromMinutes(5);

    private record CacheEntry(Lazy<Task<WebSearchOutcome>> Task, DateTimeOffset CreatedAt);

    private record ProviderState
    {
        public int ConsecutiveFailures;
        public DateTimeOffset OpenedAt;
        public bool Tripped;
    }

    private readonly IReadOnlyDictionary<string, IWebSearchProvider> _providers;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExternalSearchService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, ProviderState> _breaker = new();
    private readonly object _sync = new();
    private long _lastCleanupTicks;

    public ExternalSearchService(
        IEnumerable<IWebSearchProvider> providers, TimeProvider clock,
        ILogger<ExternalSearchService> logger)
    {
        _providers = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _clock = clock;
        _logger = logger;
    }

    /// <summary>当前配置是否已启用且可用（主开关 + 卡片开关 + 配置完整性）。</summary>
    public static bool IsActive(AiConfig aiConfig) =>
        aiConfig.WebSearchEnabled
        && (aiConfig.ExternalSearch?.Enabled ?? false)
        && ExternalSearchConfig.IsConfigured(aiConfig.ExternalSearch.Normalized());

    /// <summary>
    /// 执行一次检索（成功路径）。缓存与合流对调用方透明；失败时 Outcome 为空并返回 Failure，
    /// 但绝不抛异常——聊天回复永远优先于搜索。
    /// </summary>
    public async Task<(WebSearchOutcome Outcome, WebSearchFailure? Failure)> SearchAsync(
        AiConfig config, string query, CancellationToken ct = default)
    {
        var external = config.ExternalSearch;
        if (external is null || external.Enabled != true)
            return (WebSearchOutcome.Empty, new WebSearchFailure(WebSearchFailureKind.Disabled, null, "未启用"));

        var normalized = external.Normalized();
        if (!ExternalSearchConfig.IsConfigured(normalized))
            return (WebSearchOutcome.Empty, new WebSearchFailure(WebSearchFailureKind.Disabled, null, "配置不完整"));

        var provider = Route(normalized.Provider);
        if (provider is null)
            return (WebSearchOutcome.Empty, new WebSearchFailure(WebSearchFailureKind.Disabled, null, $"未知 provider {normalized.Provider}"));

        var startedAt = _clock.GetUtcNow();
        CleanupExpiredEntries(startedAt);
        var key = CacheKey(normalized, query);
        if (IsBreakerOpen(normalized.Provider)
            && (!_cache.TryGetValue(key, out var cachedBefore)
                || !cachedBefore.Task.IsValueCreated
                || !cachedBefore.Task.Value.IsCompletedSuccessfully
                || startedAt - cachedBefore.CreatedAt >= CacheTtl))
            return (WebSearchOutcome.Empty, new WebSearchFailure(WebSearchFailureKind.Transient, null, "熔断中"));

        var entry = GetOrCreateEntry(key, provider, normalized, query, startedAt, out var cached);
        WebSearchOutcome outcome;
        try
        {
            // 共享检索请求不绑定某一个调用方的取消令牌：任一调用方退出不应打断其他等待者。
            // 单次检索的时限由 Provider 基座按配置超时钳制，这里只取消自己的等待。
            outcome = await entry.Task.Value.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 失败条目要立刻摘除，同 query 的后续请求能重试而不是白等一个已失败的任务
            _cache.TryRemove(key, out _);
            return (WebSearchOutcome.Empty, DescribeFailure(ex));
        }

        return (outcome with { ServedFromCache = cached }, null);
    }

    /// <summary>
    /// 取缓存条目：未过期直接复用（含并发合流）；过期则原子替换。
    /// 用 Lazy 承载真实请求，替换竞争失败的一方不会白打一次 Provider。
    /// </summary>
    private CacheEntry GetOrCreateEntry(
        string key, IWebSearchProvider provider, ExternalSearchConfig config, string query,
        DateTimeOffset now, out bool cached)
    {
        var lazy = new Lazy<Task<WebSearchOutcome>>(
            () => RunAsync(provider, config, query, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);

        while (true)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                if (now - existing.CreatedAt < CacheTtl)
                {
                    cached = true;
                    return existing;
                }

                var replacement = new CacheEntry(lazy, now);
                if (_cache.TryUpdate(key, replacement, existing))
                {
                    cached = false;
                    return replacement;
                }
                continue;
            }

            var fresh = new CacheEntry(lazy, now);
            if (_cache.TryAdd(key, fresh))
            {
                cached = false;
                return fresh;
            }
        }
    }

    private async Task<WebSearchOutcome> RunAsync(
        IWebSearchProvider provider, ExternalSearchConfig config, string query, CancellationToken ct)
    {
        var started = _clock.GetTimestamp();
        try
        {
            var result = await provider.SearchAsync(config, query, ct);
            var elapsed = _clock.GetElapsedTime(started);
            _logger.LogInformation(
                "外搜完成 provider={Provider} hits={Hits} elapsedMs={Elapsed} query={Query}",
                provider.Id, result.Hits.Count, (long)elapsed.TotalMilliseconds, Truncate(query, 80));
            RecordSuccess(provider.Id);
            return result;
        }
        catch (Exception ex)
        {
            RecordFailure(provider.Id, ex);
            // 失败要抛给 SearchAsync 的缓存入口，让同一 query 的并发等待者都能感知失败
            throw;
        }
    }

    private IWebSearchProvider? Route(string providerId) =>
        _providers.TryGetValue(providerId, out var provider) ? provider : null;

    /// <summary>按 id 解析 Provider（探测/自检用；未知 id 返回 null）。</summary>
    public IWebSearchProvider? ResolveProvider(string providerId) => Route(providerId);

    private string CacheKey(ExternalSearchConfig config, string query) =>
        $"{config.Provider}|{config.BaseUrl.TrimEnd('/')}|{config.MaxResults}|{query.Trim().ToUpperInvariant()}";

    private bool IsBreakerOpen(string providerId)
    {
        lock (_sync)
        {
            if (!_breaker.TryGetValue(providerId, out var state) || !state.Tripped) return false;
            if (_clock.GetUtcNow() - state.OpenedAt >= BreakerCooldown)
            {
                state.Tripped = false;
                state.ConsecutiveFailures = 0;
                return false;
            }
            return true;
        }
    }

    private void CleanupExpiredEntries(DateTimeOffset now)
    {
        // 低频回收：不在每次请求扫描全表，也不为每条缓存创建常驻计时器。
        var nowTicks = now.UtcTicks;
        var last = Interlocked.Read(ref _lastCleanupTicks);
        if (nowTicks - last < CacheTtl.Ticks
            || Interlocked.CompareExchange(ref _lastCleanupTicks, nowTicks, last) != last)
            return;
        foreach (var (key, entry) in _cache)
        {
            if (entry.Task.IsValueCreated && entry.Task.Value.IsCompleted && now - entry.CreatedAt >= CacheTtl)
                _cache.TryRemove(key, out _);
        }
    }

    private void RecordSuccess(string providerId)
    {
        lock (_sync)
        {
            if (_breaker.TryGetValue(providerId, out var state))
            {
                state.ConsecutiveFailures = 0;
                state.Tripped = false;
            }
        }
    }

    private void RecordFailure(string providerId, Exception ex)
    {
        lock (_sync)
        {
            var state = _breaker.GetOrAdd(providerId, _ => new ProviderState());
            state.ConsecutiveFailures++;
            if (state.ConsecutiveFailures >= BreakerThreshold && !state.Tripped)
            {
                state.Tripped = true;
                state.OpenedAt = _clock.GetUtcNow();
                _logger.LogWarning(
                    "外搜熔断打开 provider={Provider} 连续失败 {Failures} 次，{Cooldown} 分钟后重试",
                    providerId, state.ConsecutiveFailures, BreakerCooldown.TotalMinutes);
            }
        }
    }

    private static WebSearchFailure DescribeFailure(Exception ex) =>
        ex is WebSearchRequestException typed
            ? new WebSearchFailure(typed.Kind, typed.HttpStatus, typed.Message)
            : new WebSearchFailure(WebSearchFailureKind.Transient, null, ex.Message);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>自检注入点：清空缓存与熔断状态。</summary>
    internal void ResetForTests()
    {
        _cache.Clear();
        _breaker.Clear();
    }
}
