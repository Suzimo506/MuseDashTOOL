using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.Views;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Linq;
using MdModManager.Helpers;
using System.Diagnostics;

namespace MdModManager.ViewModels;

public partial class AlbumDetailViewModel : ObservableObject, IDisposable
{
    private const int UpdateNotificationDurationMs = 5000;
    private static readonly SemaphoreSlim _coverSemaphore = new(7);

    [ObservableProperty]
    private string _statusMessage = string.Empty;
    private readonly ChartDownloadViewModel _chartDownloadViewModel;
    private readonly IAlbumCollectionService _collectionService;
    private readonly IConfigService _configService;
    private readonly IDownloadManagerService _downloadManagerService;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    private DesignerCategory? _category;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPersonalRepository))]
    [NotifyPropertyChangedFor(nameof(HasHomepageLink))]
    private string _homepageUrl = string.Empty;

    [ObservableProperty]
    private ObservableCollection<MdmcChart> _charts = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    [NotifyPropertyChangedFor(nameof(IsNotLoading))]
    [NotifyCanExecuteChangedFor(nameof(LoadNextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadPrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadFirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadLastPageCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    public bool IsNotLoading => !IsLoading;

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        _chartDownloadViewModel.StopPlayback();
        CurrentPage = 1;
        _ = ReloadAsync();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    // ── 分页 ──────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    [NotifyCanExecuteChangedFor(nameof(LoadNextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadPrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadFirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadLastPageCommand))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    [NotifyCanExecuteChangedFor(nameof(LoadNextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadLastPageCommand))]
    private int _totalPages = 1;

    [ObservableProperty]
    private string _jumpPageText = string.Empty;

    [ObservableProperty]
    private bool _isEditingPageNumber;

    public bool CanLoadNext => CurrentPage < TotalPages && !IsLoading;
    public bool CanLoadPrev => CurrentPage > 1 && !IsLoading;
    public bool IsPersonalRepository => AlbumCollectionService.IsPersonalRepositoryName(Category?.Name);
    public bool HasHomepageLink => IsPersonalRepository && !string.IsNullOrWhiteSpace(HomepageUrl);

    [ObservableProperty] private double? _requestedScrollY;

    // ── 5 页 LRU 缓存 ────────────────────────────────────────────────────────
    private readonly Dictionary<string, (IList<MdmcChart> charts, int totalPages)> _pageCache = new();
    private readonly List<string> _cacheKeys = new();
    private const int MaxCachedPages = 5;

    private List<MdmcChart> _allFullIndex = new();
    private List<MdmcChart> _filteredIndex = new();

    public IAsyncRelayCommand<MdmcChart> TogglePreviewCommand => _chartDownloadViewModel.TogglePreviewCommand;
    public IAsyncRelayCommand<MdmcChart> DownloadChartCommand => _chartDownloadViewModel.DownloadChartCommand;
    public bool EnableMarquee => _chartDownloadViewModel.EnableMarquee;

    public AlbumDetailViewModel(
        ChartDownloadViewModel chartDownloadViewModel,
        IAlbumCollectionService collectionService,
        IConfigService configService,
        IDownloadManagerService downloadManagerService,
        INotificationService notificationService)
    {
        _chartDownloadViewModel = chartDownloadViewModel;
        _collectionService = collectionService;
        _configService = configService;
        _downloadManagerService = downloadManagerService;
        _notificationService = notificationService;

        _chartDownloadViewModel.PropertyChanged += OnChartDownloadViewModelPropertyChanged;
    }

    private void OnChartDownloadViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartDownloadViewModel.PreviewStatusText))
        {
            UpdateStatusMessage();
        }
    }

    public void Dispose()
    {
        _chartDownloadViewModel.PropertyChanged -= OnChartDownloadViewModelPropertyChanged;
    }

    private string GetCacheKey(int page, string query) => $"{Category?.Name}|{query.Trim()}|{page}";

    private void AddToCache(string key, (IList<MdmcChart> charts, int totalPages) result)
    {
        if (_pageCache.ContainsKey(key))
        {
            _cacheKeys.Remove(key);
        }
        else if (_cacheKeys.Count >= MaxCachedPages)
        {
            var oldKey = _cacheKeys[0];
            _cacheKeys.RemoveAt(0);
            if (_pageCache.TryGetValue(oldKey, out var oldResult))
            {
                foreach (var c in oldResult.charts)
                    c.CoverImage = null; // 释放位图内存
            }
            _pageCache.Remove(oldKey);
        }

        _pageCache[key] = result;
        _cacheKeys.Add(key);
    }

    private void ClearPageCache()
    {
        foreach (var kvp in _pageCache)
        {
            foreach (var c in kvp.Value.charts)
                c.CoverImage = null;
        }
        _pageCache.Clear();
        _cacheKeys.Clear();
    }

    private void UpdateStatusMessage()
    {
        var previewText = _chartDownloadViewModel.PreviewStatusText;
        if (!string.IsNullOrEmpty(previewText) && (previewText.Contains("正在缓冲") || previewText.Contains("正在播放")))
        {
            StatusMessage = previewText;
            return;
        }

        if (IsLoading)
        {
            StatusMessage = "正在载入...";
            return;
        }

        if (IsEmpty)
        {
            StatusMessage = "未找到符合条件的谱面";
            return;
        }

        StatusMessage = $"第 {CurrentPage} / {TotalPages} 页，共 {_filteredIndex.Count} 张谱面";
    }

    public async Task InitializeAsync(DesignerCategory category, string searchText = "")
    {
        Log($"Initializing album detail for '{category.Name}' with search query '{searchText}'.");
        Category = category;
        HomepageUrl = AlbumCollectionService.GetPersonalRepositoryHomepage(category.Name);
        OnPropertyChanged(nameof(IsPersonalRepository));
        SearchText = searchText;
        
        ClearPageCache();
        _allFullIndex.Clear();
        _filteredIndex.Clear();
        CurrentPage = 1;

        IsLoading = true;
        IsEmpty = false;
        StatusMessage = "正在获取整合包谱面...";

        // 1. 优先加载本地缓存
        var localCharts = await _collectionService.GetLocalChartsAsync(category.Name);
        if (localCharts.Count > 0)
        {
            ProcessAndLoadCharts(localCharts);
            await ReloadAsync();
            IsLoading = false; // 有本地数据，先取消加载动画
        }

        // 2. 后台并行拉取远端，进行比对更新
        _ = Task.Run(async () =>
        {
            try
            {
                var remoteCharts = await _collectionService.GetChartsAsync(category.Name);
                if (remoteCharts.Count > 0)
                {
                    bool needsUpdate = DesignerChartUpdateComparer.HasChartListChanged(localCharts, remoteCharts);

                    if (needsUpdate)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                        {
                            ProcessAndLoadCharts(remoteCharts);
                            await ReloadAsync();
                            _notificationService.ShowSuccess("曲包检测到更新，已强制刷新界面~", UpdateNotificationDurationMs);
                            Log($"Remote update detected and applied for '{category.Name}'.");
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Background remote sync failed for '{category.Name}': {ex}");
            }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { IsLoading = false; });
            }
        });
    }

    private void ProcessAndLoadCharts(List<DesignerChart> charts)
    {
        _allFullIndex.Clear();
        foreach (var c in charts)
        {
            List<string> difficultyLabels = new List<string>();
            if (c.Difficulties != null && c.Difficulties.Count > 0)
            {
                foreach (var d in c.Difficulties)
                {
                    if (!string.IsNullOrEmpty(d))
                    {
                        var splitted = d.Split(new char[] { ',', '，', ' ', '/', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        difficultyLabels.AddRange(splitted.Where(x => !string.IsNullOrWhiteSpace(x)));
                    }
                }
            }
            
            if (difficultyLabels.Count == 0)
            {
                var urlDecoded = System.Net.WebUtility.UrlDecode(c.DownloadUrl);
                var match = System.Text.RegularExpressions.Regex.Match(
                    urlDecoded,
                    @"\[Lv\.(.*?)\]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                difficultyLabels = ExtractDifficultyLabels(match);
            }

            _allFullIndex.Add(new MdmcChart
            {
                Id = string.IsNullOrWhiteSpace(c.Id) ? c.DownloadUrl : c.Id,
                Title = c.Title,
                Artist = c.Artist,
                Charter = c.Author,
                Bpm = NormalizeBpm(c.Bpm),
                CustomCoverUrl = ResolveResourceUrl(c.CoverUrl),
                ResolvedCoverSource = ResolveResourceUrl(c.CoverUrl),
                CustomDownloadUrl = ResolveResourceUrl(c.DownloadUrl),
                CustomDemoUrl = ResolveResourceUrl(c.DemoUrl),
                CustomDemoMp3Url = ResolveResourceUrl(c.DemoMp3Url),
                Sheets = difficultyLabels
                    .Select(label => new MdmcSheet { Difficulty = label })
                    .ToList()
            });
        }
    }

    private async Task ReloadAsync()
    {
        IsLoading = true;
        IsEmpty = false;
        UpdateStatusMessage();
        Charts.Clear();

        var query = SearchText.Trim().ToLowerInvariant();

        _filteredIndex = _allFullIndex;
        if (!string.IsNullOrWhiteSpace(query))
        {
            _filteredIndex = _allFullIndex.Where(c => 
                c.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                c.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                c.Charter?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
            ).ToList();
        }

        var cacheKey = GetCacheKey(CurrentPage, query);

        if (_pageCache.TryGetValue(cacheKey, out var cached))
        {
            TotalPages = Math.Max(1, cached.totalPages);
            foreach (var c in cached.charts) 
            {
                c.SearchText = SearchText;
                Charts.Add(c);
            }
            _cacheKeys.Remove(cacheKey);
            _cacheKeys.Add(cacheKey);
            IsEmpty = Charts.Count == 0;
            IsLoading = false;
            UpdateStatusMessage();
            return;
        }

        var pageSize = 12;
        var totalCount = _filteredIndex.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));
        
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        if (CurrentPage < 1) CurrentPage = 1;

        var pageCharts = _filteredIndex
            .Skip((CurrentPage - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        foreach (var c in pageCharts)
        {
            c.SearchText = SearchText;
            Charts.Add(c);
        }

        AddToCache(cacheKey, (pageCharts, TotalPages));

        IsEmpty = Charts.Count == 0;
        IsLoading = false;
        UpdateStatusMessage();

        RequestedScrollY = 0;

        _ = LoadCoversAsync(pageCharts);
    }

    [RelayCommand(CanExecute = nameof(CanLoadPrev))]
    private async Task LoadFirstPageAsync() { CurrentPage = 1; await ReloadAsync(); }

    [RelayCommand(CanExecute = nameof(CanLoadPrev))]
    private async Task LoadPrevPageAsync() { if (CurrentPage > 1) { CurrentPage--; await ReloadAsync(); } }

    [RelayCommand(CanExecute = nameof(CanLoadNext))]
    private async Task LoadNextPageAsync() { if (CurrentPage < TotalPages) { CurrentPage++; await ReloadAsync(); } }

    [RelayCommand(CanExecute = nameof(CanLoadNext))]
    private async Task LoadLastPageAsync() { CurrentPage = TotalPages; await ReloadAsync(); }

    [RelayCommand]
    private void StartEditPage() { JumpPageText = CurrentPage.ToString(); IsEditingPageNumber = true; }

    [RelayCommand]
    private void CancelEditPage() { IsEditingPageNumber = false; JumpPageText = ""; }

    [RelayCommand]
    private async Task JumpPageAsync()
    {
        IsEditingPageNumber = false;
        if (int.TryParse(JumpPageText.Trim(), out int page))
        {
            page = Math.Max(1, Math.Min(page, TotalPages));
            if (page != CurrentPage)
            {
                CurrentPage = page;
                await ReloadAsync();
            }
        }
        JumpPageText = "";
    }

    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        if (_filteredIndex.Count == 0)
            return;

        if (!IsDotNet6Installed())
        {
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow as MainWindow
                : null;

            if (mainWindow != null)
            {
                await mainWindow.ShowMessageBoxAsync("请先在Mod列表顶部下载.net6运行环境！");
            }

            return;
        }

        var queued = 0;
        foreach (var chart in _filteredIndex)
        {
            var url = chart.CustomDownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
                continue;

            // 特例：修复调色盘等特殊路径在批量下载时的镜像识别问题
            if (url.Contains("~%23FFFFFF~") || url.Contains("~#FFFFFF~") || (chart.Title?.Contains("调色盘") == true))
            {
                var manualUrl = url.Replace("/blob/", "/").Replace("github.com", "raw.githubusercontent.com").Replace("~#FFFFFF~", "~%23FFFFFF~");
                url = GitHubMirrorHelper.ApplyMirror(manualUrl, _configService.Config.DownloadSource);
                chart.CustomDownloadUrl = url;
            }

            _downloadManagerService.EnqueueDownload(chart);
            queued++;
        }

        if (queued > 0)
        {
            _notificationService.ShowSuccess($"已添加 {queued} 张谱面到下载列表");
            Log($"Queued {queued} charts for category '{Category?.Name}'.");
        }
    }

    private bool IsDotNet6Installed()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
            return false;

        var net6Path = System.IO.Path.Combine(gamePath, "MelonLoader", "net6", "MelonLoader.dll");
        return System.IO.File.Exists(net6Path);
    }

    private static List<string> ExtractDifficultyLabels(Match match)
    {
        if (!match.Success)
            return ["?"];

        var raw = match.Groups[1].Value;
        var labels = raw
            .Split([',', '，', ' ', '/', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToList();

        return labels.Count > 0 ? labels : ["?"];
    }

    private static string NormalizeBpm(string? bpm)
    {
        if (string.IsNullOrWhiteSpace(bpm))
            return string.Empty;

        return Regex.Replace(bpm.Trim(), @"\d+(\.\d+)?", match =>
        {
            return decimal.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value.ToString("G29", CultureInfo.InvariantCulture)
                : match.Value;
        });
    }

    private string ResolveResourceUrl(string? url)
    {
        return GitHubMirrorHelper.ApplyMirror(url, _configService.Config.DownloadSource);
    }

    private async Task LoadCoversAsync(List<MdmcChart> pageCharts)
    {
        var tasks = new List<Task>();

        foreach (var chart in pageCharts)
        {
            if (string.IsNullOrWhiteSpace(chart.CustomCoverUrl))
                continue;

            if (chart.HasDisplayCoverSource) continue;

            tasks.Add(Task.Run(async () =>
            {
                await _coverSemaphore.WaitAsync();
                try
                {
                    if (chart.HasDisplayCoverSource) return;

                    var fetchUrl = chart.CustomCoverUrl;
                    if (!string.IsNullOrEmpty(fetchUrl) && (fetchUrl.Contains("~%23FFFFFF~") || fetchUrl.Contains("~#FFFFFF~") || (chart.Title?.Contains("调色盘") == true)))
                    {
                        var manualUrl = fetchUrl.Replace("/blob/", "/").Replace("github.com", "raw.githubusercontent.com").Replace("~#FFFFFF~", "~%23FFFFFF~");
                        fetchUrl = GitHubMirrorHelper.ApplyMirror(manualUrl, _configService.Config.DownloadSource);
                    }

                    Avalonia.Threading.Dispatcher.UIThread.Post(() => chart.ResolvedCoverSource = fetchUrl);
                }
                catch (Exception)
                {
                }
                finally
                {
                    _coverSemaphore.Release();
                }
            }));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            Ioc.Default.GetRequiredService<ChartDownloadViewModel>().StopPlayback();
            var collectionVm = Ioc.Default.GetRequiredService<AlbumCollectionViewModel>();
            mainVm.CurrentPage = collectionVm;
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenHomepage()
    {
        if (string.IsNullOrWhiteSpace(HomepageUrl))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(HomepageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _notificationService.ShowFailure("打开失败", "无法打开个人主页链接");
            Log($"Failed to open homepage for '{Category?.Name}': {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        RuntimeLog.Write("AlbumDetailViewModel", message);
    }

}
