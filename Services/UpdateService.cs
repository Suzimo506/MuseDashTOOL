using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private const string CurrentVersion = "v1.1.2"; // 当前程序版本号
    private const string GitHubApiUrl = "https://api.github.com/repos/KuoKing506/-MuseDashTOOL/releases/latest";

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

        // 优先寻找 exe，只发布 exe，但逻辑上仍兼容 zip 兜底。
        foreach (var asset in release.Assets)
        {
            if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.BrowserDownloadUrl;
                fileName = asset.Name;
                break;
            }
        }

        // 如果没有 exe，再尝试寻找 zip 资源。
        if (string.IsNullOrEmpty(downloadUrl))
        {
            foreach (var asset in release.Assets)
            {
                if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
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

        _notificationService.ShowInfo("发现新版本，正在后台下载更新...");
        RuntimeLog.Write("UpdateService", $"开始下载更新包：{downloadUrl}");

        // 真正下载安装包时，才使用用户选择的下载源做镜像加速。
        // 这样可以保证“检查更新”稳定，同时保留下载阶段的加速能力。
        string proxiedUrl = GitHubMirrorHelper.ApplyMirror(downloadUrl, _configService.Config.DownloadSource);
        RuntimeLog.Write("UpdateService", $"实际下载地址：{proxiedUrl}");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(proxiedUrl);
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("UpdateService", $"请求更新包失败：{ex.Message}");

            // 下载更新包超时时，明确提醒用户切换下载源后重试，避免用户不知道下一步该做什么。
            if (ex is TaskCanceledException)
            {
                _notificationService.ShowFailure("更新失败", "下载更新包超时，请切换下载源后重试。");
            }
            else
            {
                _notificationService.ShowFailure("更新失败", $"下载更新包时发生错误：{ex.Message}");
            }
            return;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Write("UpdateService", $"更新包下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                _notificationService.ShowFailure("更新失败", $"下载更新包失败（HTTP {(int)response.StatusCode}）。");
                return;
            }

            var tempFile = Path.Combine(Path.GetTempPath(), "MuseDashTOOL_New" + Path.GetExtension(fileName));

            try
            {
                using var fs = new FileStream(tempFile, FileMode.Create);
                await response.Content.CopyToAsync(fs);
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("UpdateService", $"保存更新包失败：{ex.Message}");
                _notificationService.ShowFailure("更新失败", $"保存更新包失败：{ex.Message}");
                return;
            }

            _pendingUpdateFile = tempFile;
            RuntimeLog.Write("UpdateService", $"更新包下载完成，已保存到临时文件：{tempFile}");

            // 不管后续弹框是否成功，都先明确告诉用户“更新包已经下载完成”。
            _notificationService.ShowSuccess("新版本下载完成");

            // 对话框必须在 UI 线程弹出，但这里不再把是否弹框成功作为成功下载的前提。
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                await ShowUpdateReadyDialogAsync();
            });
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

        // 反复等待当前进程退出并尝试覆盖 exe，直到替换成功或达到重试上限。
        var script = $@"
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
