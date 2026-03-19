using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MdModManager.Services;

namespace MdModManager.Services;

public interface IUpdateService
{
    Task CheckAndApplyUpdateAsync();
}

public class UpdateService : IUpdateService
{
    private const string CurrentVersion = "v1.1.0";
    private const string GitHubApiUrl = "https://api.github.com/repos/KuoKing506/-MuseDashTOOL/releases/latest";
    private const string ProxyUrl = "https://mirror.ghproxy.com/";
    private readonly HttpClient _httpClient;

    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "MuseDashModTool-Updater");
    }

    public async Task CheckAndApplyUpdateAsync()
    {
        try
        {
            // 在开发环境下（dotnet run）跳过更新检测，避免干扰
            if (Debugger.IsAttached || IsRunningFromSource())
            {
                return;
            }

            var latestRelease = await GetLatestReleaseAsync();
            if (latestRelease == null) return;

            string latestVersion = latestRelease.TagName;
            if (IsNewerVersion(latestVersion, CurrentVersion))
            {
                await DownloadAndApplyUpdate(latestRelease);
            }
        }
        catch (Exception ex)
        {
            // 静默失败，不干扰用户启动
            Debug.WriteLine($"Update check failed: {ex.Message}");
        }
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
            // 对检测逻辑也使用代理，提高连接成功率
            string proxiedApiUrl = ProxyUrl + GitHubApiUrl;
            var response = await _httpClient.GetStringAsync(proxiedApiUrl);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            return JsonSerializer.Deserialize<GitHubRelease>(response, options);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Latest release fetch failed (with proxy): {ex.Message}");
            // 回退到直连（万一代理挂了）
            try
            {
                var response = await _httpClient.GetStringAsync(GitHubApiUrl);
                var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
                return JsonSerializer.Deserialize<GitHubRelease>(response, options);
            }
            catch
            {
                return null;
            }
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

        // 优先使用镜像下载
        string proxiedUrl = ProxyUrl + downloadUrl;
        var response = await _httpClient.GetAsync(proxiedUrl);
        
        if (!response.IsSuccessStatusCode)
        {
            // 如果镜像失败，尝试直接从 GitHub 下载
            response = await _httpClient.GetAsync(downloadUrl);
        }

        if (!response.IsSuccessStatusCode) return;

        var tempFile = Path.Combine(Path.GetTempPath(), "MuseDashTOOL_New" + Path.GetExtension(fileName));
        using (var fs = new FileStream(tempFile, FileMode.Create))
        {
            await response.Content.CopyToAsync(fs);
        }

        CreateUpdaterScript(tempFile);
        
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
