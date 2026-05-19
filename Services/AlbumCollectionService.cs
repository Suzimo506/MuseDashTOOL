using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Reflection;
using MdModManager.Models;
using MdModManager.Helpers;

namespace MdModManager.Services;

public interface IAlbumCollectionService
{
    Task<List<DesignerCategory>> GetLocalCollectionsAsync();
    Task<List<DesignerCategory>> GetCollectionsAsync();
    Task SaveCollectionsCacheAsync(IEnumerable<DesignerCategory> collections);
    Task<List<DesignerChart>> GetLocalChartsAsync(string categoryName);
    Task<List<DesignerChart>> GetChartsAsync(string categoryName);
    Task<List<MdmcChart>> GetLocalCommunityChartsAsync(string name);
    Task<List<MdmcChart>> GetCommunityChartsAsync(string name, string repoUrl);
    Task<List<(DesignerCategory Category, DesignerChart Chart)>> SearchChartsAsync(string query);
    Task<List<(string CategoryName, MdmcChart Chart)>> SearchCommunityChartsAsync(string query);
    void ReleaseCollectionChartsCache(string categoryName);
    void ReleaseCommunityChartsCache(string name);
}

public class AlbumCollectionService : IAlbumCollectionService
{
    public const string TEdgeoolGroupName = "TEdgeool";

    public static readonly string[] TEdgeoolChildCategoryNames =
    {
        "Anime TEdgeool",
        "Touhou TEdgeool",
        "Rhythm TEdgeool",
        "Vocal &idol TEdgeool"
    };

    public const string TEdgeoolDescription = "『TEdegool』是一个由浅浅小饼干组织的，专为喵斯兔提供的长期多系列曲包，不定时更新，欢迎投稿，或许下一次更新就会出现你的谱面！";
    public const string TEdgeoolHomepageUrl = "https://space.bilibili.com/87417184/upload/video";

    private const string Owner = "KuoKing506";
    private const string Repo = "CustomAlbums_Collection";
    private const string Branch = "main";
    private const string GitHubApiBase = $"https://api.github.com/repos/{Owner}/{Repo}/contents";
    private const string RemoteIndexUrl = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/designers.json";

    // ── 新整合包仓库 ──
    private const string NewRepo = "NewCollectionAlbums";
    private const string NewRepoGitHub = $"https://github.com/{Owner}/{NewRepo}";
    private const string NewRepoRawBase = $"https://raw.githubusercontent.com/{Owner}/{NewRepo}/{Branch}";
    private const string NewRepoCollectionIndexUrl = $"{NewRepoRawBase}/Collection_index.json";
    private const string NewRepoReleaseTag = "NO.1";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg", ".mp3", ".wav", ".flac"
    };

    private readonly HttpClient _http = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(15));
    private List<DesignerCategory>? _categoryCache;
    private readonly SemaphoreSlim _collectionsLock = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> _indexLocks = new(StringComparer.OrdinalIgnoreCase);
    private List<DesignerCategory>? _metadataCache;
    private readonly Dictionary<string, List<DesignerChart>> _chartsCache = new(StringComparer.OrdinalIgnoreCase);

    // 社区仓库配置
    public static readonly (string Name, string RepoUrl)[] CommunityConfigs = 
    { 
        ("通过审议", "https://download.suzimo.site/通过审议"), 
        ("令人生草", "https://download.suzimo.site/令人生草"), 
        ("待定或有些小问题", "https://download.suzimo.site/待定或有些小问题")
    };

    // 新增谱师个人仓库时，只需要把远端文件夹名加入这里，就会从“曲包”移动到“谱师个人仓库”分类。
    public static readonly string[] PersonalRepositoryDisplayOrder =
    {
        "桔子的谱面仓库",
        "Greenhub的个人谱面",
        "Dunno的谱面小库",
        "石井圣人的谱面仓库",
        "布布西里的谱面",
        "独家特供抽象谱面（宋Jerry）",
        "MC谱面（虚无）",
        "MC谱面（鱼，好大的鱼）",
        "超高难度奉上!!!（HuRew_奇迹）",
        "MC谱面（云星雨）",
        "MC谱面（谱师：龙星）",
        "MC谱面（是陌灬鸭 老哥）",
        "MC谱面（星云）",
        "MC谱面（懒惰的sans)",
        "MC谱面（小赖赖酱）",
        "EX谱面（霞诗子）",
        "MC谱面（土味刻晴）",
        "EX谱面（谱师：XXX7）",
        "MC谱师（中二的救赎）"
    };

    public static readonly HashSet<string> PersonalRepositoryNames = new(PersonalRepositoryDisplayOrder, StringComparer.OrdinalIgnoreCase);

    // 链接先留空，后续你只需要在这里填写对应主页地址即可。
    public static readonly Dictionary<string, string> PersonalRepositoryHomepages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["桔子的谱面仓库"] = "",
        ["Greenhub的个人谱面"] = "https://space.bilibili.com/130793282?spm_id_from=333.1387.follow.user_card.click",
        ["Dunno的谱面小库"] = "https://space.bilibili.com/1548500340?spm_id_from=333.1387.follow.user_card.click",
        ["石井圣人的谱面仓库"] = "https://v.douyin.com/JPY1Nk6P6bM/ 3@9.com :2pm",
        ["布布西里的谱面"] = "",
        ["独家特供抽象谱面（宋Jerry）"] = "",
        ["MC谱面（虚无）"] = "",
        ["MC谱面（鱼，好大的鱼）"] = "",
        ["超高难度奉上!!!（HuRew_奇迹）"] = "",
        ["MC谱面（云星雨）"] = "",
        ["MC谱面（谱师：龙星）"] = "",
        ["MC谱面（是陌灬鸭 老哥）"] = "",
        ["MC谱面（星云）"] = "",
        ["MC谱面（懒惰的sans)"] = "",
        ["MC谱面（小赖赖酱）"] = "",
        ["EX谱面（霞诗子）"] = "",
        ["MC谱面（土味刻晴）"] = "",
        ["EX谱面（谱师：XXX7）"] = "",
        ["MC谱师（中二的救赎）"] = ""
    };

    public static bool IsPersonalRepositoryName(string? name)
        => !string.IsNullOrWhiteSpace(name) && PersonalRepositoryNames.Contains(name);

    public static bool IsTEdgeoolGroupName(string? name)
        => string.Equals(name, TEdgeoolGroupName, StringComparison.OrdinalIgnoreCase);

    public static bool IsTEdgeoolChildCategoryName(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
           !IsTEdgeoolGroupName(name) &&
           name.Contains(TEdgeoolGroupName, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeTEdgeoolName(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : Regex.Replace(name, @"\s+", string.Empty).Trim();

    public static string GetPersonalRepositoryHomepage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        return PersonalRepositoryHomepages.TryGetValue(name, out var url) ? url : string.Empty;
    }

    public static string GetTEdgeoolHomepage(string? name)
        => IsTEdgeoolGroupName(name) ? TEdgeoolHomepageUrl : string.Empty;

    private readonly Dictionary<string, List<MdmcChart>> _communityChartsCache = new(StringComparer.OrdinalIgnoreCase);

    public AlbumCollectionService()
    {
        _http.DefaultRequestHeaders.Remove("User-Agent");
        _http.DefaultRequestHeaders.Add("User-Agent", "MuseDashTOOL-AlbumCollection");
    }

    public async Task<List<DesignerCategory>> GetLocalCollectionsAsync()
    {
        if (_categoryCache != null) return _categoryCache;
        var cachePath = Path.Combine(AppContext.BaseDirectory, "Cache", "FoldersList.json");
        if (File.Exists(cachePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(cachePath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json, options);
                if (result != null && result.TryGetValue("collections", out var folders))
                {
                    var excludeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "通过审议", "令人生草", "待定或有些小问题", "Pictures", "Mods" };
                    var categories = folders.Where(f => !excludeNames.Contains(f)).OrderBy(f => f)
                        .Select(f => new DesignerCategory { Name = f, Description = "" }).ToList();
                    _categoryCache = categories;
                    return categories;
                }
                
                // 尝试解析为包含描述的结构
                var newResult = JsonSerializer.Deserialize<NewCollectionIndex>(json, options);
                if (newResult?.Collections != null && newResult.Collections.Count > 0)
                {
                    if (_categoryCache == null) _categoryCache = new List<DesignerCategory>();
                    var excludeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "通过审议", "令人生草", "待定或有些小问题", "Pictures", "Mods" };
                    var categories = newResult.Collections
                        .Where(c => !excludeNames.Contains(c.Name))
                        .OrderBy(c => c.Name)
                        .Select(c => 
                        {
                            var existing = _categoryCache.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
                            if (existing != null)
                            {
                                if (!string.IsNullOrWhiteSpace(c.Description)) existing.Description = c.Description;
                                return existing;
                            }
                            return new DesignerCategory { Name = c.Name, Description = c.Description ?? "" };
                        }).ToList();
                    _categoryCache = categories;
                    return categories;
                }
            }
            catch { }
        }
        return new List<DesignerCategory>();
    }

    public async Task<List<DesignerCategory>> GetCollectionsAsync()
    {
        await _collectionsLock.WaitAsync();
        try
        {
            // 移除内存缓存拦截，确保后台同步任务能真正触发网络请求
            // 与 GetChartsAsync 逻辑对齐，由调用方（ViewModel）控制本地/远端的加载顺序

        List<DesignerCategory> categories = new();
        var cachePath = Path.Combine(AppContext.BaseDirectory, "Cache", "FoldersList.json");

        // 1. 尝试从远端获取（去掉 ?t= 以命中 CDN 缓存）
        try
        {
            var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";
            var infoDomain = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.AlbumInfoDomain) ? MirrorDomainRegistry.AlbumInfoDomain : $"workerdl.{baseHost}";
            var workerUrl = $"https://{infoDomain}/api/list";
            var json = await _http.GetStringAsync(workerUrl);
            
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json, options);

            if (result != null && result.TryGetValue("collections", out var folders))
            {
                var excludeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "通过审议", "令人生草", "待定或有些小问题", "Pictures", "Mods" };
                if (_categoryCache == null) _categoryCache = new List<DesignerCategory>();
                categories = folders.Where(f => !excludeNames.Contains(f)).OrderBy(f => f)
                    .Select(f => 
                    {
                        var existing = _categoryCache.FirstOrDefault(x => string.Equals(x.Name, f, StringComparison.OrdinalIgnoreCase));
                        return existing ?? new DesignerCategory { Name = f, Description = "" };
                    }).ToList();
            }
            else
            {
                // 尝试解析为包含描述的结构
                var newResult = JsonSerializer.Deserialize<NewCollectionIndex>(json, options);
                if (newResult?.Collections != null && newResult.Collections.Count > 0)
                {
                    if (_categoryCache == null) _categoryCache = new List<DesignerCategory>();
                    var excludeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "通过审议", "令人生草", "待定或有些小问题", "Pictures", "Mods" };
                    categories = newResult.Collections
                        .Where(c => !excludeNames.Contains(c.Name))
                        .OrderBy(c => c.Name)
                        .Select(c => 
                        {
                            var existing = _categoryCache.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
                            if (existing != null)
                            {
                                if (!string.IsNullOrWhiteSpace(c.Description)) existing.Description = c.Description;
                                return existing;
                            }
                            return new DesignerCategory { Name = c.Name, Description = c.Description ?? "" };
                        }).ToList();
                }
            }

            if (categories.Count > 0)
            {
                // 成功后同步到本地缓存
                try
                {
                    await TrySaveCacheIfChangedAsync(cachePath, json);

                    // 同步清理本地多余的缓存文件 (Orphaned Cache Cleanup)
                    var indexCacheDir = Path.Combine(AppContext.BaseDirectory, "Cache", "CollectionIndexes");
                    if (Directory.Exists(indexCacheDir))
                    {
                        var remoteFolderSet = categories.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var localFiles = Directory.GetFiles(indexCacheDir, "*.json");
                        foreach (var file in localFiles)
                        {
                            var fileName = Path.GetFileNameWithoutExtension(file);
                            if (!remoteFolderSet.Contains(fileName))
                            {
                                File.Delete(file);
                                Log($"Cleaned up orphaned cache file: {fileName}");
                            }
                        }
                    }
                } catch { }

                Log($"Loaded {categories.Count} collection folders from Remote (CDN).");
                _categoryCache = categories;
                return categories;
            }
        }
        catch (Exception ex)
        {
            Log($"Remote fetch failed, falling back to local: {ex.Message}");
        }

            // 2. 远端失败，尝试从本地缓存读取
            return await GetLocalCollectionsAsync();
        }
        finally
        {
            _collectionsLock.Release();
        }
    }

    public async Task SaveCollectionsCacheAsync(IEnumerable<DesignerCategory> collections)
    {
        var cachePath = Path.Combine(AppContext.BaseDirectory, "Cache", "FoldersList.json");
        var payload = new
        {
            collections = collections
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new
                {
                    name = c.Name,
                    description = c.Description ?? string.Empty
                })
                .ToList()
        };

        var json = JsonSerializer.Serialize(payload);
        await TrySaveCacheIfChangedAsync(cachePath, json);
    }

    private async Task<List<DesignerChart>> FetchSpecialR2CategoryAsync(string name)
    {
        var encodedName = Uri.EscapeDataString(name);
        var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";
        
        string? json = null;
        var cacheDir = Path.Combine(AppContext.BaseDirectory, "Cache", "CollectionIndexes");
        var cachePath = Path.Combine(cacheDir, $"{name}.json");

        // 1. 尝试从远端获取（去掉 ?t=）
        try
        {
            var downloadDomain = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.AlbumDownloadDomain) ? MirrorDomainRegistry.AlbumDownloadDomain : $"download.{baseHost}";
            var url = $"https://{downloadDomain}/{encodedName}/index.json";
            json = await _http.GetStringAsync(url);
            Log($"Fetched remote index for '{name}' from {url}");

            // 使用统一的按需同步逻辑
            await TrySaveCacheIfChangedAsync(cachePath, json);
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException httpEx && httpEx.StatusCode == HttpStatusCode.NotFound)
            {
                TryDeleteCollectionCache(name, cachePath);
                _chartsCache.Remove(name);
                _categoryCache?.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                Log($"Collection '{name}' returned 404. Removed local cache and treating it as deleted.");
                return new List<DesignerChart>();
            }

            Log($"Remote fetch failed for '{name}': {ex.Message}. Falling back to local cache.");
            
            // 远端失败，回退到本地缓存读取，保证界面不为空
            if (File.Exists(cachePath))
            {
                try { json = await File.ReadAllTextAsync(cachePath); } catch { }
            }
        }

        if (string.IsNullOrEmpty(json))
        {
            return new List<DesignerChart>();
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        
        List<CommunityIndexItem>? items = null;
        
        // 尝试格式 1：完整的 Collection_index.json 结构（包含 collections 数组）
        try {
            var newCol = JsonSerializer.Deserialize<NewCollectionIndex>(json, options);
            if (newCol?.Collections != null && newCol.Collections.Count > 0) {
                var match = newCol.Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) ?? newCol.Collections.First();
                items = match.Charts;
                
                // 同步描述到缓存
                if (!string.IsNullOrWhiteSpace(match.Description))
                {
                    var cachedCat = _categoryCache?.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (cachedCat != null) cachedCat.Description = match.Description;
                }
            }
        } catch { }

        // 尝试格式 2：包含 Release Tag 和 Charts 的老社区源结构
        if (items == null) {
            try {
                var wrapper = JsonSerializer.Deserialize<CommunityIndexWrapper>(json, options);
                items = wrapper?.Charts;
            } catch { }
        }

        // 尝试格式 3：最简单的纯粹 List 数组
        if (items == null) {
            try { items = JsonSerializer.Deserialize<List<CommunityIndexItem>>(json, options); } catch { }
        }

        if (items == null || items.Count == 0) {
            Log($"Failed to extract charts array from JSON for '{name}'. JSON length: {json.Length}");
            return new List<DesignerChart>();
        }

        var charts = new List<DesignerChart>();
        foreach(var item in items.AsEnumerable().Reverse()) {
           var cover = BuildR2ResourceUrl(baseHost, name, "covers", item.CoverUrl);
           var demo = BuildR2ResourceUrl(baseHost, name, "demos", item.DemoUrl);
           var mp3 = BuildR2ResourceUrl(baseHost, name, "demos", item.DemoMp3Url);
           
           var dlUrl = BuildR2ResourceUrl(baseHost, name, "mdm", item.DownloadUrl);

           string cleanTitle = !string.IsNullOrEmpty(item.OriginalId) ? item.OriginalId : (item.Title ?? "");
           cleanTitle = Regex.Replace(cleanTitle, @"^\[(?:Lv|LV|lv)[.\s]?\s*[^\]]+\]\s*", "", RegexOptions.IgnoreCase);

           charts.Add(new DesignerChart {
               Id = !string.IsNullOrWhiteSpace(item.Id) ? item.Id : (item.DownloadUrl ?? Guid.NewGuid().ToString()),
               Title = cleanTitle,
               Artist = item.Artist,
               Author = item.Charter,
               Bpm = item.Bpm,
               CoverUrl = cover,
               DemoUrl = demo,
               DemoMp3Url = mp3,
               DownloadUrl = dlUrl,
               Difficulties = item.Difficulties
           });
        }
        return charts;
    }

    public async Task<List<DesignerChart>> GetLocalChartsAsync(string categoryName)
    {
        // 优先使用内存缓存（后台全量同步后已填充）
        if (_chartsCache.TryGetValue(categoryName, out var cached))
            return cached;

        var cachePath = Path.Combine(AppContext.BaseDirectory, "Cache", "CollectionIndexes", $"{categoryName}.json");
        if (!File.Exists(cachePath)) return new List<DesignerChart>();
        try
        {
            var json = await File.ReadAllTextAsync(cachePath);
            var charts = ParseDesignerCharts(json, categoryName);
            _chartsCache[categoryName] = charts; // 回写内存缓存，避免重复解析
            return charts;
        }
        catch { return new List<DesignerChart>(); }
    }

    public async Task<List<DesignerChart>> GetChartsAsync(string categoryName)
    {
        var charts = await FetchSpecialR2CategoryAsync(categoryName);
        _chartsCache[categoryName] = charts;
        return charts;
    }

    private List<DesignerChart> ParseDesignerCharts(string json, string name)
    {
        var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        List<CommunityIndexItem>? items = null;
        try {
            var newCol = JsonSerializer.Deserialize<NewCollectionIndex>(json, options);
            if (newCol?.Collections != null && newCol.Collections.Count > 0) {
                var match = newCol.Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) ?? newCol.Collections.First();
                items = match.Charts;

                // 同步描述到缓存，确保搜索时也能显示描述
                if (!string.IsNullOrWhiteSpace(match.Description))
                {
                    var cachedCat = _categoryCache?.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (cachedCat != null) cachedCat.Description = match.Description;
                }
            }
        } catch { }
        if (items == null) {
            try { items = JsonSerializer.Deserialize<CommunityIndexWrapper>(json, options)?.Charts; } catch { }
        }
        if (items == null) {
            try { items = JsonSerializer.Deserialize<List<CommunityIndexItem>>(json, options); } catch { }
        }
        if (items == null) return new List<DesignerChart>();

        var charts = new List<DesignerChart>();
        foreach(var item in items.AsEnumerable().Reverse()) {
           var cover = BuildR2ResourceUrl(baseHost, name, "covers", item.CoverUrl);
           var demo = BuildR2ResourceUrl(baseHost, name, "demos", item.DemoUrl);
           var mp3 = BuildR2ResourceUrl(baseHost, name, "demos", item.DemoMp3Url);
           var dlUrl = BuildR2ResourceUrl(baseHost, name, "mdm", item.DownloadUrl);
           string cleanTitle = !string.IsNullOrEmpty(item.OriginalId) ? item.OriginalId : (item.Title ?? "");
           cleanTitle = Regex.Replace(cleanTitle, @"^\[(?:Lv|LV|lv)[.\s]?\s*[^\]]+\]\s*", "", RegexOptions.IgnoreCase);
           charts.Add(new DesignerChart {
               Id = !string.IsNullOrWhiteSpace(item.Id) ? item.Id : (item.DownloadUrl ?? Guid.NewGuid().ToString()),
               Title = cleanTitle, Artist = item.Artist, Author = item.Charter,
               Bpm = item.Bpm, CoverUrl = cover, DemoUrl = demo, DemoMp3Url = mp3,
               DownloadUrl = dlUrl, Difficulties = item.Difficulties
           });
        }
        return charts;
    }

    public async Task<List<(DesignerCategory Category, DesignerChart Chart)>> SearchChartsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<(DesignerCategory Category, DesignerChart Chart)>();

        var results = new List<(DesignerCategory Category, DesignerChart Chart)>();
        var normalizedQuery = query.Trim().ToLowerInvariant();

        // 搜索 R2 整合包（纯本地缓存）
        var newCategories = await GetLocalCollectionsAsync();
        if (newCategories != null)
        {
            foreach (var col in newCategories)
            {
                var charts = await GetLocalChartsAsync(col.Name);
                if (charts == null || charts.Count == 0) continue;

                foreach (var chart in charts)
                {
                    if (chart.Title?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                        chart.Author?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                        chart.Artist?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        results.Add((col, CloneAndNormalizeChart(chart)));
                    }
                }
            }
        }

        return results;
    }

    public async Task<List<(string CategoryName, MdmcChart Chart)>> SearchCommunityChartsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<(string CategoryName, MdmcChart Chart)>();

        var normalizedQuery = query.Trim().ToLowerInvariant();
        var results = new List<(string CategoryName, MdmcChart Chart)>();

        var tasks = CommunityConfigs.Select(async config =>
        {
            try
            {
                // 社区搜索同样优先使用本地缓存
                var charts = await GetLocalCommunityChartsAsync(config.Name);
                var matches = charts.Where(c => 
                    c.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                    c.Artist.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                    c.Charter.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                ).ToList();

                lock (results)
                {
                    foreach (var m in matches)
                        results.Add((config.Name, m));
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to search community repo '{config.Name}': {ex.Message}");
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    public async Task<List<MdmcChart>> GetLocalCommunityChartsAsync(string name)
    {
        if (_communityChartsCache.TryGetValue(name, out var cached)) return cached;
        string? localPath = FindLocalCommunityIndexPath(name);
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return new List<MdmcChart>();
        try
        {
            var json = await File.ReadAllTextAsync(localPath);
            var repoUrl = CommunityConfigs.FirstOrDefault(c => c.Name == name).RepoUrl ?? "";
            return ParseCommunityCharts(json, name, repoUrl);
        }
        catch { return new List<MdmcChart>(); }
    }

    public async Task<List<MdmcChart>> GetCommunityChartsAsync(string name, string repoUrl)
    {
        string json = "";
        try
        {
            string finalUrl;
            if (repoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            {
                var rawBaseUrl = repoUrl.Replace("github.com", "raw.githubusercontent.com") + "/main";
                var rawIndexUrl = rawBaseUrl + "/index.json";
                var configService = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<IConfigService>();
                var source = configService?.Config?.DownloadSource ?? "Auto";
                finalUrl = GitHubMirrorHelper.ApplyMirror(rawIndexUrl, source);
            }
            else
            {
                finalUrl = repoUrl.TrimEnd('/') + "/index.json";
            }
            
            json = await _http.GetStringAsync(finalUrl);
            Log($"Fetched remote index for community repo '{name}'");
            
            var cachePath = Path.Combine(AppContext.BaseDirectory, "Cache", "CommunityIndexes", $"{name}.json");
            await TrySaveCacheIfChangedAsync(cachePath, json);
        }
        catch (Exception ex)
        {
            Log($"Remote fetch failed for community repo '{name}': {ex.Message}");
            return new List<MdmcChart>();
        }

        return ParseCommunityCharts(json, name, repoUrl);
    }

    public void ReleaseCollectionChartsCache(string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return;

        _chartsCache.Remove(categoryName);
        Log($"Released collection charts cache: {categoryName}");
    }

    public void ReleaseCommunityChartsCache(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        _communityChartsCache.Remove(name);
        Log($"Released community charts cache: {name}");
    }

    private List<MdmcChart> ParseCommunityCharts(string json, string name, string repoUrl)
    {
        var rawBaseUrlFinal = repoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase) 
            ? repoUrl.Replace("github.com", "raw.githubusercontent.com") + "/main"
            : repoUrl;
        
        List<CommunityIndexItem>? items = null;
        List<string> defaultReleaseTags = [];
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        try {
            var newCol = JsonSerializer.Deserialize<NewCollectionIndex>(json, options);
            if (newCol?.Collections != null && newCol.Collections.Count > 0) {
                var match = newCol.Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) ?? newCol.Collections.First();
                items = match.Charts;
            }
        } catch { }

        if (items == null) {
            try {
                var wrapper = JsonSerializer.Deserialize<CommunityIndexWrapper>(json, options);
                if (wrapper != null) {
                    defaultReleaseTags = CommunityReleaseHelper.MergeReleaseTags(null, wrapper.ReleaseTag, wrapper.ReleaseTags);
                    items = wrapper.Charts;
                }
            } catch { }
        }

        if (items == null) {
            try { items = JsonSerializer.Deserialize<List<CommunityIndexItem>>(json, options); } catch { }
        }

        if (items == null) return new List<MdmcChart>();

        var charts = items.Select(item => MapToIndexChart(item, rawBaseUrlFinal, repoUrl, defaultReleaseTags)).ToList();
        _communityChartsCache[name] = charts;
        return charts;
    }

    private static string? FindLocalCommunityIndexPath(string categoryName)
    {
        var candidates = new List<string>();
        var basePaths = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        
        foreach (var bp in basePaths)
        {
            candidates.Add(Path.Combine(bp, "Cache", "CommunityIndexes", $"{categoryName}.json"));
            
            var current = new DirectoryInfo(bp);
            for (var depth = 0; depth < 5 && current != null; depth++, current = current.Parent)
            {
                candidates.Add(Path.Combine(current.FullName, "Cache", "CommunityIndexes", $"{categoryName}.json"));
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private MdmcChart MapToIndexChart(
        CommunityIndexItem item,
        string rawBaseUrl,
        string githubRepoUrl,
        IReadOnlyList<string> defaultReleaseTags)
    {
        var isGithub = githubRepoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase);
        var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";
        var repoName = Path.GetFileName(githubRepoUrl.TrimEnd('/'));

        var coverUrl = item.CoverUrl;
        if (!string.IsNullOrEmpty(coverUrl) && !coverUrl.StartsWith("http"))
            coverUrl = isGithub ? rawBaseUrl + "/covers/" + coverUrl : BuildR2ResourceUrl(baseHost, repoName, "covers", coverUrl);

        var demoUrl = item.DemoUrl;
        if (!string.IsNullOrEmpty(demoUrl) && !demoUrl.StartsWith("http"))
            demoUrl = isGithub ? rawBaseUrl + "/demos/" + demoUrl : BuildR2ResourceUrl(baseHost, repoName, "demos", demoUrl);

        var demoMp3Url = item.DemoMp3Url;
        if (!string.IsNullOrEmpty(demoMp3Url) && !demoMp3Url.StartsWith("http"))
            demoMp3Url = isGithub ? rawBaseUrl + "/demos/" + demoMp3Url : BuildR2ResourceUrl(baseHost, repoName, "demos", demoMp3Url);

        var downloadUrl = item.DownloadUrl;
        if (!isGithub)
        {
            downloadUrl = BuildR2ResourceUrl(baseHost, repoName, "mdm", downloadUrl);
        }
        else
        {
            // 旧版下载逻辑
            // var candidateTags = CommunityReleaseHelper.MergeReleaseTags(defaultReleaseTags, item.ReleaseTag, item.ReleaseTags);
            // downloadUrl = CommunityReleaseHelper.ResolveReleaseDownloadUrl(downloadUrl, githubRepoUrl, candidateTags);

            // 新版 R2 下载逻辑
            if (!string.IsNullOrEmpty(downloadUrl) && !downloadUrl.StartsWith("http"))
            {
                downloadUrl = BuildR2ResourceUrl(baseHost, repoName, "mdm", downloadUrl);
            }
        }

        // 映射逻辑参考 CommunityCategoryDetailViewModel.cs
        var sheets = new List<MdmcSheet>();
        if (item.Difficulties != null && item.Difficulties.Count > 0)
        {
            foreach (var d in item.Difficulties)
            {
                if (!string.IsNullOrEmpty(d))
                {
                    var splitted = d.Split(new char[] { ',', '，', ' ', '/', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var s in splitted)
                        sheets.Add(new MdmcSheet { Difficulty = s });
                }
            }
        }
        else if (!string.IsNullOrEmpty(item.Difficulty) && item.Difficulty != "0" && item.Difficulty != "?")
        {
            foreach (var p in item.Difficulty.Split(new char[] { ',', '，', ' ', '/', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                sheets.Add(new MdmcSheet { Difficulty = p });
        }

        string cleanTitle = !string.IsNullOrEmpty(item.OriginalId) ? item.OriginalId : (item.Title ?? "");
        cleanTitle = Regex.Replace(cleanTitle, @"^\[(?:Lv|LV|lv)[.\s]?\s*[^\]]+\]\s*", "", RegexOptions.IgnoreCase);
        cleanTitle = Regex.Replace(cleanTitle, @"^(?:Lv|LV|lv)[.\s]?\s*\d+\s*", "", RegexOptions.IgnoreCase);
        if (cleanTitle.EndsWith(".mdm", StringComparison.OrdinalIgnoreCase))
            cleanTitle = cleanTitle[..^4];

        return new MdmcChart
        {
            Id = !string.IsNullOrWhiteSpace(item.Id) ? item.Id : (item.DownloadUrl ?? Guid.NewGuid().ToString()),
            Title = cleanTitle.Trim(),
            Artist = item.Artist,
            Charter = item.Charter,
            Bpm = item.Bpm,
            CustomCoverUrl = coverUrl,
            CustomDemoUrl = demoUrl,
            CustomDemoMp3Url = demoMp3Url,
            CustomDownloadUrl = downloadUrl,
            Sheets = sheets
        };
    }

    // 内部类，用于解析群友索引
    private class CommunityIndexWrapper { [JsonPropertyName("release_tag")] public JsonElement ReleaseTag { get; set; } [JsonPropertyName("release_tags")] public JsonElement ReleaseTags { get; set; } [JsonPropertyName("charts")] public List<CommunityIndexItem> Charts { get; set; } = new(); }
    private class CommunityIndexItem { [JsonPropertyName("id")] public string Id { get; set; } = ""; [JsonPropertyName("original_id")] public string OriginalId { get; set; } = ""; [JsonPropertyName("title")] public string Title { get; set; } = ""; [JsonPropertyName("artist")] public string Artist { get; set; } = ""; [JsonPropertyName("charter")] public string Charter { get; set; } = ""; [JsonPropertyName("bpm")] public string Bpm { get; set; } = ""; [JsonPropertyName("scene")] public string Scene { get; set; } = ""; [JsonPropertyName("difficulty")] public string Difficulty { get; set; } = ""; [JsonPropertyName("difficulties")] public List<string>? Difficulties { get; set; } [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = ""; [JsonPropertyName("demo_url")] public string DemoUrl { get; set; } = ""; [JsonPropertyName("demo_mp3_url")] public string DemoMp3Url { get; set; } = ""; [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = ""; [JsonPropertyName("release_tag")] public JsonElement ReleaseTag { get; set; } [JsonPropertyName("release_tags")] public JsonElement ReleaseTags { get; set; } }

    private class NewCollectionIndex { [JsonPropertyName("collections")] public List<NewCollectionEntry> Collections { get; set; } = new(); }
    private class NewCollectionEntry { [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("description")] public string Description { get; set; } = ""; [JsonPropertyName("charts")] public List<CommunityIndexItem> Charts { get; set; } = new(); }

    private async Task<List<GitHubContentItem>> GetRepoContentsAsync(string path)
    {
        var encodedPath = string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var url = string.IsNullOrWhiteSpace(encodedPath)
            ? $"{GitHubApiBase}?ref={Branch}"
            : $"{GitHubApiBase}/{encodedPath}?ref={Branch}";
        Log($"Fetching repo contents: {url}");
        var items = await _http.GetFromJsonAsync<List<GitHubContentItem>>(url);
        Log($"Repo contents fetched for '{path}', item count: {items?.Count ?? 0}");
        return items ?? new List<GitHubContentItem>();
    }

    private static List<DesignerChart> BuildChartsFromFiles(IEnumerable<GitHubContentItem> items)
    {
        var map = new Dictionary<string, DesignerChart>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase))
                continue;

            var ext = Path.GetExtension(item.Name);
            var stem = Path.GetFileNameWithoutExtension(item.Name);
            var url = NormalizeResourceUrl(item.DownloadUrl ?? BuildRawUrl(item.Path));

            if (string.Equals(ext, ".mdm", StringComparison.OrdinalIgnoreCase))
            {
                var chart = GetOrCreateChart(map, stem, item.Path);
                chart.DownloadUrl = url;
                Log($"Mapped MDM: key='{stem}', url='{url}'");
                continue;
            }

            if (ImageExtensions.Contains(ext))
            {
                var key = TrimKnownSuffix(stem, "_cover");
                var chart = GetOrCreateChart(map, key, item.Path);
                chart.CoverUrl = url;
                Log($"Mapped cover: key='{key}', file='{item.Name}', url='{url}'");
                continue;
            }

            if (AudioExtensions.Contains(ext))
            {
                var key = TrimKnownSuffix(stem, "_demo");
                var chart = GetOrCreateChart(map, key, item.Path);
                if (string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    chart.DemoMp3Url = url;
                    Log($"Mapped MP3 demo: key='{key}', url='{url}'");
                }
                else if (string.IsNullOrEmpty(chart.DemoUrl))
                {
                    chart.DemoUrl = url;
                    Log($"Mapped audio demo: key='{key}', file='{item.Name}', url='{url}'");
                }
            }
        }

        var result = map.Values
            .Where(x => !string.IsNullOrEmpty(x.DownloadUrl))
            .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var chart in result)
        {
            Log($"Built chart: title='{chart.Title}', download='{chart.DownloadUrl}', cover='{chart.CoverUrl}', demo='{chart.DemoUrl}', mp3='{chart.DemoMp3Url}'");
        }

        return result;
    }

    private static DesignerChart GetOrCreateChart(
        IDictionary<string, DesignerChart> map,
        string key,
        string path)
    {
        if (map.TryGetValue(key, out var existing))
            return existing;

        var chart = new DesignerChart
        {
            Id = path,
            Title = ExtractTitle(key)
        };
        map[key] = chart;
        return chart;
    }

    private static string TrimKnownSuffix(string value, string suffix)
    {
        return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length]
            : value;
    }

    private static string ExtractTitle(string rawName)
    {
        var title = Regex.Replace(rawName, @"^\s*\[Lv\.[^\]]+\]\s*", "");
        title = title.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(title) ? rawName : title;
    }

    private static string BuildRawUrl(string path)
    {
        var encoded = string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/{encoded}";
    }

    private async Task EnrichChartsFromMetadataAsync(string categoryName, List<DesignerChart> charts)
    {
        if (charts.Count == 0)
            return;

        var metadataCategory = (await GetMetadataIndexAsync())
            .FirstOrDefault(x => string.Equals(x.Name, categoryName, StringComparison.OrdinalIgnoreCase));

        if (metadataCategory == null || metadataCategory.Charts.Count == 0)
        {
            Log($"No metadata found for '{categoryName}'.");
            return;
        }

        var metadataLookup = BuildMetadataLookup(metadataCategory.Charts);

        foreach (var chart in charts)
        {
            foreach (var key in GetChartLookupKeys(chart))
            {
                if (!metadataLookup.TryGetValue(key, out var metadata))
                    continue;

                chart.Artist = metadata.Artist;
                chart.Author = metadata.Author;
                chart.Bpm = metadata.Bpm;
                chart.Id = string.IsNullOrWhiteSpace(chart.Id) ? metadata.Id : chart.Id;
                chart.DemoMp3Url = string.IsNullOrWhiteSpace(chart.DemoMp3Url)
                    ? NormalizeResourceUrl(metadata.DemoMp3Url)
                    : chart.DemoMp3Url;
                Log($"Metadata matched for '{chart.Title}' using key '{key}'.");
                break;
            }
        }
    }

    private async Task<List<DesignerCategory>> GetCollectionsFromJsonFallbackAsync()
    {
        var collections = await GetMetadataIndexAsync();
        return collections
            .Select(x => new DesignerCategory
            {
                Name = x.Name,
                Description = x.Description
            })
            .ToList();
    }

    private async Task<List<DesignerChart>> GetChartsFromJsonFallbackAsync(string categoryName)
    {
        var collections = await GetMetadataIndexAsync();
        return collections
            .FirstOrDefault(x => string.Equals(x.Name, categoryName, StringComparison.OrdinalIgnoreCase))
            ?.Charts
            .Select(CloneAndNormalizeChart)
            .ToList() ?? new List<DesignerChart>();
    }

    private async Task<List<DesignerCategory>> GetMetadataIndexAsync()
    {
        if (_metadataCache != null)
            return _metadataCache;

        // 1. Try Local Folder (for development/overrides)
        try
        {
            var localPath = FindLocalIndexPath();
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                var json = await File.ReadAllTextAsync(localPath);
                _metadataCache = JsonSerializer.Deserialize<List<DesignerCategory>>(json) ?? new List<DesignerCategory>();
                Log($"Loaded {_metadataCache.Count} album folders from local metadata: {localPath}");
                return _metadataCache;
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to load local metadata: {ex.Message}");
        }

        // 2. Try Embedded Resource (Primary source for standalone EXE)
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "MdModManager.SongRepository.designers.json";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();
                _metadataCache = JsonSerializer.Deserialize<List<DesignerCategory>>(json) ?? new List<DesignerCategory>();
                Log($"Loaded {_metadataCache.Count} album folders from embedded resources.");
                return _metadataCache;
            }
        }
        catch (Exception innerEx)
        {
            Log($"Failed to load embedded metadata: {innerEx.Message}");
        }

        // 3. Remote Fallback (As a last resort if user explicitly deleted local and something is wrong with assembly)
        try
        {
            Log("Attempting remote metadata fallback...");
            var remoteJson = await _http.GetStringAsync(RemoteIndexUrl);
            _metadataCache = JsonSerializer.Deserialize<List<DesignerCategory>>(remoteJson) ?? new List<DesignerCategory>();
            Log($"Loaded {_metadataCache.Count} album folders from remote metadata fallback.");
            return _metadataCache;
        }
        catch (Exception ex)
        {
            Log($"All metadata sources failed: {ex.Message}");
            _metadataCache = new List<DesignerCategory>();
        }

        return _metadataCache;
    }

    private static Dictionary<string, DesignerChart> BuildMetadataLookup(IEnumerable<DesignerChart> charts)
    {
        var lookup = new Dictionary<string, DesignerChart>(StringComparer.OrdinalIgnoreCase);

        foreach (var chart in charts)
        {
            foreach (var key in GetChartLookupKeys(chart))
            {
                lookup.TryAdd(key, chart);
            }
        }

        return lookup;
    }

    private static IEnumerable<string> GetChartLookupKeys(DesignerChart chart)
    {
        if (!string.IsNullOrWhiteSpace(chart.Title))
            yield return NormalizeLookupKey(chart.Title);

        if (!string.IsNullOrWhiteSpace(chart.DownloadUrl))
        {
            var normalizedUrl = NormalizeResourceUrl(chart.DownloadUrl);
            if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
            {
                var rawStem = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri.AbsolutePath));
                if (!string.IsNullOrWhiteSpace(rawStem))
                    yield return NormalizeLookupKey(ExtractTitle(rawStem));
            }
        }
    }

    private static string NormalizeLookupKey(string value)
    {
        return value.Trim().Replace('_', ' ');
    }

    private static DesignerChart CloneAndNormalizeChart(DesignerChart chart)
    {
        return new DesignerChart
        {
            Id = chart.Id,
            Title = chart.Title,
            Artist = chart.Artist,
            Author = chart.Author,
            Bpm = chart.Bpm,
            CoverUrl = NormalizeResourceUrl(chart.CoverUrl),
            DownloadUrl = NormalizeResourceUrl(chart.DownloadUrl),
            DemoUrl = NormalizeResourceUrl(chart.DemoUrl),
            DemoMp3Url = NormalizeResourceUrl(chart.DemoMp3Url),
            Difficulties = chart.Difficulties != null ? new List<string>(chart.Difficulties) : null
        };
    }

    private static string NormalizeResourceUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var normalized = url
            .Replace("YourUsername/YourRepo", $"{Owner}/{Repo}", StringComparison.OrdinalIgnoreCase)
            .Replace($"/{Branch}/SongRepository/", $"/{Branch}/", StringComparison.OrdinalIgnoreCase)
            .Replace("/main/SongRepository/", "/main/", StringComparison.OrdinalIgnoreCase)
            .Replace("/master/SongRepository/", "/master/", StringComparison.OrdinalIgnoreCase);

        return normalized;
    }

    private static string? FindLocalIndexPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, "SongRepository", "designers.json"),
            Path.Combine(AppContext.BaseDirectory, "SongRepository", "designers.json")
        };

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 5 && current != null; depth++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "SongRepository", "designers.json"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed class GitHubContentItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }
    }

    /// <summary>
    /// 标准化构造 R2 资源的绝对 URL，自动处理编码与子目录
    /// </summary>
    private static string BuildR2ResourceUrl(string baseHost, string categoryName, string subFolder, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        if (fileName.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return fileName;

        // 对每一段路径进行 URL 编码
        var encodedCategory = Uri.EscapeDataString(categoryName.Trim().Replace("\\", "/").Trim('/'));
        var encodedSubFolder = Uri.EscapeDataString(subFolder.Trim().Trim('/'));
        
        // 文件名可能本身包含子路径 mdm/xxx.mdm，需要拆分处理
        var parts = fileName.Trim().Replace("\\", "/").Trim('/').Split('/');
        var encodedFileName = string.Join("/", parts.Select(Uri.EscapeDataString));

        var downloadDomain = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.AlbumDownloadDomain) 
            ? MirrorDomainRegistry.AlbumDownloadDomain 
            : $"download.{baseHost}";
        return $"https://{downloadDomain}/{encodedCategory}/{encodedSubFolder}/{encodedFileName}";
    }

    private async Task TrySaveCacheIfChangedAsync(string cachePath, string newContent)
    {
        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(cachePath))
            {
                var oldContent = await File.ReadAllTextAsync(cachePath);
                if (oldContent.Trim() == newContent.Trim()) return;
            }

            await File.WriteAllTextAsync(cachePath, newContent);
            Log($"Cache updated: {Path.GetFileName(cachePath)}");
        }
        catch (Exception ex)
        {
            Log($"Failed to save cache to {cachePath}: {ex.Message}");
        }
    }

    private void TryDeleteCollectionCache(string name, string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to delete cache for '{name}': {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        RuntimeLog.Write("AlbumCollectionService", message);
    }
}
