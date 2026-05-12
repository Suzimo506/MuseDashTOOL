using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
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
using MdModManager.Helpers;
using NAudio.Vorbis;
using NAudio.Wave;

namespace MdModManager.ViewModels;

public partial class ChartDownloadViewModel : ObservableObject, IDisposable
{
    private readonly IChartDownloadService _downloadService;
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    private readonly IDownloadManagerService _downloadManagerService;
    private static readonly HttpClient _coverHttp = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(15));
    private static readonly SemaphoreSlim _coverSemaphore = new(7);
    private readonly Lock _coverLoadLock = new();
    private readonly HashSet<string> _coverLoadingIds = new(StringComparer.Ordinal);

    static ChartDownloadViewModel()
    {
        _coverHttp.DefaultRequestHeaders.Remove("User-Agent");
        _coverHttp.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    // ── 谱面列表 ─────────────────────────────────────────────────────────────
    public ObservableCollection<MdmcChart> Charts { get; } = new();

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private bool _isLoading = false;

    [ObservableProperty] private bool _isLoadingMore = false;
    [ObservableProperty] private string _statusMessage = "正在初始化…";
    [ObservableProperty] private string _previewStatusText = string.Empty;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int _todayUpdatesCount = 0;

    // ── 搜索 ──────────────────────────────────────────────────────────────────
    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        private set => SetProperty(ref _searchText, value);
    }

    private string _searchDraftText = string.Empty;
    public string SearchDraftText
    {
        get => _searchDraftText;
        set => SetProperty(ref _searchDraftText, value);
    }

    /// <summary>是否启用谱面名称滚动</summary>
    public bool EnableMarquee => _configService.Config.EnableChartNameMarquee;

    private CancellationTokenSource? _searchCts;

    // ── 排序 ──────────────────────────────────────────────────────────────────
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
                _ = ReloadAsync();
            }
        }
    }

    public bool IsSortByLikes => SelectedSortIndex == 0;
    public bool IsSortByLatest => SelectedSortIndex == 1;
    public bool IsSortByDifficulty => SelectedSortIndex == 2;

    [ObservableProperty] private bool _isAscending = false;   // 默认从高到低 (desc)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RankedOnly))]
    private bool _showUnranked = true;   // 显示未评级

    public bool RankedOnly
    {
        get => !ShowUnranked;
        set
        {
            if (ShowUnranked == !value)
                return;

            ShowUnranked = !value;
            OnPropertyChanged();
            CurrentPage = 1;
            _ = ReloadAsync();
        }
    }

    // ── 分页 ──────────────────────────────────────────────────────────────────
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private int _currentPage = 1;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private int _totalPages = 1;

    [ObservableProperty] private string _jumpPageText = string.Empty; // 跳转页码文本
    [ObservableProperty] private bool _isEditingPageNumber = false; // 是否处于页码编辑状态

    partial void OnCurrentPageChanged(int value)
    {
        JumpPageText = value.ToString();
    }
    
    // 信号量：用于通知 View 需要滚动到哪个 Y 坐标
    [ObservableProperty] private double? _requestedScrollY;
    
    private int _currentLoadId = 0;
    
    private double _currentScrollY = 0;
    private const int Columns = 4;
    private const int PageSize = 15;

    public bool CanLoadNext => CurrentPage < TotalPages && !IsLoading;
    public bool CanLoadPrev => CurrentPage > 1 && !IsLoading;

    // ── 音频播放 ──────────────────────────────────────────────────────────────
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

    // 页面缓存: Key 为 "sort|order|query|page"
    // 存储超过 5 页就释放最旧的一页
    private string _lastLoadedKey = string.Empty;
    private readonly System.Collections.Generic.Dictionary<string, (IList<MdmcChart> charts, int totalPages)> _pageCache = new();
    private readonly System.Collections.Generic.List<string> _cacheKeys = new();
    private const string ProtectedCacheKey = "likes|desc||1|True";

    private string GetCacheKey(int page, string sort, string order, string query, bool showUnranked) 
        => $"{sort}|{order}|{query.Trim()}|{page}|{showUnranked}";

    private void AddToCache(string key, (IList<MdmcChart> charts, int totalPages) result)
    {
        if (_pageCache.ContainsKey(key))
        {
            _cacheKeys.Remove(key);
        }
        else if (_cacheKeys.Count >= 5)
        {
            // 找到第一个不是受保护 Key 的最旧索引
            string? keyToEvict = null;
            for (int i = 0; i < _cacheKeys.Count; i++)
            {
                if (_cacheKeys[i] != ProtectedCacheKey)
                {
                    keyToEvict = _cacheKeys[i];
                    _cacheKeys.RemoveAt(i);
                    break;
                }
            }

            if (keyToEvict != null)
            {
                if (_pageCache.TryGetValue(keyToEvict, out var oldResult))
                {
                    foreach (var c in oldResult.charts)
                    {
                        // 额外检查：如果要删除的谱面也在受保护的首页中，则不要清除它的图片
                        if (_pageCache.TryGetValue(ProtectedCacheKey, out var protectedPage) && protectedPage.charts.Contains(c))
                            continue;
                            
                        c.CoverImage = null; // 释放大内存位图引用
                    }
                }
                _pageCache.Remove(keyToEvict);
                RuntimeLog.Write("ChartDownloadVM", $"Cache full, evicted: {keyToEvict}");
            }
        }
        
        _pageCache[key] = result;
        _cacheKeys.Add(key);
    }

    public async Task PreloadAllSortsAsync()
    {
        foreach (var opt in SortOptions)
        {
            var key = GetCacheKey(1, opt.Value, "desc", "", !ShowUnranked);
            if (_pageCache.ContainsKey(key)) continue;

            try 
            {
                var result = await _downloadService.FetchChartsAsync(1, opt.Value, "desc", "", !ShowUnranked);
                if (result.charts.Count > 0)
                {
                    AddToCache(key, result);
                    
                    // 预加载封面
                    // 如果是点赞数排序（首页），则加载全页封面，否则只加载前 3 个
                    int preloadCount = (opt.Value == "likes") ? result.charts.Count : Math.Min(result.charts.Count, 3);
                    
                    for (int i = 0; i < preloadCount; i++)
                    {
                        var chart = result.charts[i];
                        _ = LoadSingleCoverAsync(chart);
                    }
                    
                    if (opt.Value == "likes")
                        RuntimeLog.Write("ChartDownloadVM", $"Preloaded all covers for Likes page 1.");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("ChartDownloadVM", $"Preload failed for {opt.Value}: {ex.Message}");
            }
        }
    }

    private async Task LoadSingleCoverAsync(MdmcChart chart)
    {
        if (chart.HasDisplayCoverSource) return;
        await _coverSemaphore.WaitAsync();
        try
        {
            if (chart.HasDisplayCoverSource) return;
            await ChartCoverSourceResolver.EnsureResolvedAsync(chart);
        }
        catch { /* 已忽略 */ }
        finally { _coverSemaphore.Release(); }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        SearchDraftText = SearchText;

        // 优化：如果已经在点赞榜首页且已经有数据，不再触发重载
        if (Charts.Count > 0 && CurrentPage == 1 && SelectedSortIndex == 0 && string.IsNullOrWhiteSpace(SearchText) && !IsAscending)
        {
            _ = UpdateTodayUpdatesCountAsync();
            UpdateStatusMessage();
            _ = EnsureCurrentPageCoversLoadedAsync();
            return;
        }

        CurrentPage = 1;
        _currentScrollY = 0;
        _ = UpdateTodayUpdatesCountAsync(); // 异步更新今日数量
        await ReloadAsync(ct);
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

    // ── 命令 ──────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task Refresh() 
    {
        // 手动刷新时执行完全清理，获取最新数据
        ClearPageCache(false);
        CurrentPage = 1;
        _currentScrollY = 0;
        _ = UpdateTodayUpdatesCountAsync();
        await ReloadAsync(default, true);
    }

    private void ClearPageCache(bool keepProtected = true)
    {
        // 提取受保护的页面数据以便后续保留
        (IList<MdmcChart> charts, int totalPages) protectedData = default;
        bool hasProtected = _pageCache.TryGetValue(ProtectedCacheKey, out protectedData);

        foreach (var kvp in _pageCache)
        {
            // 如果是常规清理（保留受保护页），且当前就是受保护页，则跳过位图清理
            if (keepProtected && kvp.Key == ProtectedCacheKey)
                continue;

            foreach (var c in kvp.Value.charts)
            {
                // 如果这个谱面也在受保护的页面中，则绝对不能清除它的位图
                if (hasProtected && protectedData.charts.Contains(c))
                    continue;

                c.CoverImage = null;
            }
        }

        _pageCache.Clear();
        _cacheKeys.Clear();

        // 如果需要保留，则重新塞回字典和索引列表
        if (keepProtected && hasProtected)
        {
            _pageCache[ProtectedCacheKey] = protectedData;
            _cacheKeys.Add(ProtectedCacheKey);
        }
        else
        {
            _lastLoadedKey = string.Empty; // 缓存若被完全清空，重置上次加载的 Key
        }
    }

    [RelayCommand]
    private async Task ClearSearch()
    {
        _searchCts?.Cancel();
        SearchDraftText = string.Empty;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            SearchText = string.Empty;
            CurrentPage = 1;
            await ReloadAsync();
        }
    }

    [RelayCommand]
    public async Task ApplySearchAsync()
    {
        _searchCts?.Cancel();
        var newQuery = SearchDraftText?.Trim() ?? string.Empty;
        var currentQuery = SearchText?.Trim() ?? string.Empty;

        if (string.Equals(newQuery, currentQuery, StringComparison.Ordinal))
            return;

        SearchText = newQuery;
        CurrentPage = 1;
        await ReloadAsync();
    }

    [RelayCommand]
    private void OpenMdmcWebsite()
    {
        try
        {
            var url = "https://mdmc.moe/";
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start("xdg-open", url);
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", url);
            }
        }
        catch { /* ignore */ }
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
        _ = ReloadAsync();
    }

    [RelayCommand]
    private async Task LoadNextPage()
    {
        if (CanLoadNext)
        {
            CurrentPage++;
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task LoadPrevPage()
    {
        if (CanLoadPrev)
        {
            CurrentPage--;
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task LoadFirstPageAsync()
    {
        CurrentPage = 1;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task LoadLastPageAsync()
    {
        CurrentPage = TotalPages;
        await ReloadAsync();
    }

    /// <summary>强制重新加载当前页面（跳过缓存）</summary>
    [RelayCommand]
    public async Task ReloadCurrentPageAsync()
    {
        await ReloadAsync(force: true);
    }

    /// <summary>跳转到指定页码</summary>
    [RelayCommand]
    private void StartEditPage()
    {
        JumpPageText = CurrentPage.ToString();
        IsEditingPageNumber = true;
    }

    [RelayCommand]
    private void CancelEditPage()
    {
        JumpPageText = CurrentPage.ToString();
        IsEditingPageNumber = false;
    }

    [RelayCommand]
    private async Task JumpPageAsync()
    {
        // 增加保护：如果已经退出编辑状态（例如已处理完 KeyBinding），则不再重复执行
        if (!IsEditingPageNumber) return;

        var text = JumpPageText; 
        IsEditingPageNumber = false;

        if (string.IsNullOrWhiteSpace(text)) return;

        if (int.TryParse(text, out int targetPage))
        {
            // 限制页码范围在 1 到 TotalPages 之间
            targetPage = Math.Clamp(targetPage, 1, TotalPages);
            
            CurrentPage = targetPage;
            await ReloadAsync();
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

        var url = chart.DownloadUrl;
        // 特例修正：调色盘等特殊路径下载直连
        if (!string.IsNullOrEmpty(url) && (url.Contains("~%23FFFFFF~") || url.Contains("~#FFFFFF~") || (chart.Title?.Contains("调色盘") == true)))
        {
            var manualUrl = url.Replace("/blob/", "/").Replace("github.com", "raw.githubusercontent.com").Replace("~#FFFFFF~", "~%23FFFFFF~");
            url = GitHubMirrorHelper.ApplyMirror(manualUrl, _configService.Config.DownloadSource);
            // 顺便同步给界面引用的 CustomDownloadUrl
            chart.CustomDownloadUrl = url;
        }

        _downloadManagerService.EnqueueDownload(chart);
        _notificationService.ShowSuccess($"已添加到下载列表: 《{chart.Title}》");
    }

    // ── 私有辅助方法 ──────────────────────────────────────────────────────────

    private async Task ReloadAsync(CancellationToken externalCt = default, bool force = false)
    {
        int myId = Interlocked.Increment(ref _currentLoadId);
        
        // 1. 获取请求参数并计算 Key
        var sort  = SortOptions[SelectedSortIndex].Value;
        var order = IsAscending ? "asc" : "desc";
        var query = SearchText.Trim();
        var cacheKey = GetCacheKey(CurrentPage, sort, order, query, !ShowUnranked);

        // 核心修复：无论是否提前返回，只要发起了新的 ReloadAsync，必须立刻取消上一个未完成的后台请求
        // 否则如果触发提前返回，后台请求不会被取消，会在完成后强行篡改 UI 数据，导致 UI 与页码脱节
        _listCts?.Cancel();

        // 2. 检查是否已经在显示该页面
        if (!force && cacheKey == _lastLoadedKey && Charts.Count > 0)
        {
            IsLoading = false;
            UpdateStatusMessage();
            _ = EnsureCurrentPageCoversLoadedAsync();
            return;
        }

        _listCts = new CancellationTokenSource();
        StopPlayback();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _listCts.Token, externalCt);
        var ct = linkedCts.Token;

        try
        {
            IList<MdmcChart> charts;
            int totalPages;

            // 3. 尝试在缓存中查找 (如果是强制刷新，则跳过缓存直接联网)
            if (!force && _pageCache.TryGetValue(cacheKey, out var cached))
            {
                charts = cached.charts;
                totalPages = cached.totalPages;
                // 更新 LRU 顺序
                _cacheKeys.Remove(cacheKey);
                _cacheKeys.Add(cacheKey);
                RuntimeLog.Write("ChartDownloadVM", $"Page {CurrentPage} hit cache: {cacheKey}");
            }
            else
            {
                // 仅在需要从网络获取时才显示“正在加载”
                IsLoading = true;
                StatusMessage = "正在加载谱面列表…";

                var result = await _downloadService.FetchChartsAsync(
                    CurrentPage, sort, order, query, !ShowUnranked, ct);
                charts = result.charts;
                totalPages = result.totalPages;
                if (charts.Count == 0 && !string.IsNullOrWhiteSpace(query))
                {
                    // 仅在搜索模式下尝试恢复
                    var recovered = await TryRecoverEmptyResultAsync(sort, order, ct);
                    if (recovered != null)
                    {
                        charts = recovered.Value.charts;
                        totalPages = recovered.Value.totalPages;
                    }
                }
                else if (charts.Count == 0 && CurrentPage > 1)
                {
                    // 非搜索模式下，如果第 2 页及以后返回空，提示用户手动重试
                    _notificationService.ShowFailure("翻页失败", "服务器未返回数据，请尝试重试");
                }

                // 加入缓存
                AddToCache(cacheKey, (charts, totalPages));
            }

            if (myId != _currentLoadId) return;

            // 4. 更新 UI 数据
            TotalPages = Math.Max(1, totalPages);
            Charts.Clear(); 
            foreach (var c in charts) Charts.Add(c);

            IsEmpty = Charts.Count == 0;
            _lastLoadedKey = cacheKey; // 标记当前页面已就绪
            
            // 记录日志并重置滚动位置
            RuntimeLog.Write("ChartDownloadVM", $"Page updated: {Charts.Count} items on page {CurrentPage}/{TotalPages} (Key={cacheKey})");
            RequestedScrollY = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // 加载失败时，如果不是第一页，考虑恢复页码（可选，或者保持现状提示用户手动刷新）
            if (myId == _currentLoadId)
            {
                StatusMessage = "加载失败：" + ex.Message;
                _notificationService.ShowFailure("网络异常", "请检查网络或点击页码重试");
            }
        }
        finally
        {
            if (myId == _currentLoadId)
            {
                IsLoading = false;
                UpdateStatusMessage();
            }
            _ = EnsureCurrentPageCoversLoadedAsync();
        }
    }

    /// <summary>由 View 调用，更新当前滚动位置并触发内存管理</summary>
    private async Task<(IList<MdmcChart> charts, int totalPages)?> TryRecoverEmptyResultAsync(string sort, string order, CancellationToken ct)
    {
        // 彻底移除自动回跳逻辑，避免服务器波动导致翻页中断
        if (CurrentPage == 1 && !IsAscending)
        {
            var key = GetCacheKey(1, sort, order, "", !ShowUnranked);
            if (_pageCache.TryGetValue(key, out var cached) && cached.charts.Count > 0)
            {
                RuntimeLog.Write("ChartDownloadVM", $"Recovered empty result from cache for sort={sort}, count={cached.charts.Count}.");
                return cached;
            }
        }

        var retry = await _downloadService.FetchChartsAsync(1, sort, order, string.Empty, !ShowUnranked, ct);
        if (retry.charts.Count > 0)
        {
            RuntimeLog.Write("ChartDownloadVM", $"Recovered empty result by retry for sort={sort}, count={retry.charts.Count}.");
            if (!IsAscending)
            {
                var key = GetCacheKey(1, sort, order, "", !ShowUnranked);
                AddToCache(key, retry);
            }
            return retry;
        }

        RuntimeLog.Write("ChartDownloadVM", $"Retry still empty for sort={sort}.");
        return null;
    }

    public void UpdateScrollPosition(double yOffset)
    {
        _currentScrollY = yOffset;
        UpdateStatusMessage();
    }

    private void UpdateStatusMessage()
    {
        if (!string.IsNullOrEmpty(PreviewStatusText))
        {
            StatusMessage = PreviewStatusText;
            return;
        }

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

        StatusMessage = $"第 {CurrentPage} / {TotalPages} 页，今日更新 {TodayUpdatesCount} 张谱面";
    }


    /// <summary>异步并行加载当前页缺失的封面图，避免重复排队同一张封面。</summary>
    private async Task EnsureCurrentPageCoversLoadedAsync()
    {
        var chartsSnapshot = Charts.ToList();
        if (chartsSnapshot.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>();

        foreach (var chart in chartsSnapshot)
        {
            if (!TryMarkCoverLoadStarted(chart))
            {
                continue;
            }

            tasks.Add(LoadSingleCoverTrackedAsync(chart));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private bool TryMarkCoverLoadStarted(MdmcChart chart)
    {
        if (chart.HasDisplayCoverSource || string.IsNullOrWhiteSpace(chart.Id))
        {
            return false;
        }

        lock (_coverLoadLock)
        {
            return _coverLoadingIds.Add(chart.Id);
        }
    }

    private void MarkCoverLoadFinished(MdmcChart chart)
    {
        lock (_coverLoadLock)
        {
            _coverLoadingIds.Remove(chart.Id);
        }
    }

    private async Task LoadSingleCoverTrackedAsync(MdmcChart chart)
    {
        await _coverSemaphore.WaitAsync();
        try
        {
            if (chart.HasDisplayCoverSource)
            {
                return;
            }

            await ChartCoverSourceResolver.EnsureResolvedAsync(chart);
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("ChartDownloadVM", $"Cover load failed: id='{chart.Id}', title='{chart.Title}', error='{ex.Message}'");
        }
        finally
        {
            MarkCoverLoadFinished(chart);
            _coverSemaphore.Release();
        }
    }

    private async Task PlayDemoAsync(MdmcChart chart)
    {
        // 取消之前的加载请求
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        int retryCount = 0;
        while (retryCount < 2)
        {
            try
            {
                // 默认优先尝试 .ogg
                string url = !string.IsNullOrWhiteSpace(chart.DemoUrl) ? chart.DemoUrl : chart.DemoMp3Url;
                
                // 特例修正：调色盘等特殊路径试听
                if (!string.IsNullOrEmpty(url) && (url.Contains("~%23FFFFFF~") || url.Contains("~#FFFFFF~") || (chart.Title?.Contains("调色盘") == true)))
                {
                    var manualUrl = url.Replace("/blob/", "/").Replace("github.com", "raw.githubusercontent.com").Replace("~#FFFFFF~", "~%23FFFFFF~");
                    url = GitHubMirrorHelper.ApplyMirror(manualUrl, _configService.Config.DownloadSource);
                }

                string ext = Path.GetExtension(url ?? string.Empty);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    PreviewStatusText = $"正在缓冲 {chart.Title} 试听文件";
                    UpdateStatusMessage();
                });

                using var response = await _coverHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                
                // 如果 .ogg 不存在 (404) 或失效，尝试回退到 .mp3
                if (!response.IsSuccessStatusCode && !ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(chart.DemoMp3Url) && url != chart.DemoMp3Url)
                {
                    url = chart.DemoMp3Url;
                    ext = Path.GetExtension(url ?? string.Empty);
                    RuntimeLog.Write("ChartDownloadVM", $"Preview fallback to mp3: '{url}'");
                    
                    using var fallbackRes = await _coverHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (fallbackRes.IsSuccessStatusCode)
                    {
                        var fallbackBytes = await fallbackRes.Content.ReadAsByteArrayAsync(ct);
                        await StartAudioStreamAsync(fallbackBytes, ext, chart, ct);
                        return;
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"HTTP 响应异常: {(int)response.StatusCode}");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                await StartAudioStreamAsync(bytes, ext, chart, ct);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                retryCount++;
                RuntimeLog.Write("ChartDownloadVM", $"Preview attempt {retryCount} failed: {ex.Message}");
                if (retryCount < 2 && HttpHelper.UseOptimizedIps)
                {
                    HttpHelper.InvalidateFastestIp();
                    await Task.Delay(500, ct);
                    continue;
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    StatusMessage = $"试听音频加载失败: {ex.Message}");
                break;
            }
        }
    }

    private async Task StartAudioStreamAsync(byte[] bytes, string ext, MdmcChart chart, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        var ms = new MemoryStream(bytes);
        IWaveProvider waveProvider = CreateWaveProvider(ms, ext);

        _waveOut = new WaveOutEvent();
        _waveOut.Init(waveProvider);
        _waveOut.Volume = (float)_configService.Config.ChartPreviewVolume;

        var cts = _stopCts = new CancellationTokenSource();
        var provider = waveProvider;

        _waveOut.PlaybackStopped += (_, _) =>
        {
            if (!cts.IsCancellationRequested)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_playingChart == chart)
                    {
                        _playingChart = null;
                        PreviewStatusText = string.Empty;
                        UpdateStatusMessage();
                    }
                    chart.IsPlaying = false;
                });
            if (provider is IDisposable d) d.Dispose();
            ms.Dispose();
        };

        _waveOut.Play();
        _playingChart = chart;
        chart.IsPlaying = true;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PreviewStatusText = $"正在播放 {chart.Title} 试听";
            UpdateStatusMessage();
        });

        await Task.CompletedTask;
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
        // 如果正在加载音频，立刻取消加载以释放 HTTP 连接
        _loadCts?.Cancel();
        _loadCts = null;

        _stopCts?.Cancel();
        _stopCts = null;

        if (_playingChart != null)
        {
            _playingChart.IsPlaying = false;
            _playingChart = null;
        }

        PreviewStatusText = string.Empty;
        UpdateStatusMessage();

        var waveOut = _waveOut;
        _waveOut = null;
        if (waveOut != null)
        {
            // 将停止和释放放到后台，避免音频驱动延迟导致 UI 卡死
            _ = Task.Run(() =>
            {
                try
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Write("ChartDownloadVM", $"Error disposing WaveOut: {ex.Message}");
                }
            });
        }
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
        ClearPageCache(false); // 彻底释放所有缓存，包括受保护的首页
        Charts.Clear(); 
    }
}
