using System.Net.Http.Headers;
using System.Text.Json;
using MdModManager.Helpers;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IModRepositoryConfigService
{
    /// <summary>获取远端 mod 仓库配置（内存缓存）</summary>
    Task<ModRepositoryConfig> GetConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>远端配置是否已成功获取（mod_links_url 非空）</summary>
    bool IsRemoteConfigActive { get; }

    /// <summary>后台预热：在软件启动时调用，不阻塞 UI</summary>
    Task PreloadAsync();
}

public sealed class ModRepositoryConfigService : IModRepositoryConfigService
{
    private const string RemoteConfigUrl = "https://raw.githubusercontent.com/Suzimo506/MuseDashTOOL/main/mod_repository.json";
    private const string CacheFileName = "mod_repository.json";

    private readonly HttpClient _httpClient;
    private readonly string _cacheFolderPath;
    private readonly string _cacheFilePath;
    private ModRepositoryConfig? _cachedConfig;
    private bool _isLoaded;

    public bool IsRemoteConfigActive =>
        _cachedConfig != null && !string.IsNullOrWhiteSpace(_cachedConfig.ModLinksUrl);

    public ModRepositoryConfigService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolderPath = Path.Combine(appData, "MdModManager");
        _cacheFilePath = Path.Combine(_cacheFolderPath, CacheFileName);

        _httpClient = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(4));
        _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
            MustRevalidate = true
        };
    }

    public async Task PreloadAsync()
    {
        try
        {
            await GetConfigAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("ModRepoConfig", $"Preload failed (non-fatal): {ex.Message}");
        }
    }

    public async Task<ModRepositoryConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoaded && _cachedConfig != null)
            return _cachedConfig;

        // 1. 尝试从远端获取（最新配置）
        var remote = await TryLoadFromRemoteAsync(cancellationToken);
        if (remote != null)
        {
            _cachedConfig = remote;
            _isLoaded = true;
            return remote;
        }

        // 2. 远端失败时尝试读取本地缓存
        var cached = TryLoadFromCache();
        if (cached != null)
        {
            _cachedConfig = cached;
            _isLoaded = true;
            return cached;
        }

        // 3. 都失败时返回空配置
        _cachedConfig = new ModRepositoryConfig();
        _isLoaded = true;
        return _cachedConfig;
    }

    private async Task<ModRepositoryConfig?> TryLoadFromRemoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{RemoteConfigUrl}?ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var parsed = Deserialize(json);
            if (parsed == null) return null;

            // 写入本地缓存
            try
            {
                Directory.CreateDirectory(_cacheFolderPath);
                await File.WriteAllTextAsync(_cacheFilePath, json, cancellationToken).ConfigureAwait(false);
            }
            catch { /* 缓存写入失败不影响主流程 */ }

            RuntimeLog.Write("ModRepoConfig", string.IsNullOrWhiteSpace(parsed.ModLinksUrl)
                ? "远端配置已获取，mod_links_url 为空，将使用默认源"
                : $"远端配置已获取，mod_links_url = {parsed.ModLinksUrl}");

            return parsed;
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("ModRepoConfig", $"拉取远端配置失败：{ex.Message}");
            return null;
        }
    }

    private ModRepositoryConfig? TryLoadFromCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath)) return null;
            var json = File.ReadAllText(_cacheFilePath);
            var parsed = Deserialize(json);
            if (parsed != null)
                RuntimeLog.Write("ModRepoConfig", $"已加载本地缓存配置：{_cacheFilePath}");
            return parsed;
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("ModRepoConfig", $"读取缓存失败：{ex.Message}");
            return null;
        }
    }

    private static ModRepositoryConfig? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ModRepositoryConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch { return null; }
    }
}
