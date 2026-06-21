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
    private const int EuterpeFetchSize = 45;

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
        _euterpeClient.DefaultRequestHeaders.UserAgent.ParseAdd("MuseDashTOOL/1.5.1");
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
            var path = $"charts/search?size={EuterpeFetchSize}&sort=recommended&q={Uri.EscapeDataString(query)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await _euterpeClient.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new GlobalChartSearchServiceResult(
                    GlobalChartSource.Euterpe,
                    sourceName,
                    Array.Empty<GlobalChartSearchResult>(),
                    GlobalChartSourceStatus.Warning,
                    "Euterpe 登录已失效，请重新登录后再搜索。");
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize(json, GlobalChartSearchJsonContext.Default.EuterpeSearchResponse);
            var items = result?.Items ?? new List<EuterpeChart>();

            var results = items
                .Select(chart => new GlobalChartSearchResult
                {
                    Source = GlobalChartSource.Euterpe,
                    SourceName = sourceName,
                    EuterpeChart = chart
                })
                .ToList();

            return BuildResult(GlobalChartSource.Euterpe, sourceName, results);
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

    private async Task<GlobalChartSearchServiceResult> SearchQqGroupAsync(string query, CancellationToken ct)
    {
        const string sourceName = "QQ群谱面";
        try
        {
            var missingIndexes = AlbumCollectionService.CommunityConfigs
                .Where(config => !_albumCollectionService.HasLocalCommunityIndex(config.Name))
                .Select(config => config.Name)
                .ToList();

            if (missingIndexes.Count == AlbumCollectionService.CommunityConfigs.Count)
            {
                return new GlobalChartSearchServiceResult(
                    GlobalChartSource.QQGroup,
                    sourceName,
                    Array.Empty<GlobalChartSearchResult>(),
                    GlobalChartSourceStatus.Warning,
                    "QQ群谱面索引 JSON 尚未下载。请先进入“QQ群谱面”页面同步索引。");
            }

            var communityResults = await _albumCollectionService.SearchCommunityChartsAsync(query);
            ct.ThrowIfCancellationRequested();

            var results = communityResults
                .Select(item => new GlobalChartSearchResult
                {
                    Source = GlobalChartSource.QQGroup,
                    SourceName = sourceName,
                    SourceDetail = item.CategoryName,
                    MdmcChart = item.Chart
                })
                .ToList();

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
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EuterpeSearchResponse))]
[JsonSerializable(typeof(EuterpeChart))]
[JsonSerializable(typeof(List<EuterpeChart>))]
[JsonSerializable(typeof(MapSlotInfo))]
[JsonSerializable(typeof(List<MapSlotInfo>))]
[JsonSerializable(typeof(EuterpeBuildZipResponse))]
internal partial class GlobalChartSearchJsonContext : JsonSerializerContext;
