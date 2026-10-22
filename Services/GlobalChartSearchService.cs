using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Helpers;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IGlobalChartSearchService
{
    Task<IReadOnlyList<GlobalChartSearchServiceResult>> SearchAsync(string query, CancellationToken ct = default);
}

public sealed class GlobalChartSearchService : IGlobalChartSearchService
{
    private const int MdmcFetchPages = 3;
    private const int MdmcPageSize = 15;
    private readonly IChartDownloadService _chartDownloadService;
    private readonly IAlbumCollectionService _albumCollectionService;

    public GlobalChartSearchService(
        IChartDownloadService chartDownloadService,
        IAlbumCollectionService albumCollectionService)
    {
        _chartDownloadService = chartDownloadService;
        _albumCollectionService = albumCollectionService;
    }

    public async Task<IReadOnlyList<GlobalChartSearchServiceResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
            return Array.Empty<GlobalChartSearchServiceResult>();

        var mdmcTask = SearchMdmcAsync(trimmedQuery, ct);
        var qqTask = SearchQqGroupAsync(trimmedQuery, ct);

        await Task.WhenAll(mdmcTask, qqTask);
        return new[]
        {
            await mdmcTask,
            await qqTask
        };
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
