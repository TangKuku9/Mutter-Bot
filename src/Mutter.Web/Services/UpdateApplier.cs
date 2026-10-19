using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Mutter.Web.Models;

namespace Mutter.Web.Services;

public sealed class UpdateApplier
{
    private const string UpdateDirName = ".update";
    private const string DownloadDirName = "download";
    private const string StagingDirName = "staging";
    private const string StagingNewDirName = "staging-new";
    private const string ExtractDirName = "extract";
    private const string StateFileName = "state.json";
    private const string MarkerFileName = "pending.json";
    private const string BackupDirName = ".backup";
    private const string DownloadClientName = "update-download";
    private const int MaxDownloadAttempts = 5;
    private static readonly TimeSpan MaxPendingAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StatePersistInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions HelperJsonOptions = new() { WriteIndented = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<UpdateApplier> _logger;
    private readonly string _appDir;
    private readonly object _sync = new();
    private StagedUpdate? _staged;
    private PersistedUpdateState _state = new();
    private UpdateProgressDto _progress = new() { Phase = "idle" };
    private bool _loaded;
    private Task? _stageTask;
    private CancellationTokenSource? _stageCts;
    private int _installBusy;
    private DateTimeOffset _lastPersistAt = DateTimeOffset.MinValue;

    public UpdateApplier(
        IHttpClientFactory httpClientFactory,
        IHostApplicationLifetime lifetime,
        ILogger<UpdateApplier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _lifetime = lifetime;
        _logger = logger;
        _appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private string UpdateDir => Path.Combine(_appDir, UpdateDirName);
    private string StatePath => Path.Combine(UpdateDir, StateFileName);
    private string DownloadDir => Path.Combine(UpdateDir, DownloadDirName);
    private string StagingDir => Path.Combine(UpdateDir, StagingDirName);
    private string StagingNewDir => Path.Combine(UpdateDir, StagingNewDirName);
    private string ExtractDir => Path.Combine(UpdateDir, ExtractDirName);

    public bool TryBeginInstall()
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            if (_stageTask is { IsCompleted: false })
                return false;
            return Interlocked.CompareExchange(ref _installBusy, 1, 0) == 0;
        }
    }

    public void EndInstall() => Interlocked.Exchange(ref _installBusy, 0);

    public StagedUpdate? GetStaged()
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            return _staged;
        }
    }

    public UpdateProgressDto GetProgress()
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            return _progress;
        }
    }

    /// <summary>
    /// 启动后台下载并立即返回。下载状态写入安装目录的 .update 下，页面刷新或服务重启都不会丢；
    /// 已在进行中时重复调用直接返回当前状态，不报冲突。
    /// </summary>
    public StageStartResult StartStage(SelfUpdatePlan plan)
    {
        lock (_sync)
        {
            EnsureLoadedLocked();
            if (Volatile.Read(ref _installBusy) != 0)
                return StageStartResult.Rejected("正在安装更新，请稍候");

            if (_stageTask is { IsCompleted: false })
                return new StageStartResult(false, null, _progress);

            if (_staged is not null &&
                string.Equals(_staged.Version, plan.Version, StringComparison.Ordinal))
            {
                return new StageStartResult(true, _staged, _progress);
            }

            PrepareForPlanLocked(plan);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            _stageCts = cts;
            var token = cts.Token;
            _stageTask = Task.Run(() => RunStageAsync(plan, token), CancellationToken.None);
            return new StageStartResult(false, null, _progress);
        }
    }

    private void PrepareForPlanLocked(SelfUpdatePlan plan)
    {
        var sameAsset = string.Equals(_state.Version, plan.Version, StringComparison.Ordinal) &&
                        string.Equals(_state.AssetName, plan.AssetName, StringComparison.Ordinal);

        if (!sameAsset)
        {
            ClearWorkspaceLocked();
            _state = new PersistedUpdateState
            {
                Version = plan.Version,
                AssetName = plan.AssetName,
                AssetUrl = plan.AssetUrl,
                ChecksumUrl = plan.ChecksumUrl,
                AssetSize = plan.AssetSize
            };
        }
        else
        {
            _state.AssetUrl = plan.AssetUrl;
            _state.ChecksumUrl = plan.ChecksumUrl;
            if (plan.AssetSize > 0)
                _state.AssetSize = plan.AssetSize;
        }

        if (_staged is null)
        {
            TryDelete(StagingDir);
            TryDelete(StagingNewDir);
        }

        var archivePath = GetArchivePath(plan.AssetName);
        var length = GetFileLength(archivePath);
        if (length > 0 && _state.AssetSize > 0 && length > _state.AssetSize)
        {
            TryDelete(archivePath);
            length = 0;
            _state.ETag = null;
            _state.LastModified = null;
        }

        _state.DownloadedBytes = length;
        _state.Phase = "downloading";
        _state.Error = null;
        _state.StagingDirectory = null;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        _lastPersistAt = DateTimeOffset.MinValue;
        UpdateProgressLocked(active: true, "downloading", null, persistNow: true);
    }

    private void EnsureLoadedLocked()
    {
        if (_loaded)
            return;
        _loaded = true;

        if (File.Exists(StatePath))
        {
            try
            {
                _state = JsonSerializer.Deserialize<PersistedUpdateState>(
                    File.ReadAllText(StatePath),
                    StateJsonOptions) ?? new PersistedUpdateState();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取更新状态失败，按空闲状态处理");
                _state = new PersistedUpdateState();
            }
        }

        // 已经运行在这个版本上，说明残留的下载/暂存目录来自手动升级或未完成的旧流程，直接清掉。
        if (SameVersion(_state.Version, GetCurrentVersion()))
        {
            ClearWorkspaceLocked();
            _state = new PersistedUpdateState();
            UpdateProgressLocked(active: false, "idle", null, persistNow: false);
            return;
        }

        var stagingValid = StagingIsValid() && !string.IsNullOrWhiteSpace(_state.Version);
        if (stagingValid)
        {
            _staged = new StagedUpdate(StagingDir, _state.Version!, _state.AssetName ?? string.Empty);
            _state.Phase = "done";
            _state.StagingDirectory = StagingDir;
            UpdateProgressLocked(active: false, "done", null, persistNow: true);
            return;
        }

        if (!string.IsNullOrEmpty(_state.StagingDirectory))
        {
            TryDelete(StagingDir);
            TryDelete(StagingNewDir);
            _state.StagingDirectory = null;
        }

        var partial = GetPartialLengthLocked();
        if (partial > _state.DownloadedBytes)
            _state.DownloadedBytes = partial;

        switch (_state.Phase)
        {
            case "downloading":
            case "retrying":
            case "verifying":
            case "extracting":
                // 进程在任务中途退出：保留断点，回到页面时显示可继续的状态。
                if (partial > 0)
                {
                    _state.Phase = "paused";
                    _state.Error = null;
                    UpdateProgressLocked(active: false, "paused", null, persistNow: true);
                }
                else
                {
                    _state.Phase = "error";
                    _state.Error = "上次下载未完成，请重新下载";
                    UpdateProgressLocked(active: false, "error", _state.Error, persistNow: true);
                }
                break;

            case "paused":
                if (partial > 0)
                {
                    UpdateProgressLocked(active: false, "paused", null, persistNow: false);
                }
                else
                {
                    _state.Phase = "idle";
                    UpdateProgressLocked(active: false, "idle", null, persistNow: true);
                }
                break;

            case "error":
                UpdateProgressLocked(active: false, "error", _state.Error, persistNow: false);
                break;

            default:
                _state.Phase = "idle";
                UpdateProgressLocked(active: false, "idle", null, persistNow: false);
                break;
        }
    }

    private void ClearWorkspaceLocked()
    {
        _staged = null;
        TryDelete(UpdateDir);
    }

    /// <summary>下载、校验并暂存更新包；此时不写安装标记，取消安装后不会在退出时被应用。</summary>
    private async Task RunStageAsync(SelfUpdatePlan plan, CancellationToken ct)
    {
        try
        {
            ValidateAssetUrl(plan.AssetUrl);
            ValidateAssetUrl(plan.ChecksumUrl);
            Directory.CreateDirectory(DownloadDir);

            var expectedHash = await ResolveChecksumAsync(plan, ct);
            await DownloadWithRetriesAsync(plan, ct);

            var archivePath = GetArchivePath(plan.AssetName);
            lock (_sync)
                UpdateProgressLocked(active: true, "verifying", null, persistNow: true);

            var actualHash = await ComputeSha256Async(archivePath, ct);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(archivePath);
                lock (_sync)
                {
                    _state.DownloadedBytes = 0;
                    _state.ETag = null;
                    _state.LastModified = null;
                    _state.ExpectedHash = null;
                }
                throw new InvalidOperationException("更新包 SHA256 校验失败，已删除损坏文件，请重新下载");
            }

            lock (_sync)
                UpdateProgressLocked(active: true, "extracting", null, persistNow: true);

            TryDelete(StagingNewDir);
            TryDelete(ExtractDir);
            Directory.CreateDirectory(ExtractDir);
            ExtractArchive(archivePath, ExtractDir);
            var packageRoot = FindPackageRoot(ExtractDir);
            Directory.CreateDirectory(StagingNewDir);
            MoveDirectoryContents(packageRoot, StagingNewDir);

            var exeName = OperatingSystem.IsWindows() ? "mutter-bot.exe" : "mutter-bot";
            if (!File.Exists(Path.Combine(StagingNewDir, exeName)))
                throw new InvalidOperationException("更新包缺少可执行文件，已取消更新");

            TryDelete(StagingDir);
            Directory.Move(StagingNewDir, StagingDir);
            TryDelete(DownloadDir);
            TryDelete(ExtractDir);

            lock (_sync)
            {
                _staged = new StagedUpdate(StagingDir, plan.Version, plan.AssetName);
                _state.DownloadedBytes = Math.Max(_state.DownloadedBytes, _state.AssetSize);
                _state.StagingDirectory = StagingDir;
                _state.Error = null;
                UpdateProgressLocked(active: false, "done", null, persistNow: true);
            }

            _logger.LogInformation("更新包已暂存：{Asset} -> {Staging}", plan.AssetName, StagingDir);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_sync)
            {
                var partial = GetPartialLengthLocked();
                _state.DownloadedBytes = Math.Max(_state.DownloadedBytes, partial);
                if (partial > 0)
                {
                    UpdateProgressLocked(active: false, "paused", null, persistNow: true);
                }
                else
                {
                    _state.Phase = "idle";
                    UpdateProgressLocked(active: false, "idle", null, persistNow: true);
                }
            }
            _logger.LogInformation("更新下载已随服务停止而暂停，可稍后继续");
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                var partial = GetPartialLengthLocked();
                _state.DownloadedBytes = Math.Max(_state.DownloadedBytes, partial);
                UpdateProgressLocked(active: false, "error", ex.Message, persistNow: true);
            }
            _logger.LogError(ex, "下载更新包失败");
        }
        finally
        {
            lock (_sync)
            {
                _stageCts?.Dispose();
                _stageCts = null;
                _stageTask = null;
            }
        }
    }

    private async Task<string> ResolveChecksumAsync(SelfUpdatePlan plan, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(_state.ExpectedHash))
                return _state.ExpectedHash;
        }

        Exception? lastError = null;
        string? checksumText = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var client = _httpClientFactory.CreateClient(DownloadClientName);
                checksumText = await client.GetStringAsync(plan.ChecksumUrl, timeout.Token);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                lastError = ex;
                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }

        if (checksumText is null)
            throw new InvalidOperationException($"获取更新包校验值失败：{lastError?.Message}");

        // SHA256SUMS.txt 缺少条目是确定性失败，不参与重试。
        var expectedHash = ParseChecksum(checksumText, plan.AssetName);
        lock (_sync)
        {
            _state.ExpectedHash = expectedHash;
            PersistStateLocked();
        }
        return expectedHash;
    }

    private async Task DownloadWithRetriesAsync(SelfUpdatePlan plan, CancellationToken ct)
    {
        Exception? lastError = null;
        var attempt = 1;
        var bestProgressBytes = GetFileLength(GetArchivePath(plan.AssetName));

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _state.DownloadedBytes = Math.Max(_state.DownloadedBytes, GetPartialLengthLocked());
                UpdateProgressLocked(active: true, "downloading", null, persistNow: true);
            }

            try
            {
                await DownloadAttemptAsync(plan, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                lastError = ex;
                var currentBytes = GetFileLength(GetArchivePath(plan.AssetName));

                // 只要整体下载确有推进，就重置重试预算：慢链路可能反复断线，但每次都能继续向前。
                if (currentBytes > bestProgressBytes + 256 * 1024)
                {
                    bestProgressBytes = currentBytes;
                    attempt = 1;
                }

                _logger.LogWarning(ex, "更新包下载中断（第 {Attempt}/{Max} 次）", attempt, MaxDownloadAttempts);
                if (attempt >= MaxDownloadAttempts)
                    break;
                attempt++;

                lock (_sync)
                {
                    _state.DownloadedBytes = Math.Max(_state.DownloadedBytes, currentBytes);
                    UpdateProgressLocked(active: true, "retrying", null, persistNow: true);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(20, 2 * attempt)), ct);
            }
        }

        throw new IOException($"下载更新包失败：{lastError?.Message ?? "网络连接中断"}", lastError);
    }

    private async Task DownloadAttemptAsync(SelfUpdatePlan plan, CancellationToken ct)
    {
        var archivePath = GetArchivePath(plan.AssetName);
        var existingBytes = GetFileLength(archivePath);
        var resumeOffset = existingBytes;
        if (existingBytes > 0 && plan.AssetSize > 0 && existingBytes > plan.AssetSize)
        {
            TryDelete(archivePath);
            existingBytes = 0;
            resumeOffset = 0;
            lock (_sync)
            {
                _state.ETag = null;
                _state.LastModified = null;
            }
        }

        if (existingBytes > 0 && plan.AssetSize > 0 && existingBytes == plan.AssetSize)
        {
            lock (_sync)
            {
                _state.AssetSize = plan.AssetSize;
                _state.DownloadedBytes = existingBytes;
                UpdateProgressLocked(active: true, "downloading", null, persistNow: true);
            }
            return;
        }

        string? eTag;
        string? lastModified;
        lock (_sync)
        {
            eTag = _state.ETag;
            lastModified = _state.LastModified;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, plan.AssetUrl);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
            if (!string.IsNullOrWhiteSpace(eTag) &&
                EntityTagHeaderValue.TryParse(eTag, out var entityTag))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
            }
            else if (DateTimeOffset.TryParse(
                         lastModified,
                         CultureInfo.InvariantCulture,
                         DateTimeStyles.AssumeUniversal,
                         out var lastModifiedAt))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(lastModifiedAt);
            }
        }

        var client = _httpClientFactory.CreateClient(DownloadClientName);
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(HeaderTimeout);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                headerTimeout.Token);
            // 响应头已到达，后续只受逐次读取的停滞超时约束，不能让这个定时器影响长时间下载。
            headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"连接下载服务器超过 {HeaderTimeout.TotalSeconds:0} 秒无响应");
        }

        using (response)
        {
            long totalBytes;
            var append = false;

            if (response.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0)
            {
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.From != existingBytes)
                {
                    // 本地断点与服务端不一致且无法靠 If-Range 纠正，删掉重来，避免重试循环死锁在同一个偏移上。
                    TryDelete(archivePath);
                    lock (_sync)
                    {
                        _state.ETag = null;
                        _state.LastModified = null;
                        _state.DownloadedBytes = 0;
                    }
                    throw new HttpRequestException("断点续传响应的起始位置与本地文件不一致，将重新下载");
                }

                if (contentRange.Length is long serverLength &&
                    plan.AssetSize > 0 &&
                    serverLength != plan.AssetSize)
                {
                    TryDelete(archivePath);
                    lock (_sync)
                    {
                        _state.ETag = null;
                        _state.LastModified = null;
                        _state.DownloadedBytes = 0;
                    }
                    throw new HttpRequestException("更新包已在服务器上变化，将重新下载");
                }

                totalBytes = contentRange.Length ?? plan.AssetSize;
                append = true;
                _logger.LogInformation(
                    "断点续传：从 {Downloaded}/{Total} 字节继续下载 {Asset}",
                    existingBytes,
                    totalBytes,
                    plan.AssetName);

                lock (_sync)
                {
                    if (response.Headers.ETag is not null)
                        _state.ETag = response.Headers.ETag.ToString();
                    if (response.Content.Headers.LastModified is { } lastModifiedAt)
                        _state.LastModified = lastModifiedAt.ToString("R");
                }
            }
            else if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                throw new IOException("服务器返回了未请求的部分内容");
            }
            else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                TryDelete(archivePath);
                lock (_sync)
                {
                    _state.ETag = null;
                    _state.LastModified = null;
                    _state.DownloadedBytes = 0;
                }
                throw new HttpRequestException("断点续传请求被拒绝，将重新下载");
            }
            else
            {
                response.EnsureSuccessStatusCode();
                // 服务器忽略了 Range 或资源已变化：截断旧文件，从头下载。
                if (resumeOffset > 0)
                {
                    _logger.LogInformation(
                        "服务器未接受断点续传（HTTP {Status}），从头下载 {Asset}",
                        (int)response.StatusCode,
                        plan.AssetName);
                }
                existingBytes = 0;
                totalBytes = response.Content.Headers.ContentLength ?? plan.AssetSize;
                lock (_sync)
                {
                    _state.ETag = response.Headers.ETag?.ToString();
                    _state.LastModified = response.Content.Headers.LastModified?.ToString("R");
                }
            }

            if (totalBytes <= 0)
                totalBytes = plan.AssetSize;

            lock (_sync)
            {
                if (totalBytes > 0)
                    _state.AssetSize = totalBytes;
                _state.DownloadedBytes = existingBytes;
                UpdateProgressLocked(active: true, "downloading", null, persistNow: true);
            }

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(
                archivePath,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[32 * 1024];
            var downloadedBytes = existingBytes;
            while (true)
            {
                var read = await ReadWithIdleTimeoutAsync(input, buffer, ct);
                if (read <= 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                downloadedBytes += read;

                lock (_sync)
                {
                    if (_state.AssetSize > 0 && downloadedBytes > _state.AssetSize)
                        throw new IOException("下载数据超过预期长度");
                    _state.DownloadedBytes = downloadedBytes;
                    var persist = DateTimeOffset.UtcNow - _lastPersistAt >= StatePersistInterval;
                    UpdateProgressLocked(active: true, _state.Phase, null, persist);
                }
            }

            await output.FlushAsync(ct);
            if (totalBytes > 0 && downloadedBytes < totalBytes)
                throw new IOException($"连接提前结束（{downloadedBytes}/{totalBytes} 字节）");
        }
    }

    /// <summary>用户确认安装后写入待更新标记；Linux 在服务停止后据此替换文件。</summary>
    public void ConfirmStaged(string appDir, StagedUpdate staged)
    {
        var updateDir = Path.Combine(appDir, UpdateDirName);
        Directory.CreateDirectory(updateDir);

        var marker = new PendingUpdate
        {
            Version = staged.Version,
            AssetName = staged.AssetName,
            StagingDirectory = staged.StagingDirectory,
            CreatedAt = DateTimeOffset.UtcNow
        };
        File.WriteAllText(
            Path.Combine(updateDir, MarkerFileName),
            JsonSerializer.Serialize(marker, MarkerJsonOptions),
            Encoding.UTF8);
    }

    public void PrepareWindowsHelper(string appDir, StagedUpdate staged)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("仅 Windows 需要更新助手");

        var helperDir = Path.Combine(Path.GetTempPath(), "mutter-bot-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDir);

        var logDir = Path.Combine(appDir, "logs");
        Directory.CreateDirectory(logDir);

        var restartArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var config = new WindowsHelperConfig
        {
            AppDir = appDir,
            StagingDir = staged.StagingDirectory,
            UpdateDir = Path.Combine(appDir, UpdateDirName),
            BackupBase = Path.Combine(appDir, BackupDirName),
            ParentPid = Environment.ProcessId,
            ExeName = "mutter-bot.exe",
            Arguments = restartArgs,
            Hidden = restartArgs.Any(a => a is "--headless" or "-h"),
            LogPath = Path.Combine(logDir, "update.log"),
            CurrentVersion = GetCurrentVersion(),
            Version = staged.Version
        };

        var scriptPath = Path.Combine(helperDir, "apply.ps1");
        var configPath = Path.Combine(helperDir, "config.json");
        File.WriteAllText(scriptPath, WindowsHelperScript, new UTF8Encoding(false));
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, HelperJsonOptions), new UTF8Encoding(false));

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            powershell = "powershell.exe";

        var psi = new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -ConfigPath \"{configPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = appDir
        };

        try
        {
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 Windows 更新助手");
            process.Dispose();
            _logger.LogInformation("Windows 更新助手已启动，等待主进程退出后替换文件");
        }
        catch
        {
            TryDelete(helperDir);
            throw;
        }
    }

    /// <summary>
    /// 仅 Linux 使用：ConsoleHost 停止服务并退出前，把暂存文件替换到程序目录。
    /// 正在运行的旧进程允许被替换文件，随后退出，systemd 会拉起新版本。
    /// </summary>
    public void ApplyPendingAfterShutdown(string appDir)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var updateDir = Path.Combine(appDir, UpdateDirName);
        var markerPath = Path.Combine(updateDir, MarkerFileName);
        if (!File.Exists(markerPath))
            return;

        PendingUpdate? marker;
        try
        {
            marker = JsonSerializer.Deserialize<PendingUpdate>(
                File.ReadAllText(markerPath),
                MarkerJsonOptions);
        }
        catch (Exception ex)
        {
            Log(appDir, $"读取待更新标记失败：{ex.Message}");
            TryDelete(updateDir);
            return;
        }

        if (marker is null ||
            DateTimeOffset.UtcNow - marker.CreatedAt > MaxPendingAge ||
            string.IsNullOrWhiteSpace(marker.StagingDirectory) ||
            !Directory.Exists(marker.StagingDirectory))
        {
            Log(appDir, "待更新标记已过期或暂存目录不存在，取消本次更新");
            TryDelete(updateDir);
            return;
        }

        var stagingDir = marker.StagingDirectory;
        var exeName = OperatingSystem.IsWindows() ? "mutter-bot.exe" : "mutter-bot";
        if (!File.Exists(Path.Combine(stagingDir, exeName)))
        {
            Log(appDir, "暂存目录缺少可执行文件，取消本次更新");
            TryDelete(updateDir);
            TryDeleteWorkRoot(stagingDir);
            return;
        }

        var currentVersion = GetCurrentVersion();
        var backupBase = Path.Combine(appDir, BackupDirName);
        var backupDir = Path.Combine(backupBase, $"v{currentVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}");

        try
        {
            Directory.CreateDirectory(backupDir);
            CopyProgramItems(appDir, backupDir);
        }
        catch (Exception ex)
        {
            Log(appDir, $"备份旧版本失败，取消更新：{ex.Message}");
            TryDelete(updateDir);
            TryDeleteWorkRoot(stagingDir);
            return;
        }

        try
        {
            ClearProgramItems(appDir);
            CopyProgramItems(stagingDir, appDir);
            SetUnixPermissions(appDir);
            TryDelete(updateDir);
            TryDeleteWorkRoot(stagingDir);
            DeleteOldBackups(backupBase, backupDir);
            Log(appDir, $"已更新到 v{marker.Version}，旧版本备份：{backupDir}");
        }
        catch (Exception ex)
        {
            Log(appDir, $"更新失败，尝试恢复旧版本：{ex.Message}");
            try
            {
                ClearProgramItems(appDir);
                CopyProgramItems(backupDir, appDir);
                SetUnixPermissions(appDir);
                Log(appDir, "已恢复旧版本");
            }
            catch (Exception restoreEx)
            {
                Log(appDir, $"恢复旧版本失败，请手动从 {backupDir} 回滚：{restoreEx.Message}");
            }
            TryDelete(updateDir);
            TryDeleteWorkRoot(stagingDir);
        }
    }

    private void UpdateProgressLocked(bool active, string phase, string? error, bool persistNow)
    {
        var totalBytes = _state.AssetSize;
        var downloadedBytes = _state.DownloadedBytes;
        var percent = totalBytes > 0
            ? (int)Math.Min(100L, downloadedBytes * 100L / totalBytes)
            : 0;

        var resumable = active
            ? downloadedBytes > 0
            : phase is "paused" or "error" && GetPartialLengthLocked() > 0;

        _state.Phase = phase;
        _state.Error = error;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        _progress = new UpdateProgressDto
        {
            Active = active,
            Percent = percent,
            DownloadedBytes = downloadedBytes,
            TotalBytes = totalBytes,
            Phase = phase,
            Version = _state.Version,
            AssetName = _state.AssetName,
            Error = error,
            Resumable = resumable,
            Staged = _staged is not null,
            UpdatedAt = _state.UpdatedAt
        };

        if (persistNow && !string.IsNullOrWhiteSpace(_state.Version))
        {
            PersistStateLocked();
            _lastPersistAt = _state.UpdatedAt;
        }
    }

    private void PersistStateLocked()
    {
        try
        {
            Directory.CreateDirectory(UpdateDir);
            var tempPath = StatePath + ".tmp";
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(_state, StateJsonOptions),
                new UTF8Encoding(false));
            File.Move(tempPath, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入更新状态失败");
        }
    }

    private bool StagingIsValid()
    {
        var exeName = OperatingSystem.IsWindows() ? "mutter-bot.exe" : "mutter-bot";
        return Directory.Exists(StagingDir) && File.Exists(Path.Combine(StagingDir, exeName));
    }

    private string GetArchivePath(string assetName) => Path.Combine(DownloadDir, assetName);

    private long GetPartialLengthLocked()
    {
        if (string.IsNullOrWhiteSpace(_state.AssetName))
            return 0;
        return GetFileLength(GetArchivePath(_state.AssetName));
    }

    private static long GetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static async ValueTask<int> ReadWithIdleTimeoutAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken ct)
    {
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleTimeout.CancelAfter(ReadIdleTimeout);
        try
        {
            return await input.ReadAsync(buffer, idleTimeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"下载连接超过 {ReadIdleTimeout.TotalSeconds:0} 秒未收到数据");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ParseChecksum(string checksumText, string assetName)
    {
        foreach (var line in checksumText.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                parts[^1].TrimStart('*').Equals(assetName, StringComparison.Ordinal))
            {
                return parts[0];
            }
        }
        throw new InvalidOperationException("SHA256SUMS.txt 中缺少更新包校验值");
    }

    private static void ExtractArchive(string archivePath, string destination)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
            return;
        }

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
            return;
        }

        throw new InvalidOperationException("不支持的更新包格式");
    }

    private static string FindPackageRoot(string extractDir)
    {
        var exeName = OperatingSystem.IsWindows() ? "mutter-bot.exe" : "mutter-bot";
        var matches = Directory.GetFiles(extractDir, exeName, SearchOption.AllDirectories);
        if (matches.Length != 1)
            throw new InvalidOperationException("更新包结构不正确：未找到唯一的可执行文件");

        var root = Path.GetDirectoryName(matches[0])
            ?? throw new InvalidOperationException("更新包结构不正确");
        var extractRoot = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(extractRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("更新包路径异常");
        return root;
    }

    private static void MoveDirectoryContents(string sourceDir, string destinationDir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDir).ToArray())
        {
            var destination = Path.Combine(destinationDir, Path.GetFileName(entry));
            if (Directory.Exists(entry))
                Directory.Move(entry, destination);
            else
                File.Move(entry, destination);
        }
    }

    private static void CopyProgramItems(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDir).ToArray())
        {
            var name = Path.GetFileName(entry);
            var isDirectory = Directory.Exists(entry);
            if (IsProtectedName(name, isDirectory))
                continue;

            var destination = Path.Combine(destinationDir, name);
            if (isDirectory)
                CopyProgramItems(entry, destination);
            else
                File.Copy(entry, destination, overwrite: true);
        }
    }

    private static void ClearProgramItems(string appDir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(appDir).ToArray())
        {
            var name = Path.GetFileName(entry);
            var isDirectory = Directory.Exists(entry);
            if (IsProtectedName(name, isDirectory))
                continue;

            if (isDirectory)
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
    }

    private static bool IsProtectedName(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return name.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(BackupDirName, StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(UpdateDirName, StringComparison.OrdinalIgnoreCase);
        }

        if (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Equals("mutter-bot.db", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("mutter-bot.db-wal", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("mutter-bot.db-shm", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.Contains(".bak.", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetUnixPermissions(string appDir)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                   UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        foreach (var name in new[] { "mutter-bot", "install.sh" })
        {
            var path = Path.Combine(appDir, name);
            if (File.Exists(path))
                File.SetUnixFileMode(path, mode);
        }
    }

    private static void DeleteOldBackups(string backupBase, string keepBackup)
    {
        foreach (var dir in Directory.GetDirectories(backupBase))
        {
            if (string.Equals(dir, keepBackup, StringComparison.Ordinal))
                continue;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void ValidateAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新地址不是 GitHub Release，已拒绝下载");
        }
    }

    private static string GetCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";

    private static bool SameVersion(string? left, string? right)
    {
        static string Normalize(string? value) => (value ?? string.Empty).Trim().TrimStart('v', 'V');
        var a = Normalize(left);
        var b = Normalize(right);
        return a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteWorkRoot(string stagingDir)
    {
        TryDelete(stagingDir);
        var parent = Path.GetDirectoryName(stagingDir);
        if (parent is not null &&
            Path.GetFileName(parent).StartsWith("mutter-bot-update-", StringComparison.Ordinal))
        {
            TryDelete(parent);
        }
    }

    private static void Log(string appDir, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        try
        {
            var logDir = Path.Combine(appDir, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "update.log"), line + Environment.NewLine);
        }
        catch { }
        Console.WriteLine("[Update] " + message);
    }

    private sealed class PersistedUpdateState
    {
        public string Phase { get; set; } = "idle";
        public string? Version { get; set; }
        public string? AssetName { get; set; }
        public string? AssetUrl { get; set; }
        public string? ChecksumUrl { get; set; }
        public long AssetSize { get; set; }
        public long DownloadedBytes { get; set; }
        public string? ExpectedHash { get; set; }
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public string? Error { get; set; }
        public string? StagingDirectory { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class PendingUpdate
    {
        public string Version { get; set; } = string.Empty;
        public string AssetName { get; set; } = string.Empty;
        public string StagingDirectory { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class WindowsHelperConfig
    {
        public string AppDir { get; set; } = string.Empty;
        public string StagingDir { get; set; } = string.Empty;
        public string UpdateDir { get; set; } = string.Empty;
        public string BackupBase { get; set; } = string.Empty;
        public int ParentPid { get; set; }
        public string ExeName { get; set; } = string.Empty;
        public string[] Arguments { get; set; } = [];
        public bool Hidden { get; set; }
        public string LogPath { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }

    private const string WindowsHelperScript = """
        param([Parameter(Mandatory=$true)][string]$ConfigPath)
        $ErrorActionPreference = 'Stop'
        $cfg = Get-Content -Raw -LiteralPath $ConfigPath -Encoding UTF8 | ConvertFrom-Json

        function Write-UpdateLog([string]$Message) {
            $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
            try { Add-Content -LiteralPath $cfg.LogPath -Value $line -Encoding UTF8 } catch { }
        }

        function Start-Mutter {
            $startArgs = @{
                FilePath = (Join-Path $cfg.AppDir $cfg.ExeName)
                WorkingDirectory = $cfg.AppDir
            }
            $restartArgs = @($cfg.Arguments | Where-Object { $_ })
            if ($restartArgs.Count -gt 0) { $startArgs.ArgumentList = $restartArgs }
            if ($cfg.Hidden) { $startArgs.WindowStyle = 'Hidden' }
            Start-Process @startArgs
        }

        $backupDir = $null
        $backupComplete = $false
        $excludeDirs = @(
            (Join-Path $cfg.AppDir 'logs'),
            (Join-Path $cfg.AppDir '.backup'),
            (Join-Path $cfg.AppDir '.update')
        )
        $excludeFiles = @('appsettings*.json', 'mutter-bot.db', 'mutter-bot.db-wal', 'mutter-bot.db-shm', '*.bak.*')

        try {
            while (Get-Process -Id $cfg.ParentPid -ErrorAction SilentlyContinue) {
                Start-Sleep -Milliseconds 500
            }
            Write-UpdateLog "parent process exited; applying update to $($cfg.Version)"

            New-Item -ItemType Directory -Force -Path $cfg.BackupBase | Out-Null
            $backupDir = Join-Path $cfg.BackupBase ("v" + $cfg.CurrentVersion + "-" + (Get-Date -Format 'yyyyMMddHHmmss'))
            New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

            $backupArgs = @($cfg.AppDir, $backupDir, '/E', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS',
                '/XD', $excludeDirs[0], $excludeDirs[1], $excludeDirs[2],
                '/XF', $excludeFiles[0], $excludeFiles[1], $excludeFiles[2], $excludeFiles[3], $excludeFiles[4])
            & robocopy @backupArgs | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "backup failed (robocopy $LASTEXITCODE)" }
            $backupComplete = $true
            Write-UpdateLog "backup created at $backupDir"

            $applyArgs = @($cfg.StagingDir, $cfg.AppDir, '/MIR', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS',
                '/XD', $excludeDirs[0], $excludeDirs[1], $excludeDirs[2],
                '/XF', $excludeFiles[0], $excludeFiles[1], $excludeFiles[2], $excludeFiles[3], $excludeFiles[4])
            & robocopy @applyArgs | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "apply failed (robocopy $LASTEXITCODE)" }

            Get-ChildItem -LiteralPath $cfg.BackupBase -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -ne $backupDir } |
                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

            Remove-Item -Recurse -Force $cfg.UpdateDir -ErrorAction SilentlyContinue
            Write-UpdateLog "update applied; starting $($cfg.ExeName)"
            Start-Mutter
        }
        catch {
            Write-UpdateLog ("update failed: " + $_.Exception.Message)
            try {
                if ($backupComplete -and $backupDir -and (Test-Path $backupDir)) {
                    $restoreArgs = @($backupDir, $cfg.AppDir, '/MIR', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS',
                        '/XD', $excludeDirs[0], $excludeDirs[1], $excludeDirs[2],
                        '/XF', $excludeFiles[0], $excludeFiles[1], $excludeFiles[2], $excludeFiles[3], $excludeFiles[4])
                    & robocopy @restoreArgs | Out-Null
                    Write-UpdateLog "restored previous version"
                }
                else {
                    Write-UpdateLog "backup incomplete; install directory left untouched"
                }
                Start-Mutter
            }
            catch {
                Write-UpdateLog ("restore/start failed: " + $_.Exception.Message)
            }
        }
        finally {
            Remove-Item -Recurse -Force (Split-Path -Parent $cfg.StagingDir) -ErrorAction SilentlyContinue
            Remove-Item -Recurse -Force $cfg.UpdateDir -ErrorAction SilentlyContinue
            Remove-Item -Recurse -Force (Split-Path -Parent $ConfigPath) -ErrorAction SilentlyContinue
        }
        """;
}

public sealed record StagedUpdate(string StagingDirectory, string Version, string AssetName);

public sealed record StageStartResult(
    bool Staged,
    StagedUpdate? StagedUpdate,
    UpdateProgressDto Progress,
    string? Error = null)
{
    public static StageStartResult Rejected(string error) =>
        new(false, null, new UpdateProgressDto { Phase = "busy" }, error);
}
