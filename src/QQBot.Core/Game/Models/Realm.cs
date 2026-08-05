namespace QQBot.Core.Game.Models;

/// <summary>
/// 境界阶梯定义。9 大境界，越高境界突破成功率越低。
/// Threshold = 从该境界突破所需修为；突破成功后修为清零进入下一境界。
/// </summary>
public static class Realm
{
    public record Definition(string Name, long Threshold, double BreakRate);

    public static readonly Definition[] Realms =
    {
        new("炼气", 100, 0.95),
        new("筑基", 800, 0.85),
        new("金丹", 2_400, 0.70),
        new("元婴", 7_200, 0.55),
        new("化神", 21_600, 0.40),       // 3x 递增，肝帝30天终局
        new("炼虚", 64_800, 0.30),
        new("合体", 194_400, 0.25),
        new("大乘", 583_200, 0.20),
        new("渡劫", 1_749_600, 0.15),
    };

    public static int MaxIndex => Realms.Length - 1;

    public static string NameOf(int index) =>
        (uint)index < Realms.Length ? Realms[index].Name : "飞升";

    public static bool IsMax(int index) => index >= MaxIndex;
}
