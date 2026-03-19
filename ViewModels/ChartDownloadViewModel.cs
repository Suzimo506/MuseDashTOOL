using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.Views;
using NAudio.Vorbis;
using NAudio.Wave;

namespace MdModManager.ViewModels;

public partial class ChartDownloadViewModel : ObservableObject, IDisposable
{
    private readonly IChartDownloadService _downloadService;
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    private readonly IDownloadManagerService _downloadManagerService;
    private static readonly HttpClient _coverHttp = new() 
    { 
        Timeout = TimeSpan.FromSeconds(15) 
    };

    static ChartDownloadViewModel()
    {
        _coverHttp.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    // ── Charts list ───────────────────────────────────────────────────────────
    public ObservableCollection<MdmcChart> Charts { get; } = new();

    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private bool _isLoadingMore = false;
    [ObservableProperty] private string _statusMessage = "正在初始化…";
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int _todayUpdatesCount = 0;

    // ── Search ────────────────────────────────────────────────────────────────
    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                CurrentPage = 1; // 搜索时重置回第一页
                if (string.IsNullOrWhiteSpace(value))
                {
                    _searchCts?.Cancel();
                    _ = ReloadAsync(false);
                }
                else
                {
                    _ = ReloadDebouncedAsync();
                }
            }
        }
    }

    /// <summary>是否启用谱面名称滚动</summary>
    public bool EnableMarquee => _configService.Config.EnableChartNameMarquee;

    private CancellationTokenSource? _searchCts;
    private async Task ReloadDebouncedAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        try
        {
            await Task.Delay(500, ct); // 500ms 延迟，避免输入过快频繁请求
            if (!ct.IsCancellationRequested)
            {
                await ReloadAsync(false, ct);
            }
        }
        catch (TaskCanceledException) { /* ignored */ }
    }

    // ── Sort ──────────────────────────────────────────────────────────────────
    /// <summary>排序方式列表，显示中文，Value 是 API 参数</summary>
    public (string Label, string Value)[] SortOptions { get; } = new[]
    {
        ("点赞数", "likes"),
        ("最新上传", "latest"),
        ("难度", "difficulty"),
    };

    private int _selectedSortIndex = 0;
    public int SelectedSortIndex
    {
        get => _selectedSortIndex;
        set
        {
            if (SetProperty(ref _selectedSortIndex, value))
            {
                OnPropertyChanged(nameof(IsSortByLikes));
                OnPropertyChanged(nameof(IsSortByLatest));
                OnPropertyChanged(nameof(IsSortByDifficulty));
                IsAscending = false; // 切换分类时强制重置为“从高到低”
                CurrentPage = 1;
                _ = RefreshCommand.ExecuteAsync(null);
            }
        }
    }

    public bool IsSortByLikes => SelectedSortIndex == 0;
    public bool IsSortByLatest => SelectedSortIndex == 1;
    public bool IsSortByDifficulty => SelectedSortIndex == 2;

    [ObservableProperty] private bool _isAscending = false;   // 默认从高到低 (desc)
    [ObservableProperty] private bool _showUnranked = true;   // 显示未评级

    // ── Pagination ────────────────────────────────────────────────────────────
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalPages = 1;
    private int _currentLoadId = 0;
    
    private double _currentScrollY = 0;
    private const int CleanupThreshold = 60; // 滚动过远清理阈值
    private const int ItemHeightApprox = 260; // 单行大概高度 (包含边距)
    private const int Columns = 4;
    private const int PageSize = 20;
    private const int RowsPerPage = PageSize / Columns; // 5

    public bool CanLoadMore => CurrentPage < TotalPages && !IsLoading && !IsLoadingMore;

    // ── Audio playback ────────────────────────────────────────────────────────
    private WaveOutEvent? _waveOut;
    private MdmcChart? _playingChart;
    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _loadCts;  // 用于取消正在下载的音频
    private CancellationTokenSource? _listCts;  // 用于取消正在搜索/加载的列表

    // ─────────────────────────────────────────────────────────────────────────
    public ChartDownloadViewModel(
        IChartDownloadService downloadService,
        IConfigService configService,
        INotificationService notificationService,
        IDownloadManagerService downloadManagerService)
    {
        _downloadService = downloadService;
        _configService = configService;
        _notificationService = notificationService;
        _downloadManagerService = downloadManagerService;
    }

    // 缓存第一页的各排序结果: Key 为 sort 参数 (likes, latest, difficulty)
    private readonly System.Collections.Generic.Dictionary<string, (IList<MdmcChart> charts, int totalPages)> _firstPageCache = new();

    public async Task PreloadAllSortsAsync()
    {
        foreach (var opt in SortOptions)
        {
            if (_firstPageCache.ContainsKey(opt.Value)) continue;
            var result = await _downloadService.FetchChartsAsync(1, opt.Value, "desc", "", !ShowUnranked);
            if (result.charts.Count > 0)
            {
                _firstPageCache[opt.Value] = result;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        CurrentPage = 1;
        _currentScrollY = 0;
        _ = UpdateTodayUpdatesCountAsync(); // 异步更新今日数量
        await ReloadAsync(false, ct);
    }

    private async Task UpdateTodayUpdatesCountAsync()
    {
        try
        {
            // 获取最新上传的第一页来计算今日更新
            var (charts, _) = await _downloadService.FetchChartsAsync(1, "latest", "desc", "", false);
            var today = DateTime.UtcNow.Date;
            int count = 0;
            foreach (var c in charts)
            {
                if (c.UploadedAt?.ToUniversalTime().Date == today)
                    count++;
                else if (c.UploadedAt?.ToUniversalTime().Date < today)
                    break; // 说明之后都不是今天的了
            }
            TodayUpdatesCount = count;
            UpdateStatusMessage();
        }
        catch { /* ignore */ }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task Refresh() 
    {
        CurrentPage = 1;
        _currentScrollY = 0;
        _ = UpdateTodayUpdatesCountAsync();
        await ReloadAsync(false);
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    private async Task OpenAlbumCollectionAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            await mainVm.NavigateToAlbumCollectionCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private void ToggleSortOrder()
    {
        IsAscending = !IsAscending;
        CurrentPage = 1;
        _ = ReloadAsync(false);
    }

    [RelayCommand]
    private async Task LoadNextPage()
    {
        if (CanLoadMore)
        {
            CurrentPage++;
            await ReloadAsync(true);
        }
    }

    /// <summary>切换试听状态</summary>
    [RelayCommand]
    private async Task TogglePreview(MdmcChart chart)
    {
        // 如果点击的就是正在播放的那一首，停止播放
        if (_playingChart == chart)
        {
            StopPlayback();
            return;
        }

        // StopPlayback() 会通过 _loadCts.Cancel() 取消正在进行的下载请求
        // 不需要 _isLoadingPreview 保护，否则在缓冲过程中点击其他歌会被静默丢弃
        StopPlayback();
        await PlayDemoAsync(chart);
    }

    private bool IsDotNet6Installed()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) return false;
        var net6Path = Path.Combine(gamePath, "MelonLoader", "net6", "MelonLoader.dll");
        return File.Exists(net6Path);
    }

    /// <summary>加入下载队列</summary>
    [RelayCommand]
    private async Task DownloadChartAsync(MdmcChart chart)
    {
        if (!IsDotNet6Installed())
        {
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow as MainWindow : null;
            if (mainWindow != null)
            {
                await mainWindow.ShowMessageBoxAsync("请先在mod列表顶部下载.net6运行环境！");
                return;
            }
        }

        _downloadManagerService.EnqueueDownload(chart);
        _notificationService.ShowSuccess($"已添加到下载列表: 《{chart.Title}》");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task ReloadAsync(bool append = false, CancellationToken externalCt = default)
    {
        if (!append)
        {
            _listCts?.Cancel();
            _listCts = new CancellationTokenSource();
            StopPlayback();
        }

        // 这里的 ct 综合了内部 _listCts 和外部传入的 ct
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _listCts?.Token ?? CancellationToken.None, externalCt);
        var ct = linkedCts.Token;

        if (append) IsLoadingMore = true;
        else IsLoading = true;

        StatusMessage = append ? "正在加载更多…" : "正在加载谱面列表…";
        
        int myId = Interlocked.Increment(ref _currentLoadId);

        try
        {
            var sort  = SortOptions[SelectedSortIndex].Value;
            var order = IsAscending ? "asc" : "desc";
            var query = SearchText.Trim();
            RuntimeLog.Write("ChartDownloadVM", $"Reload start: append={append}, sort={sort}, order={order}, page={CurrentPage}, query='{query}', rankedOnly={!ShowUnranked}, loadId={myId}");

            IList<MdmcChart> charts;
            int totalPages;

            // 只有在第 1 页、降序且没有搜索词时，尝试使用缓存
            if (!append && CurrentPage == 1 && !IsAscending && string.IsNullOrEmpty(query) && _firstPageCache.TryGetValue(sort, out var cached))
            {
                charts = cached.charts;
                totalPages = cached.totalPages;
            }
            else
            {
                var result = await _downloadService.FetchChartsAsync(
                    CurrentPage, sort, order, query, !ShowUnranked, ct);
                charts = result.charts;
                totalPages = result.totalPages;
                if (!append && charts.Count == 0 && string.IsNullOrWhiteSpace(query))
                {
                    var recovered = await TryRecoverEmptyResultAsync(sort, order, ct);
                    if (recovered != null)
                    {
                        charts = recovered.Value.charts;
                        totalPages = recovered.Value.totalPages;
                        result = recovered.Value;
                    }
                }
                
                // 如果是第一页的正向请求结果，存入缓存以供后续秒开
                if (!append && CurrentPage == 1 && !IsAscending && string.IsNullOrEmpty(query))
                {
                    _firstPageCache[sort] = result;
                }
            }

            if (myId != _currentLoadId) return; // 已有更新的请求

            TotalPages = Math.Max(1, totalPages);
            OnPropertyChanged(nameof(CanLoadMore));

            if (!append)
            {
                Charts.Clear();
            }

            foreach (var c in charts)
            {
                Charts.Add(c);
            }

            IsEmpty = Charts.Count == 0;
            UpdateStatusMessage();
            RuntimeLog.Write("ChartDownloadVM", $"Reload end: sort={sort}, page={CurrentPage}, visible={Charts.Count}, fetched={charts.Count}, totalPages={TotalPages}, isEmpty={IsEmpty}, loadId={myId}");
        }
        catch (OperationCanceledException)
        {
            // Ignore canceled reloads. A newer request is usually already in flight.
            RuntimeLog.Write("ChartDownloadVM", $"Reload canceled: page={CurrentPage}, loadId={myId}");
        }
        catch (Exception ex)
        {
            if (myId == _currentLoadId)
                StatusMessage = "加载失败：" + ex.Message;
        }
        finally
        {
            if (myId == _currentLoadId)
            {
                IsLoading = false;
                IsLoadingMore = false;
            }
            // Start lazy loading covers after list is updated
            _ = LoadCoversAsync();
        }
    }

    /// <summary>由 View 调用，更新当前滚动位置并触发内存管理</summary>
    private async Task<(IList<MdmcChart> charts, int totalPages)?> TryRecoverEmptyResultAsync(string sort, string order, CancellationToken ct)
    {
        if (CurrentPage > 1)
        {
            RuntimeLog.Write("ChartDownloadVM", $"Empty result on page {CurrentPage} for sort={sort}, reset to page 1.");
            CurrentPage = 1;
        }

        if (CurrentPage == 1 && !IsAscending && _firstPageCache.TryGetValue(sort, out var cached) && cached.charts.Count > 0)
        {
            RuntimeLog.Write("ChartDownloadVM", $"Recovered empty result from cache for sort={sort}, count={cached.charts.Count}.");
            return cached;
        }

        var retry = await _downloadService.FetchChartsAsync(1, sort, order, string.Empty, !ShowUnranked, ct);
        if (retry.charts.Count > 0)
        {
            RuntimeLog.Write("ChartDownloadVM", $"Recovered empty result by retry for sort={sort}, count={retry.charts.Count}.");
            if (!IsAscending)
                _firstPageCache[sort] = retry;
            return retry;
        }

        RuntimeLog.Write("ChartDownloadVM", $"Retry still empty for sort={sort}.");
        return null;
    }

    public void UpdateScrollPosition(double yOffset)
    {
        _currentScrollY = yOffset;
        CheckMemoryCleanup();
        UpdateStatusMessage();
        
        _ = LoadCoversAsync(); // 恢复为直接触发加载，不再防抖
    }

    private void UpdateStatusMessage()
    {
        if (IsLoading)
        {
            StatusMessage = "正在加载谱面列表…";
            return;
        }

        if (IsEmpty)
        {
            StatusMessage = "没有找到符合条件的谱面";
            return;
        }

        // 根据滚动位置计算当前所处的页码
        int visibleRow = (int)(_currentScrollY / ItemHeightApprox);
        int currentPage = (visibleRow / RowsPerPage) + 1;
        // 限制在已加载的页数范围内
        currentPage = Math.Clamp(currentPage, 1, CurrentPage);

        StatusMessage = $"第 {currentPage} / {TotalPages} 页，今日更新 {TodayUpdatesCount} 张谱面";
    }

    private void CheckMemoryCleanup()
    {
        // 计算当前可见的第一行索引
        int firstVisibleIndex = (int)(_currentScrollY / ItemHeightApprox) * Columns;
        
        // 往下滚：清理上方过远的封面 (超过阈值 60 个)
        for (int i = 0; i < firstVisibleIndex - CleanupThreshold; i++)
        {
            if (i >= 0 && i < Charts.Count && Charts[i].CoverImage != null)
            {
                Charts[i].CoverImage = null;
            }
        }

        // 往上滚：清理下方过远的封面 (比如距离当前视图下方 100 个以外，可选，主要保上方)
        // 这里暂时只处理上方的释放，以满足用户“滚动超过60个释放上方”的要求
    }

    /// <summary>异步逐一加载封面图 (恢复为简单的顺序加载)</summary>
    private async Task LoadCoversAsync()
    {
        // 计算当前窗口及缓冲区内的索引范围
        int firstVisibleRow = (int)(_currentScrollY / ItemHeightApprox);
        int startIndex = Math.Max(0, (firstVisibleRow - 1) * Columns);
        int endIndex = Math.Min(Charts.Count, (firstVisibleRow + 5) * Columns);

        for (int i = startIndex; i < endIndex; i++)
        {
            var chart = Charts[i];
            if (chart.CoverImage != null) continue;

            try
            {
                var bytes = await _coverHttp.GetByteArrayAsync(chart.CoverUrl);
                using var ms = new MemoryStream(bytes);
                var bmp = new Bitmap(ms);
                
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    chart.CoverImage = bmp;
                });
            }
            catch { /* cover unavailable */ }
        }
    }

    private async Task PlayDemoAsync(MdmcChart chart)
    {
        // 取消之前的加载请求
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
            // 默认优先尝试 .ogg
            string url = !string.IsNullOrWhiteSpace(chart.DemoUrl) ? chart.DemoUrl : chart.DemoMp3Url;
            string ext = Path.GetExtension(url ?? string.Empty);
            RuntimeLog.Write("ChartDownloadVM", $"Preview start: title='{chart.Title}', demo='{chart.DemoUrl}', mp3='{chart.DemoMp3Url}', chosen='{url}', ext='{ext}'");

            // 先探测文件状态
            var response = await _coverHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            RuntimeLog.Write("ChartDownloadVM", $"Preview response for '{chart.Title}': {(int)response.StatusCode} {response.StatusCode}");
            
            // 如果 .ogg 不存在 (404) 或失效，尝试回退到 .mp3
            if (!response.IsSuccessStatusCode && !ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(chart.DemoMp3Url))
            {
                url = chart.DemoMp3Url;
                ext = Path.GetExtension(url ?? string.Empty);
                response.Dispose();
                RuntimeLog.Write("ChartDownloadVM", $"Preview fallback to mp3 for '{chart.Title}': '{url}'");
                response = await _coverHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                RuntimeLog.Write("ChartDownloadVM", $"Preview fallback response for '{chart.Title}': {(int)response.StatusCode} {response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                // 如果回退后依然失效，给出提示
                Console.WriteLine($"[ChartDownloadVM] No demo available for {chart.Title}: HTTP {(int)response.StatusCode}");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    StatusMessage = $"《{chart.Title}》暂无试听文件");
                response.Dispose();
                return;
            }

            // 如果加载期间用户已取消或切换到其他谱面，无需继续播放
            if (ct.IsCancellationRequested) 
            {
                response.Dispose();
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            response.Dispose();
            RuntimeLog.Write("ChartDownloadVM", $"Preview download complete for '{chart.Title}', bytes={bytes.Length}");

            if (ct.IsCancellationRequested) return;

            var ms = new MemoryStream(bytes);
            IWaveProvider waveProvider = CreateWaveProvider(ms, ext);

            _waveOut = new WaveOutEvent();
            _waveOut.Init(waveProvider);
            _waveOut.Volume = (float)_configService.Config.ChartPreviewVolume;

            _stopCts = new CancellationTokenSource();
            var cts = _stopCts;
            var provider = waveProvider;

            _waveOut.PlaybackStopped += (_, _) =>
            {
                if (!cts.IsCancellationRequested)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (_playingChart == chart) _playingChart = null;
                        chart.IsPlaying = false;
                    });
                if (provider is IDisposable d) d.Dispose();
                ms.Dispose();
            };

            _waveOut.Play();
            _playingChart = chart;
            chart.IsPlaying = true;
        }
        catch (OperationCanceledException) { /* 用户取消，忽略 */ }
        catch (Exception ex)
        {
            RuntimeLog.Write("ChartDownloadVM", $"Preview error: {ex}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                StatusMessage = $"试听出错: {ex.Message}");
        }
    }

    private static IWaveProvider CreateWaveProvider(Stream stream, string ext)
    {
        if (string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase))
            return new Mp3FileReader(stream);

        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
            return new WaveFileReader(stream);

        return new VorbisWaveReader(stream);
    }

    public void StopPlayback()
    {
        // 如果正在加载音频，取消加载
        _loadCts?.Cancel();
        _loadCts = null;

        _stopCts?.Cancel();
        _stopCts = null;

        if (_playingChart != null)
        {
            _playingChart.IsPlaying = false;
            _playingChart = null;
        }

        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        _listCts?.Cancel();
        _listCts?.Dispose();
        _listCts = null;

        StopPlayback();
        _currentScrollY = 0;
        Charts.Clear(); // 释放内存：移除对所有谱面对象极其封面图片的引用
    }
}
