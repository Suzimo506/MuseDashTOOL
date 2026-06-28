using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Helpers;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IGlobalChartSearchService
{
    Task<IReadOnlyList<GlobalChartSearchServiceResult>> SearchAsync(string query, CancellationToken ct = default);
    Task<EuterpeBuildZipResponse> BuildEuterpeZipAsync(long cid, CancellationToken ct = default);
}

public sealed class GlobalChartSearchService : IGlobalChartSearchService
{
    private const int MdmcFetchPages = 3;
    private const int MdmcPageSize = 15;
    private const int EuterpeFetchSize = 50;
    private const int EuterpeSearchPages = 2;
    private const long EuterpeOfficialUserUid = 0;
    private const string EuterpeBrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    private static readonly TimeSpan OfficialUserChartsQuickWait = TimeSpan.FromMilliseconds(800);
    private static readonly SemaphoreSlim OfficialUserChartsLock = new(1, 1);
    private static readonly string[] EuterpeSearchSorts = { "recommended" };
    private static List<EuterpeChart>? OfficialUserChartsCache;
    private static Task? OfficialUserChartsWarmupTask;

    private readonly IChartDownloadService _chartDownloadService;
    private readonly IAlbumCollectionService _albumCollectionService;
    private readonly AuthState _authState;
    private readonly HttpClient _euterpeClient;

    public GlobalChartSearchService(
        IChartDownloadService chartDownloadService,
        IAlbumCollectionService albumCollectionService,
        AuthState authState,
        AuthHeaderHandler authHeaderHandler)
    {
        _chartDownloadService = chartDownloadService;
        _albumCollectionService = albumCollectionService;
        _authState = authState;
        _euterpeClient = new HttpClient(authHeaderHandler) { BaseAddress = new Uri("https://euterpe-org.com/api/") };
        // Euterpe 搜索接口只有浏览器 UA 会返回网页同款谱面集合。
        _euterpeClient.DefaultRequestHeaders.UserAgent.ParseAdd(EuterpeBrowserUserAgent);
        StartOfficialUserChartsWarmup();
    }

    public async Task<IReadOnlyList<GlobalChartSearchServiceResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
            return Array.Empty<GlobalChartSearchServiceResult>();

        var mdmcTask = SearchMdmcAsync(trimmedQuery, ct);
        var euterpeTask = SearchEuterpeAsync(trimmedQuery, ct);
        var qqTask = SearchQqGroupAsync(trimmedQuery, ct);

        await Task.WhenAll(mdmcTask, euterpeTask, qqTask);
        return new[]
        {
            await mdmcTask,
            await euterpeTask,
            await qqTask
        };
    }

    public async Task<EuterpeBuildZipResponse> BuildEuterpeZipAsync(long cid, CancellationToken ct = default)
    {
        using var buildReq = new HttpRequestMessage(HttpMethod.Post, $"workspace/charts/{cid}/build-zip");
        using var response = await _euterpeClient.SendAsync(buildReq, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize(json, GlobalChartSearchJsonContext.Default.EuterpeBuildZipResponse);
        if (result == null || string.IsNullOrWhiteSpace(result.Path))
            throw new InvalidOperationException("未找到可用的谱面下载版本");

        return result;
    }

    private async Task<GlobalChartSearchServiceResult> SearchMdmcAsync(string query, CancellationToken ct)
    {
        const string sourceName = "MDMC";
        try
        {
            var all = new List<MdmcChart>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var page = 1; page <= MdmcFetchPages; page++)
            {
                var (charts, totalPages) = await _chartDownloadService.FetchChartsAsync(
                    page, "likes", "desc", query, false, ct);

                foreach (var chart in charts)
                {
                    if (string.IsNullOrWhiteSpace(chart.Id) || seenIds.Add(chart.Id))
                        all.Add(chart);
                }

                if (page >= totalPages || charts.Count < MdmcPageSize)
                    break;
            }

            var results = all
                .Select(chart => new GlobalChartSearchResult
                {
                    Source = GlobalChartSource.Mdmc,
                    SourceName = sourceName,
                    MdmcChart = chart
                })
                .ToList();

            return BuildResult(GlobalChartSource.Mdmc, sourceName, results);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("GlobalChartSearchService", $"MDMC search failed: {ex}");
            return new GlobalChartSearchServiceResult(
                GlobalChartSource.Mdmc,
                sourceName,
                Array.Empty<GlobalChartSearchResult>(),
                GlobalChartSourceStatus.Error,
                "MDMC 搜索失败：" + ex.Message);
        }
    }

    private async Task<GlobalChartSearchServiceResult> SearchEuterpeAsync(string query, CancellationToken ct)
    {
        const string sourceName = "Euterpe";
        if (_authState.CurrentUser == null)
        {
            return new GlobalChartSearchServiceResult(
                GlobalChartSource.Euterpe,
                sourceName,
                Array.Empty<GlobalChartSearchResult>(),
                GlobalChartSourceStatus.Warning,
                "Euterpe 未登录，无法搜索和下载谱面。");
        }

        try
        {
            var (items, officialIndexPending) = await SearchMergedEuterpeChartsAsync(query, ct);

            var results = items
                .Select(chart => new GlobalChartSearchResult
                {
                    Source = GlobalChartSource.Euterpe,
                    SourceName = sourceName,
                    EuterpeChart = chart
                })
                .ToList();

            if (officialIndexPending)
            {
                return new GlobalChartSearchServiceResult(
                    GlobalChartSource.Euterpe,
                    sourceName,
                    results,
                    GlobalChartSourceStatus.Warning,
                    results.Count == 0
                        ? "Euterpe 官方账号谱面索引仍在加载，稍后重试可搜索更多收录谱面。"
                        : $"找到 {results.Count} 张谱面，Euterpe 官方账号谱面索引仍在加载。");
            }

            return BuildResult(GlobalChartSource.Euterpe, sourceName, results);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new GlobalChartSearchServiceResult(
                GlobalChartSource.Euterpe,
                sourceName,
                Array.Empty<GlobalChartSearchResult>(),
                GlobalChartSourceStatus.Warning,
                "Euterpe 登录已失效，请重新登录后再搜索。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("GlobalChartSearchService", $"Euterpe search failed: {ex}");
            return new GlobalChartSearchServiceResult(
                GlobalChartSource.Euterpe,
                sourceName,
                Array.Empty<GlobalChartSearchResult>(),
                GlobalChartSourceStatus.Error,
                "Euterpe 搜索失败：" + ex.Message);
        }
    }

    private async Task<(List<EuterpeChart> Charts, bool OfficialIndexPending)> SearchMergedEuterpeChartsAsync(string query, CancellationToken ct)
    {
        StartOfficialUserChartsWarmup();

        var publicCharts = await SearchPublicEuterpeChartsAsync(query, ct);
        if (OfficialUserChartsCache == null && OfficialUserChartsWarmupTask != null)
        {
            var completedTask = await Task.WhenAny(
                OfficialUserChartsWarmupTask,
                Task.Delay(OfficialUserChartsQuickWait, ct));
            ct.ThrowIfCancellationRequested();
            if (completedTask == OfficialUserChartsWarmupTask)
                await OfficialUserChartsWarmupTask;
        }

        var merged = new Dictionary<long, EuterpeChart>();
        foreach (var chart in publicCharts)
        {
            merged.TryAdd(chart.Cid, chart);
        }

        var officialCharts = OfficialUserChartsCache;
        if (officialCharts == null)
            return (merged.Values.ToList(), true);

        foreach (var chart in officialCharts.Where(chart => IsEuterpeChartMatch(chart, query)))
        {
            merged.TryAdd(chart.Cid, chart);
        }

        return (merged.Values.ToList(), false);
    }

    private async Task<List<EuterpeChart>> SearchPublicEuterpeChartsAsync(string query, CancellationToken ct)
    {
        var merged = new Dictionary<long, EuterpeChart>();
        foreach (var sort in EuterpeSearchSorts)
        {
            var charts = await SearchPublicEuterpeChartsBySortAsync(query, sort, ct);
            foreach (var chart in charts)
            {
                merged.TryAdd(chart.Cid, chart);
            }
        }

        return merged.Values.ToList();
    }

    private async Task<List<EuterpeChart>> SearchPublicEuterpeChartsBySortAsync(string query, string sort, CancellationToken ct)
    {
        var allCharts = new List<EuterpeChart>();
        string? cursor = null;
        var page = 0;

        do
        {
            page++;
            var path = $"charts/search?size={EuterpeFetchSize}&sort={Uri.EscapeDataString(sort)}&q={Uri.EscapeDataString(query)}";
            if (!string.IsNullOrEmpty(cursor))
            {
                path += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await _euterpeClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize(json, GlobalChartSearchJsonContext.Default.EuterpeSearchResponse);
            if (result == null)
                break;

            allCharts.AddRange(result.Items);
            cursor = result.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor) && page < EuterpeSearchPages);

        return allCharts;
    }

    private void StartOfficialUserChartsWarmup()
    {
        if (_authState.CurrentUser == null)
            return;

        if (OfficialUserChartsCache != null)
            return;

        if (OfficialUserChartsWarmupTask is { IsCompleted: false })
            return;

        OfficialUserChartsWarmupTask = Task.Run(async () =>
        {
            try
            {
                await LoadOfficialUserChartsAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("GlobalChartSearchService", $"Euterpe official user chart cache failed: {ex}");
            }
        });
    }

    private async Task<List<EuterpeChart>> LoadOfficialUserChartsAsync(CancellationToken ct)
    {
        if (OfficialUserChartsCache != null)
            return OfficialUserChartsCache;

        await OfficialUserChartsLock.WaitAsync(ct);
        try
        {
            if (OfficialUserChartsCache != null)
                return OfficialUserChartsCache;

            var charts = new List<EuterpeChart>();
            string? cursor = null;

            do
            {
                var path = $"users/{EuterpeOfficialUserUid}/charts?size={EuterpeFetchSize}";
                if (!string.IsNullOrEmpty(cursor))
                {
                    path += $"&cursor={Uri.EscapeDataString(cursor)}";
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                using var response = await _euterpeClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize(json, GlobalChartSearchJsonContext.Default.EuterpeSearchResponse);
                if (result == null)
                    break;

                charts.AddRange(result.Items);
                cursor = result.NextCursor;
            }
            while (!string.IsNullOrEmpty(cursor));

            OfficialUserChartsCache = charts;
            return OfficialUserChartsCache;
        }
        finally
        {
            OfficialUserChartsLock.Release();
        }
    }

    private static bool IsEuterpeChartMatch(EuterpeChart chart, string query)
    {
        return SearchHelper.IsMatch(chart.Name, query, true) ||
               SearchHelper.IsMatch(chart.Author, query, true) ||
               SearchHelper.IsMatch(chart.OwnerNickname, query, true) ||
               SearchHelper.IsMatch(chart.CharterInfo, query, true);
    }

    private async Task<GlobalChartSearchServiceResult> SearchQqGroupAsync(string query, CancellationToken ct)
    {
        const string sourceName = "QQ群谱面";
        try
        {
            var missingIndexes = AlbumCollectionService.CommunityConfigs
                .Where(config => !_albumCollectionService.HasLocalCommunityIndex(config.Name))
                .Select(config => config.Name)
                .ToList();

            var collectionResults = await _albumCollectionService.SearchChartsAsync(query);
            var communityResults = await _albumCollectionService.SearchCommunityChartsAsync(query);
            ct.ThrowIfCancellationRequested();

            var collectionChartResults = collectionResults
                .Select(item => new GlobalChartSearchResult
                {
                    Source = GlobalChartSource.QQGroup,
                    SourceName = sourceName,
                    SourceDetail = item.Category.Name,
                    MdmcChart = MapDesignerChart(item.Category, item.Chart)
                });

            var communityChartResults = communityResults
                .Select(item => new GlobalChartSearchResult
                {
                    Source = GlobalChartSource.QQGroup,
                    SourceName = sourceName,
                    SourceDetail = item.CategoryName,
                    MdmcChart = item.Chart
                });

            var results = collectionChartResults
                .Concat(communityChartResults)
                .ToList();

            if (missingIndexes.Count == AlbumCollectionService.CommunityConfigs.Count)
            {
                return new GlobalChartSearchServiceResult(
                    GlobalChartSource.QQGroup,
                    sourceName,
                    results,
                    GlobalChartSourceStatus.Warning,
                    results.Count == 0
                        ? "QQ群谱面索引 JSON 尚未下载。请先进入“QQ群谱面”页面同步索引。"
                        : "群友自制谱面索引 JSON 尚未下载，已返回曲包与谱师个人仓库结果。");
            }

            if (missingIndexes.Count > 0)
            {
                return new GlobalChartSearchServiceResult(
                    GlobalChartSource.QQGroup,
                    sourceName,
                    results,
                    GlobalChartSourceStatus.Warning,
                    $"部分 QQ 群谱面索引未下载：{string.Join("、", missingIndexes)}");
            }

            return BuildResult(GlobalChartSource.QQGroup, sourceName, results);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("GlobalChartSearchService", $"QQ group search failed: {ex}");
            return new GlobalChartSearchServiceResult(
                GlobalChartSource.QQGroup,
                sourceName,
                Array.Empty<GlobalChartSearchResult>(),
                GlobalChartSourceStatus.Error,
                "QQ群谱面搜索失败：" + ex.Message);
        }
    }

    private static GlobalChartSearchServiceResult BuildResult(
        GlobalChartSource source,
        string sourceName,
        IReadOnlyList<GlobalChartSearchResult> results)
    {
        var message = results.Count == 0 ? "没有找到匹配谱面" : $"找到 {results.Count} 张谱面";
        return new GlobalChartSearchServiceResult(source, sourceName, results, GlobalChartSourceStatus.Ready, message);
    }

    private static MdmcChart MapDesignerChart(DesignerCategory category, DesignerChart chart)
    {
        return new MdmcChart
        {
            Id = chart.Id,
            Title = chart.Title,
            Artist = chart.Artist,
            Charter = chart.Author,
            Bpm = chart.Bpm,
            CustomCoverUrl = chart.CoverUrl,
            ResolvedCoverSource = chart.CoverUrl,
            CustomDemoUrl = chart.DemoUrl,
            CustomDemoMp3Url = chart.DemoMp3Url,
            CustomDownloadUrl = chart.DownloadUrl,
            SourceCategoryName = category.Name,
            IsCommunitySource = false,
            Sheets = ExtractDifficultySheets(chart)
        };
    }

    private static List<MdmcSheet> ExtractDifficultySheets(DesignerChart chart)
    {
        var sheets = new List<MdmcSheet>();
        if (chart.Difficulties == null)
            return sheets;

        foreach (var difficulty in chart.Difficulties)
        {
            if (string.IsNullOrWhiteSpace(difficulty))
                continue;

            var parts = difficulty.Split(
                new[] { ',', '，', ' ', '/', '、' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var part in parts)
                sheets.Add(new MdmcSheet { Difficulty = part });
        }

        return sheets;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EuterpeSearchResponse))]
[JsonSerializable(typeof(EuterpeChart))]
[JsonSerializable(typeof(List<EuterpeChart>))]
[JsonSerializable(typeof(MapSlotInfo))]
[JsonSerializable(typeof(List<MapSlotInfo>))]
[JsonSerializable(typeof(EuterpeBuildZipResponse))]
internal partial class GlobalChartSearchJsonContext : JsonSerializerContext;
