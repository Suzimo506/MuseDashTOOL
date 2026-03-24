using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MdModManager.Helpers;
using MdModManager.Services;

namespace MdModManager.Services;

public interface IUpdateService
{
    Task CheckAndApplyUpdateAsync();
}

public class UpdateService : IUpdateService
{
    private const string CurrentVersion = "v1.1.1"; // 同时也顺手更新一下版本号常量
    private const string GitHubApiUrl = "https://api.github.com/repos/KuoKing506/-MuseDashTOOL/releases/latest";
    private readonly HttpClient _httpClient;
    private readonly IConfigService _configService;

    public UpdateService(IConfigService configService)
    {
        _configService = configService;
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

                // 没发现更新或检测失败，每 30 分钟轮询一次
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

        foreach (var asset in release.Assets)
        {
            if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || 
                asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.BrowserDownloadUrl;
                fileName = asset.Name;
                break;
            }
        }

        if (string.IsNullOrEmpty(downloadUrl)) return;

        // 使用用户选定的下载源进行镜像加速
        string proxiedUrl = GitHubMirrorHelper.ApplyMirror(downloadUrl, _configService.Config.DownloadSource);
        var response = await _httpClient.GetAsync(proxiedUrl);
        
        if (!response.IsSuccessStatusCode) return;

        if (!response.IsSuccessStatusCode) return;

        var tempFile = Path.Combine(Path.GetTempPath(), "MuseDashTOOL_New" + Path.GetExtension(fileName));
        using (var fs = new FileStream(tempFile, FileMode.Create))
        {
            await response.Content.CopyToAsync(fs);
        }

        CreateUpdaterScript(tempFile);
        
        // 启动更新脚本并强制退出主程序，实现一键重启
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

        var script = $@"
@echo off
timeout /t 2 /nobreak > nul
taskkill /f /im MuseDashTOOL.exe > nul 2>&1
move /y ""{newFile}"" ""{currentExe}""
start """" ""{currentExe}""
del ""%~f0""
";
        File.WriteAllText("updater.bat", script);
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
