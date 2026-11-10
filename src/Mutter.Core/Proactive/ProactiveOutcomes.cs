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
    /// <summary>联网核实后模型选择不说（草稿可能有误或已无必要），不降级成草稿硬发。</summary>
    public const string SkippedAfterSearch = "skipped_after_search";
    public const string SkippedAiUnavailable = "skipped_ai_unavailable";
    public const string FailedAiCall = "failed_ai_call";
    public const string SkippedAiTruncated = "skipped_ai_truncated";
    public const string SkippedAiUnparsable = "skipped_ai_unparsable";
    public const string SkippedAiBusy = "skipped_ai_busy";
    public const string SkippedDisabled = "skipped_disabled";
    public const string SkippedDuplicate = "skipped_duplicate";
    /// <summary>发送前被取消（停机或投递期限到达），平台尚未收到请求。</summary>
    public const string SkippedCancelled = "skipped_cancelled";

    public const string FailedReceiverRejected = "failed_rejected";
    public const string FailedRateLimited = "failed_rate_limited";
    public const string FailedNoChannel = "failed_no_channel";
    public const string FailedApiError = "failed_api_error";
    public const string DeliveryUnknown = "delivery_unknown";

    /// <summary>允许落库的结果码。平台返回的原始错误文本只能进 Detail，不能当 Outcome。</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        SentPassive, SentActive,
        SkippedQuietHours, SkippedCooldown, SkippedGroupCap, SkippedGlobalCap,
        SkippedAiDeclined, SkippedAfterSearch, SkippedAiUnavailable, SkippedAiTruncated,
        SkippedAiUnparsable, SkippedAiBusy, SkippedDisabled, SkippedDuplicate, SkippedCancelled,
        FailedAiCall, FailedReceiverRejected, FailedRateLimited, FailedNoChannel, FailedApiError,
        DeliveryUnknown
    };

    /// <summary>
    /// 模型前门禁会使用的结果码；其中冷却、日限、开关关闭也可能由模型后的投递复核返回。
    /// 因此结果码本身不能证明是否经过模型，只用于旧留痕的兼容判断。
    /// SkippedDuplicate 来自发送账本 PrepareAsync，模型已经评估过，故不在模型前门禁。
    /// </summary>
    private static readonly HashSet<string> BeforeAi = new(StringComparer.Ordinal)
    {
        SkippedDisabled, SkippedQuietHours, SkippedCooldown, SkippedGroupCap, SkippedGlobalCap,
        SkippedAiUnavailable, SkippedAiBusy
    };

    public static bool IsKnown(string? value) => value is not null && Known.Contains(value);

    public static bool IsBeforeAi(string? value) => value is not null && BeforeAi.Contains(value);

    /// <summary>投递细节是已知结果码时直接沿用，否则归为平台发送失败。</summary>
    public static string FromDeliveryDetail(string? detail) =>
        IsKnown(detail) ? detail! : FailedApiError;
}
