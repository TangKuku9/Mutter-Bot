using System.Buffers.Binary;

namespace Mutter.Core.Services;

/// <summary>用每次生成的四色排列验证模型读取了本次请求里的图片。</summary>
public sealed class WebSearchImageChallenge
{
    // 128×128 的索引色 PNG，左上、右上、左下、右下的调色板索引依次为 0、1、2、3。
    private const string PngTemplate =
        "iVBORw0KGgoAAAANSUhEUgAAAIAAAACAAgMAAAC+UIlYAAAADFBMVEX/AAAA/wAAAP///wDWAo97AAAALElEQVR42u3KMQEAIAAEoS9pSUvqfhlgZosTEwRBEARBSLjxQhAEQRAEIeEDYM/4aidIYMUAAAAASUVORK5CYII=";

    private const int PaletteOffset = 41;
    private const int PaletteLength = 12;
    private const string AnswerPrefix = "COLORS:";
    private static readonly (string Name, uint Rgb)[] Colors =
    [
        ("red", 0xff0000), ("green", 0x00b000), ("blue", 0x003bff), ("yellow", 0xffeb00),
        ("orange", 0xff8500), ("purple", 0x8e27cc), ("white", 0xffffff), ("black", 0x000000)
    ];

    private readonly string[] _expected;

    private WebSearchImageChallenge(byte[] png, string[] expected)
    {
        Image = new AiResolvedImage("image/png", png, string.Empty);
        _expected = expected;
    }

    public AiResolvedImage Image { get; }

    public const string Instruction =
        "第一行以 COLORS: 开头，按左上、右上、左下、右下顺序用逗号隔开写出图片的四种颜色。"
        + "使用英文颜色名 red、green、blue、yellow、orange、purple、white、black。然后完成以下任务。";

    public static WebSearchImageChallenge Create()
    {
        var colors = Colors.ToArray();
        Random.Shared.Shuffle(colors);
        var png = Convert.FromBase64String(PngTemplate);
        var expected = new string[4];
        for (var i = 0; i < expected.Length; i++)
        {
            var (name, rgb) = colors[i];
            var offset = PaletteOffset + i * 3;
            png[offset] = (byte)(rgb >> 16);
            png[offset + 1] = (byte)(rgb >> 8);
            png[offset + 2] = (byte)rgb;
            expected[i] = name;
        }

        // PNG 的 CRC 覆盖块类型 PLTE 和随后的 RGB 数据。
        var paletteChunk = png.AsSpan(PaletteOffset - 4, PaletteLength + 4);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(PaletteOffset + PaletteLength), ComputeCrc(paletteChunk));
        return new WebSearchImageChallenge(png, expected);
    }

    public bool Matches(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
        {
            var answer = line.Trim('*', '`', ' ');
            if (!answer.StartsWith(AnswerPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var colors = answer[AnswerPrefix.Length..].Trim('*', '`', ' ')
                .Split(',', StringSplitOptions.TrimEntries);
            return colors.SequenceEqual(_expected, StringComparer.OrdinalIgnoreCase);
        }
        return false;
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
        }
        return ~crc;
    }
}
