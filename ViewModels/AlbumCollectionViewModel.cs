using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.Helpers;
using System.Globalization;

namespace MdModManager.ViewModels;

public partial class DesignerCategoryItemViewModel : ObservableObject
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    [ObservableProperty]
    private DesignerCategory _category;

    [ObservableProperty]
    private Bitmap? _coverImage;

    public DesignerCategoryItemViewModel(DesignerCategory category)
    {
        _category = category;
    }

    public void LoadCoverImage()
    {
        if (CoverImage != null)
            return;

        var picturesPath = FindPicturesPath();
        if (picturesPath == null)
        {
            Log($"Pictures directory not found for '{Category.Name}'. Trying embedded fallback...");
            TryLoadEmbeddedNormalCover();
            return;
        }

        var filePath = FindBestCoverPath(picturesPath, Category.Name);
        if (filePath == null)
        {
            Log($"No folder cover matched for '{Category.Name}' in '{picturesPath}'. Trying embedded fallback...");
            TryLoadEmbeddedNormalCover();
            return;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            CoverImage = new Bitmap(stream);
            Log($"Loaded folder cover for '{Category.Name}': '{filePath}'");
        }
        catch (Exception ex)
        {
            Log($"Failed to load folder cover for '{Category.Name}' from '{filePath}': {ex.Message}. Trying embedded fallback...");
            TryLoadEmbeddedNormalCover();
        }
    }

    private void TryLoadEmbeddedNormalCover()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "MdModManager.Pictures.Normal.jpg";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                CoverImage = new Bitmap(stream);
                Log($"Loaded embedded Normal cover for '{Category.Name}'");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to load embedded Normal cover: {ex.Message}");
        }
    }

    public void ReleaseResources()
    {
        CoverImage?.Dispose();
        CoverImage = null;
    }

    private static string? FindPicturesPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Pictures"),
            Path.Combine(Environment.CurrentDirectory, "Pictures")
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? FindBestCoverPath(string picturesPath, string categoryName)
    {
        var files = Directory.EnumerateFiles(picturesPath)
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0)
            return null;

        var exact = files.FirstOrDefault(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), categoryName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        var searchKeys = BuildSearchKeys(categoryName).ToList();
        var bestMatch = files
            .Select(path => new
            {
                Path = path,
                Score = CalculateMatchScore(Path.GetFileNameWithoutExtension(path), searchKeys)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path.Length)
            .FirstOrDefault();

        if (bestMatch != null)
            return bestMatch.Path;

        return files.FirstOrDefault(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), "Normal", StringComparison.OrdinalIgnoreCase));
    }

    private static int CalculateMatchScore(string fileStem, IReadOnlyCollection<string> searchKeys)
    {
        if (searchKeys.Count == 0)
            return 0;

        var normalizedStem = NormalizeName(fileStem);
        var score = 0;

        foreach (var key in searchKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (string.Equals(normalizedStem, key, StringComparison.OrdinalIgnoreCase))
                score = Math.Max(score, 1000 + key.Length);
            else if (normalizedStem.Contains(key, StringComparison.OrdinalIgnoreCase))
                score = Math.Max(score, 700 + key.Length);
            else if (key.Contains(normalizedStem, StringComparison.OrdinalIgnoreCase))
                score = Math.Max(score, 500 + normalizedStem.Length);
        }

        return score;
    }

    private static IEnumerable<string> BuildSearchKeys(string categoryName)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(categoryName);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var normalized = NormalizeName(current);
            if (!string.IsNullOrWhiteSpace(normalized))
                candidates.Add(normalized);

            foreach (var variant in TrimCommonTerms(current))
            {
                var normalizedVariant = NormalizeName(variant);
                if (!string.IsNullOrWhiteSpace(normalizedVariant) && candidates.Add(normalizedVariant))
                    queue.Enqueue(variant);
            }
        }

        return candidates;
    }

    private static IEnumerable<string> TrimCommonTerms(string value)
    {
        var variants = new[]
        {
            value.Replace("B站", "哔哩哔哩", StringComparison.OrdinalIgnoreCase),
            value.Replace("哔哩哔哩", "Bilibili", StringComparison.OrdinalIgnoreCase),
            value.Replace("《", string.Empty).Replace("》", string.Empty),
            value.Replace("（", "(").Replace("）", ")"),
            value.Replace("【曲包】", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("曲包", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("主题包", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("精选集", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("个人谱面仓库", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("专属谱面仓库", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("谱面仓库", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("仓库", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("Pack", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("Vol.1", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("Vol.2", string.Empty, StringComparison.OrdinalIgnoreCase),
            value.Replace("Vol.3", string.Empty, StringComparison.OrdinalIgnoreCase)
        };

        return variants.Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string NormalizeName(string value)
    {
        var normalized = value.ToLowerInvariant();
        normalized = normalized
            .Replace("b站", "哔哩哔哩", StringComparison.OrdinalIgnoreCase)
            .Replace("（", "(")
            .Replace("）", ")")
            .Replace("《", string.Empty)
            .Replace("》", string.Empty);

        return Regex.Replace(normalized, @"[\s\p{P}\p{S}_]+", string.Empty);
    }

    private static void Log(string message)
    {
        RuntimeLog.Write("AlbumCollectionVM", message);
    }

    internal static string? ResolvePicturesPath() => FindPicturesPath();
}

public partial class CommunityCategoryItemViewModel : ObservableObject
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    public string Name { get; }
    public string RepoUrl { get; }

    [ObservableProperty]
    private Bitmap? _coverImage;

    public CommunityCategoryItemViewModel(string name, string repoUrl)
    {
        Name = name;
        RepoUrl = repoUrl;
    }

    public void LoadCoverImage()
    {
        if (CoverImage != null)
            return;

        var picturesPath = DesignerCategoryItemViewModel.ResolvePicturesPath();
        if (picturesPath == null)
        {
            RuntimeLog.Write("AlbumCollectionVM", $"Pictures directory not found for community item '{Name}'. Trying embedded...");
            TryLoadEmbeddedResource();
            return;
        }

        var filePath = Extensions
            .Select(ext => Path.Combine(picturesPath, Name + ext))
            .FirstOrDefault(File.Exists);

        if (filePath == null)
        {
            RuntimeLog.Write("AlbumCollectionVM", $"No community cover matched for '{Name}' in '{picturesPath}'. Trying embedded...");
            TryLoadEmbeddedResource();
            return;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            CoverImage = new Bitmap(stream);
            RuntimeLog.Write("AlbumCollectionVM", $"Loaded community cover for '{Name}': '{filePath}'");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("AlbumCollectionVM", $"Failed to load community cover for '{Name}' from '{filePath}': {ex.Message}. Trying embedded...");
            TryLoadEmbeddedResource();
        }
    }

    private void TryLoadEmbeddedResource()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            // Try specific resource name based on community category name
            var ext = Name == "令人生草" ? ".jpg" : ".png";
            var resourceName = $"MdModManager.Pictures.{Name}{ext}";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                CoverImage = new Bitmap(stream);
                RuntimeLog.Write("AlbumCollectionVM", $"Loaded embedded community cover for {Name}");
            }
            else
            {
                // Fallback to Normal.jpg if specific not found
                var fallbackName = "MdModManager.Pictures.Normal.jpg";
                using var fallbackStream = assembly.GetManifestResourceStream(fallbackName);
                if (fallbackStream != null)
                {
                    CoverImage = new Bitmap(fallbackStream);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("AlbumCollectionVM", $"Failed to load embedded community cover: {ex.Message}");
        }
    }

    public void ReleaseResources()
    {
        CoverImage?.Dispose();
        CoverImage = null;
    }
}

public partial class AlbumCollectionViewModel : ObservableObject
{
    private readonly IAlbumCollectionService _collectionService;
    private readonly ChartDownloadViewModel _chartDownloadViewModel;
    private static readonly SemaphoreSlim _coverSemaphore = new(7);
    private readonly List<DesignerCategoryItemViewModel> _allCategoriesBackup = new();
    private readonly List<CommunityCategoryItemViewModel> _allCommunityCategoriesBackup = new();
    private bool _isInitialized;
    private bool _isSyncing;

    [ObservableProperty] private ObservableCollection<DesignerCategoryItemViewModel> _categories = new();
    [ObservableProperty] private ObservableCollection<CommunityCategoryItemViewModel> _communityCategories = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private double _scrollOffset;
    [ObservableProperty] private ObservableCollection<MdmcChart> _searchResults = new();

    public bool HasSearchResults => SearchResults.Count > 0;

    // 分页与内存管理
    private List<MdmcChart> _allSearchItemsBackup = new();
    private readonly List<int> _loadedPages = new();
    private const int PageSize = 12;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private int _totalPages = 1;

    [ObservableProperty] private string _jumpPageText = string.Empty;
    [ObservableProperty] private bool _isEditingPageNumber = false;
    [ObservableProperty] private double? _requestedSearchScrollY;

    partial void OnCurrentPageChanged(int value)
    {
        JumpPageText = value.ToString();
    }

    public bool CanLoadNext => CurrentPage < TotalPages && !IsLoading;
    public bool CanLoadPrev => CurrentPage > 1 && !IsLoading;

    [RelayCommand]
    private async Task LoadNextPage()
    {
        if (CanLoadNext) await LoadPageAsync(CurrentPage + 1);
    }

    [RelayCommand]
    private async Task LoadPrevPage()
    {
        if (CanLoadPrev) await LoadPageAsync(CurrentPage - 1);
    }

    [RelayCommand]
    private async Task LoadFirstPage() => await LoadPageAsync(1);

    [RelayCommand]
    private async Task LoadLastPage() => await LoadPageAsync(TotalPages);

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
    private async Task JumpPage()
    {
        if (!IsEditingPageNumber) return;
        var text = JumpPageText;
        IsEditingPageNumber = false;
        if (int.TryParse(text, out int targetPage))
        {
            targetPage = Math.Clamp(targetPage, 1, TotalPages);
            await LoadPageAsync(targetPage);
        }
    }

    private async Task LoadPageAsync(int page)
    {
        CurrentPage = page;

        // LRU 页面缓存管理
        if (!_loadedPages.Contains(page))
        {
            _loadedPages.Add(page);
        }
        else
        {
            _loadedPages.Remove(page);
            _loadedPages.Add(page);
        }

        if (_loadedPages.Count > 5)
        {
            int oldestPage = _loadedPages[0];
            _loadedPages.RemoveAt(0);

            // 清理旧页面覆盖图片
            var oldItems = _allSearchItemsBackup.Skip((oldestPage - 1) * PageSize).Take(PageSize);
            foreach (var item in oldItems)
            {
                item.CoverImage = null;
            }
            RuntimeLog.Write("AlbumCollectionVM", $"Evicted page {oldestPage} covers from memory.");
        }

        var pageItems = _allSearchItemsBackup.Skip((page - 1) * PageSize).Take(PageSize).ToList();

        RequestedSearchScrollY = 0;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SearchResults.Clear();
            foreach (var chart in pageItems)
            {
                SearchResults.Add(chart);
            }
            OnPropertyChanged(nameof(HasSearchResults));
            UpdateStatusMessage();

            // 异步加载该页面的封面
            _ = LoadSearchResultsCoversAsync(pageItems);
        });
    }

    [ObservableProperty]
    private string _searchText = string.Empty;

    // 是否正在由于搜索而过滤
    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // 搜索时的取消令牌
    private CancellationTokenSource? _searchCts;

    partial void OnSearchTextChanged(string value)
    {
        _ = SearchAndFilterAsync(value);
    }

    public AlbumCollectionViewModel(IAlbumCollectionService collectionService)
    {
        _collectionService = collectionService;
        _chartDownloadViewModel = Ioc.Default.GetRequiredService<ChartDownloadViewModel>();
    }

    private void OnDownloadViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartDownloadViewModel.PreviewStatusText))
        {
            UpdateStatusMessage();
        }
    }

    private int _lastChartCount = 0;
    private void UpdateStatusMessage()
    {
        // 只有在正在缓冲或正在播放时，才优先显示试听状态
        var previewText = _chartDownloadViewModel.PreviewStatusText;
        if (!string.IsNullOrEmpty(previewText) && (previewText.Contains("正在缓冲") || previewText.Contains("正在播放")))
        {
            StatusMessage = previewText;
            return;
        }

        if (IsSearching)
        {
            StatusMessage = $"匹配 {_lastChartCount} 个搜索结果";
            return;
        }

        StatusMessage = string.Empty;
    }

    private async Task SearchAndFilterAsync(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        try
        {
            await Task.Delay(300, ct); // 300ms 防抖
            
            _chartDownloadViewModel.StopPlayback();
            
            if (string.IsNullOrWhiteSpace(query))
            {
                IsSearching = false;
                RestoreOriginalCollections();
                return;
            }

            IsSearching = true;
            
            if (_allCategoriesBackup.Count == 0 && Categories.Count > 0) _allCategoriesBackup.AddRange(Categories);
            if (_allCommunityCategoriesBackup.Count == 0 && CommunityCategories.Count > 0) _allCommunityCategoriesBackup.AddRange(CommunityCategories);

            var normalizedQuery = query.Trim().ToLowerInvariant();

            // 1. 搜索设计师谱面
            var designerResults = await _collectionService.SearchChartsAsync(normalizedQuery);
            if (ct.IsCancellationRequested) return;

            // 2. 搜索群友自制谱面
            var communitySearchResults = await _collectionService.SearchCommunityChartsAsync(normalizedQuery);
            if (ct.IsCancellationRequested) return;

            // 3. 构建统一的谱面搜索结果列表
            var allChartResults = new List<MdmcChart>();
            
            // 映射设计师谱面
            foreach (var (cat, chart) in designerResults)
            {
                allChartResults.Add(new MdmcChart
                {
                    Id = chart.Id,
                    Title = chart.Title,
                    Artist = chart.Artist,
                    Charter = chart.Author,
                    Bpm = chart.Bpm,
                    CustomCoverUrl = chart.CoverUrl,
                    CustomDemoUrl = chart.DemoUrl,
                    CustomDemoMp3Url = chart.DemoMp3Url,
                    CustomDownloadUrl = chart.DownloadUrl,
                    Sheets = ExtractDifficultySheets(chart),
                    SearchText = query // 用于粉色高亮
                });
            }

            // 添加群友谱面
            foreach (var (catName, chart) in communitySearchResults)
            {
                chart.SearchText = query; // 用于粉色高亮
                allChartResults.Add(chart);
            }

            // 4. 计算匹配的分类，用于保留文件夹显示
            var matchingDesignerCategoryNames = designerResults.Select(r => r.Category.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matchingCommunityCategoryNames = communitySearchResults.Select(r => r.CategoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var filteredCats = _allCategoriesBackup.Where(catVM => 
                catVM.Category.Name?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                matchingDesignerCategoryNames.Contains(catVM.Category.Name ?? string.Empty)
            ).ToList();

            var filteredCommCats = _allCommunityCategoriesBackup.Where(catVM => 
                catVM.Name?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                matchingCommunityCategoryNames.Contains(catVM.Name ?? string.Empty)
            ).ToList();

            if (ct.IsCancellationRequested) return;

            _lastChartCount = allChartResults.Count;
            _allSearchItemsBackup = allChartResults;
            TotalPages = Math.Max(1, (int)Math.Ceiling((double)_allSearchItemsBackup.Count / PageSize));
            _loadedPages.Clear();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Categories.Clear();
                foreach (var cat in filteredCats) Categories.Add(cat);
                
                CommunityCategories.Clear();
                foreach (var cat in filteredCommCats) CommunityCategories.Add(cat);
            });

            await LoadPageAsync(1);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Write("AlbumCollectionVM", $"Search filtering failed: {ex.Message}");
        }
    }

    private async Task LoadSearchResultsCoversAsync(List<MdmcChart> charts)
    {
        var configService = Ioc.Default.GetRequiredService<IConfigService>();
        var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        
        var tasks = charts.Select(async chart =>
        {
            if (chart.CoverImage != null || string.IsNullOrEmpty(chart.CoverUrl)) return;
            await _coverSemaphore.WaitAsync();
            try
            {
                var bytes = await httpClient.GetByteArrayAsync(chart.CoverUrl);
                using var stream = new MemoryStream(bytes);
                chart.CoverImage = new Bitmap(stream);
            }
            catch { }
            finally { _coverSemaphore.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private void RestoreOriginalCollections()
    {
        _chartDownloadViewModel.StopPlayback();
        foreach (var item in _allSearchItemsBackup)
        {
            item.CoverImage = null;
        }
        _allSearchItemsBackup.Clear();
        _loadedPages.Clear();
        CurrentPage = 1;
        TotalPages = 1;

        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));

        if (_allCategoriesBackup.Count > 0)
        {
            Categories.Clear();
            foreach (var cat in _allCategoriesBackup) Categories.Add(cat);
        }
        if (_allCommunityCategoriesBackup.Count > 0)
        {
            CommunityCategories.Clear();
            foreach (var cat in _allCommunityCategoriesBackup) CommunityCategories.Add(cat);
        }
        UpdateStatusMessage();
    }


    [RelayCommand]
    public void ClearSearch() => SearchText = string.Empty;

    // ── 试听与下载命令 (转发自 ChartDownloadViewModel) ───────────────────
    public IAsyncRelayCommand<MdmcChart> TogglePreviewCommand => 
        Ioc.Default.GetRequiredService<ChartDownloadViewModel>().TogglePreviewCommand;
    
    public IAsyncRelayCommand<MdmcChart> DownloadChartCommand => 
        Ioc.Default.GetRequiredService<ChartDownloadViewModel>().DownloadChartCommand;


    public async Task InitializeAsync()
    {
        if (_isSyncing) return;
        _isSyncing = true;

        if (_isInitialized && (Categories.Count > 0 || CommunityCategories.Count > 0))
        {
            IsLoading = false;
            IsEmpty = !Categories.Any();
            _isSyncing = false;
            return;
        }

        IsLoading = true;
        IsEmpty = false;
        
        ReleaseResources();
        _chartDownloadViewModel.PropertyChanged += OnDownloadViewModelPropertyChanged;

        // 1. 优先加载本地缓存数据 (如果有的话)
        var collections = await _collectionService.GetLocalCollectionsAsync();
        await LoadCategoriesAsync(collections);
        
        if (Categories.Count > 0)
        {
            IsLoading = false; // 有本地数据先展示
        }

        // 2. 后台静默刷新总列表
        _ = Task.Run(async () =>
        {
            try
            {
                // 注意：GetCollectionsAsync 内部会尝试去拉取远端
                var remoteCollections = await _collectionService.GetCollectionsAsync();
                
                // 简单比对数量，如果发生变化则刷新
                if (remoteCollections.Count != collections.Count || !remoteCollections.Select(c => c.Name).SequenceEqual(collections.Select(c => c.Name)))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                    {
                        await LoadCategoriesAsync(remoteCollections);
                        Log("Album collection list updated from remote.");
                    });
                }
            }
            catch (Exception ex) { Log($"Failed to sync folder list in background: {ex.Message}"); }
            finally 
            { 
                _isSyncing = false;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { IsLoading = false; _isInitialized = true; }); 
            }
        });
    }

    private async Task LoadCategoriesAsync(List<DesignerCategory> collections)
    {
        Categories.Clear();
        CommunityCategories.Clear();

        // 这里的社区分类配置应该保持一致
        var communityConfigs = new[] 
        { 
            ("通过审议", "https://download.suzimo.site/通过审议"), 
            ("令人生草", "https://download.suzimo.site/令人生草"), 
            ("待定或有些小问题", "https://download.suzimo.site/待定或有些小问题")
        };

        var communityTasks = communityConfigs.Select(config => 
        {
            var item = new CommunityCategoryItemViewModel(config.Item1, config.Item2);
            CommunityCategories.Add(item);
            return Task.Run(async () => {
                await _coverSemaphore.WaitAsync();
                try { item.LoadCoverImage(); } finally { _coverSemaphore.Release(); }
            });
        }).ToList();

        var designerTasks = collections.Select(category => 
        {
            var item = new DesignerCategoryItemViewModel(category);
            Categories.Add(item);
            return Task.Run(async () => {
                await _coverSemaphore.WaitAsync();
                try { item.LoadCoverImage(); } finally { _coverSemaphore.Release(); }
            });
        }).ToList();

        await Task.WhenAll(communityTasks.Concat(designerTasks));

        _allCategoriesBackup.Clear();
        _allCategoriesBackup.AddRange(Categories);
        _allCommunityCategoriesBackup.Clear();
        _allCommunityCategoriesBackup.AddRange(CommunityCategories);

        IsEmpty = !Categories.Any();
    }

    private void Log(string msg) => RuntimeLog.Write("AlbumCollectionVM", msg);

    public void ReleaseResources()
    {
        foreach (var item in Categories)
            item.ReleaseResources();

        foreach (var item in CommunityCategories)
            item.ReleaseResources();

        foreach (var item in _allSearchItemsBackup)
            item.CoverImage = null;
        
        _allSearchItemsBackup.Clear();
        _loadedPages.Clear();
        CurrentPage = 1;
        TotalPages = 1;

        Categories.Clear();
        CommunityCategories.Clear();
        ClearSearch(); // 释放搜索结果内存并清空文本框
        _chartDownloadViewModel.PropertyChanged -= OnDownloadViewModelPropertyChanged;
        _isInitialized = false;
        IsLoading = false;
        IsEmpty = false;
        ScrollOffset = 0; // 同时也重置位置缓存
        RuntimeLog.Write("AlbumCollectionVM", "Released album collection folder and scroll position cache.");
    }

    [RelayCommand]
    private async Task OpenCategoryAsync(DesignerCategoryItemViewModel item)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            Ioc.Default.GetRequiredService<ChartDownloadViewModel>().StopPlayback();
            var detailVm = Ioc.Default.GetRequiredService<AlbumDetailViewModel>();
            mainVm.CurrentPage = detailVm;
            // 不传递 SearchText，避免用户搜索“整合包名称”进入后，因为谱面不包含该名称而导致列表为空
            await detailVm.InitializeAsync(item.Category, string.Empty);
        }
    }

    [RelayCommand]
    private async Task OpenCommunityCategoryAsync(CommunityCategoryItemViewModel item)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            Ioc.Default.GetRequiredService<ChartDownloadViewModel>().StopPlayback();
            var detailVm = Ioc.Default.GetRequiredService<CommunityCategoryDetailViewModel>();
            mainVm.CurrentPage = detailVm;
            await detailVm.InitializeAsync(item.Name, item.RepoUrl);
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            ReleaseResources();
            await mainVm.NavigateToChartDownloadCommand.ExecuteAsync(null);
        }
    }

    private static List<MdmcSheet> ExtractDifficultySheets(DesignerChart chart)
    {
        var labels = new List<string>();
        if (chart.Difficulties != null && chart.Difficulties.Count > 0)
        {
            foreach (var d in chart.Difficulties)
            {
                if (!string.IsNullOrEmpty(d))
                {
                    labels.AddRange(d.Split(new char[] { ',', '，', ' ', '/', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                     .Where(x => !string.IsNullOrWhiteSpace(x)));
                }
            }
        }

        if (labels.Count == 0 && !string.IsNullOrWhiteSpace(chart.DownloadUrl))
        {
            var urlDecoded = System.Net.WebUtility.UrlDecode(chart.DownloadUrl);
            var match = System.Text.RegularExpressions.Regex.Match(
                urlDecoded,
                @"\[Lv\.(.*?)\]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                labels.AddRange(match.Groups[1].Value
                    .Split(new char[] { ',', '，', ' ', '/', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            }
        }
        
        if (labels.Count == 0)
            labels.Add("?");
            
        return labels.Select(l => new MdmcSheet { Difficulty = l }).ToList();
    }
}
