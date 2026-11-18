using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mutter.Core;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;

namespace Mutter.Infrastructure.Services;

public sealed class QqStickerMaterialService(
    QqBotApiService api, IHttpClientFactory http, IServiceScopeFactory scopes, IOptions<MutterOptions> options,
    TimeProvider clock, ILogger<QqStickerMaterialService> logger) : IStickerMaterialService
{
    private const int MaxManaged = 80;
    private const string Prefix = "qqbot-media://image/v1/";
    private readonly SemaphoreSlim _upload = new(1);
    private readonly Dictionary<string, DateTime> _leased = new(StringComparer.Ordinal);

    public async Task<string?> GetReferenceAsync(string hash, StickerFile file, CancellationToken ct)
    {
        var appId = options.Value.AppId;
        if (string.IsNullOrWhiteSpace(appId)) return null;
        await _upload.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var expired in _leased.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                _leased.Remove(expired);

            var owned = db.QqStickerMaterials.Where(item => item.AppId == appId);
            var cached = await owned.SingleOrDefaultAsync(item => item.Hash == hash, ct);
            if (cached is not null && cached.ExpiresAt > now.AddSeconds(35) && IsMaterialReference(cached.Reference))
                return Lease(hash, cached.Reference, now);

            if (cached is not null)
            {
                if (_leased.ContainsKey(hash)) return null;
                await DeleteOwnedAsync(db, cached, ct);
            }
            if (await owned.CountAsync(ct) >= MaxManaged && !await EvictAsync(db, owned, ct))
                return null;

            using var storage = http.CreateClient("qq-sticker-storage");
            (string ImageId, string Reference, int Ttl) uploaded;
            try { uploaded = await api.UploadStickerMaterialAsync(hash, file, storage, ct); }
            catch (QqApiException ex) when (ex.ErrCode == 40035007)
            {
                if (!await EvictAsync(db, owned, ct)) return null;
                uploaded = await api.UploadStickerMaterialAsync(hash, file, storage, ct);
            }
            db.QqStickerMaterials.Add(new QqStickerMaterialEntity
            {
                AppId = appId, Hash = hash, ImageId = uploaded.ImageId, Reference = uploaded.Reference,
                ExpiresAt = clock.GetUtcNow().UtcDateTime.AddSeconds(uploaded.Ttl)
            });
            await db.SaveChangesAsync(ct);
            return Lease(hash, uploaded.Reference, clock.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("QQ 表情素材准备失败 ({ErrorType})", ex.GetType().Name);
            return null;
        }
        finally { _upload.Release(); }
    }

    private async Task<bool> EvictAsync(AppDbContext db, IQueryable<QqStickerMaterialEntity> owned, CancellationToken ct)
    {
        var items = await owned.OrderBy(item => item.ExpiresAt).Take(MaxManaged).ToListAsync(ct);
        var victim = items.FirstOrDefault(item => !_leased.ContainsKey(item.Hash));
        if (victim is null) return false;
        await DeleteOwnedAsync(db, victim, ct);
        return true;
    }

    public async Task InvalidateAsync(string hash, string reference, CancellationToken ct)
    {
        await _upload.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var appId = options.Value.AppId;
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var expired = clock.GetUtcNow().UtcDateTime;
            await db.QqStickerMaterials.Where(item => item.AppId == appId && item.Hash == hash
                    && item.Reference == reference)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.ExpiresAt, expired), ct);
        }
        finally { _upload.Release(); }
    }

    private async Task DeleteOwnedAsync(AppDbContext db, QqStickerMaterialEntity material, CancellationToken ct)
    {
        await api.DeleteStickerMaterialAsync(material.ImageId, ct);
        db.QqStickerMaterials.Remove(material);
        await db.SaveChangesAsync(ct);
    }

    private string Lease(string hash, string reference, DateTime now)
    {
        _leased[hash] = now.AddMinutes(1);
        return reference;
    }

    internal static bool IsMaterialReference(string? reference) =>
        reference is not null && reference.Length > Prefix.Length && reference.Length <= 1024
        && reference.StartsWith(Prefix, StringComparison.Ordinal)
        && reference[Prefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '=' or '~');
}
