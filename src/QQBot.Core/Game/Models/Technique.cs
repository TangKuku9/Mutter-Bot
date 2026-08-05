namespace QQBot.Core.Game.Models;

/// <summary>
/// 功法定义。功法影响挂机效率与五行契合。
/// Tier 1~4 对应凡品/灵品/宝品/仙品，越高品级效率越高。
/// </summary>
public static class Technique
{
    public record Definition(string Id, string Name, string Element, double RateFactor, int Tier);

    public static readonly Definition[] Techniques =
    {
        // Tier 1 - 凡品（初始/低级）
        new("wuwei",    "无为诀",     "无", 1.00, 1),
        new("houtu",    "厚土经",     "土", 1.10, 1),
        // Tier 2 - 灵品（常见掉落）
        new("qingmu",   "青木功",     "木", 1.15, 2),
        new("fenghuo",  "烽火诀",     "火", 1.20, 2),
        new("xuanbing", "玄冰诀",     "水", 1.20, 2),
        new("gengjin",  "庚金剑诀",   "金", 1.25, 2),
        // Tier 3 - 宝品（稀有掉落）
        new("changsheng","万木长生功","木", 1.35, 3),
        new("fentian",  "焚天宝典",   "火", 1.40, 3),
        new("jiuyou",   "九幽寒霜诀", "水", 1.35, 3),
        new("taiyi",    "太乙剑经",   "金", 1.45, 3),
        new("yuandian", "大地源典",   "土", 1.30, 3),
        // Tier 4 - 仙品（极稀有掉落）
        new("hundun",   "混沌真经",   "无", 1.60, 4),
        new("leishen",  "天雷神诀",   "雷", 1.65, 4),
        new("bingpo",   "冰魄神功",   "冰", 1.65, 4),
    };

    public static string NameOf(string? id) =>
        Techniques.FirstOrDefault(t => t.Id == id)?.Name ?? "无";

    public static double RateFactorOf(string? id) =>
        Techniques.FirstOrDefault(t => t.Id == id)?.RateFactor ?? 1.0;

    public static string ElementOf(string? id) =>
        Techniques.FirstOrDefault(t => t.Id == id)?.Element ?? "无";

    public static int TierOf(string? id) =>
        Techniques.FirstOrDefault(t => t.Id == id)?.Tier ?? 0;

    public static string TierName(int tier) => tier switch
    {
        1 => "凡品",
        2 => "灵品",
        3 => "宝品",
        4 => "仙品",
        _ => "无"
    };

    /// <summary>
    /// 计算功法对指定灵根的有效效率系数。
    /// 功法属性与灵根属性一致时额外 +15% 加成。
    /// </summary>
    public static double EffectiveRateFactor(string? techniqueId, int spiritRootType)
    {
        var baseRate = RateFactorOf(techniqueId);
        var techElement = ElementOf(techniqueId);
        var rootElement = (uint)spiritRootType < SpiritRoot.Roots.Length
            ? SpiritRoot.Roots[spiritRootType].Element
            : null;

        if (!string.IsNullOrEmpty(techElement) && techElement != "无" && techElement == rootElement)
            return baseRate * 1.15;

        return baseRate;
    }

    /// <summary>
    /// 判断 newTechniqueId 是否比 currentTechniqueId 更好（对指定灵根而言）。
    /// </summary>
    public static bool IsBetter(string? newTechniqueId, string? currentTechniqueId, int spiritRootType)
    {
        return EffectiveRateFactor(newTechniqueId, spiritRootType)
             > EffectiveRateFactor(currentTechniqueId, spiritRootType);
    }

    /// <summary>获取指定品级的所有功法。</summary>
    public static Definition[] ByTier(int tier) =>
        Techniques.Where(t => t.Tier == tier).ToArray();
}
