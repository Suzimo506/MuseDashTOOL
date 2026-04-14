using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    Task<List<DesignerCategory>> GetCollectionsAsync();
    Task<List<DesignerChart>> GetChartsAsync(string categoryName);
    Task<List<(DesignerCategory Category, DesignerChart Chart)>> SearchChartsAsync(string query);
    Task<List<(string CategoryName, MdmcChart Chart)>> SearchCommunityChartsAsync(string query);
}

public class AlbumCollectionService : IAlbumCollectionService
{
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
    private List<DesignerCategory>? _metadataCache;
    private readonly Dictionary<string, List<DesignerChart>> _chartsCache = new(StringComparer.OrdinalIgnoreCase);

    // 新整合包仓库缓存
    private NewCollectionIndex? _newCollectionCache;

    // 社区仓库配置
    private static readonly (string Name, string RepoUrl)[] CommunityConfigs = 
    { 
        ("通过审议", "https://download.suzimo.site/1_Csutom-Albums-Repository"), 
        ("令人生草", "https://download.suzimo.site/3_Custom-Albums-Repository"), 
        ("待定或存在小问题", "https://download.suzimo.site/2_Custom-Albums-Repository"),
        ("未经审查", "https://download.suzimo.site/%E6%9C%AA%E7%BB%8F%E5%AE%A1%E6%9F%A5"),
        ("宫守文学曲包", "https://download.suzimo.site/宫守文学曲包")
    };

    private readonly Dictionary<string, List<MdmcChart>> _communityChartsCache = new(StringComparer.OrdinalIgnoreCase);

    public AlbumCollectionService()
    {
        _http.DefaultRequestHeaders.Remove("User-Agent");
        _http.DefaultRequestHeaders.Add("User-Agent", "MuseDashTOOL-AlbumCollection");
    }

    public async Task<List<DesignerCategory>> GetCollectionsAsync()
    {
        if (_categoryCache != null)
            return _categoryCache;

        List<DesignerCategory> categories = new();
        try
        {
            var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";
            var workerUrl = $"https://workerdl.{baseHost}/api/list";
            var json = await _http.GetStringAsync(workerUrl);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json, options);

            if (result != null && result.TryGetValue("collections", out var folders))
            {
                categories = folders
                    .OrderBy(f => f)
                    .Select(f => new DesignerCategory
                    {
                        Name = f,
                        Description = "R2 云端专属整合包"
                    })
                    .ToList();
                Log($"Loaded {categories.Count} collection folders dynamically from Cloudflare R2.");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to get dynamic R2 folder list: {ex}");
        }

        _categoryCache = categories;
        return _categoryCache;
    }

    private async Task<List<DesignerChart>> FetchSpecialR2CategoryAsync(string name)
    {
        var encodedName = Uri.EscapeDataString(name);
        var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";
        // 使用原生的 R2 域名进行文件拉取，动态受控于统一镜像配置
        var url = $"https://download.{baseHost}/{encodedName}/index.json";
        var json = await _http.GetStringAsync(url);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        
        List<CommunityIndexItem>? items = null;
        
        // 尝试格式 1：完整的 Collection_index.json 结构（包含 collections 数组）
        try {
            var newCol = JsonSerializer.Deserialize<NewCollectionIndex>(json, options);
            if (newCol?.Collections != null) {
                // 这个文件里面可能只装了一个包，我们提取第一个，或者匹配名字的
                var match = newCol.Collections.FirstOrDefault();
                if (match != null) items = match.Charts;
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
        foreach(var item in items) {
           var cover = BuildR2ResourceUrl(baseHost, name, "covers", item.CoverUrl);
           var demo = BuildR2ResourceUrl(baseHost, name, "demos", item.DemoUrl);
           var mp3 = BuildR2ResourceUrl(baseHost, name, "demos", item.DemoMp3Url);
           
           var dlUrl = BuildR2ResourceUrl(baseHost, name, "mdm", item.DownloadUrl);

           string cleanTitle = !string.IsNullOrEmpty(item.OriginalId) ? item.OriginalId : (item.Title ?? "");
           cleanTitle = Regex.Replace(cleanTitle, @"^\[(?:Lv|LV|lv)[.\s]?\s*[^\]]+\]\s*", "", RegexOptions.IgnoreCase);

           charts.Add(new DesignerChart {
               Id = item.Id,
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

    public async Task<List<DesignerChart>> GetChartsAsync(string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return new List<DesignerChart>();

        if (_chartsCache.TryGetValue(categoryName, out var cached))
            return cached;

        try {
            var charts = await FetchSpecialR2CategoryAsync(categoryName);
            _chartsCache[categoryName] = charts;
            return charts;
        } catch (Exception ex) {
            Log($"Failed to load R2 category '{categoryName}': {ex.Message}");
            return new List<DesignerChart>();
        }
    }

    public async Task<List<(DesignerCategory Category, DesignerChart Chart)>> SearchChartsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<(DesignerCategory Category, DesignerChart Chart)>();

        var collections = await GetMetadataIndexAsync();
        var results = new List<(DesignerCategory Category, DesignerChart Chart)>();
        var normalizedQuery = query.Trim().ToLowerInvariant();

        foreach (var category in collections)
        {
            foreach (var chart in category.Charts)
            {
                if (chart.Title?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                    chart.Author?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                    chart.Artist?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true)
                {
                    results.Add((category, CloneAndNormalizeChart(chart)));
                }
            }
        }

        // 同时搜索新仓库的整合包
        var newIndex = await GetNewCollectionIndexAsync();
        if (newIndex?.Collections != null)
        {
            foreach (var col in newIndex.Collections)
            {
                var fakeCategory = new DesignerCategory { Name = col.Name };
                foreach (var item in col.Charts)
                {
                    var title = !string.IsNullOrEmpty(item.OriginalId) ? item.OriginalId : item.Title;
                    if (title?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                        item.Charter?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                        item.Artist?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        results.Add((fakeCategory, MapNewRepoItemToDesignerChart(item, col.Name)));
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
                var charts = await GetCommunityChartsAsync(config.Name, config.RepoUrl);
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

    private async Task<List<MdmcChart>> GetCommunityChartsAsync(string name, string repoUrl)
    {
        if (_communityChartsCache.TryGetValue(name, out var cached))
            return cached;

        string json = "";
        string? localPath = FindLocalCommunityIndexPath(name);
        
        try
        {
            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            {
                json = await File.ReadAllTextAsync(localPath);
                Log($"Using local index for community repo '{name}': {localPath}");
            }
            else
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
                string rawBaseUrl;
                string rawIndexUrl;
                string finalUrl;
                
                if (repoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    rawBaseUrl = repoUrl.Replace("github.com", "raw.githubusercontent.com") + "/main";
                    rawIndexUrl = rawBaseUrl + $"/index.json?t={timestamp}";
                    var configService = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<IConfigService>();
                    var source = configService?.Config?.DownloadSource ?? "Auto";
                    finalUrl = GitHubMirrorHelper.ApplyMirror(rawIndexUrl, source);
                }
                else
                {
                    rawBaseUrl = repoUrl;
                    rawIndexUrl = rawBaseUrl + $"/index.json?t={timestamp}";
                    finalUrl = rawIndexUrl;
                }
                
                json = await _http.GetStringAsync(finalUrl);
                Log($"Fetched remote index for community repo '{name}'");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to get index for '{name}': {ex.Message}");
            return new List<MdmcChart>();
        }

        var rawBaseUrlFinal = repoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase) 
            ? repoUrl.Replace("github.com", "raw.githubusercontent.com") + "/main"
            : repoUrl;
        List<CommunityIndexItem>? items = null;
        List<string> defaultReleaseTags = [];

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var wrapper = JsonSerializer.Deserialize<CommunityIndexWrapper>(json, options);
            if (wrapper != null && wrapper.Charts != null)
            {
                defaultReleaseTags = CommunityReleaseHelper.MergeReleaseTags(
                    null,
                    wrapper.ReleaseTag,
                    wrapper.ReleaseTags);
                items = wrapper.Charts;
            }
        }
        catch
        {
            try 
            { 
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                items = JsonSerializer.Deserialize<List<CommunityIndexItem>>(json, options); 
            } 
            catch { }
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
            candidates.Add(Path.Combine(bp, "SongRepository", categoryName, "repo", "index.json"));
            candidates.Add(Path.Combine(bp, "SongRepository", categoryName, "index.json"));
            
            var current = new DirectoryInfo(bp);
            for (var depth = 0; depth < 5 && current != null; depth++, current = current.Parent)
            {
                candidates.Add(Path.Combine(current.FullName, "SongRepository", categoryName, "repo", "index.json"));
                candidates.Add(Path.Combine(current.FullName, "SongRepository", categoryName, "index.json"));
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
        var coverUrl = item.CoverUrl;
        if (!string.IsNullOrEmpty(coverUrl) && !coverUrl.StartsWith("http"))
            coverUrl = rawBaseUrl + "/covers/" + coverUrl;

        var demoUrl = item.DemoUrl;
        if (!string.IsNullOrEmpty(demoUrl) && !demoUrl.StartsWith("http"))
            demoUrl = rawBaseUrl + "/demos/" + demoUrl;

        var demoMp3Url = item.DemoMp3Url;
        if (!string.IsNullOrEmpty(demoMp3Url) && !demoMp3Url.StartsWith("http"))
            demoMp3Url = rawBaseUrl + "/demos/" + demoMp3Url;

        var downloadUrl = item.DownloadUrl;
        if (!githubRepoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";
            var repoName = Path.GetFileName(githubRepoUrl.TrimEnd('/'));
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
                var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";
                var repoName = Path.GetFileName(githubRepoUrl.TrimEnd('/'));
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
            Id = item.Id,
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

    // ── 新整合包仓库数据模型 ──
    private class NewCollectionIndex { [JsonPropertyName("collections")] public List<NewCollectionEntry> Collections { get; set; } = new(); }
    private class NewCollectionEntry { [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("description")] public string Description { get; set; } = ""; [JsonPropertyName("charts")] public List<CommunityIndexItem> Charts { get; set; } = new(); }

    // ── 新整合包仓库：获取索引 ──
    private async Task<NewCollectionIndex?> GetNewCollectionIndexAsync()
    {
        if (_newCollectionCache != null) return _newCollectionCache;

        string? json = null;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        // 1. 本地文件优先
        try
        {
            var localPath = FindLocalNewCollectionIndexPath();
            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            {
                json = await File.ReadAllTextAsync(localPath);
                Log($"Loaded new collection index from local: {localPath}");
            }
        }
        catch (Exception ex) { Log($"Local new collection index read failed: {ex.Message}"); }

        // 2. 远端获取
        if (string.IsNullOrEmpty(json))
        {
            try
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
                var rawUrl = $"{NewRepoCollectionIndexUrl}?t={ts}";
                
                var configService = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<IConfigService>();
                var source = configService?.Config?.DownloadSource ?? "Auto";
                var finalUrl = GitHubMirrorHelper.ApplyMirror(rawUrl, source);

                json = await _http.GetStringAsync(finalUrl);
                Log("Fetched new collection index from remote.");
            }
            catch (Exception ex) { Log($"Remote new collection index fetch failed: {ex.Message}"); }
        }

        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            _newCollectionCache = JsonSerializer.Deserialize<NewCollectionIndex>(json, options);
            Log($"Parsed new collection index: {_newCollectionCache?.Collections.Count ?? 0} collections.");
        }
        catch (Exception ex) { Log($"Failed to parse new collection index: {ex.Message}"); }

        return _newCollectionCache;
    }

    private async Task<List<DesignerCategory>> GetNewRepoCollectionNamesAsync()
    {
        var result = new List<DesignerCategory>();
        var index = await GetNewCollectionIndexAsync();
        if (index?.Collections == null) return result;

        foreach (var col in index.Collections)
        {
            if (!string.IsNullOrWhiteSpace(col.Name))
            {
                result.Add(new DesignerCategory { Name = col.Name, Description = col.Description });
                Log($"New collection discovered: '{col.Name}' with {col.Charts.Count} charts.");
            }
        }
        return result;
    }

    private async Task<List<DesignerChart>?> TryGetNewRepoChartsAsync(string categoryName)
    {
        var index = await GetNewCollectionIndexAsync();
        var col = index?.Collections?.FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));
        if (col == null) return null;

        var charts = col.Charts.Select(item => MapNewRepoItemToDesignerChart(item, col.Name)).ToList();
        Log($"New repo category '{categoryName}' resolved to {charts.Count} charts.");
        return charts;
    }

    private DesignerChart MapNewRepoItemToDesignerChart(CommunityIndexItem item, string collectionName)
    {
        var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";

        // 资源链接统一使用 R2 构造助手
        var coverUrl = BuildR2ResourceUrl(baseHost, collectionName, "covers", item.CoverUrl);
        var demoUrl = BuildR2ResourceUrl(baseHost, collectionName, "demos", item.DemoUrl);
        var demoMp3Url = BuildR2ResourceUrl(baseHost, collectionName, "demos", item.DemoMp3Url);

        // 旧版下载逻辑
        /*
        // 下载：GitHub Release {NewRepoGitHub}/releases/download/NO.1/{download_url}
        var downloadUrl = item.DownloadUrl;
        if (!string.IsNullOrEmpty(downloadUrl) && !downloadUrl.StartsWith("http"))
            downloadUrl = $"{NewRepoGitHub}/releases/download/{NewRepoReleaseTag}/{downloadUrl}";
        */

        // 新版 R2 下载逻辑
        var downloadUrl = BuildR2ResourceUrl(baseHost, collectionName, "mdm", item.DownloadUrl);

        string title = !string.IsNullOrEmpty(item.OriginalId) ? item.OriginalId : (item.Title ?? "");

        return new DesignerChart
        {
            Id = item.Id,
            Title = title.Trim(),
            Artist = item.Artist,
            Author = item.Charter,
            Bpm = item.Bpm,
            CoverUrl = coverUrl,
            DemoUrl = demoUrl,
            DemoMp3Url = demoMp3Url,
            DownloadUrl = downloadUrl,
            Difficulties = item.Difficulties
        };
    }

    private static string? FindLocalNewCollectionIndexPath()
    {
        var candidates = new List<string>();
        var basePaths = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach (var bp in basePaths)
        {
            candidates.Add(Path.Combine(bp, "SongRepository", "Collection_index.json"));
            var current = new DirectoryInfo(bp);
            for (var depth = 0; depth < 5 && current != null; depth++, current = current.Parent)
                candidates.Add(Path.Combine(current.FullName, "SongRepository", "Collection_index.json"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

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
            DemoMp3Url = NormalizeResourceUrl(chart.DemoMp3Url)
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

        return $"https://download.{baseHost}/{encodedCategory}/{encodedSubFolder}/{encodedFileName}";
    }

    private static void Log(string message)
    {
        RuntimeLog.Write("AlbumCollectionService", message);
    }
}
