using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Infrastructure.Repositories;

namespace Mutter.Infrastructure.Services;

public sealed class GroupStickerCollector(
    GroupStickerLibrary library, IHttpClientFactory http, TimeProvider clock, ILogger<GroupStickerCollector> logger)
    : BackgroundService, IGroupStickerCollector
{
    private sealed record Work(string GroupId, Uri Url, string Label, DateTimeOffset EnqueuedAt);

    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });

    public void TryEnqueue(string groupId, StickerAttachment attachment)
    {
        if (attachment.AdvertisedSize > StickerSettings.MaxSourceBytes || attachment.Url.Length > 4096) return;
        var url = attachment.Url.StartsWith("//", StringComparison.Ordinal) ? "https:" + attachment.Url : attachment.Url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsPublicHttps(uri)) return;
        if (!_queue.Writer.TryWrite(new Work(groupId, uri, attachment.Label, clock.GetUtcNow())))
            logger.LogDebug("表情收藏队列已满，跳过本次附件");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await library.ReconcileFilesAsync(stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning("表情图库启动检查失败 ({ErrorType})", ex.GetType().Name); }

        using var client = http.CreateClient("qq-sticker-storage");
        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (clock.GetUtcNow() - work.EnqueuedAt > TimeSpan.FromMinutes(1)) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                if (!await library.CanCollectAsync(work.GroupId, deadline.Token)) continue;
                var data = await DownloadAsync(client, work.Url, deadline.Token);
                if (data is not null) await library.CollectAsync(work.GroupId, work.Label, data, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // 附件地址可能带临时签名，仅记录异常类型。
                logger.LogDebug("表情收藏未完成 ({ErrorType})", ex.GetType().Name);
            }
        }
    }

    private static async Task<byte[]?> DownloadAsync(HttpClient client, Uri url, CancellationToken ct)
    {
        for (var redirects = 0; ; redirects++)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects == 2 || response.Headers.Location is not { } location
                    || !Uri.TryCreate(url, location, out var next) || !IsPublicHttps(next)) return null;
                url = next;
                continue;
            }

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > StickerSettings.MaxSourceBytes)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var data = new MemoryStream();
            var buffer = new byte[32 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (data.Length + read > StickerSettings.MaxSourceBytes) return null;
                data.Write(buffer, 0, read);
            }
            return data.ToArray();
        }
    }

    internal static bool IsPublicHttps(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.HostNameType == UriHostNameType.Dns && uri.IdnHost.Contains('.') && !uri.IsLoopback;
}
