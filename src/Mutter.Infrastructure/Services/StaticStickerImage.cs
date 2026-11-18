using System.Buffers.Binary;
using Mutter.Core.Interfaces;

namespace Mutter.Infrastructure.Services;

internal static class StaticStickerImage
{
    public const int MaxBytes = 3 * 1024 * 1024;
    private const int MaxSide = 2048;

    public static StickerFile? Read(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.Length is < 12 or > MaxBytes) return null;
        var (type, size) = data[0] == 137 && data[1..].StartsWith("PNG"u8)
            ? ("image/png", ReadPng(data))
            : data.StartsWith("RIFF"u8) && data[8..].StartsWith("WEBP"u8)
                ? ("image/webp", ReadWebp(data))
                : ("image/jpeg", ReadJpeg(data));
        return size.Width is > 0 and <= MaxSide && size.Height is > 0 and <= MaxSide
            ? new StickerFile(bytes, type, size.Width, size.Height) : null;
    }

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

    private static (int Width, int Height) ReadWebp(ReadOnlySpan<byte> data)
    {
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
