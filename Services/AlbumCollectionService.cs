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
}

public class AlbumCollectionService : IAlbumCollectionService
{
    private const string Owner = "KuoKing506";
    private const string Repo = "CustomAlbums_Collection";
    private const string Branch = "main";
    private const string GitHubApiBase = $"https://api.github.com/repos/{Owner}/{Repo}/contents";
    private const string RemoteIndexUrl = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/designers.json";

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

    public AlbumCollectionService()
    {
        _http.DefaultRequestHeaders.Remove("User-Agent");
        _http.DefaultRequestHeaders.Add("User-Agent", "MuseDashTOOL-AlbumCollection");
    }

    public async Task<List<DesignerCategory>> GetCollectionsAsync()
    {
        if (_categoryCache != null)
            return _categoryCache;

        try
        {
            var items = await GetRepoContentsAsync(string.Empty);
            var metadataByName = (await GetMetadataIndexAsync())
                .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

            _categoryCache = items
                .Where(x => string.Equals(x.Type, "dir", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new DesignerCategory
                {
                    Name = x.Name,
                    Description = metadataByName.TryGetValue(x.Name, out var category)
                        ? category.Description
                        : string.Empty
                })
                .ToList();

            Log($"Loaded {_categoryCache.Count} album folders from GitHub.");

            return _categoryCache;
        }
        catch (Exception ex)
        {
            Log($"Failed to crawl GitHub folders: {ex}");
            return await GetCollectionsFromJsonFallbackAsync();
        }
    }

    public async Task<List<DesignerChart>> GetChartsAsync(string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return new List<DesignerChart>();

        if (_chartsCache.TryGetValue(categoryName, out var cached))
            return cached;

        try
        {
            var items = await GetRepoContentsAsync(categoryName);
            var charts = BuildChartsFromFiles(items);
            await EnrichChartsFromMetadataAsync(categoryName, charts);
            Log($"Category '{categoryName}' resolved to {charts.Count} charts from {items.Count} repo items.");
            _chartsCache[categoryName] = charts;
            return charts;
        }
        catch (Exception ex)
        {
            Log($"Failed to crawl charts for '{categoryName}': {ex}");
            var fallback = await GetChartsFromJsonFallbackAsync(categoryName);
            _chartsCache[categoryName] = fallback;
            return fallback;
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
                // 搜索条件：标题、作者或艺术家
                if (chart.Title?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                    chart.Author?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                    chart.Artist?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true)
                {
                    results.Add((category, chart));
                }
            }
        }

        return results;
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

        return GitHubMirrorHelper.NormalizeCanonicalUrl(normalized);
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

    private static void Log(string message)
    {
        RuntimeLog.Write("AlbumCollectionService", message);
    }
}
