namespace Mutter.Core.Proactive;

/// <summary>
/// 一次择机评估的落库结论。面板按这些值统计「看了多少、说了几次、为什么没说」，
/// 新增取值时保持旧值语义不变，否则历史统计会串档。
/// </summary>
public static class ProactiveOutcomes
{
    public const string SentPassive = "sent_passive";
    public const string SentActive = "sent_active";

    public const string SkippedQuietHours = "skipped_quiet";
    public const string SkippedCooldown = "skipped_cooldown";
    public const string SkippedGroupCap = "skipped_group_cap";
    public const string SkippedGlobalCap = "skipped_global_cap";
    /// <summary>AI 这回没话可说（Line 为空）。</summary>
    public const string SkippedAiDeclined = "skipped_ai_declined";
    public const string SkippedAiUnavailable = "skipped_ai_unavailable";
    public const string SkippedAiBusy = "skipped_ai_busy";
    public const string SkippedDisabled = "skipped_disabled";

    public const string FailedReceiverRejected = "failed_rejected";
    public const string FailedRateLimited = "failed_rate_limited";
    public const string FailedNoChannel = "failed_no_channel";
    public const string FailedApiError = "failed_api_error";
}
