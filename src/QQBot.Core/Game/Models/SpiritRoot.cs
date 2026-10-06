namespace QQBot.Core.Game.Models;

/// <summary>
/// 灵根定义。五行灵根（金木水火土）+ 变异灵根（雷/冰）。
/// Grade 1 最优（天灵根），5 最差（杂灵根）。品级影响挂机效率与突破加成。
/// </summary>
public static class SpiritRoot
{
    public record Definition(int Type, string Name, string Element);

    public static readonly Definition[] Roots =
    {
        new(0, "金灵根", "金"),
        new(1, "木灵根", "木"),
        new(2, "水灵根", "水"),
        new(3, "火灵根", "火"),
        new(4, "土灵根", "土"),
        new(5, "雷灵根", "雷"), // 变异
        new(6, "冰灵根", "冰"), // 变异
    };

    public static string NameOf(int type) =>
        (uint)type < Roots.Length ? Roots[type].Name : "凡根";

    public static bool IsVariant(int type) => type is 5 or 6;

    /// <summary>挂机效率系数：品级越小越快。grade1=1.8, grade5=0.2。</summary>
    public static double RateFactor(int grade)
    {
        var g = Math.Clamp(grade, 1, 5);
        return 2.2 - g * 0.4; // 1→1.8, 2→1.4, 3→1.0, 4→0.6, 5→0.2
    }

    /// <summary>突破加成：品级越好成功率越高。grade1→+0.15, grade5→-0.15。</summary>
    public static double BreakthroughBonus(int grade)
    {
        var g = Math.Clamp(grade, 1, 5);
        return (3 - g) * 0.075; // 1→+0.15, 3→0, 5→-0.15
    }
}
