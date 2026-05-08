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
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace MdModManager.ViewModels;

public partial class DesignerCategoryItemViewModel : ObservableObject
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];
    private const int CoverDecodeWidth = 360;

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
            CoverImage = DecodeCoverBitmap(stream);
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
                CoverImage = DecodeCoverBitmap(stream);
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

    internal static Bitmap DecodeCoverBitmap(Stream stream)
    {
        return Bitmap.DecodeToWidth(stream, CoverDecodeWidth, BitmapInterpolationMode.MediumQuality);
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
            CoverImage = DesignerCategoryItemViewModel.DecodeCoverBitmap(stream);
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
                CoverImage = DesignerCategoryItemViewModel.DecodeCoverBitmap(stream);
                RuntimeLog.Write("AlbumCollectionVM", $"Loaded embedded community cover for {Name}");
            }
            else
            {
                // Fallback to Normal.jpg if specific not found
                var fallbackName = "MdModManager.Pictures.Normal.jpg";
                using var fallbackStream = assembly.GetManifestResourceStream(fallbackName);
                if (fallbackStream != null)
                {
                    CoverImage = DesignerCategoryItemViewModel.DecodeCoverBitmap(fallbackStream);
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
    private readonly INotificationService _notificationService;
    private readonly IConfigService _configService;
    private static readonly SemaphoreSlim _coverSemaphore = new(7);
    private readonly SemaphoreSlim _lazyCoverLoadSemaphore = new(1, 1);
    private readonly List<DesignerCategoryItemViewModel> _allCategoriesBackup = new();
    private readonly List<DesignerCategoryItemViewModel> _allPersonalRepositoryCategoriesBackup = new();
    private readonly List<CommunityCategoryItemViewModel> _allCommunityCategoriesBackup = new();
    private bool _isInitialized;
    private bool _isSyncing;

    [ObservableProperty] private ObservableCollection<DesignerCategoryItemViewModel> _categories = new();
    [ObservableProperty] private ObservableCollection<DesignerCategoryItemViewModel> _personalRepositoryCategories = new();
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

    public Task EnsureCategoryCoversLoadedAsync(IEnumerable<DesignerCategoryItemViewModel> items)
        => EnsureCoverItemsLoadedCoreAsync(items.Cast<object>().ToList());

    public Task EnsureCommunityCoversLoadedAsync(IEnumerable<CommunityCategoryItemViewModel> items)
        => EnsureCoverItemsLoadedCoreAsync(items.Cast<object>().ToList());

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

    private async Task EnsureCoverItemsLoadedCoreAsync(IReadOnlyList<object> items)
    {
        if (items.Count == 0)
            return;

        if (!await _lazyCoverLoadSemaphore.WaitAsync(0))
            return;

        try
        {
            var tasks = new List<Task>();
            foreach (var item in items)
            {
                switch (item)
                {
                    case DesignerCategoryItemViewModel designer when designer.CoverImage == null:
                        tasks.Add(Task.Run(async () =>
                        {
                            await _coverSemaphore.WaitAsync();
                            try { designer.LoadCoverImage(); } finally { _coverSemaphore.Release(); }
                        }));
                        break;
                    case CommunityCategoryItemViewModel community when community.CoverImage == null:
                        tasks.Add(Task.Run(async () =>
                        {
                            await _coverSemaphore.WaitAsync();
                            try { community.LoadCoverImage(); } finally { _coverSemaphore.Release(); }
                        }));
                        break;
                }
            }

            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }
        finally
        {
            _lazyCoverLoadSemaphore.Release();
        }
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

    public AlbumCollectionViewModel(IAlbumCollectionService collectionService, INotificationService notificationService, IConfigService configService)
    {
        _collectionService = collectionService;
        _notificationService = notificationService;
        _configService = configService;
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
            if (_allPersonalRepositoryCategoriesBackup.Count == 0 && PersonalRepositoryCategories.Count > 0) _allPersonalRepositoryCategoriesBackup.AddRange(PersonalRepositoryCategories);
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
                    SourceCategoryName = cat.Name,
                    IsCommunitySource = false,
                    Sheets = ExtractDifficultySheets(chart),
                    SearchText = query // 用于粉色高亮
                });
            }

            // 添加群友谱面
            foreach (var (catName, chart) in communitySearchResults)
            {
                chart.SearchText = query; // 用于粉色高亮
                chart.SourceCategoryName = catName;
                chart.IsCommunitySource = true;
                allChartResults.Add(chart);
            }

            // 4. 计算匹配的分类，用于保留文件夹显示
            var matchingDesignerCategoryNames = designerResults.Select(r => r.Category.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matchingCommunityCategoryNames = communitySearchResults.Select(r => r.CategoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var filteredCats = _allCategoriesBackup.Where(catVM => 
                catVM.Category.Name?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                matchingDesignerCategoryNames.Contains(catVM.Category.Name ?? string.Empty)
            ).ToList();

            var filteredPersonalRepoCats = _allPersonalRepositoryCategoriesBackup.Where(catVM =>
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

                PersonalRepositoryCategories.Clear();
                foreach (var cat in filteredPersonalRepoCats) PersonalRepositoryCategories.Add(cat);
                
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
        if (_allPersonalRepositoryCategoriesBackup.Count > 0)
        {
            PersonalRepositoryCategories.Clear();
            foreach (var cat in _allPersonalRepositoryCategoriesBackup) PersonalRepositoryCategories.Add(cat);
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

    [RelayCommand]
    private async Task DownloadPictures()
    {
        try
        {
            var targetDir = Path.Combine(AppContext.BaseDirectory, "Pictures");
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";
            var infoDomain = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.AlbumInfoDomain) 
                ? MirrorDomainRegistry.AlbumInfoDomain 
                : $"workerdl.{baseHost}";
            var downloadDomain = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.AlbumDownloadDomain) 
                ? MirrorDomainRegistry.AlbumDownloadDomain 
                : $"download.{baseHost}";

            using var client = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(30));
            // 清除默认的 mdmc 引用来源，设置为自己的域名，防止 CDN 防盗链拦截
            client.DefaultRequestHeaders.Referrer = new Uri($"https://{baseHost}/");

            // 1. 获取远端 Pictures 目录下的所有文件清单
            var listUrl = $"https://{infoDomain}/api/list_pictures";
            var remoteFiles = new List<string>();
            try
            {
                var json = await client.GetStringAsync(listUrl);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("files", out var filesArray))
                {
                    foreach (var item in filesArray.EnumerateArray())
                    {
                        var fileName = item.GetString();
                        if (!string.IsNullOrEmpty(fileName)) remoteFiles.Add(fileName);
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("DownloadPictures", $"获取远端清单失败: {ex.Message}");
                _notificationService.ShowFailure("同步失败", "无法获取远端文件清单，请检查网络或 Worker 配置。");
                return;
            }

            if (remoteFiles.Count == 0)
            {
                _notificationService.ShowSuccess("远端仓库目前没有任何封面。");
                return;
            }

            // 2. 检查本地已经存在的文件 (全名匹配)
            var localFiles = new HashSet<string>(
                Directory.GetFiles(targetDir).Select(f => Path.GetFileName(f) ?? string.Empty), 
                StringComparer.OrdinalIgnoreCase);

            // 3. 找出本地缺失的远端文件
            var missingFiles = remoteFiles.Where(f => !localFiles.Contains(f)).ToList();
            
            if (missingFiles.Count == 0)
            {
                _notificationService.ShowSuccess("所有封面均已同步至最新，无需更新！");
                return;
            }

            var notification = _notificationService.ShowPersistentProgress($"准备同步 {missingFiles.Count} 个新封面...");
            
            int successCount = 0;
            int failCount = 0;

            for (int i = 0; i < missingFiles.Count; i++)
            {
                var fileName = missingFiles[i];
                // 确保在主线程更新 UI
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    notification.ProgressValue = (double)i / missingFiles.Count * 100;
                    notification.Message = $"正在同步封面: {fileName} ({i + 1}/{missingFiles.Count})";
                });

                var encodedName = Uri.EscapeDataString(fileName);
                var url = $"https://{downloadDomain}/Pictures/{encodedName}";
                
                if (_configService.Config.DownloadSource != "suzimo")
                {
                    url = GitHubMirrorHelper.ApplyMirror(url, _configService.Config.DownloadSource);
                }

                try
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (response.IsSuccessStatusCode)
                    {
                        var targetPath = Path.Combine(targetDir, fileName);
                        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        await response.Content.CopyToAsync(fs);
                        successCount++;
                    }
                    else
                    {
                        failCount++;
                        RuntimeLog.Write("DownloadPictures", $"[FAILURE] 无法下载: {fileName} (HTTP {response.StatusCode})");
                    }
                }
                catch (Exception ex)
                {
                    failCount++;
                    RuntimeLog.Write("DownloadPictures", $"[ERROR] 下载异常 {fileName}: {ex.Message}");
                }
            }

            _notificationService.RemoveNotification(notification);

            if (failCount > 0)
            {
                _notificationService.ShowInfo($"同步完成！成功 {successCount} 个，失败 {failCount} 个。", 3000);
            }
            else
            {
                _notificationService.ShowSuccess($"同步完成！成功下载 {successCount} 个新封面。");
            }

            // 4. 刷新封面 (先释放旧资源，再重新加载)
            foreach (var cat in Categories) { cat.ReleaseResources(); cat.LoadCoverImage(); }
            foreach (var cat in PersonalRepositoryCategories) { cat.ReleaseResources(); cat.LoadCoverImage(); }
            foreach (var cat in CommunityCategories) { cat.ReleaseResources(); cat.LoadCoverImage(); }
        }
        catch (Exception ex)
        {
            _notificationService.ShowFailure("操作失败", ex.Message);
        }
    }


    // ── 试听与下载命令 (转发自 ChartDownloadViewModel) ───────────────────
    public IAsyncRelayCommand<MdmcChart> TogglePreviewCommand => 
        Ioc.Default.GetRequiredService<ChartDownloadViewModel>().TogglePreviewCommand;
    
    public IAsyncRelayCommand<MdmcChart> DownloadChartCommand => 
        Ioc.Default.GetRequiredService<ChartDownloadViewModel>().DownloadChartCommand;


    public async Task InitializeAsync()
    {
        if (_isSyncing) return;
        _isSyncing = true;

        if (_isInitialized && (Categories.Count > 0 || PersonalRepositoryCategories.Count > 0 || CommunityCategories.Count > 0))
        {
            IsLoading = false;
            IsEmpty = !Categories.Any() && !PersonalRepositoryCategories.Any() && !CommunityCategories.Any();
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

        // 2. 后台静默全量刷新
        _ = Task.Run(async () =>
        {
            try
            {
                // 首先同步总目录表
                var remoteCollections = await _collectionService.GetCollectionsAsync();
                
                // 比对并刷新目录显示
                var remoteNames = remoteCollections.Select(c => c.Name).ToList();
                var localNames = collections.Select(c => c.Name).ToList();
                if (remoteNames.Count != localNames.Count || !remoteNames.SequenceEqual(localNames))
                {
                    var addedNames = remoteNames.Except(localNames).ToList();
                    string updateMsg = addedNames.Any() 
                        ? $"有内容更新了！《{string.Join("》、《", addedNames)}》"
                        : "曲包目录检测到更新，已自动刷新~";

                    Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                    {
                        await LoadCategoriesAsync(remoteCollections);
                        _notificationService?.ShowSuccess(updateMsg);
                    });
                }

                // 核心优化：全量预取所有曲包的歌曲索引 (Index Sync)
                // 这样进入具体文件夹就是瞬开，且全局搜索速度极快且数据最新
                var syncTasks = remoteCollections.Select(async col => 
                {
                    try
                    {
                        var charts = await _collectionService.GetChartsAsync(col.Name);
                        return (Category: col, HasCharts: charts.Count > 0);
                    }
                    catch
                    {
                        return (Category: col, HasCharts: true);
                    }
                }).ToList();

                // 同时同步社区曲包
                var communityTasks = AlbumCollectionService.CommunityConfigs.Select(async config =>
                {
                    try { await _collectionService.GetCommunityChartsAsync(config.Name, config.RepoUrl); } catch { }
                });

                var syncResults = await Task.WhenAll(syncTasks);
                var survivingCollections = syncResults
                    .Where(x => x.HasCharts)
                    .Select(x => x.Category)
                    .ToList();

                if (survivingCollections.Count != remoteCollections.Count)
                {
                    await _collectionService.SaveCollectionsCacheAsync(survivingCollections);

                    Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                    {
                        await LoadCategoriesAsync(survivingCollections);
                        _notificationService?.ShowSuccess("检测到远端有曲包已删除，目录已自动同步。");
                    });
                }

                await Task.WhenAll(communityTasks);
                Log("Full background sync completed: All indexes are now up-to-date locally.");
            }
            catch (Exception ex) { Log($"Failed to sync collections in background: {ex.Message}"); }
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
        PersonalRepositoryCategories.Clear();
        CommunityCategories.Clear();

        var baseHost = !string.IsNullOrWhiteSpace(MirrorDomainRegistry.SuzimoHost) ? MirrorDomainRegistry.SuzimoHost : "suzimo.site";
        var communityConfigs = new[] 
        { 
            ("通过审议", $"https://download.{baseHost}/通过审议"), 
            ("令人生草", $"https://download.{baseHost}/令人生草"), 
            ("待定或有些小问题", $"https://download.{baseHost}/待定或有些小问题")
        };

        foreach (var config in communityConfigs)
        {
            CommunityCategories.Add(new CommunityCategoryItemViewModel(config.Item1, config.Item2));
        }

        foreach (var category in collections)
        {
            var item = new DesignerCategoryItemViewModel(category);
            if (AlbumCollectionService.IsPersonalRepositoryName(category.Name))
            {
                PersonalRepositoryCategories.Add(item);
            }
            else
            {
                Categories.Add(item);
            }
        }

        _allCategoriesBackup.Clear();
        _allCategoriesBackup.AddRange(Categories);
        _allPersonalRepositoryCategoriesBackup.Clear();
        _allPersonalRepositoryCategoriesBackup.AddRange(PersonalRepositoryCategories);
        _allCommunityCategoriesBackup.Clear();
        _allCommunityCategoriesBackup.AddRange(CommunityCategories);

        IsEmpty = !Categories.Any() && !PersonalRepositoryCategories.Any() && !CommunityCategories.Any();

        await EnsureCategoryCoversLoadedAsync(Categories.Take(6));
        await EnsureCategoryCoversLoadedAsync(PersonalRepositoryCategories.Take(3));
        await EnsureCommunityCoversLoadedAsync(CommunityCategories.Take(3));
    }

    private void Log(string msg) => RuntimeLog.Write("AlbumCollectionVM", msg);

    public void ReleaseResources()
    {
        foreach (var item in Categories)
            item.ReleaseResources();

        foreach (var item in PersonalRepositoryCategories)
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
        PersonalRepositoryCategories.Clear();
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
    private async Task OpenSearchResultCategoryAsync(MdmcChart chart)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.SourceCategoryName))
            return;

        if (chart.IsCommunitySource)
        {
            var targetCommunityCategory = _allCommunityCategoriesBackup
                .FirstOrDefault(x => string.Equals(x.Name, chart.SourceCategoryName, StringComparison.OrdinalIgnoreCase));

            if (targetCommunityCategory != null)
            {
                await OpenCommunityCategoryAsync(targetCommunityCategory);
            }

            return;
        }

        var targetCategory = _allCategoriesBackup
            .Concat(_allPersonalRepositoryCategoriesBackup)
            .FirstOrDefault(x => string.Equals(x.Category.Name, chart.SourceCategoryName, StringComparison.OrdinalIgnoreCase));

        if (targetCategory == null)
            return;

        await OpenCategoryAsync(targetCategory);
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
