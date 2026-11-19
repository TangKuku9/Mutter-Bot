namespace Mutter.Core.Models.Config;

public static class StickerSettings
{
    public const int MaxSourceBytes = 16 * 1024 * 1024;
    public const int MaxStoredBytes = 3 * 1024 * 1024;
    public const int MaxStoredSide = 2048;
    public const int MaxLabelLength = 60;
    public const int DefaultReplyTendency = 1;
    public const int DefaultProactiveTendency = 0;
    public const int DefaultIntervalMinutes = 3;
    public const int MaxIntervalMinutes = 60;
    public static int NormalizeTendency(int value) => Math.Clamp(value, 0, 2);
    public static int NormalizeInterval(int value) => Math.Clamp(value, 0, MaxIntervalMinutes);
}
