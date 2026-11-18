namespace Mutter.Core.Models;

public class ParsedMessage
{
    public string PlainText { get; set; } = string.Empty;
    public bool HasImage { get; set; }
    public bool HasAtBot { get; set; }
    public List<string> ImageUrls { get; set; } = new();
    public StickerAttachment? Sticker { get; set; }
    public string RawSegmentsJson { get; set; } = string.Empty;
}

public sealed record StickerAttachment(string Url, string Label, int? AdvertisedSize);
