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
    private CancellationTokenSource? _gifPlaybackCts;

    [ObservableProperty]
    private string _statusMessage = string.Empty;
    private readonly ChartDownloadViewModel _chartDownloadViewModel;
    private readonly IAlbumCollectionService _collectionService;
    private readonly IConfigService _configService;
    private readonly IDownloadManagerService _downloadManagerService;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVirtualCategory))]
    [NotifyPropertyChangedFor(nameof(ShowChartContent))]
    [NotifyPropertyChangedFor(nameof(ShowVirtualCardMode))]
    [NotifyPropertyChangedFor(nameof(ShowVirtualListMode))]
    private DesignerCategory? _category;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPersonalRepository))]
    [NotifyPropertyChangedFor(nameof(HasHomepageLink))]
    private string _homepageUrl = string.Empty;

    [ObservableProperty]
    private ObservableCollection<MdmcChart> _charts = new();

    [ObservableProperty]
    private ObservableCollection<DesignerCategoryItemViewModel> _subCategories = new();

    [ObservableProperty]
    private ObservableCollection<DesignerCategoryItemViewModel> _subCategoryListLeftColumn = new();

    [ObservableProperty]
    private ObservableCollection<DesignerCategoryItemViewModel> _subCategoryListRightColumn = new();

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewModeToolTip))]
    [NotifyPropertyChangedFor(nameof(ViewModeIconData))]
    [NotifyPropertyChangedFor(nameof(ShowVirtualCardMode))]
    [NotifyPropertyChangedFor(nameof(ShowVirtualListMode))]
    private bool _isListMode;

    public bool CanLoadNext => CurrentPage < TotalPages && !IsLoading;
    public bool CanLoadPrev => CurrentPage > 1 && !IsLoading;
    public bool IsPersonalRepository => AlbumCollectionService.IsPersonalRepositoryName(Category?.Name);
    public bool HasHomepageLink => IsVirtualCategory || (IsPersonalRepository && !string.IsNullOrWhiteSpace(HomepageUrl));
    public bool IsVirtualCategory => Category?.IsVirtualGroup == true;
    public bool ShowChartContent => !IsVirtualCategory;
    public bool ShowVirtualCardMode => IsVirtualCategory && !IsListMode;
    public bool ShowVirtualListMode => IsVirtualCategory && IsListMode;
    public string ViewModeToolTip => IsListMode ? "切换为封面模式" : "切换为列表模式";
    public string ViewModeIconData => IsListMode
        ? "M796.444444 1024 227.555556 1024C102.4 1024 0 921.6 0 796.444444L0 227.555556C0 102.4 102.4 0 227.555556 0L796.444444 0C921.6 0 1024 102.4 1024 227.555556L1024 796.444444C1024 921.6 921.6 1024 796.444444 1024ZM910.222222 227.555556C910.222222 164.807111 859.192889 113.777778 796.444444 113.777778L227.555556 113.777778C164.807111 113.777778 113.777778 164.807111 113.777778 227.555556L113.777778 796.444444C113.777778 859.192889 164.807111 910.222222 227.555556 910.222222L796.444444 910.222222C859.192889 910.222222 910.222222 859.192889 910.222222 796.444444L910.222222 227.555556ZM739.555556 796.444444 512 796.444444C480.711111 796.444444 455.111111 770.844444 455.111111 739.555556 455.111111 708.266667 480.711111 682.666667 512 682.666667L739.555556 682.666667C770.844444 682.666667 796.444444 708.266667 796.444444 739.555556 796.444444 770.844444 770.844444 796.444444 739.555556 796.444444ZM739.555556 568.888889 512 568.888889C480.711111 568.888889 455.111111 543.288889 455.111111 512 455.111111 480.711111 480.711111 455.111111 512 455.111111L739.555556 455.111111C770.844444 455.111111 796.444444 480.711111 796.444444 512 796.444444 543.288889 770.844444 568.888889 739.555556 568.888889ZM739.555556 341.333333 512 341.333333C480.711111 341.333333 455.111111 315.733333 455.111111 284.444444 455.111111 253.155556 480.711111 227.555556 512 227.555556L739.555556 227.555556C770.844444 227.555556 796.444444 253.155556 796.444444 284.444444 796.444444 315.847111 770.844444 341.333333 739.555556 341.333333ZM284.444444 796.444444C253.041778 796.444444 227.555556 770.958222 227.555556 739.555556 227.555556 708.152889 253.041778 682.666667 284.444444 682.666667 315.847111 682.666667 341.333333 708.152889 341.333333 739.555556 341.333333 770.958222 315.847111 796.444444 284.444444 796.444444ZM284.444444 568.888889C253.041778 568.888889 227.555556 543.402667 227.555556 512 227.555556 480.597333 253.041778 455.111111 284.444444 455.111111 315.847111 455.111111 341.333333 480.597333 341.333333 512 341.333333 543.402667 315.847111 568.888889 284.444444 568.888889ZM284.444444 341.333333C253.041778 341.333333 227.555556 315.847111 227.555556 284.444444 227.555556 253.041778 253.041778 227.555556 284.444444 227.555556 315.847111 227.555556 341.333333 253.041778 341.333333 284.444444 341.333333 315.847111 315.847111 341.333333 284.444444 341.333333Z"
        : "M905.846154 0h-708.923077A196.923077 196.923077 0 0 0 0 196.923077v630.153846A196.923077 196.923077 0 0 0 196.923077 1024h708.923077a196.923077 196.923077 0 0 0 196.923077-196.923077v-630.153846A196.923077 196.923077 0 0 0 905.846154 0z m-708.923077 945.230769A118.153846 118.153846 0 0 1 78.769231 827.076923v-630.153846A118.153846 118.153846 0 0 1 196.923077 78.769231h708.923077A118.153846 118.153846 0 0 1 1024 196.923077V472.615385a464.738462 464.738462 0 0 0-393.846154 454.498461V945.230769z m708.923077 0H708.923077v-18.904615A385.969231 385.969231 0 0 1 1024 551.384615v275.692308a118.153846 118.153846 0 0 1-118.153846 118.153846z M393.846154 236.307692a157.538462 157.538462 0 1 0 157.538461 157.538462 157.538462 157.538462 0 0 0-157.538461-157.538462z m0 236.307693a78.769231 78.769231 0 1 1 78.769231-78.769231 78.769231 78.769231 0 0 1-78.769231 78.769231z";

    partial void OnIsListModeChanged(bool value)
    {
        if (_configService.Config.AlbumCollectionListMode != value)
        {
            _configService.Config.AlbumCollectionListMode = value;
            _ = _configService.SaveAsync();
        }

        if (value)
        {
            foreach (var item in _allSubCategories)
                item.ReleaseResources();
        }
        else
        {
            _ = LoadSubCategoryCoversAsync(SubCategories);
        }

        var collectionVm = Ioc.Default.GetService<AlbumCollectionViewModel>();
        if (collectionVm != null && collectionVm.IsListMode != value)
        {
            collectionVm.IsListMode = value;
        }
    }

    [ObservableProperty] private double? _requestedScrollY;

    // ── 5 页 LRU 缓存 ────────────────────────────────────────────────────────
    private readonly Dictionary<string, (IList<MdmcChart> charts, int totalPages)> _pageCache = new();
    private readonly List<string> _cacheKeys = new();
    private const int MaxCachedPages = 5;

    private List<MdmcChart> _allFullIndex = new();
    private List<MdmcChart> _filteredIndex = new();
    private readonly List<DesignerCategoryItemViewModel> _allSubCategories = new();
    private DesignerCategory? _parentVirtualCategory;

    public IAsyncRelayCommand<MdmcChart> TogglePreviewCommand => _chartDownloadViewModel.TogglePreviewCommand;
    public IAsyncRelayCommand<MdmcChart> DownloadChartCommand => _chartDownloadViewModel.DownloadChartCommand;
    public bool EnableMarquee => _chartDownloadViewModel.EnableMarquee;

    public void PrepareForNavigation(DesignerCategory category, string searchText = "")
    {
        _gifPlaybackCts?.Cancel();
        _gifPlaybackCts?.Dispose();
        _gifPlaybackCts = null;

        Category = category;
        IsListMode = _configService.Config.AlbumCollectionListMode;
        HomepageUrl = category.IsVirtualGroup
            ? AlbumCollectionService.GetTEdgeoolHomepage(category.Name)
            : AlbumCollectionService.GetPersonalRepositoryHomepage(category.Name);
        OnPropertyChanged(nameof(IsPersonalRepository));
        OnPropertyChanged(nameof(IsVirtualCategory));
        OnPropertyChanged(nameof(ShowChartContent));
        OnPropertyChanged(nameof(ShowVirtualCardMode));
        OnPropertyChanged(nameof(ShowVirtualListMode));

        ClearPageCache();
        _allFullIndex.Clear();
        _filteredIndex.Clear();
        _allSubCategories.Clear();
        Charts.Clear();
        SubCategories.Clear();
        foreach (var child in category.SubCategories)
        {
            var item = new DesignerCategoryItemViewModel(child);
            _allSubCategories.Add(item);
            SubCategories.Add(item);
        }
        CurrentPage = 1;
        TotalPages = 1;
        JumpPageText = "1";
        RequestedScrollY = null;

        IsEmpty = false;
        IsLoading = true;
        StatusMessage = category.IsVirtualGroup ? "正在载入子曲包..." : "正在获取整合包谱面...";
        SearchText = searchText;
    }

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
        _isListMode = _configService.Config.AlbumCollectionListMode;

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
        ReleaseResources();
        _gifPlaybackCts?.Cancel();
        _gifPlaybackCts?.Dispose();
        _gifPlaybackCts = null;
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
                    c.ReleaseResources();
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
                c.ReleaseResources();
        }
        _pageCache.Clear();
        _cacheKeys.Clear();
    }

    public void ReleaseResources()
    {
        _chartDownloadViewModel.StopPlayback();

        _gifPlaybackCts?.Cancel();
        _gifPlaybackCts?.Dispose();
        _gifPlaybackCts = null;

        ClearPageCache();

        foreach (var chart in _allFullIndex)
        {
            ChartCoverSourceResolver.ReleaseChartCache(chart);
            chart.ReleaseResources();
        }
        foreach (var chart in _filteredIndex)
        {
            ChartCoverSourceResolver.ReleaseChartCache(chart);
            chart.ReleaseResources();
        }
        foreach (var chart in Charts)
        {
            ChartCoverSourceResolver.ReleaseChartCache(chart);
            chart.ReleaseResources();
        }

        _allFullIndex.Clear();
        _filteredIndex.Clear();
        Charts.Clear();

        foreach (var item in _allSubCategories)
            item.ReleaseResources();
        foreach (var item in SubCategories)
            item.ReleaseResources();
        foreach (var item in SubCategoryListLeftColumn)
            item.ReleaseResources();
        foreach (var item in SubCategoryListRightColumn)
            item.ReleaseResources();

        _allSubCategories.Clear();
        SubCategories.Clear();
        SubCategoryListLeftColumn.Clear();
        SubCategoryListRightColumn.Clear();

        RequestedScrollY = null;
        IsEditingPageNumber = false;
        IsLoading = false;
        IsEmpty = false;
        StatusMessage = string.Empty;
        AsyncImageLoaderCacheHelper.ClearMemoryCache();

        if (Category?.Name != null)
            _collectionService.ReleaseCollectionChartsCache(Category.Name);
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
            StatusMessage = IsVirtualCategory ? "未找到符合条件的子曲包" : "未找到符合条件的谱面";
            return;
        }

        if (IsVirtualCategory)
        {
            StatusMessage = $"共 {SubCategories.Count} 个子曲包";
            return;
        }

        StatusMessage = $"第 {CurrentPage} / {TotalPages} 页，共 {_filteredIndex.Count} 张谱面";
    }

    public async Task InitializeAsync(DesignerCategory category, string searchText = "")
    {
        Log($"Initializing album detail for '{category.Name}' with search query '{searchText}'.");
        PrepareForNavigation(category, searchText);

        if (category.IsVirtualGroup)
        {
            _parentVirtualCategory = null;
            ReloadSubCategories();
            await LoadSubCategoryCoversAsync(SubCategories);
            IsLoading = false;
            UpdateStatusMessage();
            return;
        }

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
                IsAnimatedCoverPlaybackEnabled = false,
                CustomCoverUrl = ResolveResourceUrl(c.CoverUrl),
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
        if (IsVirtualCategory)
        {
            ReloadSubCategories();
            await Task.CompletedTask;
            return;
        }

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
        _ = EnableAnimatedCoversDeferredAsync(pageCharts);
    }

    private void ReloadSubCategories()
    {
        SubCategories.Clear();
        foreach (var item in _allSubCategories)
            SubCategories.Add(item);

        RefreshSubCategoryListModeColumns();
        CurrentPage = 1;
        TotalPages = 1;
        IsEmpty = SubCategories.Count == 0;
        IsLoading = false;
        UpdateStatusMessage();
    }

    private static async Task LoadSubCategoryCoversAsync(IEnumerable<DesignerCategoryItemViewModel> items)
    {
        var tasks = items
            .Where(item => item.CoverImage == null)
            .Select(async item =>
            {
                await _coverSemaphore.WaitAsync();
                try { item.LoadCoverImage(); }
                finally { _coverSemaphore.Release(); }
            })
            .ToList();

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    private void RefreshSubCategoryListModeColumns()
    {
        SplitIntoColumns(SubCategories, SubCategoryListLeftColumn, SubCategoryListRightColumn);
    }

    private static void SplitIntoColumns<T>(
        IEnumerable<T> source,
        ObservableCollection<T> leftColumn,
        ObservableCollection<T> rightColumn)
    {
        leftColumn.Clear();
        rightColumn.Clear();

        var index = 0;
        foreach (var item in source)
        {
            if (index++ % 2 == 0)
                leftColumn.Add(item);
            else
                rightColumn.Add(item);
        }
    }

    [RelayCommand]
    private void ToggleListMode()
    {
        IsListMode = !IsListMode;
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
    private async Task OpenSubCategoryAsync(DesignerCategoryItemViewModel item)
    {
        if (item?.Category == null)
            return;

        _chartDownloadViewModel.StopPlayback();
        ReleaseResources();
        _parentVirtualCategory = Category?.IsVirtualGroup == true ? Category : null;
        await InitializeAsync(item.Category, string.Empty);
    }

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
                await mainWindow.ShowMessageBoxAsync("请先安装.net6环境！");
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
        return DotNetRuntimeHelper.IsDotNet6Installed();
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

                    chart.CustomCoverUrl = fetchUrl;
                    await ChartCoverSourceResolver.EnsureResolvedAsync(chart);
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
        if (_parentVirtualCategory != null)
        {
            var parent = _parentVirtualCategory;
            ReleaseResources();
            await InitializeAsync(parent, string.Empty);
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            Ioc.Default.GetRequiredService<ChartDownloadViewModel>().StopPlayback();
            var collectionVm = Ioc.Default.GetRequiredService<AlbumCollectionViewModel>();
            ReleaseResources();
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

    private async Task EnableAnimatedCoversDeferredAsync(IEnumerable<MdmcChart> pageCharts)
    {
        _gifPlaybackCts?.Cancel();
        _gifPlaybackCts?.Dispose();
        _gifPlaybackCts = new CancellationTokenSource();
        var ct = _gifPlaybackCts.Token;

        try
        {
            await Task.Delay(350, ct);
            foreach (var chart in pageCharts)
            {
                if (ct.IsCancellationRequested) break;

                if (!chart.HasAnimatedDisplayCoverSource)
                {
                    chart.IsAnimatedCoverPlaybackEnabled = true;
                    continue;
                }

                // GIF 封面: 先在后台下载到本地临时文件，再启用动画
                try
                {
                    var localSource = await ChartCoverSourceResolver.PrepareAnimatedSourceAsync(
                        chart.DisplayCoverSource, ct);

                    if (!string.IsNullOrWhiteSpace(localSource) && !ct.IsCancellationRequested)
                    {
                        // 先让 Image 可见，再设置源，这样库在可见状态下收到源变更才能正确启动动画
                        chart.IsAnimatedCoverPlaybackEnabled = true;
                        chart.ResolvedCoverSource = localSource;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Log(string message)
    {
        RuntimeLog.Write("AlbumDetailViewModel", message);
    }

}
