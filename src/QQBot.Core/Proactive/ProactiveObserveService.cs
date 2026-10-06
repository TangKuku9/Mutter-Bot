using System.Text.RegularExpressions;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Proactive;

/// <summary>
/// 择机发言的观察窗口与门禁。纯内存：机器人单进程部署，重启丢失窗口只是少评估一次，
/// 不值得为它建表；每日上限的重置按北京时间的日历日计数。
/// </summary>
public sealed class ProactiveObserveService
{
    /// <summary>窗口硬上限。评估被关掉或长时间失败时防止内存无界增长。</summary>
    private const int MaxBufferedMessages = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, GroupState> _states = new();
    private string _globalDate = string.Empty;
    private int _globalSentCount;

    private static readonly Regex SpaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>采集一条非 @ 群聊消息进本群窗口。文本为空或纯 @ 段则忽略。</summary>
    public void Observe(
        string groupId, string senderName, string text, string messageId,
        IReadOnlyList<string>? imageUrls = null)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0 || messageId.Length == 0) return;

        lock (_gate)
        {
            var state = GetState(groupId);
            if (state.Buffer.Count == 0)
                state.FirstBufferedAtUtc = DateTime.UtcNow;

            state.Buffer.Add(new ObservedMessage(
                senderName, normalized, messageId, DateTime.UtcNow, imageUrls));

            // 长期没人评估时窗口会一直涨，只保留最近的若干条，旧话题直接过期
            while (state.Buffer.Count > MaxBufferedMessages)
                state.Buffer.RemoveAt(0);
        }
    }

    /// <summary>窗口是否攒够到了该评估的时候。</summary>
    public bool IsDue(string groupId, ProactiveChatConfig config, DateTime utcNow)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(groupId, out var state) || state.Buffer.Count == 0) return false;
            // 首条消息攒够一个窗口时长
            if (utcNow - state.FirstBufferedAtUtc < TimeSpan.FromSeconds(config.WindowSeconds)) return false;
            // 两次评估至少隔一个窗口，防止消息不断流时评估连着打
            if (utcNow - state.LastEvaluatedUtc < TimeSpan.FromSeconds(config.WindowSeconds)) return false;
            if (utcNow - state.LastSentUtc < TimeSpan.FromMinutes(config.CooldownMinutes)) return false;
            return true;
        }
    }

    /// <summary>取走当前窗口并清空，只保留最近 maxMessages 条。取走后旧消息不再参与下一次评估。</summary>
    public ProactiveWindow? Take(string groupId, int maxMessages)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(groupId, out var state) || state.Buffer.Count == 0) return null;

            var snapshot = state.Buffer.ToArray();
            state.Buffer.Clear();
            state.FirstBufferedAtUtc = DateTime.MinValue;

            if (snapshot.Length > maxMessages)
                snapshot = snapshot[^maxMessages..];

            return new ProactiveWindow
            {
                GroupId = groupId,
                Messages = snapshot,
                LastMessageId = snapshot[^1].MessageId,
                LastMessageUtcAt = snapshot[^1].UtcAt
            };
        }
    }

    public void MarkEvaluated(string groupId, DateTime utcNow)
    {
        lock (_gate) GetState(groupId).LastEvaluatedUtc = utcNow;
    }

    /// <summary>
    /// 发言前的门禁复核：静默时段、同群间隔、群与全局日上限。
    /// 调用方应在拿到 AI 判定后再问一次，因为评估本身可能跨了几分钟。
    /// </summary>
    public string? BlockReason(string groupId, ProactiveChatConfig config, DateTime utcNow)
    {
        lock (_gate)
        {
            if (config.IsQuietNow(utcNow)) return ProactiveOutcomes.SkippedQuietHours;

            var state = GetState(groupId);
            if (utcNow - state.LastSentUtc < TimeSpan.FromMinutes(config.CooldownMinutes))
                return ProactiveOutcomes.SkippedCooldown;

            var today = BeijingDate(utcNow);
            RollDate(state, today);
            if (state.SentTodayCount >= config.MaxPerGroupPerDay) return ProactiveOutcomes.SkippedGroupCap;

            if (today != _globalDate)
            {
                _globalDate = today;
                _globalSentCount = 0;
            }
            if (_globalSentCount >= config.MaxGlobalPerDay) return ProactiveOutcomes.SkippedGlobalCap;

            return null;
        }
    }

    public void MarkSent(string groupId, DateTime utcNow)
    {
        lock (_gate)
        {
            var state = GetState(groupId);
            var today = BeijingDate(utcNow);
            RollDate(state, today);
            state.SentTodayCount++;
            state.LastSentUtc = utcNow;

            if (today != _globalDate)
            {
                _globalDate = today;
                _globalSentCount = 0;
            }
            _globalSentCount++;
        }
    }

    /// <summary>面板展示用：当前有消息待评估的群。</summary>
    public IReadOnlyList<(string GroupId, int Pending)> PendingWindows()
    {
        lock (_gate)
        {
            return _states.Where(kv => kv.Value.Buffer.Count > 0)
                .Select(kv => (kv.Key, kv.Value.Buffer.Count))
                .ToList();
        }
    }

    private static void RollDate(GroupState state, string today)
    {
        if (state.CountDate == today) return;
        state.CountDate = today;
        state.SentTodayCount = 0;
    }

    private GroupState GetState(string groupId)
    {
        if (!_states.TryGetValue(groupId, out var state))
        {
            state = new GroupState();
            _states[groupId] = state;
        }
        return state;
    }

    /// <summary>剥离 QQ 的 &lt;@!id&gt;/&lt;image&gt; 等占位段并压缩空白。</summary>
    private static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new System.Text.StringBuilder(text.Length);
        var inSegment = false;
        foreach (var c in text)
        {
            if (c == '<') { inSegment = true; continue; }
            if (inSegment)
            {
                if (c == '>') inSegment = false;
                continue;
            }
            sb.Append(c);
        }

        return SpaceRun.Replace(sb.ToString().Trim(), " ");
    }

    // 官方频控与"每日"都以北京日界为准；服务器时区可能不是 CST，故显式偏移而非取本地时间
    private static string BeijingDate(DateTime utcNow) => utcNow.AddHours(8).ToString("yyyy-MM-dd");

    private sealed class GroupState
    {
        public readonly List<ObservedMessage> Buffer = new();
        public DateTime FirstBufferedAtUtc;
        public DateTime LastEvaluatedUtc = DateTime.MinValue;
        public DateTime LastSentUtc = DateTime.MinValue;
        public string CountDate = string.Empty;
        public int SentTodayCount;
    }
}
