using System.Net.Http.Headers;
using System.Text.Json;
using MdModManager.Helpers;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IChartUploadConfigService
{
    Task<ChartUploadConfig> GetConfigAsync(CancellationToken cancellationToken = default);
    void InvalidateCache();
}

public sealed class ChartUploadConfigService : IChartUploadConfigService
{
    private const string RemoteConfigUrl = "https://download.suzimo.site/%E6%9C%AA%E7%BB%8F%E5%AE%A1%E6%9F%A5/chart-upload-config.json";
    private const string ConfigFileName = "chart-upload-config.json";

    private readonly HttpClient _httpClient;
    private readonly string _cacheFolderPath;
    private readonly string _cacheFilePath;
    private readonly object _cacheLock = new();
    private ChartUploadConfig? _cachedConfig;

    public ChartUploadConfigService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolderPath = Path.Combine(appData, "MdModManager");
        _cacheFilePath = Path.Combine(_cacheFolderPath, ConfigFileName);

        _httpClient = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(4));
        _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
            MustRevalidate = true
        };
    }

    public async Task<ChartUploadConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        lock (_cacheLock)
        {
            if (_cachedConfig != null)
                return CloneConfig(_cachedConfig);
        }

        if (TryLoadFromLocalFile(out var localConfig))
            return CacheAndClone(localConfig);

        if (await TryLoadFromCacheAsync(cancellationToken).ConfigureAwait(false) is { } cacheConfig)
            return CacheAndClone(cacheConfig);

        if (await TryLoadFromRemoteAsync(cancellationToken).ConfigureAwait(false) is { } remoteConfig)
            return CacheAndClone(remoteConfig);

        return CacheAndClone(ChartUploadConfig.CreateDefault());
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedConfig = null;
        }
    }

    private ChartUploadConfig CacheAndClone(ChartUploadConfig config)
    {
        var normalized = Normalize(config);
        lock (_cacheLock)
        {
            _cachedConfig = normalized;
        }

        return CloneConfig(normalized);
    }

    private bool TryLoadFromLocalFile(out ChartUploadConfig config)
    {
        foreach (var path in GetLocalCandidatePaths())
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var json = File.ReadAllText(path);
                var parsed = Deserialize(json);
                if (parsed != null)
                {
                    config = parsed;
                    RuntimeLog.Write("ChartUploadConfig", $"已加载本地上传配置：{path}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("ChartUploadConfig", $"读取本地上传配置失败：{path}，{ex.Message}");
            }
        }

        config = ChartUploadConfig.CreateDefault();
        return false;
    }

    private async Task<ChartUploadConfig?> TryLoadFromCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
                return null;

            var json = await File.ReadAllTextAsync(_cacheFilePath, cancellationToken).ConfigureAwait(false);
            var parsed = Deserialize(json);
            if (parsed != null)
                RuntimeLog.Write("ChartUploadConfig", $"已加载缓存上传配置：{_cacheFilePath}");

            return parsed;
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("ChartUploadConfig", $"读取缓存上传配置失败：{ex.Message}");
            return null;
        }
    }

    private async Task<ChartUploadConfig?> TryLoadFromRemoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{RemoteConfigUrl}?ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var parsed = Deserialize(json);
            if (parsed == null)
                return null;

            Directory.CreateDirectory(_cacheFolderPath);
            await File.WriteAllTextAsync(_cacheFilePath, json, cancellationToken).ConfigureAwait(false);
            RuntimeLog.Write("ChartUploadConfig", $"已刷新远程上传配置：{RemoteConfigUrl}");
            return parsed;
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("ChartUploadConfig", $"拉取远程上传配置失败：{ex.Message}");
            return null;
        }
    }

    private static ChartUploadConfig? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ChartUploadConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static ChartUploadConfig Normalize(ChartUploadConfig config)
    {
        config.ApiUrl = config.ApiUrl?.Trim() ?? string.Empty;
        config.Notice = config.Notice?.Trim() ?? string.Empty;
        config.DefaultReleaseTag = string.IsNullOrWhiteSpace(config.DefaultReleaseTag)
            ? "NO.1"
            : config.DefaultReleaseTag.Trim();
        config.TimeoutSeconds = config.TimeoutSeconds <= 0 ? 120 : config.TimeoutSeconds;
        config.Categories = config.Categories?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (config.Categories.Count == 0)
            config.Categories = ChartUploadConfig.GetDefaultCategories();

        config.FallbackApiUrls = config.FallbackApiUrls?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        return config;
    }

    private static ChartUploadConfig CloneConfig(ChartUploadConfig config) => new()
    {
        Enabled = config.Enabled,
        ApiUrl = config.ApiUrl,
        FallbackApiUrls = new List<string>(config.FallbackApiUrls),
        Notice = config.Notice,
        Categories = new List<string>(config.Categories),
        DefaultReleaseTag = config.DefaultReleaseTag,
        TimeoutSeconds = config.TimeoutSeconds
    };

    private static IEnumerable<string> GetLocalCandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        yield return Path.Combine(Environment.CurrentDirectory, ConfigFileName);
    }
}
