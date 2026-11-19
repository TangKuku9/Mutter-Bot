using Mutter.Core.Models;

namespace Mutter.Core.Interfaces;

public sealed record GroupSticker(string Hash, string Label, bool IsEnabled, int SeenCount, int SentCount,
    DateTime FirstSeenAt, DateTime LastSeenAt);

public sealed record StickerFile(byte[] Bytes, string ContentType, int Width, int Height);
public enum StickerCopyStatus { Success, InvalidSelection, UnknownGroup, StickerUnavailable, TargetFull }
public sealed record StickerCopyResult(StickerCopyStatus Status, int Copied = 0, int AlreadyInTarget = 0, int Available = 0);

public sealed record StickerChoices(IReadOnlyList<GroupSticker> Items, int Tendency)
{
    public static StickerChoices Empty { get; } = new([], 0);
}

public interface IGroupStickerCollector
{
    void TryEnqueue(string groupId, StickerAttachment attachment);
}

public interface IGroupStickerLibrary
{
    Task<IReadOnlyList<GroupSticker>> ListAsync(string groupId, CancellationToken ct);
    Task<StickerChoices> ChoicesAsync(string groupId, bool proactive, CancellationToken ct);
    Task<bool> CanSendAsync(string groupId, string hash, bool proactive, CancellationToken ct);
    Task<string?> ImportAsync(string groupId, string? label, byte[] bytes, CancellationToken ct);
    Task<StickerCopyResult> CopyAsync(string sourceGroupId, string? targetGroupId, IReadOnlyList<string>? hashes,
        CancellationToken ct);
    Task<StickerFile?> ReadAsync(string groupId, string hash, bool forReply, CancellationToken ct);
    Task<bool> UpdateAsync(string groupId, string hash, string? label, bool isEnabled, CancellationToken ct,
        bool preserveLabel = false);
    Task<bool> DeleteAsync(string groupId, string hash, CancellationToken ct);
    Task RecordSentAsync(string groupId, string hash, CancellationToken ct);
}

public interface IStickerMaterialService
{
    Task<string?> GetReferenceAsync(string hash, StickerFile file, CancellationToken ct);
    Task InvalidateAsync(string hash, string reference, CancellationToken ct);
}
