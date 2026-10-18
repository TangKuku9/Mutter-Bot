using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutter.Web.Models;

namespace Mutter.Web.Services;

public sealed class UpdateApplier
{
    private const string UpdateDirName = ".update";
    private const string PendingDirName = "pending";
    private const string MarkerFileName = "pending.json";
    private const string BackupDirName = ".backup";
    private static readonly TimeSpan MaxPendingAge = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions HelperJsonOptions = new() { WriteIndented = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UpdateApplier> _logger;
    private int _busy;

    public UpdateApplier(IHttpClientFactory httpClientFactory, ILogger<UpdateApplier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool TryBegin() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void End() => Interlocked.Exchange(ref _busy, 0);

    public async Task<StagedUpdate> StageAsync(SelfUpdatePlan plan, string appDir, CancellationToken ct)
    {
        ValidateAssetUrl(plan.AssetUrl);
        ValidateAssetUrl(plan.ChecksumUrl);

        var updateDir = Path.Combine(appDir, UpdateDirName);
        var workRoot = Path.Combine(Path.GetTempPath(), "mutter-bot-update-" + Guid.NewGuid().ToString("N"));
        var stagingDir = Path.Combine(workRoot, PendingDirName);
        var downloadDir = Path.Combine(workRoot, "download");
        var extractDir = Path.Combine(workRoot, "extract");

        try
        {
            TryDelete(updateDir);
            Directory.CreateDirectory(updateDir);
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(downloadDir);
            Directory.CreateDirectory(extractDir);

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            var checksumText = await client.GetStringAsync(plan.ChecksumUrl, ct);
            var expectedHash = ParseChecksum(checksumText, plan.AssetName);

            var archivePath = Path.Combine(downloadDir, plan.AssetName);
            var actualHash = await DownloadAndHashAsync(client, plan.AssetUrl, archivePath, ct);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新包 SHA256 校验失败，已取消更新");

            ExtractArchive(archivePath, extractDir);
            var packageRoot = FindPackageRoot(extractDir);
            MoveDirectoryContents(packageRoot, stagingDir);

            var exeName = OperatingSystem.IsWindows() ? "mutter-bot.exe" : "mutter-bot";
            if (!File.Exists(Path.Combine(stagingDir, exeName)))
                throw new InvalidOperationException("更新包缺少可执行文件，已取消更新");

            var marker = new PendingUpdate
            {
                Version = plan.Version,
                AssetName = plan.AssetName,
                StagingDirectory = stagingDir,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await File.WriteAllTextAsync(
                Path.Combine(updateDir, MarkerFileName),
                JsonSerializer.Serialize(marker, MarkerJsonOptions),
                Encoding.UTF8,
                ct);

            TryDelete(downloadDir);
            TryDelete(extractDir);
            _logger.LogInformation("更新包已暂存：{Asset} -> {Staging}", plan.AssetName, stagingDir);
            return new StagedUpdate(stagingDir, plan.Version, plan.AssetName);
        }
        catch
        {
            TryDelete(workRoot);
            TryDelete(updateDir);
            throw;
        }
    }

    public void PrepareWindowsHelper(SelfUpdatePlan plan, string appDir, StagedUpdate staged)
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
            Version = plan.Version
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

    private static async Task<string> DownloadAndHashAsync(
        HttpClient client,
        string url,
        string destination,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
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
