using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Models;
using MdModManager.Helpers;

namespace MdModManager.Services;

public interface IMelonLoaderService
{
    Task<List<GitHubRelease>> GetReleasesAsync(CancellationToken cancellationToken = default);
    string? GetCurrentVersion();
    Task InstallAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task UninstallAsync();
}

public class MelonLoaderService : IMelonLoaderService
{
    private readonly IConfigService _configService;
    private readonly HttpClient _httpClient;
    private const string ApiUrl = "https://api.github.com/repos/LavaGang/MelonLoader/releases";

    public MelonLoaderService(IConfigService configService)
    {
        _configService = configService;
        _httpClient = HttpHelper.CreateOptimizedClient(TimeSpan.FromMinutes(5));
    }

    public async Task<List<GitHubRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetStringAsync(ApiUrl, cancellationToken);
            var releases = JsonSerializer.Deserialize(response, AppJsonContext.Default.GitHubReleaseArray);
            return new List<GitHubRelease>(releases ?? Array.Empty<GitHubRelease>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch ML releases: {ex}");
            return new List<GitHubRelease>();
        }
    }

    public string? GetCurrentVersion()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) return null;

        var paths = new[]
        {
            Path.Combine(gamePath, "MelonLoader", "net6", "MelonLoader.dll"),
            Path.Combine(gamePath, "MelonLoader", "net35", "MelonLoader.dll"),
            Path.Combine(gamePath, "MelonLoader", "MelonLoader.dll")
        };

        string? mlDllPath = null;
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                mlDllPath = path;
                break;
            }
        }

        if (mlDllPath == null) return null;

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(mlDllPath);
            var version = versionInfo.ProductVersion ?? versionInfo.FileVersion;
            if (version != null && version.Contains('+'))
            {
                version = version.Substring(0, version.IndexOf('+'));
            }
            return version;
        }
        catch
        {
            return null;
        }
    }

    public async Task InstallAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) throw new Exception("游戏目录未设置");

        if (Process.GetProcessesByName("MuseDash").Length > 0)
        {
            throw new Exception("检测到游戏正在运行，请先关闭游戏后再安装或更新 MelonLoader！");
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"MelonLoader_{Guid.NewGuid():N}.zip");

        try
        {
            // Download
            using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? 1L;

                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[16384]; // 增大 buffer 提高速度
                long totalRead = 0;
                int read;

                // 进度节流
                var lastReportTime = DateTime.MinValue;
                var lastProgress = -1.0;

                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                    totalRead += read;

                    var currentProgress = (double)totalRead / totalBytes * 100;
                    var now = DateTime.Now;

                    // 每 100ms 或进度变化超过 1% 汇报一次，避免 UI 刷新拖慢下载速度
                    if ((now - lastReportTime).TotalMilliseconds > 100 || Math.Abs(currentProgress - lastProgress) >= 1.0)
                    {
                        progress?.Report(currentProgress);
                        lastReportTime = now;
                        lastProgress = currentProgress;
                    }
                }
                
                // 确保最后一次进度汇报到位
                progress?.Report(100);
            }

            // 在解压新版本之前，先清理可能存在的旧版本核心文件，避免新老版本的代理 dll 或依赖冲突
            await UninstallAsync();

            // Extract
            await Task.Run(() =>
            {
                try
                {
                    using var archive = ZipFile.OpenRead(tempZip);
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var destinationPath = Path.Combine(gamePath, entry.FullName);
                        var destinationDir = Path.GetDirectoryName(destinationPath);
                        
                        if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                        {
                            Directory.CreateDirectory(destinationDir);
                        }

                        if (!string.IsNullOrEmpty(entry.Name))
                        {
                            entry.ExtractToFile(destinationPath, overwrite: true);
                        }
                    }

                    // Explicitly create normal MelonLoader accessory folders
                    string[] extraDirs = { "Mods", "Plugins", "UserData", "UserLibs", "Custom_Albums" };
                    foreach (var dir in extraDirs)
                    {
                        var path = Path.Combine(gamePath, dir);
                        if (!Directory.Exists(path))
                        {
                            Directory.CreateDirectory(path);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new Exception($"解压失败: {ex.Message} (可能游戏正在运行？请关闭游戏后再试)", ex);
                }
            }, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { /* ignore */ }
            }
        }
    }

    public async Task UninstallAsync()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) return;

        if (Process.GetProcessesByName("MuseDash").Length > 0)
        {
            throw new Exception("检测到游戏正在运行，请先关闭游戏后再卸载 MelonLoader！");
        }

        await Task.Run(() =>
        {
            var mlDir = Path.Combine(gamePath, "MelonLoader");
            if (Directory.Exists(mlDir)) Directory.Delete(mlDir, true);

            var dobby = Path.Combine(gamePath, "dobby.dll");
            if (File.Exists(dobby)) File.Delete(dobby);

            var version = Path.Combine(gamePath, "version.dll");
            if (File.Exists(version)) File.Delete(version);

            var winhttp = Path.Combine(gamePath, "winhttp.dll");
            if (File.Exists(winhttp)) File.Delete(winhttp);

            var winmm = Path.Combine(gamePath, "winmm.dll");
            if (File.Exists(winmm)) File.Delete(winmm);

            var notice = Path.Combine(gamePath, "NOTICE.txt");
            if (File.Exists(notice)) File.Delete(notice);
        });
    }
}
