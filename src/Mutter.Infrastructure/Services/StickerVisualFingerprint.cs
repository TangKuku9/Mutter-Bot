using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Mutter.Infrastructure.Services;

internal static class StickerVisualFingerprint
{
    public const int SampleSide = 48;
    private const int Version = 1;
    private const int PixelHashOffset = 7;
    private const int SampleOffset = PixelHashOffset + SHA256.HashSizeInBytes;
    private const int SampleBytes = SampleSide * SampleSide * 4;
    private const int RegionSide = 6;
    private const int RegionsPerSide = SampleSide / RegionSide;
    private const int MaxRegionDifference = 14 * RegionSide * RegionSide;
    private const int MaxTotalDifference = 6 * SampleSide * SampleSide;

    public static byte[] Create(ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> sample, int width, int height,
        byte orientation, bool lossy)
    {
        // 记录显示尺寸、方向和原像素摘要。缩略图保留预乘透明度和颜色，用来核验转码差异。
        var fingerprint = new byte[SampleOffset + SampleBytes];
        fingerprint[0] = Version;
        fingerprint[1] = lossy ? (byte)1 : (byte)0;
        fingerprint[2] = orientation;
        BinaryPrimitives.WriteUInt16LittleEndian(fingerprint.AsSpan(3), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(fingerprint.AsSpan(5), (ushort)height);
        SHA256.HashData(pixels, fingerprint.AsSpan(PixelHashOffset, SHA256.HashSizeInBytes));
        sample.CopyTo(fingerprint.AsSpan(SampleOffset));
        return fingerprint;
    }

    public static bool Matches(byte[]? candidate, byte[]? incoming)
    {
        if (candidate is not { Length: SampleOffset + SampleBytes } || candidate[0] != Version
            || incoming is not { Length: SampleOffset + SampleBytes } || incoming[0] != Version) return false;

        var sameGeometry = candidate.AsSpan(2, 5).SequenceEqual(incoming.AsSpan(2, 5));
        if (sameGeometry)
        {
            if (candidate.AsSpan(PixelHashOffset, SHA256.HashSizeInBytes)
                .SequenceEqual(incoming.AsSpan(PixelHashOffset, SHA256.HashSizeInBytes))) return true;
            // 同尺寸的无损图必须像素一致，保留使用同一底图改字的表情变体。
            if (candidate[1] == 0 && incoming[1] == 0) return false;
        }

        var firstRatio = BinaryPrimitives.ReadUInt16LittleEndian(candidate.AsSpan(3))
            * BinaryPrimitives.ReadUInt16LittleEndian(incoming.AsSpan(5));
        var secondRatio = BinaryPrimitives.ReadUInt16LittleEndian(incoming.AsSpan(3))
            * BinaryPrimitives.ReadUInt16LittleEndian(candidate.AsSpan(5));
        if (40L * Math.Abs(firstRatio - secondRatio) > Math.Max(firstRatio, secondRatio)) return false;

        return SamplesMatch(candidate.AsSpan(SampleOffset), incoming.AsSpan(SampleOffset));
    }

    private static bool SamplesMatch(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        Span<int> regions = stackalloc int[RegionsPerSide * RegionsPerSide];
        regions.Clear();
        var total = 0;
        for (var y = 0; y < SampleSide; y++)
        for (var x = 0; x < SampleSide; x++)
        {
            var offset = (y * SampleSide + x) * 4;
            var red = Math.Abs(first[offset] - second[offset]);
            var green = Math.Abs(first[offset + 1] - second[offset + 1]);
            var blue = Math.Abs(first[offset + 2] - second[offset + 2]);
            var alpha = Math.Abs(first[offset + 3] - second[offset + 3]);
            var difference = Math.Max(Math.Max(red, green), Math.Max(blue, alpha));
            var region = y / RegionSide * RegionsPerSide + x / RegionSide;
            regions[region] += difference;
            total += difference;
            // 局部校验避免凭整图平均差吞掉位置集中的文字、表情或透明边界变化。
            if (regions[region] > MaxRegionDifference || total > MaxTotalDifference) return false;
        }
        return true;
    }
}
