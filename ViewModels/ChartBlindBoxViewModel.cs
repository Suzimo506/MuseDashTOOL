using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.Helpers;

namespace MdModManager.ViewModels;

public partial class ChartBlindBoxViewModel : ObservableObject, IDisposable
{
    private const int DisplayCount = 10;
    private static readonly SemaphoreSlim _coverSemaphore = new(5);

    private readonly ChartDownloadViewModel _chartDownloadViewModel;
    private readonly IAlbumCollectionService _collectionService;
    private readonly Random _random = new();

    private List<MdmcChart> _allCharts = new();
    private List<MdmcChart> _filteredPool = new();
    private CancellationTokenSource? _loadingCts;

    [ObservableProperty]
    private ObservableCollection<MdmcChart> _charts = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "正在加载谱面池...";

    // 0=全部, 1=高难(>=11), 2=低难(<=8)
    [ObservableProperty]
    private int _selectedFilterIndex;

    partial void OnSelectedFilterIndexChanged(int value)
    {
        RebuildFilteredPool();
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterHigh));
        OnPropertyChanged(nameof(IsFilterLow));
    }

    public bool IsFilterAll => SelectedFilterIndex == 0;
    public bool IsFilterHigh => SelectedFilterIndex == 1;
    public bool IsFilterLow => SelectedFilterIndex == 2;

    public string FilterLabel => SelectedFilterIndex switch
    {
        1 => "高难谱面 (≥11)",
        2 => "低难谱面 (≤8)",
        _ => "全部谱面"
    };

    // 转发试听和下载命令
    public IAsyncRelayCommand<MdmcChart> TogglePreviewCommand => _chartDownloadViewModel.TogglePreviewCommand;
    public IAsyncRelayCommand<MdmcChart> DownloadChartCommand => _chartDownloadViewModel.DownloadChartCommand;
    public bool EnableMarquee => _chartDownloadViewModel.EnableMarquee;

    public ChartBlindBoxViewModel()
    {
        _chartDownloadViewModel = Ioc.Default.GetRequiredService<ChartDownloadViewModel>();
        _collectionService = Ioc.Default.GetRequiredService<IAlbumCollectionService>();
    }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        StatusMessage = "正在加载谱面池...";

        try
        {
            var allCharts = new List<MdmcChart>();

            // 加载所有社区仓库的本地缓存
            foreach (var config in AlbumCollectionService.CommunityConfigs)
            {
                var charts = await _collectionService.GetLocalCommunityChartsAsync(config.Name);
                foreach (var c in charts)
                {
                    c.SourceCategoryName = config.Name;
                    c.IsCommunitySource = true;
                    ChartCoverSourceResolver.ApplyDirectCoverSource(c);
                }
                allCharts.AddRange(charts);
            }

            // 加载所有设计师整合包的本地缓存
            var collections = await _collectionService.GetLocalCollectionsAsync();
            foreach (var col in collections)
            {
                var designerCharts = await _collectionService.GetLocalChartsAsync(col.Name);
                foreach (var dc in designerCharts)
                {
                    var chart = new MdmcChart
                    {
                        Id = dc.Id,
                        Title = dc.Title,
                        Artist = dc.Artist,
                        Charter = dc.Author,
                        Bpm = dc.Bpm,
                        CustomCoverUrl = dc.CoverUrl,
                        CustomDemoUrl = dc.DemoUrl,
                        CustomDemoMp3Url = dc.DemoMp3Url,
                        CustomDownloadUrl = dc.DownloadUrl,
                        SourceCategoryName = col.Name,
                        IsCommunitySource = false,
                        Sheets = ExtractSheets(dc)
                    };
                    ChartCoverSourceResolver.ApplyDirectCoverSource(chart);
                    allCharts.Add(chart);
                }
            }

            _allCharts = allCharts;
            RebuildFilteredPool();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
            RuntimeLog.Write("BlindBoxVM", $"Init failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildFilteredPool()
    {
        _filteredPool = SelectedFilterIndex switch
        {
            1 => _allCharts.Where(c => HasDifficultyInRange(c, 11, int.MaxValue)).ToList(),
            2 => _allCharts.Where(c => HasDifficultyInRange(c, 0, 8)).ToList(),
            _ => new List<MdmcChart>(_allCharts)
        };

        OnPropertyChanged(nameof(FilterLabel));
    }

    // 任意一个难度落在范围内即匹配
    private static bool HasDifficultyInRange(MdmcChart chart, int min, int max)
    {
        if (chart.Sheets == null || chart.Sheets.Count == 0) return false;
        foreach (var sheet in chart.Sheets)
        {
            if (TryParseDifficulty(sheet.Difficulty, out int level))
            {
                if (level >= min && level <= max) return true;
            }
        }
        return false;
    }

    // 提取难度数值
    private static bool TryParseDifficulty(string? diff, out int level)
    {
        level = 0;
        if (string.IsNullOrWhiteSpace(diff)) return false;


        var digits = new string(diff.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (string.IsNullOrEmpty(digits)) return false;


        if (double.TryParse(digits, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            level = (int)Math.Floor(d);
            return true;
        }
        return false;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        _chartDownloadViewModel.StopPlayback();

        // 取消挂起的封面加载任务
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        foreach (var c in Charts)
        {
            ChartCoverSourceResolver.ReleaseChartCache(c);
            c.ReleaseResources();
        }
        Charts.Clear();

        if (_filteredPool.Count == 0)
        {
            StatusMessage = SelectedFilterIndex switch
            {
                1 => "没有找到难度≥11的谱面",
                2 => "没有找到难度≤8的谱面",
                _ => "谱面池为空，请先打开过整合包页面"
            };
            return;
        }

        // 随机抽取不重复的谱面
        var count = Math.Min(DisplayCount, _filteredPool.Count);
        var indices = new HashSet<int>();
        while (indices.Count < count)
            indices.Add(_random.Next(_filteredPool.Count));

        var picks = indices.Select(i => _filteredPool[i]).ToList();
        foreach (var c in picks)
        {
            c.IsAnimatedCoverPlaybackEnabled = false;
            Charts.Add(c);
        }

        StatusMessage = $"从 {_filteredPool.Count} 张谱面中随机抽取了 {count} 张";

        _ = LoadCoversAsync(picks, token);
    }

    private async Task LoadCoversAsync(IEnumerable<MdmcChart> charts, CancellationToken ct)
    {
        var tasks = charts.Select(async chart =>
        {
            if (ct.IsCancellationRequested) return;
            if (chart.HasDisplayCoverSource || string.IsNullOrEmpty(chart.CustomCoverUrl)) return;
            await _coverSemaphore.WaitAsync(ct);
            try
            {
                await ChartCoverSourceResolver.EnsureResolvedAsync(chart);
            }
            catch { }
            finally { _coverSemaphore.Release(); }
        });
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
    }

    private static List<MdmcSheet> ExtractSheets(DesignerChart dc)
    {
        var labels = new List<string>();
        if (dc.Difficulties != null && dc.Difficulties.Count > 0)
        {
            foreach (var d in dc.Difficulties)
            {
                if (!string.IsNullOrEmpty(d))
                {
                    labels.AddRange(d.Split(new char[] { ',', '，', ' ', '/', '、' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                }
            }
        }

        if (labels.Count == 0 && !string.IsNullOrWhiteSpace(dc.DownloadUrl))
        {
            var urlDecoded = System.Net.WebUtility.UrlDecode(dc.DownloadUrl);
            var match = System.Text.RegularExpressions.Regex.Match(
                urlDecoded, @"\[Lv\.(.*?)\]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                labels.AddRange(match.Groups[1].Value
                    .Split(new char[] { ',', '，', ' ', '/', '、' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            }
        }

        if (labels.Count == 0) labels.Add("?");
        return labels.Select(l => new MdmcSheet { Difficulty = l }).ToList();
    }

    public void Cleanup()
    {
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = null;

        _chartDownloadViewModel.StopPlayback();
        foreach (var c in Charts)
        {
            ChartCoverSourceResolver.ReleaseChartCache(c);
            c.ReleaseResources();
        }
        Charts.Clear();
    }

    public void Dispose()
    {
        Cleanup();
    }
}
