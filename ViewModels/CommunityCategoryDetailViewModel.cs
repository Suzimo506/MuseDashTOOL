using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.Helpers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace MdModManager.ViewModels;

public partial class CommunityCategoryDetailViewModel : ObservableObject, IDisposable
{
    private readonly ChartDownloadViewModel _chartDownloadViewModel;
    private readonly IConfigService _configService;
    private readonly IDownloadManagerService _downloadManagerService;
    private readonly INotificationService _notificationService;
    private static readonly SemaphoreSlim _coverSemaphore = new(7);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    [ObservableProperty]
    private string _categoryName = string.Empty;

    [ObservableProperty]
    private string _repoUrl = string.Empty;

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

    [ObservableProperty]
    private string _statusMessage = string.Empty;

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
    private void ClearSearch() => SearchText = string.Empty;

    [ObservableProperty]
    private int _selectedSortIndex = 0;

    public bool IsSortByName => SelectedSortIndex == 0;
    public bool IsSortByLatest => SelectedSortIndex == 1;

    partial void OnSelectedSortIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSortByName));
        OnPropertyChanged(nameof(IsSortByLatest));
        CurrentPage = 1;
        _ = ReloadAsync();
    }

    [ObservableProperty]
    private bool _isAscending = false;

    [RelayCommand]
    private void ToggleSortOrder()
    {
        IsAscending = !IsAscending;
        CurrentPage = 1;
        _ = ReloadAsync();
    }

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

    public IAsyncRelayCommand<MdmcChart> TogglePreviewCommand => _chartDownloadViewModel.TogglePreviewCommand;
    public IAsyncRelayCommand<MdmcChart> DownloadChartCommand => _chartDownloadViewModel.DownloadChartCommand;
    public bool EnableMarquee => _chartDownloadViewModel.EnableMarquee;

    private readonly Dictionary<string, (IList<MdmcChart> charts, int totalPages)> _pageCache = new();
    private readonly List<string> _cacheKeys = new();
    private const int MaxCachedPages = 5;

    private List<MdmcChart> _allFullIndex = new();
    private List<MdmcChart> _filteredIndex = new();
    private List<string> _defaultReleaseTags = new();

    [ObservableProperty] private double? _requestedScrollY;

    public CommunityCategoryDetailViewModel(
        ChartDownloadViewModel chartDownloadViewModel,
        IConfigService configService,
        IDownloadManagerService downloadManagerService,
        INotificationService notificationService)
    {
        _chartDownloadViewModel = chartDownloadViewModel;
        _configService = configService;
        _downloadManagerService = downloadManagerService;
        _notificationService = notificationService;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MuseDashTOOL-CommunityDetail");
    }

    private void OnDownloadViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartDownloadViewModel.PreviewStatusText)) UpdateStatusMessage();
    }

    public async Task InitializeAsync(string categoryName, string repoUrl = "")
    {
        _chartDownloadViewModel.PropertyChanged -= OnDownloadViewModelPropertyChanged;
        _chartDownloadViewModel.PropertyChanged += OnDownloadViewModelPropertyChanged;

        CategoryName = categoryName;
        RepoUrl = repoUrl; 
        
        ClearPageCache();
        _allFullIndex.Clear();
        _filteredIndex.Clear();
        _defaultReleaseTags.Clear();
        CurrentPage = 1;
        SearchText = string.Empty;

        IsLoading = true;
        StatusMessage = "正在获取远程索引...";

        var collectionService = Ioc.Default.GetRequiredService<IAlbumCollectionService>();

        // 1. 优先加载本地缓存
        var localCharts = await collectionService.GetLocalCommunityChartsAsync(categoryName);
        if (localCharts.Count > 0)
        {
            _allFullIndex = localCharts;
            await ReloadAsync();
            IsLoading = false;
        }

        // 2. 后台并行拉取远端更新
        _ = Task.Run(async () =>
        {
            try
            {
                var remoteCharts = await collectionService.GetCommunityChartsAsync(categoryName, repoUrl);
                if (remoteCharts.Count > 0)
                {
                    bool needsUpdate = localCharts.Count == 0 || 
                                     remoteCharts.Count != localCharts.Count ||
                                     !remoteCharts.Select(c => c.Id).SequenceEqual(localCharts.Select(c => c.Id));

                    if (needsUpdate)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                        {
                            _allFullIndex = remoteCharts;
                            await ReloadAsync();
                            _notificationService.ShowSuccess("曲包检测到更新，已强制刷新界面~");
                            Log($"Remote update applied for '{categoryName}'.");
                        });
                    }
                }
            }
            catch (Exception ex) { Log($"Sync failed for '{categoryName}': {ex.Message}"); }
            finally { Avalonia.Threading.Dispatcher.UIThread.Post(() => { IsLoading = false; }); }
        });
    }

    private void Log(string msg) => RuntimeLog.Write("CommunityDetailVM", msg);

    private void UpdateStatusMessage()
    {
        var previewText = _chartDownloadViewModel.PreviewStatusText;
        if (!string.IsNullOrEmpty(previewText) && (previewText.Contains("正在缓冲") || previewText.Contains("正在播放")))
        {
            StatusMessage = previewText;
            return;
        }
        if (IsLoading) { StatusMessage = "正在载入..."; return; }
        if (IsEmpty) { StatusMessage = "未找到符合条件的谱面"; return; }
        StatusMessage = $"第 {CurrentPage} / {TotalPages} 页，共 {_filteredIndex.Count} 张谱面";
    }

    public void Dispose()
    {
        _chartDownloadViewModel.PropertyChanged -= OnDownloadViewModelPropertyChanged;
        _httpClient.Dispose();
    }

    private string GetCacheKey(int page, int sortIndex, bool ascending, string query)
        => $"{CategoryName}|{sortIndex}|{ascending}|{query.Trim()}|{page}";

    private void AddToCache(string key, (IList<MdmcChart> charts, int totalPages) result)
    {
        if (_pageCache.ContainsKey(key)) _cacheKeys.Remove(key);
        else if (_cacheKeys.Count >= MaxCachedPages)
        {
            var oldKey = _cacheKeys[0];
            _cacheKeys.RemoveAt(0);
            if (_pageCache.TryGetValue(oldKey, out var oldResult))
                foreach (var c in oldResult.charts) c.CoverImage = null;
            _pageCache.Remove(oldKey);
        }
        _pageCache[key] = result;
        _cacheKeys.Add(key);
    }

    private void ClearPageCache()
    {
        foreach (var kvp in _pageCache)
            foreach (var c in kvp.Value.charts) c.CoverImage = null;
        _pageCache.Clear();
        _cacheKeys.Clear();
    }

    private async Task ReloadAsync()
    {
        IsLoading = true;
        IsEmpty = false;
        UpdateStatusMessage();
        Charts.Clear();

        var query = SearchText.Trim().ToLowerInvariant();
        var cacheKey = GetCacheKey(CurrentPage, SelectedSortIndex, IsAscending, query);

        if (_pageCache.TryGetValue(cacheKey, out var cached))
        {
            TotalPages = Math.Max(1, cached.totalPages);
            foreach (var c in cached.charts) { c.SearchText = SearchText; Charts.Add(c); }
            _cacheKeys.Remove(cacheKey);
            _cacheKeys.Add(cacheKey);
            IsEmpty = Charts.Count == 0;
            IsLoading = false;
            UpdateStatusMessage();
            return;
        }

        _filteredIndex = _allFullIndex;
        if (!string.IsNullOrWhiteSpace(query))
        {
            _filteredIndex = _allFullIndex.Where(c => 
                c.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                c.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                c.Charter?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
            ).ToList();
        }

        if (IsSortByName)
        {
            _filteredIndex = IsAscending ? _filteredIndex.OrderBy(c => c.Title).ToList() : _filteredIndex.OrderByDescending(c => c.Title).ToList();
        }
        else if (IsSortByLatest)
        {
            _filteredIndex = IsAscending ? _filteredIndex.OrderBy(c => c.UploadedAt ?? DateTime.MinValue).ToList() : _filteredIndex.OrderByDescending(c => c.UploadedAt ?? DateTime.MinValue).ToList();
        }

        var pageSize = 12;
        var totalCount = _filteredIndex.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        if (CurrentPage < 1) CurrentPage = 1;

        var pageCharts = _filteredIndex.Skip((CurrentPage - 1) * pageSize).Take(pageSize).ToList();
        foreach (var c in pageCharts) { c.SearchText = SearchText; Charts.Add(c); }

        AddToCache(cacheKey, (pageCharts, TotalPages));
        IsEmpty = Charts.Count == 0;
        IsLoading = false;
        UpdateStatusMessage();
        RequestedScrollY = 0;

        _ = LoadCoversAsync(pageCharts);
    }

    private async Task LoadCoversAsync(IEnumerable<MdmcChart> pageCharts)
    {
        var tasks = pageCharts.Select(async chart =>
        {
            if (chart.HasDisplayCoverSource || string.IsNullOrEmpty(chart.CustomCoverUrl)) return;
            await _coverSemaphore.WaitAsync();
            try
            {
                chart.ResolvedCoverSource = chart.CustomCoverUrl;
            }
            catch { }
            finally { _coverSemaphore.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    [RelayCommand(CanExecute = nameof(CanLoadNext))]
    private async Task LoadNextPage() { if (CanLoadNext) { CurrentPage++; await ReloadAsync(); } }

    [RelayCommand(CanExecute = nameof(CanLoadPrev))]
    private async Task LoadPrevPage() { if (CanLoadPrev) { CurrentPage--; await ReloadAsync(); } }

    [RelayCommand(CanExecute = nameof(CanLoadPrev))]
    private async Task LoadFirstPageAsync() { CurrentPage = 1; await ReloadAsync(); }

    [RelayCommand(CanExecute = nameof(CanLoadNext))]
    private async Task LoadLastPageAsync() { CurrentPage = TotalPages; await ReloadAsync(); }

    [RelayCommand]
    private void StartEditPage() { JumpPageText = CurrentPage.ToString(); IsEditingPageNumber = true; }

    [RelayCommand]
    private void CancelEditPage() { JumpPageText = CurrentPage.ToString(); IsEditingPageNumber = false; }

    [RelayCommand]
    private async Task JumpPageAsync()
    {
        if (!IsEditingPageNumber) return;
        IsEditingPageNumber = false;
        if (int.TryParse(JumpPageText, out int targetPage)) { CurrentPage = Math.Clamp(targetPage, 1, TotalPages); await ReloadAsync(); }
    }

    [RelayCommand]
    private void GoBack()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            _chartDownloadViewModel.StopPlayback();
            mainVm.CurrentPage = Ioc.Default.GetRequiredService<AlbumCollectionViewModel>();
        }
    }
}
