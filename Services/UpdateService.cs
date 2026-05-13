using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Net.Http.Headers;
using MdModManager.Helpers;
using MdModManager.Views;

namespace MdModManager.Services;

public interface IUpdateService
{
    Task CheckAndApplyUpdateAsync();
    void ApplyPendingUpdate();
}

public class UpdateService : IUpdateService
{
    public const string CurrentVersion = "v1.3.1.2"; // 当前程序版本号
    private const string GitHubApiUrl = "https://api.github.com/repos/Suzimo506/MuseDashTOOL/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    private string? _pendingUpdateFile;
    private int _hasStartedChecking;

    public UpdateService(IConfigService configService, INotificationService notificationService)
    {
        _configService = configService;
        _notificationService = notificationService;
        _httpClient = HttpHelper.CreateOptimizedClient(TimeSpan.FromMinutes(10));
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "MuseDashModTool-Updater");
    }

    public Task CheckAndApplyUpdateAsync()
    {
        // 只启动一次后台检查，避免窗口事件重复触发后开启多个轮询任务。
        if (Interlocked.Exchange(ref _hasStartedChecking, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    // 开发环境下跳过自动更新，避免调试时被更新流程打断。
                    if (Debugger.IsAttached || IsRunningFromSource())
                    {
                        return;
                    }

                    var latestRelease = await GetLatestReleaseAsync();
                    if (latestRelease != null)
                    {
                        string latestVersion = latestRelease.TagName;
                        if (IsNewerVersion(latestVersion, CurrentVersion))
                        {
                            RuntimeLog.Write("UpdateService", $"检测到新版本：{latestVersion}，当前版本：{CurrentVersion}");
                            await DownloadAndApplyUpdate(latestRelease);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Write("UpdateService", $"后台更新检查失败：{ex.Message}");
                    Debug.WriteLine($"Background update check failed: {ex.Message}");
                }

                // 没发现更新或本次检查失败时，5 分钟后再重试。
                await Task.Delay(TimeSpan.FromMinutes(5));
            }
        });

        return Task.CompletedTask;
    }

    private bool IsRunningFromSource()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        return exePath != null && (exePath.Contains("bin\\Debug") || exePath.Contains("bin\\Release"));
    }

    private async Task<GitHubRelease?> GetLatestReleaseAsync()
    {
        try
        {
            // 更新检查必须直连 GitHub 官方 API。
            // 这里拿到的是 release 的 JSON 元数据，镜像站未必兼容 GitHub API，
            // 如果把 api.github.com 也替换成镜像域名，可能就拿不到正确的 tag_name 和 assets。
            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            return JsonSerializer.Deserialize<GitHubRelease>(response, options);
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("UpdateService", $"获取最新 release 信息失败：{ex.Message}");
            Debug.WriteLine($"Latest release fetch failed: {ex.Message}");
            return null;
        }
    }

    private bool IsNewerVersion(string latest, string current)
    {
        try
        {
            var latestVer = new Version(latest.TrimStart('v'));
            var currentVer = new Version(current.TrimStart('v'));
            return latestVer > currentVer;
        }
        catch
        {
            return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
        }
    }

    private async Task DownloadAndApplyUpdate(GitHubRelease release)
    {
        string? downloadUrl = null;
        string? fileName = null;

        // 优先寻找 zip 压缩包（从 v1.2.6 起发布 zip 格式）
        foreach (var asset in release.Assets)
        {
            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.BrowserDownloadUrl;
                fileName = asset.Name;
                break;
            }
        }

        // 如果没有 zip，再尝试寻找 exe 资源（兼容旧版本发布）。
        if (string.IsNullOrEmpty(downloadUrl))
        {
            foreach (var asset in release.Assets)
            {
                if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.BrowserDownloadUrl;
                    fileName = asset.Name;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(downloadUrl) || string.IsNullOrEmpty(fileName))
        {
            RuntimeLog.Write("UpdateService", $"发现新版本 {release.TagName}，但 release 中没有可下载的 exe 或 zip 资源。");
            _notificationService.ShowFailure("更新失败", "发现了新版本，但没有找到可下载的安装包。");
            return;
        }

        var progressNotif = _notificationService.ShowPersistentProgress("发现新版本，正在后台下载更新...");
        RuntimeLog.Write("UpdateService", $"开始下载更新包：{downloadUrl}");

        string proxiedUrl = GitHubMirrorHelper.ApplyMirror(downloadUrl, _configService.Config.DownloadSource);
        RuntimeLog.Write("UpdateService", $"实际下载地址：{proxiedUrl}");

        var tempFile = Path.Combine(Path.GetTempPath(), "MuseDashTOOL_New" + Path.GetExtension(fileName));

        long downloadedBytes = 0;
        long totalBytes = 0;
        string? resumeEntityTag = null;
        DateTimeOffset? resumeLastModified = null;

        int retryCount = 0;
        int maxRetryCount = 20;

        bool useOptimizedIpStrategy = false;
        if (Uri.TryCreate(proxiedUrl, UriKind.Absolute, out var uri))
        {
            useOptimizedIpStrategy = HttpHelper.UseOptimizedIps &&
                                     HttpHelper.IsOptimizedAccelerationHost(uri.Host);
        }

        // 无论重连多少次，最多尝试 maxRetryCount 次下载
        while (retryCount <= maxRetryCount)
        {
            try
            {
                // 如果是断点续传，采取追加模式写入；如果是首次下载或重新下载，采取覆盖创建模式
                var fileMode = downloadedBytes > 0 ? FileMode.Append : FileMode.Create;
                using var dst = new FileStream(tempFile, fileMode, FileAccess.Write, FileShare.None, 81920, true);
                
                using var request = new HttpRequestMessage(HttpMethod.Get, proxiedUrl);
                if (downloadedBytes > 0)
                {
                    // 设置断点续传的请求头 Range 参数，告知服务器从上一次中断的字节处继续传输数据
                    request.Headers.Range = new RangeHeaderValue(downloadedBytes, null);
                    
                    // 使用上一次下载记录的校验信息（If-Range）检查服务端文件是否被更改过，以免将错误的内容强行拼接在旧文件后导致包损坏
                    if (!string.IsNullOrWhiteSpace(resumeEntityTag) && EntityTagHeaderValue.TryParse(resumeEntityTag, out var entityTag) && !entityTag.IsWeak)
                        request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
                    else if (resumeLastModified.HasValue)
                        request.Headers.IfRange = new RangeConditionHeaderValue(resumeLastModified.Value);
                }

                RuntimeLog.Write("UpdateService", $"Attempt {retryCount}: url='{proxiedUrl}', downloaded={downloadedBytes}");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                
                if (downloadedBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    // 如果服务器不支持断点续传或文件已更新（此时返回 200 OK 而不是续传的 206 Partial Content），则清空并重置下载进度
                    RuntimeLog.Write("UpdateService", "服务器不支持断点续传或文件已更新（返回 200 OK 而非 206），重置下载进度。");
                    downloadedBytes = 0;
                    totalBytes = 0;
                    dst.SetLength(0);
                    dst.Seek(0, SeekOrigin.Begin);
                }
                
                response.EnsureSuccessStatusCode();

                // 捕获并保存断点续传的校验信息 (ETag 和 Last-Modified) 供遇到网络异常中断、下一次循环重连时核验
                if (response.Headers.ETag is { IsWeak: false } etagVal)
                    resumeEntityTag = etagVal.ToString();
                else if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    resumeEntityTag = null;

                if (response.Content.Headers.LastModified.HasValue)
                    resumeLastModified = response.Content.Headers.LastModified.Value;
                else if (response.Headers.TryGetValues("Last-Modified", out var lastModValues) && DateTimeOffset.TryParse(lastModValues.FirstOrDefault(), out var parsedLastMod))
                    resumeLastModified = parsedLastMod;
                else if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    resumeLastModified = null;

                if (totalBytes == 0)
                {
                    totalBytes = response.Content.Headers.ContentLength ?? 0;
                    if (downloadedBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                        totalBytes += downloadedBytes;
                }

                using var src = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[81920];
                int read;

                // 看门狗保护机制：使用 WaitAsync 限制网络流的读取，超过 15 秒无数据传输即视为假死或超时，断开并触发捕获异常进入下次重修
                while ((read = await src.ReadAsync(buffer, 0, buffer.Length).WaitAsync(TimeSpan.FromSeconds(15))) > 0)
                {
                    await dst.WriteAsync(buffer, 0, read);
                    downloadedBytes += read;
                    
                    if (totalBytes > 0)
                    {
                        var progress = (double)downloadedBytes / totalBytes * 100;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => progressNotif.ProgressValue = progress);
                    }
                }
                
                await dst.FlushAsync();
                
                // --- 下载成功 ---
                _notificationService.RemoveNotification(progressNotif);
                
                _pendingUpdateFile = tempFile;
                RuntimeLog.Write("UpdateService", $"更新包下载完成，已保存到临时文件：{tempFile}");
                
                _notificationService.ShowSuccess("新版本下载完成");
                Avalonia.Threading.Dispatcher.UIThread.Post(async () => await ShowUpdateReadyDialogAsync());
                
                return; // 下载完全结束，成功退出该方法和外层的最外围重试循环
            }
            catch (Exception ex)
            {
                retryCount++;
                RuntimeLog.Write("UpdateService", $"更新包下载异常 (retry={retryCount}): {ex.Message}");
                if (retryCount > maxRetryCount)
                {
                    _notificationService.RemoveNotification(progressNotif);
                    _notificationService.ShowFailure("更新失败", "尝试多次下载并反复重连依然失败，请检查网络或切换下载源！");
                    return;
                }
                
                // 遇到报错时，若当前使用了优选 IP 策略（如镜像源故障），主动将此节点标记暂时失效，下一次重连时就能自动获取新的可用 IP
                if (useOptimizedIpStrategy)
                {
                    HttpHelper.InvalidateFastestIp();
                    await Task.Delay(1000);
                }
                else
                {
                    await Task.Delay(3000);
                }
            }
        }
    }

    private async Task ShowUpdateReadyDialogAsync()
    {
        try
        {
            var mainWindow =
                (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;

            if (mainWindow == null)
            {
                RuntimeLog.Write("UpdateService", "更新包已下载完成，但主窗口尚未就绪，无法弹出确认对话框。将在退出软件时自动安装。");
                _notificationService.ShowInfo("更新已下载完成，将在软件关闭后自动安装", 4000);
                return;
            }

            bool shouldRestart = await MessageBox.ShowDialogAsync(mainWindow, "新版本下载完成，是否立即重启以应用更新？", showCancel: true);
            if (shouldRestart)
            {
                RuntimeLog.Write("UpdateService", "用户确认立即重启并应用更新。");
                ApplyUpdateInternal();
            }
            else
            {
                RuntimeLog.Write("UpdateService", "用户选择稍后安装更新，等待程序退出时自动替换。");
                _notificationService.ShowInfo("更新已就绪，将在软件关闭后自动安装", 3000);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("UpdateService", $"显示更新确认对话框失败：{ex.Message}");
            _notificationService.ShowInfo("更新已下载完成，将在软件关闭后自动安装", 4000);
        }
    }

    public void ApplyPendingUpdate()
    {
        if (!string.IsNullOrEmpty(_pendingUpdateFile) && File.Exists(_pendingUpdateFile))
        {
            ApplyUpdateInternal();
        }
    }

    private void ApplyUpdateInternal()
    {
        if (string.IsNullOrEmpty(_pendingUpdateFile)) return;

        CreateUpdaterScript(_pendingUpdateFile);

        // 启动更新脚本后立即退出当前进程，让脚本接管替换和重启。
        Process.Start(new ProcessStartInfo
        {
            FileName = "updater.bat",
            UseShellExecute = true,
            CreateNoWindow = true
        });

        Environment.Exit(0);
    }

    private void CreateUpdaterScript(string newFile)
    {
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
        if (currentExe == null) return;

        var processName = Process.GetCurrentProcess().ProcessName;
        var appDir = Path.GetDirectoryName(currentExe) ?? Environment.CurrentDirectory;
        var isZip = newFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        string script;
        if (isZip)
        {
            // ZIP 模式：解压覆盖整个目录，然后重启
            script = $@"
@echo off
setlocal enabledelayedexpansion
set ""retryCount=0""
:loop
taskkill /f /im {processName}.exe > nul 2>&1
timeout /t 1 /nobreak > nul
powershell -NoProfile -Command ""Expand-Archive -Path '{newFile}' -DestinationPath '{appDir}' -Force"" 2>nul
if errorlevel 1 (
    set /a ""retryCount+=1""
    if !retryCount! lss 10 (
        goto loop
    )
)
del ""{newFile}"" > nul 2>&1
start """" ""{currentExe}""
del ""%~f0""
";
        }
        else
        {
            // EXE 模式：直接替换单文件
            script = $@"
@echo off
setlocal enabledelayedexpansion
set ""retryCount=0""
:loop
taskkill /f /im {processName}.exe > nul 2>&1
timeout /t 1 /nobreak > nul
move /y ""{newFile}"" ""{currentExe}""
if errorlevel 1 (
    set /a ""retryCount+=1""
    if !retryCount! lss 10 (
        goto loop
    )
)
start """" ""{currentExe}""
del ""%~f0""
";
        }

        // 使用 GBK 编码写入脚本，避免 cmd.exe 在中文路径下出现乱码。
        var encoding = System.Text.Encoding.GetEncoding(936);
        File.WriteAllText("updater.bat", script, encoding);
    }

    private class GitHubRelease
    {
        public string TagName { get; set; } = string.Empty;
        public GitHubAsset[] Assets { get; set; } = Array.Empty<GitHubAsset>();
    }

    private class GitHubAsset
    {
        public string Name { get; set; } = string.Empty;
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
