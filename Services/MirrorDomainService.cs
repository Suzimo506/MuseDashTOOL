using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Helpers;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IMirrorDomainService
{
    /// <summary>
    /// 启动时仅从本地来源初始化镜像域名，避免因为访问远端而拖慢进入软件的速度。
    /// </summary>
    void Initialize();

    /// <summary>
    /// 在主界面打开后，后台直连 GitHub 检查远端镜像配置。
    /// 只有当远端域名和当前本地生效域名不同的时候，才执行更新并显示持续提示气泡。
    /// </summary>
    Task RefreshFromRemoteIfNeededAsync(CancellationToken cancellationToken = default);
}

public class MirrorDomainService : IMirrorDomainService
{
    // 真实域名配置固定从官方仓库根目录读取，避免后面每次换域名都改代码。
    private const string RemoteMirrorDomainsUrl = "https://raw.githubusercontent.com/Suzimo506/MuseDashTOOL/main/mirror-domains.json";
    private const string MirrorDomainsFileName = "mirror-domains.json";

    private readonly HttpClient _httpClient;
    private readonly INotificationService _notificationService;
    private readonly string _cacheFolderPath;
    private readonly string _cacheFilePath;
    private int _hasStartedBackgroundRefresh;

    private enum RemoteLoadResult
    {
        Loaded,
        Empty,
        Failed
    }

    public MirrorDomainService(INotificationService notificationService)
    {
        _notificationService = notificationService;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolderPath = Path.Combine(appData, "MdModManager");
        _cacheFilePath = Path.Combine(_cacheFolderPath, MirrorDomainsFileName);

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "MuseDashTOOL-MirrorBootstrap");
        _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
            MustRevalidate = true
        };
    }

    public void Initialize()
    {
        string? selectedSource = null;
        var loaded = false;
        if (TryLoadFromLocalFile())
        {
            loaded = true;
            selectedSource = "本地 mirror-domains.json";
        }

        if (!loaded && TryLoadFromCache())
        {
            loaded = true;
            selectedSource = "本地缓存";
        }

        if (!loaded && TryLoadFromEmbeddedResource())
        {
            loaded = true;
            selectedSource = "内置默认配置";
        }

        if (MirrorDomainRegistry.HasSuzimoHost)
        {
            WriteSelectedSourceLog(selectedSource ?? "未知来源");
        }
        else
        {
            RuntimeLog.Write("MirrorDomainService", "未能加载 Suzimo 镜像域名配置。");
        }
    }

    public async Task RefreshFromRemoteIfNeededAsync(CancellationToken cancellationToken = default)
    {
        // 只在主窗口生命周期里启动一次后台检查，避免重复弹出气泡。
        if (Interlocked.Exchange(ref _hasStartedBackgroundRefresh, 1) == 1)
            return;

        var currentCombined = $"{MirrorDomainRegistry.SuzimoHost}|{MirrorDomainRegistry.AlbumDownloadDomain}|{MirrorDomainRegistry.AlbumInfoDomain}|{MirrorDomainRegistry.DownloadDomain}";
        var remoteResult = await TryLoadFromRemoteAsync(cancellationToken, compareOnly: true).ConfigureAwait(false);

        if (remoteResult.Result == RemoteLoadResult.Failed)
        {
            RuntimeLog.Write("MirrorDomainService", "远端镜像配置访问失败，本次继续使用本地缓存/默认配置。");
            return;
        }

        if (remoteResult.Result == RemoteLoadResult.Empty)
        {
            RuntimeLog.Write("MirrorDomainService", "远端镜像配置未填写 Suzimo 域名，本次继续使用本地缓存/默认配置。");
            return;
        }

        if (string.Equals(currentCombined, remoteResult.Host, StringComparison.OrdinalIgnoreCase))
        {
            RuntimeLog.Write("MirrorDomainService", $"远端镜像配置未变化，无需更新：{remoteResult.Host}");
            return;
        }

        var notification = _notificationService.ShowPersistentProgress("更新镜像域名中...");
        try
        {
            var applyResult = await TryLoadFromRemoteAsync(cancellationToken, compareOnly: false).ConfigureAwait(false);
            if (applyResult.Result == RemoteLoadResult.Loaded)
            {
                RuntimeLog.Write("MirrorDomainService", $"远端镜像域名已更新");
                _notificationService.ShowInfo("镜像域名已更新", 1500);
            }
            else if (applyResult.Result == RemoteLoadResult.Empty)
            {
                RuntimeLog.Write("MirrorDomainService", "远端镜像配置在更新时为空，本次继续使用原本的本地配置。");
            }
            else
            {
                RuntimeLog.Write("MirrorDomainService", "更新镜像域名失败，本次继续使用原本的本地配置。");
            }
        }
        finally
        {
            _notificationService.RemoveNotification(notification);
        }
    }

    private bool TryLoadFromLocalFile()
    {
        foreach (var path in GetLocalCandidatePaths())
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var json = File.ReadAllText(path);
                if (TryApplyJson(json, $"local file: {path}"))
                    return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("MirrorDomainService", $"读取本地镜像配置失败：{path}，{ex.Message}");
            }
        }

        return false;
    }

    private async Task<bool> TryLoadFromCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
                return false;

            var json = await File.ReadAllTextAsync(_cacheFilePath, cancellationToken).ConfigureAwait(false);
            return TryApplyJson(json, $"cache file: {_cacheFilePath}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("MirrorDomainService", $"读取缓存镜像配置失败：{ex.Message}");
            return false;
        }
    }

    private bool TryLoadFromCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
                return false;

            var json = File.ReadAllText(_cacheFilePath);
            return TryApplyJson(json, $"cache file: {_cacheFilePath}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("MirrorDomainService", $"读取缓存镜像配置失败：{ex.Message}");
            return false;
        }
    }

    private bool TryLoadFromEmbeddedResource()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = Array.Find(
                assembly.GetManifestResourceNames(),
                static name => name.EndsWith(MirrorDomainsFileName, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(resourceName))
                return false;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return false;

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return TryApplyJson(json, $"embedded resource: {resourceName}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("MirrorDomainService", $"读取内置镜像配置失败：{ex.Message}");
            return false;
        }
    }

    private async Task<(RemoteLoadResult Result, string Host)> TryLoadFromRemoteAsync(CancellationToken cancellationToken, bool compareOnly)
    {
        try
        {
            // 给配置请求附加时间戳，尽量绕过 GitHub Raw/CDN 侧的旧缓存。
            var remoteUrl = $"{RemoteMirrorDomainsUrl}?ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var json = await _httpClient.GetStringAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
            var config = TryDeserialize(json, $"remote: {RemoteMirrorDomainsUrl}");
            if (config == null)
                return (RemoteLoadResult.Failed, string.Empty);

            var normalizedHost = MirrorDomainRegistry.NormalizeHost(config.Suzimo);
            if (string.IsNullOrWhiteSpace(normalizedHost))
            {
                RuntimeLog.Write("MirrorDomainService", $"远端镜像配置未填写 Suzimo 域名：{RemoteMirrorDomainsUrl}");
                return (RemoteLoadResult.Empty, string.Empty);
            }

            if (compareOnly)
            {
                var remoteDownloadDomain = MirrorDomainRegistry.NormalizeHost(config.DownloadDomain);
                if (string.IsNullOrWhiteSpace(remoteDownloadDomain))
                    remoteDownloadDomain = $"download.{normalizedHost}";
                var combinedRemote = $"{normalizedHost}|{MirrorDomainRegistry.NormalizeHost(config.AlbumDownloadDomain)}|{MirrorDomainRegistry.NormalizeHost(config.AlbumInfoDomain)}|{remoteDownloadDomain}";
                RuntimeLog.Write("MirrorDomainService", $"已检查远端镜像配置：{combinedRemote}");
                return (RemoteLoadResult.Loaded, combinedRemote);
            }

            MirrorDomainRegistry.Update(config);
            RuntimeLog.Write("MirrorDomainService", $"已加载镜像配置：remote: {RemoteMirrorDomainsUrl} -> {MirrorDomainRegistry.SuzimoHost}");

            Directory.CreateDirectory(_cacheFolderPath);
            await File.WriteAllTextAsync(_cacheFilePath, json, cancellationToken).ConfigureAwait(false);
            return (RemoteLoadResult.Loaded, MirrorDomainRegistry.SuzimoHost);
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("MirrorDomainService", $"拉取远程镜像配置失败：{ex.Message}");
            return (RemoteLoadResult.Failed, string.Empty);
        }
    }

    private static bool TryApplyJson(string json, string source)
    {
        var config = TryDeserialize(json, source);
        if (config == null || string.IsNullOrWhiteSpace(MirrorDomainRegistry.NormalizeHost(config.Suzimo)))
        {
            RuntimeLog.Write("MirrorDomainService", $"镜像配置无效：{source}");
            return false;
        }

        MirrorDomainRegistry.Update(config);
        RuntimeLog.Write("MirrorDomainService", $"已加载镜像配置：{source} -> {MirrorDomainRegistry.SuzimoHost}");
        return true;
    }

    private static MirrorDomainsConfig? TryDeserialize(string json, string source)
    {
        try
        {
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.MirrorDomainsConfig);
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("MirrorDomainService", $"解析镜像配置失败：{source}，{ex.Message}");
            return null;
        }
    }

    private static void WriteSelectedSourceLog(string source)
    {
        RuntimeLog.Write("MirrorDomainService", $"本次使用的镜像配置来源：{source}");
        RuntimeLog.Write("MirrorDomainService", $"当前 Suzimo 镜像域名：{MirrorDomainRegistry.SuzimoHost}");
    }

    private static string[] GetLocalCandidatePaths()
    {
        return new[]
        {
            Path.Combine(AppContext.BaseDirectory, MirrorDomainsFileName),
            Path.Combine(Environment.CurrentDirectory, MirrorDomainsFileName)
        };
    }
}
