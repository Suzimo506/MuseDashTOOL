using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IModCatalogService
{
    Task<List<ModInfo>> GetModsAsync(CancellationToken cancellationToken = default);
}

public class ModCatalogService : IModCatalogService
{
    private readonly IConfigService _configService;
    private readonly IModRepositoryConfigService _repoConfigService;
    private readonly HttpClient _httpClient;

    // 反序列化选项：
    // - 纯反射模式，不使用 AOT Source Generator，避免与其他选项发生冲突
    // - 大小写不敏感，兼容字段名大小写不一致的情况
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ModCatalogService(IConfigService configService, IModRepositoryConfigService repoConfigService)
    {
        _configService = configService;
        _repoConfigService = repoConfigService;
        _httpClient = new HttpClient();
        // 设置 User-Agent，防止部分服务器拒绝空 UA 请求
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MdModManager/1.0");
    }

    /// <summary>
    /// 从配置的 URL 下载 Mod 列表，兼容多种 JSON 格式：
    /// - 旧格式：{ "Mods": [ ... ] }（来自 GitHub MDMods/MuseDashModLinks）
    /// - 新格式：[ ... ]（来自 Gitee lxymahatma/ModLinks dev 分支）
    /// 并整合 Euterpe Market (https://euterpe-org.com/market) 的独有模组。
    /// </summary>
    public async Task<List<ModInfo>> GetModsAsync(CancellationToken cancellationToken = default)
    {
        // 1. 获取主源 (Gitee/GitHub)
        var giteeMods = await FetchGiteeModsAsync(cancellationToken);
        
        // 2. 获取 Euterpe Market 的新模组
        var euterpeMods = await FetchEuterpeModsAsync(giteeMods, cancellationToken);

        // 3. 合并列表
        var result = new List<ModInfo>(giteeMods);
        result.AddRange(euterpeMods);

        return result;
    }

    private async Task<List<ModInfo>> FetchGiteeModsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 优先使用远端仓库配置的 mod_links_url
            var repoConfig = await _repoConfigService.GetConfigAsync(cancellationToken);
            var url = !string.IsNullOrWhiteSpace(repoConfig.ModLinksUrl)
                ? repoConfig.ModLinksUrl
                : _configService.Config.ModLinksUrl;
            RuntimeLog.Write("ModCatalog", $"Fetching mod list from: {url}");
            var response = await _httpClient.GetStringAsync(url, cancellationToken);

            // 先尝试对象格式：{ "Mods": [...] }
            try
            {
                var modLinks = JsonSerializer.Deserialize<ModLinks>(response, _jsonOptions);
                if (modLinks?.Mods != null && modLinks.Mods.Length > 0)
                {
                    foreach (var m in modLinks.Mods) m.Source = "Gitee";
                    return new List<ModInfo>(modLinks.Mods);
                }
            }
            catch (JsonException) { }

            // 再尝试纯数组格式：[...]
            var modArray = JsonSerializer.Deserialize<ModInfo[]>(response, _jsonOptions);
            if (modArray != null)
            {
                foreach (var m in modArray) m.Source = "Gitee";
                return new List<ModInfo>(modArray);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModCatalog] FetchGiteeMods failed: {ex.Message}");
        }
        return new List<ModInfo>();
    }

    private async Task<List<ModInfo>> FetchEuterpeModsAsync(List<ModInfo> existingMods, CancellationToken cancellationToken)
    {
        var newMods = new List<ModInfo>();
        try
        {
            // 姓名规范化工具，用于去重比较
            static string Norm(string s) => s?.Replace(" ", "").Replace("_", "").ToLowerInvariant() ?? "";
            var existingNames = new HashSet<string>(existingMods.Select(m => Norm(m.Name)));

            // 1. 获取 Euterpe 简易列表
            var listUrl = "https://euterpe-org.com/api/catalog/mods?page=1&size=200";
            var listResponse = await _httpClient.GetStringAsync(listUrl, cancellationToken);
            using var doc = JsonDocument.Parse(listResponse);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return newMods;

            var tasks = new List<Task<ModInfo?>>();
            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name) || existingNames.Contains(Norm(name)))
                    continue;

                var mid = item.TryGetProperty("mid", out var m) ? m.GetInt32() : 0;
                if (mid <= 0) continue;

                // 2. 并行获取详情以拿到 repository 等信息
                tasks.Add(FetchEuterpeDetailAsync(mid, cancellationToken));
            }

            var details = await Task.WhenAll(tasks);
            foreach (var d in details)
            {
                if (d != null) newMods.Add(d);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModCatalog] FetchEuterpeMods failed: {ex.Message}");
        }
        return newMods;
    }

    private async Task<ModInfo?> FetchEuterpeDetailAsync(int mid, CancellationToken cancellationToken)
    {
        try
        {
            var detailUrl = $"https://euterpe-org.com/api/catalog/mods/{mid}";
            var detailResponse = await _httpClient.GetStringAsync(detailUrl, cancellationToken);
            using var doc = JsonDocument.Parse(detailResponse);
            var root = doc.RootElement;

            var mod = new ModInfo
            {
                Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Author = root.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "",
                Version = root.TryGetProperty("current_version", out var v) ? v.GetString() ?? "" : "",
                Description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                GameVersion = root.TryGetProperty("game_version", out var gv) ? gv.GetString() ?? "*" : "*",
                Source = "Euterpe"
            };

            // 处理仓库链接
            if (root.TryGetProperty("repository", out var repo) && repo.ValueKind != JsonValueKind.Null)
            {
                var repoPath = repo.GetString();
                if (!string.IsNullOrEmpty(repoPath))
                {
                    mod.HomePage = repoPath.StartsWith("http") ? repoPath : $"https://github.com/{repoPath}";
                    
                    // 尝试猜测下载文件名
                    // Euterpe API 详情里没给文件名，通常和仓库名或 Mod 名一致
                    mod.FileName = mod.Name.Replace(" ", "") + ".dll";
                }
            }

            return mod;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModCatalog] FetchEuterpeDetail({mid}) failed: {ex.Message}");
            return null;
        }
    }
}
