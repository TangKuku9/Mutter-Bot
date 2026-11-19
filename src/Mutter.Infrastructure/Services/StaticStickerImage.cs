using System.Buffers.Binary;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using SkiaSharp;

namespace Mutter.Infrastructure.Services;

internal static class StaticStickerImage
{
    private const int MaxSourceSide = 8192;
    private const int MaxSourcePixels = 25_000_000;
    private static readonly SemaphoreSlim DecodeSlot = new(1);

    internal sealed record Prepared(StickerFile File, byte[] Fingerprint);

    public static async Task<Prepared?> PrepareAsync(byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (bytes.Length is < 12 or > StickerSettings.MaxSourceBytes) return null;
        var (type, (width, height), lossy) = Inspect(bytes);
        if (width is <= 0 or > MaxSourceSide || height is <= 0 or > MaxSourceSide
            || width * height > MaxSourcePixels) return null;

        await DecodeSlot.WaitAsync(ct);
        try { return DecodeAndPrepare(new StickerFile(bytes, type, width, height), lossy, ct); }
        finally { DecodeSlot.Release(); }
    }

    private static (string Type, (int Width, int Height) Size, bool Lossy) Inspect(ReadOnlySpan<byte> data)
    {
        if (data[0] == 137 && data[1..].StartsWith("PNG"u8)) return ("image/png", ReadPng(data), false);
        if (data.StartsWith("RIFF"u8) && data[8..].StartsWith("WEBP"u8))
            return ("image/webp", ReadWebp(data, out var lossy), lossy);
        return ("image/jpeg", ReadJpeg(data), true);
    }

    private static Prepared? DecodeAndPrepare(StickerFile original, bool lossy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (bytes, _, width, height) = original;
        var fits = bytes.Length <= StickerSettings.MaxStoredBytes
            && width <= StickerSettings.MaxStoredSide && height <= StickerSettings.MaxStoredSide;
        // 合规大小的旧格式仍可使用文件摘要精确去重，即使解码器无法生成画面指纹。
        var fallback = fits ? new Prepared(original, []) : null;
        using var input = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(input);
        if (codec is null || codec.Info.Width != width || codec.Info.Height != height || codec.FrameCount > 1)
            return fallback;

        using var colorSpace = SKColorSpace.CreateSrgb();
        var alpha = codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var sourceInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, alpha, colorSpace);
        using var source = new SKBitmap();
        if (!source.TryAllocPixels(sourceInfo) || codec.GetPixels(sourceInfo, source.GetPixels()) != SKCodecResult.Success)
            return fallback;
        source.SetImmutable();
        using var image = SKImage.FromBitmap(source);

        var origin = codec.EncodedOrigin;
        var rotated = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var displayed = rotated ? new SKSizeI(height, width) : new SKSizeI(width, height);
        var transform = Orientation(origin, width, height);
        var sampleSide = StickerVisualFingerprint.SampleSide;
        using var sample = Render(image, displayed, transform, sourceInfo.WithSize(sampleSide, sampleSide),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        var fingerprint = StickerVisualFingerprint.Create(source.GetPixelSpan(), sample.GetPixelSpan(),
            displayed.Width, displayed.Height, (byte)origin, lossy);
        ct.ThrowIfCancellationRequested();

        var file = fits ? original : Compress(image, displayed, transform, sourceInfo, ct);
        return file is null ? null : new Prepared(file, fingerprint);
    }

    private static StickerFile? Compress(SKImage image, SKSizeI displayed, SKMatrix transform, SKImageInfo imageInfo,
        CancellationToken ct)
    {
        var longestSide = Math.Max(displayed.Width, displayed.Height);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        for (var side = Math.Min(longestSide, StickerSettings.MaxStoredSide); ; side = side * 3 / 4)
        {
            ct.ThrowIfCancellationRequested();
            var targetWidth = Math.Max(1, (int)Math.Round(displayed.Width * (double)side / longestSide));
            var targetHeight = Math.Max(1, (int)Math.Round(displayed.Height * (double)side / longestSide));
            using var output = Render(image, displayed, transform, imageInfo.WithSize(targetWidth, targetHeight), sampling);
            using var encoded = output.Encode(SKEncodedImageFormat.Webp, 86);
            ct.ThrowIfCancellationRequested();
            if (encoded is null || encoded.Size == 0) return null;
            if (encoded.Size <= StickerSettings.MaxStoredBytes)
                return new StickerFile(encoded.ToArray(), "image/webp", targetWidth, targetHeight);
            if (side <= 256) return null;
        }
    }

    private static SKBitmap Render(SKImage image, SKSizeI displayed, SKMatrix transform, SKImageInfo outputInfo,
        SKSamplingOptions sampling)
    {
        var output = new SKBitmap(outputInfo);
        try
        {
            using var canvas = new SKCanvas(output);
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)outputInfo.Width / displayed.Width, (float)outputInfo.Height / displayed.Height);
            canvas.Concat(transform);
            canvas.DrawImage(image, 0, 0, sampling);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static SKMatrix Orientation(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity
    };

    private static (int Width, int Height) ReadPng(ReadOnlySpan<byte> data)
    {
        if (data.Length < 45 || !data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return default;
        if (BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != 13 || !data[12..16].SequenceEqual("IHDR"u8))
            return default;
        var width = BinaryPrimitives.ReadInt32BigEndian(data[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(data[20..]);
        var hasPixels = false;
        for (var index = 8; index <= data.Length - 12;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[index..]);
            if (length > data.Length - index - 12) return default;
            var chunk = data.Slice(index + 4, 4);
            index += (int)length + 12;
            if (chunk.SequenceEqual("acTL"u8) || chunk.SequenceEqual("fcTL"u8) || chunk.SequenceEqual("fdAT"u8))
                return default;
            if (chunk.SequenceEqual("IDAT"u8) && length > 0) hasPixels = true;
            if (chunk.SequenceEqual("IEND"u8))
                return hasPixels && length == 0 && index == data.Length ? (width, height) : default;
        }
        return default;
    }

    private static (int Width, int Height) ReadWebp(ReadOnlySpan<byte> data, out bool lossy)
    {
        lossy = false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != data.Length - 8) return default;
        (int Width, int Height) size = default;
        (int Width, int Height) canvas = default;
        for (var index = 12; index < data.Length;)
        {
            if (data.Length - index < 8) return default;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data[(index + 4)..]);
            if (length > data.Length - index - 8) return default;
            var type = data.Slice(index, 4);
            var payload = data.Slice(index + 8, (int)length);
            index += 8 + (int)length + ((int)length & 1);
            if (index > data.Length || type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
                return default;
            if (type.SequenceEqual("VP8X"u8))
            {
                if (length != 10 || (payload[0] & 2) != 0 || index != 30) return default;
                canvas = (1 + ReadUInt24(payload[4..]), 1 + ReadUInt24(payload[7..]));
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (size != default || length < 10 || (payload[0] & 1) != 0
                    || !payload.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a })) return default;
                size = (BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 0x3fff,
                    BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 0x3fff);
                lossy = true;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                if (size != default || length < 5 || payload[0] != 0x2f) return default;
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
                size = ((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
            }
        }
        return canvas == default || canvas == size ? size : default;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;

    private static (int Width, int Height) ReadJpeg(ReadOnlySpan<byte> data)
    {
        if (data[0] != 0xff || data[1] != 0xd8 || data[^2] != 0xff || data[^1] != 0xd9) return default;
        (int Width, int Height) size = default;
        for (var index = 2; index < data.Length - 4;)
        {
            if (data[index++] != 0xff) return default;
            while (index < data.Length && data[index] == 0xff) index++;
            if (index + 3 > data.Length) return default;
            var marker = data[index++];
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[index..]);
            if (length < 2 || length > data.Length - index) return default;
            var payload = data.Slice(index + 2, length - 2);
            if (marker == 0xe2 && payload.StartsWith("MPF\0"u8)) return default;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (size != default || payload.Length < 6) return default;
                size = (BinaryPrimitives.ReadUInt16BigEndian(payload[3..]),
                    BinaryPrimitives.ReadUInt16BigEndian(payload[1..]));
            }
            index += length;
            if (marker == 0xda) return index < data.Length - 2 ? size : default;
        }
        return default;
    }
}
