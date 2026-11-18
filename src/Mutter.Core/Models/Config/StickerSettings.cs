namespace Mutter.Core.Models.Config;

public static class StickerSettings
{
    public const int DefaultReplyTendency = 1;
    public const int DefaultProactiveTendency = 0;
    public const int DefaultIntervalMinutes = 3;
    public const int MaxIntervalMinutes = 60;
    public static int NormalizeTendency(int value) => Math.Clamp(value, 0, 2);
    public static int NormalizeInterval(int value) => Math.Clamp(value, 0, MaxIntervalMinutes);
}
