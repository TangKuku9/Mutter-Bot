using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Services;

namespace Mutter.Infrastructure.Repositories;

public sealed class GroupStickerLibrary(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<GroupStickerLibrary> logger, string directory)
    : IGroupStickerLibrary
{
    internal const int MaxPerGroup = 200;
    private const long MaxTotalBytes = 512L * 1024 * 1024;
    private readonly SemaphoreSlim _files = new(1);
    private readonly Channel<NamingJob> _namingJobs = Channel.CreateBounded<NamingJob>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });

    internal sealed record NamingJob(string GroupId, string Hash, DateTime FirstSeenAt);
    private sealed record StoredSticker(string Hash, string Label, bool IsEnabled, DateTime FirstSeenAt, int Bytes,
        byte[]? VisualFingerprint);

    internal IAsyncEnumerable<NamingJob> ReadNamingJobsAsync(CancellationToken ct) => _namingJobs.Reader.ReadAllAsync(ct);

    public async Task<IReadOnlyList<GroupSticker>> ListAsync(string groupId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await Project(Db(scope).GroupStickers.AsNoTracking().Where(row => row.GroupId == groupId)
            .OrderByDescending(row => row.LastSeenAt).ThenBy(row => row.Hash)).Take(MaxPerGroup).ToListAsync(ct);
    }

    public async Task<StickerChoices> ChoicesAsync(string groupId, bool proactive, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = Db(scope);
        var now = clock.GetUtcNow().UtcDateTime;
        var policy = await ReadAvailablePolicyAsync(db, groupId, proactive, now, ct);
        if (policy is null) return StickerChoices.Empty;
        var items = await Project(Eligible(db, groupId, now)
            .OrderBy(row => row.SentCount).ThenByDescending(row => row.LastSeenAt).ThenBy(row => row.Hash))
            .Take(8).ToListAsync(ct);
        return new StickerChoices(items, proactive ? policy.StickerProactiveTendency : policy.StickerReplyTendency);
    }

    internal async Task<bool> CanCollectAsync(string groupId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await CollectionEnabledAsync(Db(scope), groupId, ct);
    }

    public async Task<bool> CanSendAsync(string groupId, string hash, bool proactive, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = Db(scope);
        var now = clock.GetUtcNow().UtcDateTime;
        return await ReadAvailablePolicyAsync(db, groupId, proactive, now, ct) is not null
            && await Eligible(db, groupId, now).AnyAsync(row => row.Hash == hash, ct);
    }

    public async Task<string?> ImportAsync(string groupId, string? label, byte[] bytes, CancellationToken ct)
    {
        if (await StaticStickerImage.PrepareAsync(bytes, ct) is not { } prepared)
            return "图片无法入库。请选择 16 MiB 以内、宽高各不超过 8192 像素且总计不超过 2500 万像素的静态 PNG、JPEG 或 WebP";
        return await StoreAsync(groupId, label, prepared, fromChat: false, ct)
            ? null : "图库容量已满，或所选群不存在";
    }

    internal async Task CollectAsync(string groupId, string label, byte[] bytes, CancellationToken ct)
    {
        if (await StaticStickerImage.PrepareAsync(bytes, ct) is { } prepared)
            await StoreAsync(groupId, label, prepared, fromChat: true, ct);
    }

    private async Task<bool> StoreAsync(string groupId, string? label, StaticStickerImage.Prepared prepared, bool fromChat,
        CancellationToken ct)
    {
        var (file, fingerprint) = prepared;
        var fileHash = Convert.ToHexStringLower(SHA256.HashData(file.Bytes));
        await _files.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = Db(scope);
            var entries = db.GroupStickers.Where(row => row.GroupId == groupId);
            var compareVisuals = fingerprint.Length > 0
                && !await entries.AnyAsync(row => row.Hash == fileHash && row.Label != "", ct);
            if (compareVisuals) await FingerprintLegacyAssetsAsync(db, entries, ct);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (fromChat ? !await CollectionEnabledAsync(db, groupId, ct)
                : !await db.KnownGroups.AnyAsync(group => group.GroupOpenId == groupId, ct)) return false;

            var now = clock.GetUtcNow().UtcDateTime;
            var cleanLabel = NormalizeLabel(label);
            var groupEntry = await db.GroupStickers.FindAsync([groupId, fileHash], ct);
            if (compareVisuals && string.IsNullOrEmpty(groupEntry?.Label))
            {
                var candidates = await WithAssets(db, entries).ToListAsync(ct);
                var match = PreferredOrder(candidates).FirstOrDefault(row => row.Hash == fileHash
                    || (StickerVisualFingerprint.Matches(row.VisualFingerprint, fingerprint) && HasStoredFile(row.Hash, row.Bytes)));
                if (match is not null && match.Hash != fileHash)
                    groupEntry = await db.GroupStickers.FindAsync([groupId, match.Hash], ct);
            }
            var hash = groupEntry?.Hash ?? fileHash;
            var shouldName = false;
            if (groupEntry is not null)
            {
                if (fromChat)
                {
                    groupEntry.LastSeenAt = now;
                    if (groupEntry.SeenCount < int.MaxValue) groupEntry.SeenCount++;
                }
                else if (cleanLabel.Length > 0)
                    groupEntry.Label = cleanLabel;
            }
            else
            {
                if (await db.GroupStickers.CountAsync(row => row.GroupId == groupId, ct) >= MaxPerGroup) return false;
                db.GroupStickers.Add(new GroupStickerEntity
                {
                    GroupId = groupId, Hash = hash, Label = cleanLabel, IsEnabled = true,
                    SeenCount = fromChat ? 1 : 0, FirstSeenAt = now, LastSeenAt = now
                });
                shouldName = cleanLabel.Length == 0
                    && await db.GroupConfigs.AnyAsync(row => row.GroupId == groupId && row.AutoNameStickers, ct);
            }

            if (hash == fileHash)
            {
                var asset = await db.StickerAssets.FindAsync([hash], ct);
                if (asset is null)
                {
                    if (await db.StickerAssets.SumAsync(row => (long)row.Bytes, ct) + file.Bytes.Length > MaxTotalBytes)
                        return false;
                    db.StickerAssets.Add(new StickerAssetEntity
                    {
                        Hash = hash, ContentType = file.ContentType, Width = file.Width, Height = file.Height,
                        Bytes = file.Bytes.Length, VisualFingerprint = fingerprint
                    });
                }
                else if (asset.VisualFingerprint is null || (asset.VisualFingerprint.Length == 0 && fingerprint.Length > 0))
                    asset.VisualFingerprint = fingerprint;

                var path = PathFor(hash);
                if (!File.Exists(path)) await WriteAsync(path, file.Bytes, ct);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (shouldName && !_namingJobs.Writer.TryWrite(new NamingJob(groupId, hash, now)))
                logger.LogDebug("表情自动取名队列已满 group={GroupId}", groupId);
            return true;
        }
        finally { _files.Release(); }
    }

    public async Task<StickerCopyResult> CopyAsync(string sourceGroupId, string? targetGroupId,
        IReadOnlyList<string>? hashes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetGroupId) || targetGroupId == sourceGroupId
            || hashes is null || hashes.Count is 0 or > MaxPerGroup || !hashes.All(IsHash))
            return new(StickerCopyStatus.InvalidSelection);

        var selected = hashes.Distinct(StringComparer.Ordinal).ToArray();
        await _files.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = Db(scope);
            var sourceRows = db.GroupStickers.Where(row => row.GroupId == sourceGroupId && selected.Contains(row.Hash));
            var targetRows = db.GroupStickers.Where(row => row.GroupId == targetGroupId);
            await FingerprintLegacyAssetsAsync(db, db.GroupStickers.Where(row => row.GroupId == targetGroupId
                || (row.GroupId == sourceGroupId && selected.Contains(row.Hash))), ct);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.KnownGroups.CountAsync(group => group.GroupOpenId == sourceGroupId
                    || group.GroupOpenId == targetGroupId, ct) != 2)
                return new(StickerCopyStatus.UnknownGroup);

            var source = await WithAssets(db, sourceRows).ToListAsync(ct);
            if (source.Count != selected.Length) return new(StickerCopyStatus.StickerUnavailable);

            var targetHashes = await targetRows.Select(row => row.Hash).ToListAsync(ct);
            var existing = targetHashes.ToHashSet(StringComparer.Ordinal);
            var target = await WithAssets(db, targetRows).ToListAsync(ct);
            var fingerprints = target.Where(row => row.VisualFingerprint is { Length: > 0 } && HasStoredFile(row.Hash, row.Bytes))
                .Select(row => row.VisualFingerprint).ToList();
            var additions = new List<StoredSticker>();
            foreach (var row in PreferredOrder(source))
            {
                if (existing.Contains(row.Hash)
                    || fingerprints.Any(candidate => StickerVisualFingerprint.Matches(candidate, row.VisualFingerprint))) continue;
                additions.Add(row);
                fingerprints.Add(row.VisualFingerprint);
            }
            var available = Math.Max(0, MaxPerGroup - targetHashes.Count);
            if (additions.Count > available) return new(StickerCopyStatus.TargetFull, Available: available);
            if (additions.Any(row => !HasStoredFile(row.Hash, row.Bytes)))
                return new(StickerCopyStatus.StickerUnavailable);

            // 共用已入库的文件。目标群的出现次数、发送次数和发送间隔从新条目开始记录。
            var now = clock.GetUtcNow().UtcDateTime;
            db.GroupStickers.AddRange(additions.Select(row => new GroupStickerEntity
            {
                GroupId = targetGroupId, Hash = row.Hash, Label = row.Label, IsEnabled = row.IsEnabled,
                FirstSeenAt = now, LastSeenAt = now
            }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(StickerCopyStatus.Success, additions.Count, source.Count - additions.Count);
        }
        finally { _files.Release(); }
    }

    public async Task<StickerFile?> ReadAsync(string groupId, string hash, bool forReply, CancellationToken ct)
    {
        if (!IsHash(hash)) return null;
        using var scope = scopes.CreateScope();
        var db = Db(scope);
        var asset = await (from entry in db.GroupStickers
                           join blob in db.StickerAssets on entry.Hash equals blob.Hash
                           where entry.GroupId == groupId && entry.Hash == hash
                               && (!forReply || (entry.IsEnabled && entry.Label != ""
                                   && db.GroupConfigs.Any(c => c.GroupId == groupId
                                       && c.IsEnabled && (c.UseStickersInReplies || c.UseStickersInProactive))))
                           select new { blob.Bytes, blob.ContentType, blob.Width, blob.Height }).SingleOrDefaultAsync(ct);
        if (asset is null) return null;
        var data = await ReadBytesAsync(hash, asset.Bytes, ct);
        return data is null ? null : new StickerFile(data, asset.ContentType, asset.Width, asset.Height);
    }

    private async Task<byte[]?> ReadBytesAsync(string hash, int bytes, CancellationToken ct)
    {
        if (!IsHash(hash) || bytes is <= 0 or > StickerSettings.MaxStoredBytes) return null;
        try
        {
            await using var file = new FileStream(PathFor(hash), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous);
            if (file.Length != bytes) return null;
            var data = new byte[bytes];
            await file.ReadExactlyAsync(data, ct);
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task<bool> UpdateAsync(string groupId, string hash, string? label, bool isEnabled, CancellationToken ct,
        bool preserveLabel = false)
    {
        using var scope = scopes.CreateScope();
        var clean = NormalizeLabel(label);
        return await Db(scope).GroupStickers.Where(row => row.GroupId == groupId && row.Hash == hash)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Label, row => preserveLabel ? row.Label : clean)
                .SetProperty(row => row.IsEnabled, isEnabled), ct) != 0;
    }

    internal async Task<bool> CanAutoNameAsync(NamingJob job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await AutoNameCandidates(Db(scope), job).AnyAsync(ct);
    }

    internal async Task ApplyAutoNameAsync(NamingJob job, string label, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await AutoNameCandidates(Db(scope), job).ExecuteUpdateAsync(set => set.SetProperty(row => row.Label, label), ct);
    }

    private static IQueryable<GroupStickerEntity> AutoNameCandidates(AppDbContext db, NamingJob job) =>
        db.GroupStickers.Where(row => row.GroupId == job.GroupId && row.Hash == job.Hash
            && row.FirstSeenAt == job.FirstSeenAt && row.IsEnabled && row.Label == ""
            && db.GroupConfigs.Any(config => config.GroupId == job.GroupId && config.AutoNameStickers));

    public async Task<bool> DeleteAsync(string groupId, string hash, CancellationToken ct)
    {
        if (!IsHash(hash)) return false;
        await _files.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = Db(scope);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var deleted = await db.GroupStickers.Where(row => row.GroupId == groupId && row.Hash == hash)
                .ExecuteDeleteAsync(ct);
            if (deleted == 0) return false;
            var unreferenced = !await db.GroupStickers.AnyAsync(row => row.Hash == hash, ct);
            if (unreferenced)
                await db.StickerAssets.Where(row => row.Hash == hash).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            if (unreferenced) TryDelete(PathFor(hash));
            return true;
        }
        finally { _files.Release(); }
    }

    public async Task RecordSentAsync(string groupId, string hash, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var now = clock.GetUtcNow().UtcDateTime;
        await Db(scope).GroupStickers.Where(row => row.GroupId == groupId && row.Hash == hash)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.SentCount, row => row.SentCount + 1)
                .SetProperty(row => row.LastSentAt, now), ct);
    }

    internal async Task ReconcileFilesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return;
        using var scope = scopes.CreateScope();
        var hashes = (await Db(scope).StickerAssets.Select(asset => asset.Hash).ToListAsync(ct)).ToHashSet();
        foreach (var path in Directory.EnumerateFiles(directory, "*.image"))
        {
            var hash = Path.GetFileNameWithoutExtension(path);
            if (IsHash(hash) && !hashes.Contains(hash)) TryDelete(path);
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.part"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == 97 && name[64] == '.' && IsHash(name[..64])
                && Guid.TryParseExact(name[65..], "N", out _)) TryDelete(path);
        }
    }

    private async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.part");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path);
        }
        finally { TryDelete(temp); }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning("无法移除未引用的表情包文件"); }
    }

    private static Task<bool> CollectionEnabledAsync(AppDbContext db, string groupId, CancellationToken ct) =>
        db.GroupConfigs.AnyAsync(c => c.GroupId == groupId && c.IsEnabled && c.AutoCollectStickers, ct);

    private static IQueryable<GroupStickerEntity> Eligible(AppDbContext db, string groupId, DateTime now)
    {
        var since = now.AddMinutes(-5);
        return db.GroupStickers.AsNoTracking().Where(row => row.GroupId == groupId && row.IsEnabled
            && row.Label != "" && (row.LastSentAt == null || row.LastSentAt <= since));
    }

    private static async Task<GroupConfigEntity?> ReadAvailablePolicyAsync(
        AppDbContext db, string groupId, bool proactive, DateTime now, CancellationToken ct)
    {
        var policy = await db.GroupConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.GroupId == groupId
            && c.IsEnabled && (proactive ? c.AiProactiveEnabled && c.UseStickersInProactive : c.UseStickersInReplies), ct);
        if (policy is null) return null;
        var interval = StickerSettings.NormalizeInterval(policy.StickerIntervalMinutes);
        if (interval == 0) return policy;
        var since = now.AddMinutes(-interval);
        return await db.GroupStickers.AnyAsync(row => row.GroupId == groupId && row.LastSentAt > since, ct)
            ? null : policy;
    }

    private static IQueryable<GroupSticker> Project(IQueryable<GroupStickerEntity> entries) =>
        entries.Select(row => new GroupSticker(row.Hash, row.Label, row.IsEnabled, row.SeenCount,
            row.SentCount, row.FirstSeenAt, row.LastSeenAt));

    private static IQueryable<StoredSticker> WithAssets(AppDbContext db, IQueryable<GroupStickerEntity> entries) =>
        from entry in entries
        join asset in db.StickerAssets on entry.Hash equals asset.Hash
        select new StoredSticker(entry.Hash, entry.Label, entry.IsEnabled, entry.FirstSeenAt, asset.Bytes, asset.VisualFingerprint);

    private static IOrderedEnumerable<StoredSticker> PreferredOrder(IEnumerable<StoredSticker> entries) =>
        entries.OrderByDescending(row => row.Label.Length > 0).ThenBy(row => row.FirstSeenAt)
            .ThenBy(row => row.Hash, StringComparer.Ordinal);

    private async Task FingerprintLegacyAssetsAsync(AppDbContext db, IQueryable<GroupStickerEntity> entries, CancellationToken ct)
    {
        // 旧库按需补指纹，图像解码期间不持有 SQLite 写事务。
        var hashes = entries.Select(row => row.Hash);
        var legacy = await db.StickerAssets.Where(asset => asset.VisualFingerprint == null && hashes.Contains(asset.Hash))
            .ToListAsync(ct);
        if (legacy.Count == 0) return;

        foreach (var asset in legacy)
        {
            if (!HasStoredFile(asset.Hash, asset.Bytes)) continue;
            var bytes = await ReadBytesAsync(asset.Hash, asset.Bytes, ct);
            if (bytes is not null)
                asset.VisualFingerprint = (await StaticStickerImage.PrepareAsync(bytes, ct))?.Fingerprint ?? [];
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static string NormalizeLabel(string? label) =>
        new string((label ?? string.Empty).Trim().Where(c => !char.IsControl(c)).Take(StickerSettings.MaxLabelLength).ToArray()).Trim();

    private static AppDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();
    private string PathFor(string hash) => Path.Combine(directory, hash + ".image");
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private bool HasStoredFile(string hash, int bytes)
    {
        if (!IsHash(hash) || bytes is <= 0 or > StickerSettings.MaxStoredBytes) return false;
        var file = new FileInfo(PathFor(hash));
        return file.Exists && file.Length == bytes;
    }
}
