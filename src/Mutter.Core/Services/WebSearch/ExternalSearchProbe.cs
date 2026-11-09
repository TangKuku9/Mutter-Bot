using System.Text.Json;

namespace Mutter.Core.Services.WebSearch;

/// <summary>
/// 「测试搜索」的判定与结果整理（面板 probe-search 调用）。
/// 与 WebSearchProbe（内建通道探测）一样走纯函数 + Runner 的分工，
/// 但外测不需要形状矩阵：一次真实检索成败即结论。
/// </summary>
public static class ExternalSearchProbe
{
    /// <summary>测试用的固定检索词：时效性事实，结果里出现当年份才算真命中。</summary>
    public const string TestQuery = "今天 新闻 头条";

    /// <summary>
    /// 判定测试结果。命中条数 &gt; 0 即 verified；HTTP 成功但 0 条 = no_results
    ///（提示换词或检查服务），鉴权失败 = auth_failed，其余 = failed。
    /// </summary>
    public static (string Verdict, string Detail) Judge(
        WebSearchOutcome outcome, WebSearchFailure? failure, string? providerId = null)
    {
        if (failure is not null)
        {
            return failure.Kind switch
            {
                WebSearchFailureKind.Auth => ("auth_failed",
                    // 只有 SearXNG 的 403 才可能是「实例没开 JSON 输出」；
                    // 其他服务商的 403 一律按 Key/地址问题提示，避免误导。
                    failure.HttpStatus == 403 && failure.Reason.Contains("JSON")
                    && providerId == "searxng"
                        ? "实例未开启 JSON 输出：请在 SearXNG 的 settings.yml 中给 search.formats 加上 json"
                        : providerId == "searxng"
                            ? "实例拒绝访问：请检查地址、反向代理鉴权，以及实例是否允许 JSON 输出"
                            : "鉴权被拒：请检查 API Key 或服务地址"),
                WebSearchFailureKind.Disabled => ("failed", "配置不完整或未启用"),
                _ => ("failed", $"检索失败：{failure.Reason}")
            };
        }

        if (outcome.IsEmpty)
            return ("no_results", "服务连通，但 0 条结果：建议更换检索词或检查实例质量");

        var first = outcome.Hits[0];
        return ("verified",
            $"命中 {outcome.Hits.Count} 条，示例：{Truncate(first.Title, 60)}");
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
