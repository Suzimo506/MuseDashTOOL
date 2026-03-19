using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Models;
using MdModManager.Services;

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
            Log($"Pictures directory not found for '{Category.Name}'.");
            return;
        }

        var filePath = FindBestCoverPath(picturesPath, Category.Name);
        if (filePath == null)
        {
            Log($"No folder cover matched for '{Category.Name}' in '{picturesPath}'.");
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
            Log($"Failed to load folder cover for '{Category.Name}' from '{filePath}': {ex.Message}");
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
            Path.Combine(Environment.CurrentDirectory, "Pictures"),
            Path.Combine(AppContext.BaseDirectory, "Pictures")
        };

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && current != null; depth++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "Pictures"));
        }

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

    [ObservableProperty]
    private Bitmap? _coverImage;

    public CommunityCategoryItemViewModel(string name)
    {
        Name = name;
    }

    public void LoadCoverImage()
    {
        if (CoverImage != null)
            return;

        var picturesPath = DesignerCategoryItemViewModel.ResolvePicturesPath();
        if (picturesPath == null)
        {
            RuntimeLog.Write("AlbumCollectionVM", $"Pictures directory not found for community item '{Name}'.");
            return;
        }

        var filePath = Extensions
            .Select(ext => Path.Combine(picturesPath, Name + ext))
            .FirstOrDefault(File.Exists);

        if (filePath == null)
        {
            RuntimeLog.Write("AlbumCollectionVM", $"No community cover matched for '{Name}' in '{picturesPath}'.");
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
            RuntimeLog.Write("AlbumCollectionVM", $"Failed to load community cover for '{Name}' from '{filePath}': {ex.Message}");
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
    private bool _isInitialized;

    [ObservableProperty]
    private ObservableCollection<DesignerCategoryItemViewModel> _categories = new();

    [ObservableProperty]
    private ObservableCollection<CommunityCategoryItemViewModel> _communityCategories = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

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

        foreach (var name in new[] { "通过审议", "令人生草", "待定或存在小问题" })
        {
            var item = new CommunityCategoryItemViewModel(name);
            item.LoadCoverImage();
            CommunityCategories.Add(item);
        }

        var collections = await _collectionService.GetCollectionsAsync();
        
        foreach (var category in collections)
        {
            var item = new DesignerCategoryItemViewModel(category);
            item.LoadCoverImage();
            Categories.Add(item);
        }

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
        RuntimeLog.Write("AlbumCollectionVM", "Released album collection folder cache.");
    }

    [RelayCommand]
    private async Task OpenCategoryAsync(DesignerCategoryItemViewModel item)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            var detailVm = Ioc.Default.GetRequiredService<AlbumDetailViewModel>();
            mainVm.CurrentPage = detailVm;
            await detailVm.InitializeAsync(item.Category);
        }
    }

    [RelayCommand]
    private async Task OpenCommunityCategoryAsync(CommunityCategoryItemViewModel item)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            var detailVm = Ioc.Default.GetRequiredService<CommunityCategoryDetailViewModel>();
            mainVm.CurrentPage = detailVm;
            await detailVm.InitializeAsync(item.Name);
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
