using Mutter.Core.Configuration;

namespace Mutter.Core.Services;

public sealed record UpstreamHealthSnapshot(bool Degraded, string Reason, DateTimeOffset? DegradedUntil,
    int WindowSuccess, int WindowFailure, long P50Ms, long P95Ms,
    DateTimeOffset? LastFailureAt, int? LastFailureStatus);

/// <summary>
/// 上游 AI 健康的滚动窗口追踪。降级只由失败信号驱动：窗口内失败达到阈值、连续两次失败，
/// 或上游明确限流（429，尊重 Retry-After）。恢复要求过最短降级时长且近期成功样本足够，
/// 避免一次抖动来回切换。延迟分布只展示、不参与降级——生产上游本来就常慢（P90 47s+），
/// 「慢而可用」是常态而不是故障，拿慢触发熔断会把机器人打成永久降级。
/// </summary>
public sealed class UpstreamHealthTracker(TimeProvider clock, RuntimeOptions? runtime = null)
{
    private sealed record Sample(DateTimeOffset At, bool Success, long ElapsedMs, int? Status);

    private readonly object _gate = new();
    private readonly Queue<Sample> _window = new();
    private readonly int _windowSize = runtime?.HealthWindowSize ?? 10;
    private readonly int _failureThreshold = runtime?.HealthFailureThreshold ?? 3;
    private readonly int _recoveryThreshold = runtime?.HealthRecoveryThreshold ?? 4;
    private readonly TimeSpan _minDegrade = TimeSpan.FromSeconds(runtime?.HealthMinDegradeSeconds ?? 60);
    private bool _degraded;
    private string _reason = "正常";
    private DateTimeOffset _degradedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastFailureAt;
    private int? _lastFailureStatus;

    /// <summary>当前是否降级。读取时会顺带评估恢复条件（过了最短降级时长后按近期成功样本判定）。</summary>
    public bool IsDegraded
    {
        get { lock (_gate) return EvaluateLocked(clock.GetUtcNow()); }
    }

    /// <summary>每次模型调用结束上报一次结果。retryAfter 用于 429：上游给了明确的重试间隔就尊重它（但不低于最短降级时长）。</summary>
    public void Record(bool success, long elapsedMs, int? status = null, TimeSpan? retryAfter = null)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            _window.Enqueue(new Sample(now, success, elapsedMs, status));
            while (_window.Count > _windowSize) _window.Dequeue();
            if (success) return;

            _lastFailureAt = now;
            if (status is not null) _lastFailureStatus = status;
            if (retryAfter is not null || status == 429)
            {
                DegradeLocked(now + (retryAfter is { } ra && ra > _minDegrade ? ra : _minDegrade),
                    status == 429 ? "上游限流（429）" : $"上游错误（HTTP {status}）");
                return;
            }
            var failures = _window.Count(s => !s.Success);
            var samples = _window.ToArray();
            var consecutive = 0;
            for (var i = samples.Length - 1; i >= 0 && !samples[i].Success; i--) consecutive++;
            if (failures >= _failureThreshold || consecutive >= 2)
                DegradeLocked(now + _minDegrade,
                    consecutive >= 2 && failures < _failureThreshold
                        ? "上游连续 2 次调用失败"
                        : $"近 {samples.Length} 次调用中 {failures} 次失败");
        }
    }

    public UpstreamHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            var degraded = EvaluateLocked(clock.GetUtcNow());
            var samples = _window.ToArray();
            var successes = samples.Where(s => s.Success).Select(s => s.ElapsedMs).Order().ToArray();
            long p50 = successes.Length == 0 ? 0 : successes[(int)Math.Ceiling(successes.Length * .50) - 1];
            long p95 = successes.Length == 0 ? 0 : successes[(int)Math.Ceiling(successes.Length * .95) - 1];
            return new UpstreamHealthSnapshot(degraded, _reason, degraded ? _degradedUntil : null,
                samples.Count(s => s.Success), samples.Length - samples.Count(s => s.Success),
                p50, p95, _lastFailureAt, _lastFailureStatus);
        }
    }

    private bool EvaluateLocked(DateTimeOffset now)
    {
        if (!_degraded) return false;
        if (now < _degradedUntil) return true;
        // 最短降级时长已过：给上游一个凭近期表现翻案的机会，而不是永远记着它坏过。
        var recent = _window.ToArray()[^Math.Min(5, _window.Count)..];
        var successes = recent.Count(s => s.Success);
        if (successes >= _recoveryThreshold)
        {
            _degraded = false;
            _reason = "正常";
            return false;
        }
        _reason = $"降级恢复中：近 {recent.Length} 次调用成功 {successes} 次";
        return true;
    }

    private void DegradeLocked(DateTimeOffset until, string reason)
    {
        if (until > _degradedUntil) _degradedUntil = until;
        _degraded = true;
        _reason = reason;
    }
}
