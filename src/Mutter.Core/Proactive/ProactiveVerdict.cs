using System.Text.Json.Serialization;

namespace Mutter.Core.Proactive;

/// <summary>
/// 择机发言的结构化判定结果（AI 输出）。只有一个决策口：Line 非空即发言。
/// 模型自报的置信度与 should_speak 布尔都已删除——前者不是概率、不可校准，
/// 后者会和 Line 互相打架（出现过"判定不发却已经写好一句话"被丢弃的情况）。
/// </summary>
public class ProactiveVerdict
{
    /// <summary>要发的原话（纯文本，QQ 不渲染 markdown）。空或 null 表示这次不说话。</summary>
    [JsonPropertyName("line")]
    public string? Line { get; set; }

    /// <summary>接的是哪个话题，仅用于面板留痕。</summary>
    [JsonPropertyName("topic")]
    public string? Topic { get; set; }

    /// <summary>
    /// 需要外部实时信息时才给出的检索词；空表示这次不必联网。
    /// 它只决定“要不要进入改写阶段”，不改变“说不说”的判断。
    /// </summary>
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    /// <summary>
    /// 附图的客观要点。改写那一步拿不到原图，只靠这段文字保留画面信息；
    /// 没附图或图与话题无关时为空。
    /// </summary>
    [JsonPropertyName("image_note")]
    public string? ImageNote { get; set; }

    /// <summary>不发言时的理由，仅用于面板留痕。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
