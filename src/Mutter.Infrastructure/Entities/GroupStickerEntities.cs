namespace Mutter.Infrastructure.Entities;

public sealed class StickerAssetEntity
{
    public string Hash { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bytes { get; set; }
    public byte[]? VisualFingerprint { get; set; }
}

public sealed class GroupStickerEntity
{
    public string GroupId { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int SeenCount { get; set; }
    public int SentCount { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? LastSentAt { get; set; }
}

public sealed class QqStickerMaterialEntity
{
    public string AppId { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string ImageId { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
