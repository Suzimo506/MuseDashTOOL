using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MdModManager.Helpers;
using MdModManager.Services;
using MdModManager.Views;

namespace MdModManager.Services;

public interface IUpdateService
{
    Task CheckAndApplyUpdateAsync();
    void ApplyPendingUpdate();
}

public class UpdateService : IUpdateService
{
    private const string CurrentVersion = "v1.1.1"; // 版本号常量
    private const string GitHubApiUrl = "https://api.github.com/repos/KuoKing506/-MuseDashTOOL/releases/latest";
    private readonly HttpClient _httpClient;
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    private string? _pendingUpdateFile;

    public UpdateService(IConfigService configService, INotificationService notificationService)
    {
        _configService = configService;
        _notificationService = notificationService;
        _httpClient = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(30));
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "MuseDashModTool-Updater");
    }

    public async Task CheckAndApplyUpdateAsync()
    {
        // 软件开启期间在后台一直静默进行检测
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    // 在开发环境下（dotnet run）跳过更新检测，避免干扰
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
                            // 发现新版本，静默下载并直接重启
                            await DownloadAndApplyUpdate(latestRelease);
                            return; // 一旦开始更新流程（重启），此后台任务自然退出
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Background update check failed: {ex.Message}");
                }

                // 没发现更新或检测失败，每 5 分钟轮询一次
                await Task.Delay(TimeSpan.FromMinutes(5));
            }
        });
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
            // 对检测逻辑也使用镜像加速，提高连接成功率
            string proxiedApiUrl = GitHubMirrorHelper.ApplyMirror(GitHubApiUrl, _configService.Config.DownloadSource);
            var response = await _httpClient.GetStringAsync(proxiedApiUrl);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            return JsonSerializer.Deserialize<GitHubRelease>(response, options);
        }
        catch (Exception ex)
        {
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

        // 优先寻找 exe，用户承诺只发布 exe，但逻辑上保持兼容
        foreach (var asset in release.Assets)
        {
            if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.BrowserDownloadUrl;
                fileName = asset.Name;
                break;
            }
        }

        // 如果没找到 exe 再找 zip
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

        if (string.IsNullOrEmpty(downloadUrl)) return;

        // 弹出气泡提示用户正在更新
        _notificationService.ShowInfo("发现新版本，正在后台下载更新...");

        // 使用用户选定的下载源进行镜像加速
        string proxiedUrl = GitHubMirrorHelper.ApplyMirror(downloadUrl, _configService.Config.DownloadSource);
        var response = await _httpClient.GetAsync(proxiedUrl);
        
        if (!response.IsSuccessStatusCode) return;

        var tempFile = Path.Combine(Path.GetTempPath(), "MuseDashTOOL_New" + Path.GetExtension(fileName));
        using (var fs = new FileStream(tempFile, FileMode.Create))
        {
            await response.Content.CopyToAsync(fs);
        }

        // 下载完成，准备更新流程
        _pendingUpdateFile = tempFile;

        // 在 UI 线程弹出确认对话框
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow != null)
            {
                bool shouldRestart = await MessageBox.ShowDialogAsync(mainWindow, "新版本下载完成，是否立即重启以应用更新？", showCancel: true);
                if (shouldRestart)
                {
                    ApplyUpdateInternal();
                }
                else
                {
                    _notificationService.ShowInfo("更新已就绪，将在软件关闭后自动安装", 3000);
                }
            }
        });
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
        
        // 启动更新脚本并退出
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

        // 更加健壮的脚本：不断尝试直到进程彻底关闭、文件被释放并移动成功
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
        // 使用 GBK 编码 (936) 来确保中文路径在 cmd.exe 中不乱码
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
