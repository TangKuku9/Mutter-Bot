using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using QQBot.Core.Configuration;

namespace QQBot.Core.Services;

/// <summary>能力探测状态。Unknown=未验证（按配置允许尝试）；Ok=已确认可用；Denied=被平台拒绝。</summary>
public enum CapState { Unknown = 0, Ok = 1, Denied = 2 }

/// <summary>富交互运行态快照，供面板展示与日志排障。</summary>
public readonly record struct RichUiSnapshot(
    bool ConfigEnabled,
    bool MarkdownAllowedByConfig,
    GameKeyboardMode ConfiguredKeyboardMode,
    string MarkdownState,
    string KeyboardState,
    int MarkdownDeniedCode,
    int KeyboardDeniedCode,
    DateTime? MarkdownDeniedAt,
    DateTime? KeyboardDeniedAt,
    int AllowListGroupCount,
    bool InteractionIntentConfigured,
    bool InteractionEventsReady,
    long RichSent,
    long MarkdownSent,
    long KeyboardSent,
    long LinkOnlySent,
    long PlainFallback,
    long DegradeRetries);

/// <summary>
/// 富交互能力门控（Singleton）。集中三件事，避免降级判断散落成堆的 if：
/// 1) 配置层是否允许（总开关 + 群白名单）；
/// 2) 平台层是否真的允许（首次发送被拒后记 Denied，TTL 到期再给一次探测机会）；
/// 3) 运行计数（供面板与日志观察降级情况）。
/// 任何时刻被判定为不可用，调用方都必须能退回纯文本，保证消息可达。
/// </summary>
public sealed class RichUiCapability
{
    public const string CapMarkdown = "markdown";
    public const string CapKeyboard = "keyboard";
    public const string CapInteraction = "interaction";

    private readonly RichUiOptions _opt;
    private readonly ConcurrentDictionary<string, CapState> _states = new()
    {
        [CapMarkdown] = CapState.Unknown,
        [CapKeyboard] = CapState.Unknown,
    };
    private readonly ConcurrentDictionary<string, DateTime> _deniedAt = new();
    private readonly ConcurrentDictionary<string, int> _deniedCode = new();

    // 网关发现 INTERACTION intent 未获授权而摘除时置 1：此时绝不能下发回调按钮，
    // 否则用户点下去没人结算，客户端只会一直转圈到超时。
    private int _interactionUnavailable;

    private long _markdownSent, _keyboardSent, _linkOnlySent, _plainFallback, _degradeRetries, _richSent;

    public RichUiCapability(IOptions<QQBotOptions> options)
    {
        _opt = options.Value.RichUi;
    }

    /// <summary>原始配置（键盘行列上限、label 上限等约束由调用方读取）。</summary>
    public RichUiOptions Options => _opt;

    /// <summary>总开关 + 群白名单。groupId 为 null（私聊等场景）时仅看总开关与白名单是否为空。</summary>
    public bool RichEnabled(string? groupId)
    {
        if (!_opt.Enabled) return false;
        if (_opt.GroupAllowList.Count == 0) return true;
        return groupId is not null && _opt.GroupAllowList.Contains(groupId);
    }

    /// <summary>是否使用 markdown（msg_type=2）。被平台拒绝过则退回纯文本。</summary>
    public bool UseMarkdown(string? groupId) =>
        RichEnabled(groupId) && _opt.Markdown && StateOf(CapMarkdown) != CapState.Denied;

    /// <summary>
    /// 实际生效的键盘模式。键盘只能挂在 markdown 消息上，故 markdown 不可用时强制 None；
    /// 键盘被平台拒绝时同样降为 None（此时若开了文字链则用文字链替身）；
    /// Callback 模式必须 INTERACTION intent 已生效，否则自动退回 Command（避免按钮点了没人回应、3 秒超时）。
    /// </summary>
    public GameKeyboardMode EffectiveKeyboardMode(string? groupId)
    {
        if (!UseMarkdown(groupId)) return GameKeyboardMode.None;

        var configured = _opt.ResolveKeyboardMode();
        if (configured == GameKeyboardMode.None) return GameKeyboardMode.None;
        if (configured == GameKeyboardMode.Callback && !InteractionEventsReady) configured = GameKeyboardMode.Command;
        return StateOf(CapKeyboard) == CapState.Denied ? GameKeyboardMode.None : configured;
    }

    /// <summary>回调按钮可用的前提：订阅了 INTERACTION intent 且网关没因此断连降级。</summary>
    public bool InteractionEventsReady =>
        _opt.InteractionIntent && Volatile.Read(ref _interactionUnavailable) == 0;

    /// <summary>网关报告 intent 被拒（已自动摘除）→ 锁住回调按钮。</summary>
    public void ReportInteractionUnavailable()
    {
        Interlocked.Exchange(ref _interactionUnavailable, 1);
        _deniedAt[CapInteraction] = DateTime.UtcNow;
    }

    /// <summary>真正收到过 INTERACTION_CREATE → 回调链路已打通。</summary>
    public void ReportInteractionAvailable()
    {
        Interlocked.Exchange(ref _interactionUnavailable, 0);
        MarkOk(CapInteraction);
    }

    /// <summary>是否用 markdown 内嵌文字链替代按钮（无按钮权限时的可点替身）。</summary>
    public bool UseCmdInputLinks(string? groupId) =>
        EffectiveKeyboardMode(groupId) == GameKeyboardMode.None
        && _opt.CmdInputLinks && UseMarkdown(groupId);

    /// <summary>是否使用 &lt;qqbot-at-user&gt; 真实 @ 标签（文本与 markdown 消息均支持，不依赖 markdown）。</summary>
    public bool UseAtTags(string? groupId) => RichEnabled(groupId) && _opt.AtUserTags;

    /// <summary>是否在被动回复上附 message_reference 引用。</summary>
    public bool UseMessageReference(string? groupId) => RichEnabled(groupId) && _opt.MessageReference;

    /// <summary>键盘被拒后是否仍应尝试挂按钮。</summary>
    public bool KeyboardCapabilityIntact(string? groupId) =>
        EffectiveKeyboardMode(groupId) != GameKeyboardMode.None;

    /// <summary>上报某能力被平台拒绝：记 Denied + 错误码 + 时间，后续自动降级。</summary>
    public void ReportDenied(string cap, int errCode)
    {
        _states[cap] = CapState.Denied;
        _deniedAt[cap] = DateTime.UtcNow;
        _deniedCode[cap] = errCode;
    }

    /// <summary>上报某能力发送成功，锁定为 Ok（不再无谓降级）。</summary>
    public void MarkOk(string cap)
    {
        _states[cap] = CapState.Ok;
        _deniedAt.TryRemove(cap, out _);
    }

    /// <summary>被拒超过 TTL 后回到 Unknown，给一次重新探测的机会（权限可能刚申请下来）。</summary>
    private CapState StateOf(string cap)
    {
        if (!_states.TryGetValue(cap, out var st)) return CapState.Unknown;
        if (st != CapState.Denied) return st;
        if (_opt.CapabilityTtlMinutes <= 0) return st;
        if (_deniedAt.TryGetValue(cap, out var at)
            && (DateTime.UtcNow - at).TotalMinutes >= _opt.CapabilityTtlMinutes)
        {
            _states[cap] = CapState.Unknown;
            return CapState.Unknown;
        }
        return st;
    }

    // ===== 计数（Interlocked 保证并发安全，仅用于观测） =====

    public void CountRichSent() => Interlocked.Increment(ref _richSent);
    public void CountMarkdownSent() => Interlocked.Increment(ref _markdownSent);
    public void CountKeyboardSent() => Interlocked.Increment(ref _keyboardSent);
    public void CountLinkOnlySent() => Interlocked.Increment(ref _linkOnlySent);
    public void CountPlainFallback() => Interlocked.Increment(ref _plainFallback);
    public void CountDegradeRetry() => Interlocked.Increment(ref _degradeRetries);

    public RichUiSnapshot Snapshot() => new(
        ConfigEnabled: _opt.Enabled,
        MarkdownAllowedByConfig: _opt.Markdown,
        ConfiguredKeyboardMode: _opt.ResolveKeyboardMode(),
        MarkdownState: StateOf(CapMarkdown).ToString(),
        KeyboardState: StateOf(CapKeyboard).ToString(),
        MarkdownDeniedCode: _deniedCode.TryGetValue(CapMarkdown, out var mc) ? mc : 0,
        KeyboardDeniedCode: _deniedCode.TryGetValue(CapKeyboard, out var kc) ? kc : 0,
        MarkdownDeniedAt: _deniedAt.TryGetValue(CapMarkdown, out var mt) ? mt : null,
        KeyboardDeniedAt: _deniedAt.TryGetValue(CapKeyboard, out var kt) ? kt : null,
        AllowListGroupCount: _opt.GroupAllowList.Count,
        InteractionIntentConfigured: _opt.InteractionIntent,
        InteractionEventsReady: InteractionEventsReady,
        RichSent: Volatile.Read(ref _richSent),
        MarkdownSent: Volatile.Read(ref _markdownSent),
        KeyboardSent: Volatile.Read(ref _keyboardSent),
        LinkOnlySent: Volatile.Read(ref _linkOnlySent),
        PlainFallback: Volatile.Read(ref _plainFallback),
        DegradeRetries: Volatile.Read(ref _degradeRetries));

    /// <summary>测试与灰度自检用：强制清空探测状态。</summary>
    public void ResetForDiagnostics()
    {
        _states[CapMarkdown] = CapState.Unknown;
        _states[CapKeyboard] = CapState.Unknown;
        _deniedAt.Clear();
        _deniedCode.Clear();
    }
}
