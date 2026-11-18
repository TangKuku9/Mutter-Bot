using System.Security.Cryptography;
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
        if (StaticStickerImage.Read(bytes) is not { } file)
            return "请选择静态 PNG、JPEG 或 WebP，宽和高均不能超过 2048 像素";
        return await StoreAsync(groupId, label, file, fromChat: false, ct)
            ? null : "图库容量已满，或所选群不存在";
    }

    internal Task<bool> CollectAsync(string groupId, string label, StickerFile file, CancellationToken ct) =>
        StoreAsync(groupId, label, file, fromChat: true, ct);

    private async Task<bool> StoreAsync(string groupId, string? label, StickerFile file, bool fromChat, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(file.Bytes));
        await _files.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = Db(scope);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (fromChat ? !await CollectionEnabledAsync(db, groupId, ct)
                : !await db.KnownGroups.AnyAsync(group => group.GroupOpenId == groupId, ct)) return false;

            var now = clock.GetUtcNow().UtcDateTime;
            var groupEntry = await db.GroupStickers.FindAsync([groupId, hash], ct);
            if (groupEntry is not null)
            {
                if (fromChat)
                {
                    groupEntry.LastSeenAt = now;
                    if (groupEntry.SeenCount < int.MaxValue) groupEntry.SeenCount++;
                }
                else if (!string.IsNullOrWhiteSpace(label))
                    groupEntry.Label = NormalizeLabel(label);
            }
            else
            {
                if (await db.GroupStickers.CountAsync(row => row.GroupId == groupId, ct) >= MaxPerGroup) return false;
                db.GroupStickers.Add(new GroupStickerEntity
                {
                    GroupId = groupId, Hash = hash, Label = NormalizeLabel(label), IsEnabled = true,
                    SeenCount = fromChat ? 1 : 0, FirstSeenAt = now, LastSeenAt = now
                });
            }

            var asset = await db.StickerAssets.FindAsync([hash], ct);
            if (asset is null)
            {
                if (await db.StickerAssets.SumAsync(row => (long)row.Bytes, ct) + file.Bytes.Length > MaxTotalBytes)
                    return false;
                db.StickerAssets.Add(new StickerAssetEntity
                {
                    Hash = hash, ContentType = file.ContentType, Width = file.Width, Height = file.Height,
                    Bytes = file.Bytes.Length
                });
            }

            var path = PathFor(hash);
            if (!File.Exists(path)) await WriteAsync(path, file.Bytes, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
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
                           select blob).AsNoTracking().SingleOrDefaultAsync(ct);
        if (asset is null) return null;
        try
        {
            await using var file = new FileStream(PathFor(hash), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous);
            if (file.Length != asset.Bytes || asset.Bytes is <= 0 or > StaticStickerImage.MaxBytes) return null;
            var data = new byte[asset.Bytes];
            await file.ReadExactlyAsync(data, ct);
            return new StickerFile(data, asset.ContentType, asset.Width, asset.Height);
        }
        catch (IOException) { return null; }
    }

    public async Task<bool> UpdateAsync(string groupId, string hash, string? label, bool isEnabled, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var clean = NormalizeLabel(label);
        return await Db(scope).GroupStickers.Where(row => row.GroupId == groupId && row.Hash == hash)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Label, clean)
                .SetProperty(row => row.IsEnabled, isEnabled), ct) != 0;
    }

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

    private static string NormalizeLabel(string? label) =>
        new string((label ?? string.Empty).Trim().Where(c => !char.IsControl(c)).Take(60).ToArray()).Trim();

    private static AppDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();
    private string PathFor(string hash) => Path.Combine(directory, hash + ".image");
    private static bool IsHash(string hash) => hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
