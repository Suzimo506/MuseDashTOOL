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
    private static readonly SemaphoreSlim _coverSemaphore = new(7);
    private bool _isInitialized;

    [ObservableProperty]
    private ObservableCollection<DesignerCategoryItemViewModel> _categories = new();

    [ObservableProperty]
    private ObservableCollection<CommunityCategoryItemViewModel> _communityCategories = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private double _scrollOffset;
    
    // ── 搜索功能 ────────────────────────────────────────────────────────────
    [ObservableProperty]
    private string _searchText = string.Empty;

    // 是否正在由于搜索而过滤
    [ObservableProperty]
    private bool _isSearching;

    // 原始集合备份，用于恢复
    private readonly List<DesignerCategoryItemViewModel> _allCategoriesBackup = new();
    private readonly List<CommunityCategoryItemViewModel> _allCommunityCategoriesBackup = new();

    // 搜索时的取消令牌
    private CancellationTokenSource? _searchCts;

    partial void OnSearchTextChanged(string value)
    {
        _ = SearchAndFilterAsync(value);
    }

    private async Task SearchAndFilterAsync(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        try
        {
            await Task.Delay(300, ct); // 300ms 防抖，避免频繁请求
            
            if (string.IsNullOrWhiteSpace(query))
            {
                IsSearching = false;
                RestoreOriginalCollections();
                return;
            }

            IsSearching = true;
            
            // 确保备份集合已初始化（防止搜索触发过早）
            if (_allCategoriesBackup.Count == 0 && Categories.Count > 0) _allCategoriesBackup.AddRange(Categories);
            if (_allCommunityCategoriesBackup.Count == 0 && CommunityCategories.Count > 0) _allCommunityCategoriesBackup.AddRange(CommunityCategories);

            var normalizedQuery = query.Trim().ToLowerInvariant();

            // 调用服务端搜索接口，获取所有匹配的 (分类, 谱面) 对
            var searchResults = await _collectionService.SearchChartsAsync(normalizedQuery);
            if (ct.IsCancellationRequested) return;

            // 提取所有匹配谱面所在的分类名称
            var matchingCategoryNames = searchResults
                .Select(r => r.Category.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 过滤逻辑：
            // 1. 分类本身的名称匹配搜索词
            // 2. 分类下的任何谱面匹配搜索词（通过 matchingCategoryNames 判断）
            var filteredCats = _allCategoriesBackup.Where(catVM => 
                catVM.Category.Name?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
                matchingCategoryNames.Contains(catVM.Category.Name ?? string.Empty)
            ).ToList();

            // 群友分类暂不支持深度搜索，仅按分类名过滤
            var filteredCommCats = _allCommunityCategoriesBackup.Where(catVM => 
                catVM.Name?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true
            ).ToList();

            if (ct.IsCancellationRequested) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Categories.Clear();
                foreach (var cat in filteredCats) Categories.Add(cat);
                
                CommunityCategories.Clear();
                foreach (var cat in filteredCommCats) CommunityCategories.Add(cat);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Write("AlbumCollectionVM", $"Search filtering failed: {ex.Message}");
        }
    }

    private void RestoreOriginalCollections()
    {
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
    }


    [RelayCommand]
    public void ClearSearch() => SearchText = string.Empty;

    // ── 试听与下载命令 (转发自 ChartDownloadViewModel) ───────────────────
    public IAsyncRelayCommand<MdmcChart> TogglePreviewCommand => 
        Ioc.Default.GetRequiredService<ChartDownloadViewModel>().TogglePreviewCommand;
    
    public IAsyncRelayCommand<MdmcChart> DownloadChartCommand => 
        Ioc.Default.GetRequiredService<ChartDownloadViewModel>().DownloadChartCommand;

    public AlbumCollectionViewModel(IAlbumCollectionService collectionService)
    {
        _collectionService = collectionService;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized && (Categories.Count > 0 || CommunityCategories.Count > 0))
        {
            IsLoading = false;
            IsEmpty = !Categories.Any();
            RuntimeLog.Write("AlbumCollectionVM", "Reuse cached album collection folders and covers.");
            return;
        }

        IsLoading = true;
        IsEmpty = false;
        ReleaseResources();
        Categories.Clear();
        CommunityCategories.Clear();

        var communityTasks = new List<Task>();
        var communityConfigs = new[] 
        { 
            ("通过审议", "https://github.com/KuoKing506/1_Csutom-Albums-Repository/releases"), 
            ("令人生草", "https://github.com/KuoKing506/2_Custom-Albums-Repository/releases"), 
            ("待定或存在小问题", "https://github.com/KuoKing506/3_Custom-Albums-Repository/releases") 
        };

        foreach (var (name, repoUrl) in communityConfigs)
        {
            var item = new CommunityCategoryItemViewModel(name, repoUrl);
            CommunityCategories.Add(item);
            
            communityTasks.Add(Task.Run(async () =>
            {
                await _coverSemaphore.WaitAsync();
                try
                {
                    item.LoadCoverImage();
                }
                finally
                {
                    _coverSemaphore.Release();
                }
            }));
        }

        var collections = await _collectionService.GetCollectionsAsync();
        
        var designerTasks = new List<Task>();
        foreach (var category in collections)
        {
            var item = new DesignerCategoryItemViewModel(category);
            Categories.Add(item);
            
            designerTasks.Add(Task.Run(async () =>
            {
                await _coverSemaphore.WaitAsync();
                try
                {
                    item.LoadCoverImage();
                }
                finally
                {
                    _coverSemaphore.Release();
                }
            }));
        }

        await Task.WhenAll(communityTasks.Concat(designerTasks));

        // 初始化备份集合，用于搜索过滤恢复
        _allCategoriesBackup.Clear();
        _allCategoriesBackup.AddRange(Categories);
        _allCommunityCategoriesBackup.Clear();
        _allCommunityCategoriesBackup.AddRange(CommunityCategories);

        IsEmpty = !Categories.Any();
        IsLoading = false;
        _isInitialized = true;
    }

    public void ReleaseResources()
    {
        foreach (var item in Categories)
            item.ReleaseResources();

        foreach (var item in CommunityCategories)
            item.ReleaseResources();

        Categories.Clear();
        CommunityCategories.Clear();
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
            await detailVm.InitializeAsync(item.Category, SearchText);
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
            await mainVm.NavigateToChartDownloadCommand.ExecuteAsync(null);
        }
    }
}
