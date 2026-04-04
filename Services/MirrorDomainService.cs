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
    /// 初始化镜像域名配置：
    /// 1. 先读本地文件 / 缓存 / 内置资源，确保程序启动时总有可用值。
    /// 2. 再尝试直连 GitHub 拉取最新配置，成功后覆盖当前值并刷新缓存。
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public class MirrorDomainService : IMirrorDomainService
{
    // 真实域名配置固定从官方仓库根目录读取，避免后面每次换域名都改代码。
    private const string RemoteMirrorDomainsUrl = "https://raw.githubusercontent.com/KuoKing506/-MuseDashTOOL/main/mirror-domains.json";
    private const string MirrorDomainsFileName = "mirror-domains.json";

    private readonly HttpClient _httpClient;
    private readonly string _cacheFolderPath;
    private readonly string _cacheFilePath;

    private enum RemoteLoadResult
    {
        Loaded,
        Empty,
        Failed
    }

    public MirrorDomainService()
    {
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string? selectedSource = null;

        // 远端优先：
        // 1. 只要远端 JSON 成功返回了有效域名，就立即采用远端值并刷新缓存。
        // 2. 只有远端成功返回，但没有写 Suzimo 域名时，才回退到本地文件 / 缓存 / 内置资源。
        // 3. 如果远端请求本身失败，则为了保证软件仍可启动，仍然允许走本地兜底。
        var remoteResult = await TryLoadFromRemoteAsync(cancellationToken).ConfigureAwait(false);
        if (remoteResult == RemoteLoadResult.Loaded)
        {
            selectedSource = "远端配置";
            WriteSelectedSourceLog(selectedSource);
            return;
        }

        var loaded = false;

        // 远端明确返回了空值，或者远端请求失败时，才启用本地兜底链。
        if (TryLoadFromLocalFile())
        {
            loaded = true;
            selectedSource = "本地 mirror-domains.json";
        }

        if (!loaded && await TryLoadFromCacheAsync(cancellationToken).ConfigureAwait(false))
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
            if (remoteResult == RemoteLoadResult.Failed)
            {
                RuntimeLog.Write("MirrorDomainService", "远端镜像配置访问失败，本次已回退到本地兜底配置。");
            }
            else if (remoteResult == RemoteLoadResult.Empty)
            {
                RuntimeLog.Write("MirrorDomainService", "远端镜像配置未填写 Suzimo 域名，本次已回退到本地兜底配置。");
            }

            WriteSelectedSourceLog(selectedSource ?? "未知来源");
        }
        else
        {
            RuntimeLog.Write("MirrorDomainService", "未能加载 Suzimo 镜像域名配置。");
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

    private async Task<RemoteLoadResult> TryLoadFromRemoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 给配置请求附加时间戳，尽量绕过 GitHub Raw/CDN 侧的旧缓存。
            var remoteUrl = $"{RemoteMirrorDomainsUrl}?ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var json = await _httpClient.GetStringAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
            var config = TryDeserialize(json, $"remote: {RemoteMirrorDomainsUrl}");
            if (config == null)
                return RemoteLoadResult.Failed;

            var normalizedHost = MirrorDomainRegistry.NormalizeHost(config.Suzimo);
            if (string.IsNullOrWhiteSpace(normalizedHost))
            {
                RuntimeLog.Write("MirrorDomainService", $"远端镜像配置未填写 Suzimo 域名：{RemoteMirrorDomainsUrl}");
                return RemoteLoadResult.Empty;
            }

            MirrorDomainRegistry.Update(config);
            RuntimeLog.Write("MirrorDomainService", $"已加载镜像配置：remote: {RemoteMirrorDomainsUrl} -> {MirrorDomainRegistry.SuzimoHost}");

            Directory.CreateDirectory(_cacheFolderPath);
            await File.WriteAllTextAsync(_cacheFilePath, json, cancellationToken).ConfigureAwait(false);
            return RemoteLoadResult.Loaded;
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("MirrorDomainService", $"拉取远程镜像配置失败：{ex.Message}");
            return RemoteLoadResult.Failed;
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
