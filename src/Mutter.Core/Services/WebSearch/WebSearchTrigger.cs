using System.Text.RegularExpressions;

namespace Mutter.Core.Services.WebSearch;

/// <summary>
/// 「这条消息到底该不该外搜」的纯函数判定。auto 档下按特征触发，控制按条计费成本；
/// force 恒真（择机 Phase 2 / 两段式第二阶段已明确要检索）；off 恒假。
/// 抽成独立类是为了能被 ProactiveCheck 直接覆盖，并保持判定逻辑与 IO 完全解耦。
/// </summary>
public static partial class WebSearchTrigger
{
    /// <summary>搜索意图档位：auto=按特征判定，always=调用方已确定要检索，off=禁用。</summary>
    public enum Mode
    {
        Auto,
        Always,
        Off
    }

    /// <summary>时效/事实类关键词。命中即视为有检索价值。</summary>
    private static readonly string[] TimelyKeywords =
    [
        "新闻", "热搜", "今天", "今天早上", "今晚", "昨天", "明天", "本周", "这周", "周末",
        "上周", "下周", "最近", "最新", "刚刚", "现在", "目前", "今年", "去年", "今年内",
        "比分", "赛程", "战绩", "排名", "榜单", "冠军", "夺冠", "转会", "选秀",
        "天气", "气温", "下雨", "台风", "降温", "供暖", "停水", "停电",
        "价格", "股价", "汇率", "发布", "上线", "开服", "开票", "发售", "发售日",
        "疫情", "政策", "新规", "公告", "通知", "版本", "补丁", "更新",
        "election", "news", "today", "latest", "weather", "price", "release"
    ];

    /// <summary>疑问句式词。问事实类问题时检索收益最高。</summary>
    private static readonly string[] QuestionKeywords =
        ["吗", "呢", "多少", "几", "什么", "怎么", "为什么", "哪些", "是谁", "何时", "查", "搜", "?", "？"];

    [GeneratedRegex(@"https?://[^\s，。,、）)\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(19|20)\d{2}[年\-/\.](1[0-2]|0?[1-9])[月\-/\.]([12][0-9]|3[01]|0?[1-9])日?", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    /// <summary>
    /// 判定一条用户消息是否触发外部检索。
    /// 触发条件（auto 档）：带图 / 含 URL / 命中日期 / 命中时效关键词 / 疑问词 + 足够长的事实询问。
    /// 纯闲聊（无关键词、非疑问、无图无链接无日期）不检索，保持按需成本。
    /// </summary>
    public static bool ShouldSearch(string text, bool hasImages, Mode mode)
    {
        if (mode == Mode.Off) return false;
        if (mode == Mode.Always) return true;

        if (hasImages) return true;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (UrlRegex().IsMatch(trimmed)) return true;
        if (DateRegex().IsMatch(trimmed)) return true;

        foreach (var keyword in TimelyKeywords)
        {
            if (trimmed.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // 疑问词命中 + 文本有实质长度（太短的疑问通常是寒暄）才触发
        var isQuestion = QuestionKeywords.Any(k => trimmed.Contains(k, StringComparison.Ordinal));
        return isQuestion && trimmed.Length >= 8;
    }

    /// <summary>
    /// 从用户文本中提炼检索词（外搜没有两段式看图能力时的兜底）。
    /// 去前缀称呼、去空白、限长。不追求完美——真有图时 caption 才是主来源。
    /// </summary>
    public static string ExtractQuery(string text, int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        // 去掉常见“@机器人”类前缀残留与称呼
        normalized = Regex.Replace(normalized, @"^@?[^\s:：]{1,24}[:：]\s*", string.Empty);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
