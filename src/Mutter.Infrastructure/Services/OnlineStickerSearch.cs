using Microsoft.Extensions.DependencyInjection;
using Mutter.Core.Interfaces;
using Mutter.Core.Services.WebSearch;

namespace Mutter.Infrastructure.Services;

public sealed class OnlineStickerSearch(IServiceScopeFactory scopes, ExternalSearchService search, IHttpClientFactory http)
    : IOnlineStickerSearch
{
    private readonly SemaphoreSlim _slots = new(2);

    public async Task<StickerFile?> FindAsync(string query, CancellationToken ct)
    {
        if (!_slots.Wait(0, ct)) return null;
        try
        {
            using var scope = scopes.CreateScope();
            var config = await scope.ServiceProvider.GetRequiredService<IConfigRepository>().GetAiConfigAsync(ct);
            if (config is null || !ExternalSearchService.CanSearchImages(config)) return null;

            var (result, failure) = await search.SearchAsync(config, query, ct, includeReplyImages: true);
            if (failure is not null || result.Images.Count == 0) return null;
            using var client = http.CreateClient("qq-sticker-storage");
            foreach (var candidate in result.Images.Take(3))
            {
                if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var url)) continue;
                using var download = CancellationTokenSource.CreateLinkedTokenSource(ct);
                download.CancelAfter(TimeSpan.FromSeconds(2.5));
                try
                {
                    var bytes = await StickerImageDownload.ReadAsync(client, url, download.Token);
                    if (bytes is not null && await StaticStickerImage.PrepareAsync(bytes, download.Token) is
                        { Fingerprint.Length: > 0, File: var file }) return file;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (Exception ex) when (ex is HttpRequestException or IOException) { }
            }
            return null;
        }
        finally { _slots.Release(); }
    }
}
