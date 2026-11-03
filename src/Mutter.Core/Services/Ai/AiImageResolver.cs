using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mutter.Core.Services;

public interface IAiImageResolver
{
    /// <summary>解析并缓存一张图片；类型不安全、体积超限或链接失效时返回 null。</summary>
    Task<AiResolvedImage?> ResolveAsync(string url, CancellationToken ct = default);
    ImageCacheSnapshot Snapshot() => new(0, 0, 0, 0);
}

public sealed record ImageCacheSnapshot(int Entries, long Bytes, long ByteLimit, int Downloads);
public sealed record AiImageCacheOptions(int MaxEntries = 64, long MaxBytes = 64 * 1024 * 1024);

/// <summary>
/// 所有附图在进入传输层之前都在这里落地：远程下载、data URI 解码、本地文件读取、文件头嗅探。
/// 图片只解析一次，探测/预筛/正式发送共用缓存，避免同一张 QQ 图被下载两遍。
/// </summary>
public sealed class AiImageResolver : IAiImageResolver, IDisposable
{
    /// <summary>单张图片原始字节上限。base64 后会膨胀 4/3，留出请求体余量。</summary>
    public const int MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>一次请求里所有图片的原始字节总预算，避免撞上 Gemini 20MB inline 上限。</summary>
    public const long MaxRequestImageBytes = 12 * 1024 * 1024;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private readonly HttpClient _httpClient;
    private readonly ILogger<AiImageResolver> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<AiResolvedImage?>>> _inflight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _downloads = new(4, 4);
    private readonly TimeProvider _clock;
    private readonly AiImageCacheOptions _options;
    private readonly ITimer _sweepTimer;
    private long _cacheBytes;

    private sealed record CacheEntry(DateTimeOffset ExpiresAt, DateTimeOffset AccessedAt, AiResolvedImage? Image, long Size);

    public AiImageResolver(IHttpClientFactory httpClientFactory, ILogger<AiImageResolver> logger,
        TimeProvider? clock = null, AiImageCacheOptions? options = null)
    {
        _httpClient = httpClientFactory.CreateClient("image-resolver");
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxBytes);
        _sweepTimer = _clock.CreateTimer(_ => { lock (_gate) Sweep(_clock.GetUtcNow()); }, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<AiResolvedImage?> ResolveAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        ct.ThrowIfCancellationRequested();
        var key = url.Length > 256 ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) : url;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Sweep(now);
            if (_cache.TryGetValue(key, out var cached))
            {
                _cache[key] = cached with { AccessedAt = now };
                return cached.Image;
            }
        }

        // 缓存里的下载任务不绑定第一个调用方的取消令牌：一个人取消不该让后面的人拿到失败结果。
        // 调用方只取消自己的等待，底层下载仍有 10 秒硬超时。
        var task = _inflight.GetOrAdd(key, _ => new Lazy<Task<AiResolvedImage?>>(
            () => ResolveAndCacheAsync(key, url), LazyThreadSafetyMode.ExecutionAndPublication));
        return await task.Value.WaitAsync(ct);
    }

    private async Task<AiResolvedImage?> ResolveAndCacheAsync(string key, string url)
    {
        try
        {
            lock (_gate)
            {
                Sweep(_clock.GetUtcNow());
                if (_cache.TryGetValue(key, out var cached)) return cached.Image;
            }
            var result = await SafeResolveAsync(url);
            var size = (result?.Data.LongLength ?? 0) + (result?.SourceUrl.Length ?? 0) * 2L + key.Length * 2L + 256;
            lock (_gate)
            {
                Sweep(_clock.GetUtcNow());
                if (size <= _options.MaxBytes)
                {
                    while (_cache.Count >= _options.MaxEntries || _cacheBytes + size > _options.MaxBytes)
                        Remove(_cache.MinBy(p => p.Value.AccessedAt).Key);
                    var now = _clock.GetUtcNow();
                    _cache[key] = new(now + (result is null ? TimeSpan.FromSeconds(5) : CacheTtl), now, result, size);
                    _cacheBytes += size;
                }
            }
            return result;
        }
        finally { _inflight.TryRemove(key, out _); }
    }

    public ImageCacheSnapshot Snapshot()
    {
        lock (_gate)
        {
            Sweep(_clock.GetUtcNow());
            return new(_cache.Count, _cacheBytes, _options.MaxBytes, 4 - _downloads.CurrentCount);
        }
    }

    public void Dispose() => _sweepTimer.Dispose();

    private async Task<AiResolvedImage?> SafeResolveAsync(string url)
    {
        try
        {
            return await ResolveCoreAsync(url);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "图片解析失败 url={Url}", TruncateUrl(url));
            return null;
        }
    }

    private async Task<AiResolvedImage?> ResolveCoreAsync(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return DecodeDataUri(url);

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return await DownloadAsync(url);

        var path = url;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try { path = new Uri(url).LocalPath; }
            catch (UriFormatException) { return null; }
        }

        if (File.Exists(path)) return ReadLocal(path);
        return null;
    }

    private AiResolvedImage? DecodeDataUri(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0) return null;

        var meta = url[5..comma];
        if (!meta.Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;

        // 先按 base64 体积粗筛，避免先解码再发现超大。
        var payloadLength = url.Length - comma - 1;
        if (payloadLength > MaxImageBytes * 2L) return null;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(url[(comma + 1)..]); }
        catch (FormatException) { return null; }
        if (bytes.Length > MaxImageBytes) return null;
        return Build(bytes, "data:[inline image]");
    }

    private async Task<AiResolvedImage?> DownloadAsync(string url)
    {
        using var timeout = new CancellationTokenSource(DownloadTimeout);
        await _downloads.WaitAsync(timeout.Token);
        try { return await DownloadCoreAsync(url, timeout.Token); }
        finally { _downloads.Release(); }
    }

    private async Task<AiResolvedImage?> DownloadCoreAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("image/png,image/jpeg,image/*;q=0.8");

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        var declared = response.Content.Headers.ContentLength;
        if (declared > MaxImageBytes) return null;

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0) break;
            total += read;
            if (total > MaxImageBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return Build(buffer.ToArray(), url);
    }

    private AiResolvedImage? ReadLocal(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxImageBytes) return null;
        return Build(File.ReadAllBytes(path), path);
    }

    private AiResolvedImage? Build(byte[] bytes, string sourceUrl)
    {
        var mime = DetectImageMime(bytes);
        if (!SupportedImageMime(mime))
        {
            _logger.LogInformation("图片未随请求附带（模型不收该类型）mime={Mime} url={Url}",
                mime ?? "unknown", TruncateUrl(sourceUrl));
            return null;
        }
        return new AiResolvedImage(mime!, bytes, sourceUrl);
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var key in _cache.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray()) Remove(key);
    }

    private void Remove(string key)
    {
        if (_cache.Remove(key, out var entry)) _cacheBytes -= entry.Size;
    }

    /// <summary>模型侧确认支持的类型才附：png/jpeg。gif、webp、未知一律不附，避免整次调用被打成 500。</summary>
    public static bool SupportedImageMime(string? mime) =>
        mime is "image/png" or "image/jpeg";

    /// <summary>按文件头判断图片类型，认不出返回 null。</summary>
    public static string? DetectImageMime(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
            return "image/png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return "image/jpeg";
        if (head.Length >= 12 &&
            head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F' &&
            head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
            return "image/webp";
        if (head.Length >= 3 && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F')
            return "image/gif";
        return null;
    }

    private static string TruncateUrl(string url) => url.Length <= 80 ? url : url[..80] + "…";
}
