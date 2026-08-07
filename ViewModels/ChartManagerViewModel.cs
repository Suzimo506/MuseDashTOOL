using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;
using System.IO.Compression;
using System.Text.Json;
using NAudio.Vorbis;
using NAudio.Wave;
using MdModManager.Views;

namespace MdModManager.ViewModels;

public partial class ChartManagerViewModel : ObservableObject, IDisposable
{
    private readonly IChartService _chartService;
    private readonly IConfigService _configService;
    private readonly IDownloadManagerService _downloadManagerService;
    private bool _hasShownTutorial;
    private bool _hasShownMigrationTutorial;
    private const int PageSize = 16;
    public const string RootCategoryKey = "Root_Uncategorized";
    public const string CandidateCategoryKey = "Candidate_Library";
    private const string CandidateCategoryName = "候选区";
    private enum CategoryPanelSource
    {
        CustomAlbums,
        Candidate
    }

    private static readonly SemaphoreSlim _coverSemaphore = new(7);
    private int _currentLoadId = 0;

    // 检查游戏是否运行
    private static bool IsGameRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("MuseDash")
                .Any(p => !p.HasExited && p.Id != Environment.ProcessId);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>全量谱面列表（原始数据）</summary>
    private ObservableCollection<ChartInfo> _allCharts = new();
    private readonly List<ChartInfo> _filteredCharts = new();

    /// <summary>搜索过滤后展示的列表</summary>
    public ObservableCollection<ChartInfo> Charts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _isCustomAlbumsMissing = false;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _hasNoSearchResults;

    [ObservableProperty]
    private bool _hasVisibleCharts;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private int _totalPages = 1;

    [ObservableProperty]
    private string _jumpPageText = "1";

    [ObservableProperty]
    private bool _isEditingPageNumber;

    [ObservableProperty]
    private double? _requestedScrollY;

    // 批量操作模式
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotIsBatchMode))]
    private bool _isBatchMode;

    public bool NotIsBatchMode => !IsBatchMode;

    // 当前选中数量
    [ObservableProperty]
    private int _selectedCount;

    // 当前页是否全选
    [ObservableProperty]
    private bool _isAllSelected;

    // 是否打开移动分类面板
    [ObservableProperty]
    private bool _isMovePanelOpen;

    private CategoryPanelSource _movePanelSource = CategoryPanelSource.CustomAlbums;

    // 是否打开分类管理面板
    [ObservableProperty]
    private bool _isCategoryManagerPanelOpen;

    // 是否处于分类删除模式
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowCategoryDeleteEntry))]
    [NotifyPropertyChangedFor(nameof(CanShowCategoryDeleteControls))]
    private bool _isCategoryDeleteMode;

    private CategoryPanelSource _categoryPanelSource = CategoryPanelSource.CustomAlbums;

    public string CategoryPanelSourceDisplay => _categoryPanelSource == CategoryPanelSource.Candidate
        ? "候选区"
        : "Custom_Albums";

    public string CategoryPanelSourceToggleTip => _categoryPanelSource == CategoryPanelSource.Candidate
        ? "切换显示 Custom_Albums 分类"
        : "切换显示候选区分类";

    public bool IsCategoryPanelCustomAlbumsSource => _categoryPanelSource == CategoryPanelSource.CustomAlbums;

    public bool CanShowCategoryDeleteEntry => !IsCategoryDeleteMode;

    public bool CanShowCategoryDeleteControls => IsCategoryDeleteMode;

    [ObservableProperty]
    private bool _showOnlyCandidateRootCharts;

    // 当前准备移动的谱面
    [ObservableProperty]
    private ChartInfo? _currentMovingChart;

    [ObservableProperty]
    private ObservableCollection<string> _categories = new() { "全部", RootCategoryKey, CandidateCategoryKey };

    // 可用于移动的目标分类列表
    public IEnumerable<MoveCategoryItem> MoveCategories
    {
        get
        {
            var list = new List<MoveCategoryItem>
            {
                new MoveCategoryItem { Name = "新建分类", IsCreateNew = true }
            };

            var targetCategories = _movePanelSource == CategoryPanelSource.Candidate
                ? Categories.Where(IsCandidateCategory)
                : Categories.Where(c => c == RootCategoryKey || (!IsCandidateCategory(c) && c != "全部"));

            list.AddRange(targetCategories.Select(c => new MoveCategoryItem { Name = c, IsCreateNew = false }));
            return list;
        }
    }

    public string MovePanelSourceDisplay => _movePanelSource == CategoryPanelSource.Candidate
        ? "候选区"
        : "Custom_Albums";

    public string MovePanelSourceToggleTip => _movePanelSource == CategoryPanelSource.Candidate
        ? "切换显示 Custom_Albums 目标"
        : "切换显示候选区目标";

    // 分类包装项列表用于管理界面展示
    public ObservableCollection<CategoryItem> CategoryItems { get; } = new();

    public IEnumerable<CategoryItem> VisibleCategoryItems => _categoryPanelSource == CategoryPanelSource.Candidate
        ? CategoryItems.Where(item => item.IsCandidate)
        : CategoryItems.Where(item => item.IsGlobal || item.IsCustomAlbums);

    // 勾选待删除分类的计数
    public int SelectedCategoriesForDeletionCount => CategoryItems.Count(c => c.IsSelectedForDeletion && c.CanManage);

    // 是否有选中的分类准备被删除
    public bool HasSelectedCategoriesForDeletion => SelectedCategoriesForDeletionCount > 0;

    [ObservableProperty]
    private ObservableCollection<string> _sortOptions = new()
    {
        Services.I18nService.Instance["Str_343"] ?? "按名称排序",
        Services.I18nService.Instance["Str_352"] ?? "按分类排序",
        Services.I18nService.Instance["Str_402"] ?? "难度从高到低",
        Services.I18nService.Instance["Str_403"] ?? "难度从低到高"
    };

    [ObservableProperty]
    private int _selectedSortIndex = 0;

    partial void OnSelectedSortIndexChanged(int value)
    {
        if (_isUpdatingSortOptions || value < 0) return;
        CurrentPage = 1;
        ApplyFilter();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategorySelected))]
    [NotifyPropertyChangedFor(nameof(SelectedCategoryDisplay))]
    [NotifyPropertyChangedFor(nameof(IsCandidateRootFilterAvailable))]
    private string _selectedCategory = "全部";

    // 选中可管理分类
    public bool IsCategorySelected => CanManageCategoryName(SelectedCategory);

    public bool IsCandidateRootFilterAvailable => SelectedCategory == "全部" || SelectedCategory == CandidateCategoryKey;

    // 限制分类名显示字数
    public string SelectedCategoryDisplay
    {
        get
        {
            string displayName = string.IsNullOrEmpty(SelectedCategory) || SelectedCategory == "全部" ? Services.I18nService.Instance["Str_389"] : GetCategoryDisplayName(SelectedCategory);
            return displayName.Length > 6 ? displayName.Substring(0, 6) + ".." : displayName;
        }
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (!IsCandidateRootFilterAvailable && ShowOnlyCandidateRootCharts)
        {
            _showOnlyCandidateRootCharts = false;
            OnPropertyChanged(nameof(ShowOnlyCandidateRootCharts));
        }

        CurrentPage = 1;
        ApplyFilter();

        // 更新分类项激活状态
        foreach (var item in CategoryItems)
        {
            item.IsActive = (item.Name == value);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilter();
    }

    partial void OnShowOnlyCandidateRootChartsChanged(bool value)
    {
        CurrentPage = 1;
        ApplyFilter();
    }

    partial void OnCurrentPageChanged(int value)
    {
        JumpPageText = value.ToString();
    }

    public bool EnableMarquee => _configService.Config.EnableChartNameMarquee;

    public bool CanLoadNext => CurrentPage < TotalPages && !IsLoading;
    public bool CanLoadPrev => CurrentPage > 1 && !IsLoading;

    // Audio playback
    private WaveOutEvent? _waveOut;
    private ChartInfo? _playingChart;
    private CancellationTokenSource? _stopCts;

    private readonly IChartIndexService _chartIndexService;

    [ObservableProperty]
    private bool _isToolboxPanelOpen;

    [ObservableProperty]
    private bool _isToolboxMinimized = false;

    [ObservableProperty]
    private bool _isDeduplicationRunning;

    [ObservableProperty]
    private bool _isShowingDeduplication;

    public ObservableCollection<DuplicateCheckItem> LocalDuplicatesList { get; } = new();

    public ObservableCollection<DuplicateGroupItem> DuplicateGroups { get; } = new();

    private DeduplicationScanScope _currentDuplicateScanScope = DeduplicationScanScope.CustomAlbums;

    // 工具箱底部状态信息仅显示总数
    public string ToolboxStatusString
    {
        get
        {
            bool isEn = Services.I18nService.Instance.CurrentLanguage == "en-US";
            return isEn ? $"Total {_allCharts.Count} charts" : $"共 {_allCharts.Count} 张谱面";
        }
    }

    public ChartManagerViewModel(
        IChartService chartService, 
        IConfigService configService, 
        IDownloadManagerService downloadManagerService,
        IChartIndexService chartIndexService)
    {
        _chartService = chartService;
        _configService = configService;
        _downloadManagerService = downloadManagerService;
        _chartIndexService = chartIndexService;

        // 订阅语言变更更新排序选项与重新加载文字
        Services.I18nService.Instance.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == "Item")
            {
                UpdateSortOptions();
                _ = Task.Run(() => Reload());
            }
        };
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // 获取主窗口实例
        var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null;

        // 1. 检测并弹出谱面管理界面教程
        if (!_hasShownTutorial && !_configService.Config.SuppressChartManagerTutorial)
        {
            _hasShownTutorial = true;
            if (mainWindow != null)
            {
                var title = Services.I18nService.Instance["Tutorial_Title"] ?? "教程提示";
                var message = Services.I18nService.Instance["Tutorial_ChartManager"];
                bool dontRemind = await Views.TutorialDialog.ShowDialogAsync(mainWindow, title, message);
                if (dontRemind)
                {
                    _configService.Config.SuppressChartManagerTutorial = true;
                    await _configService.SaveAsync();
                }
            }
        }

        // 2. 谱面管理教程后，串行弹出谱面迁移教程
        if (!_hasShownMigrationTutorial && !_configService.Config.SuppressChartMigrationTutorial)
        {
            _hasShownMigrationTutorial = true;
            if (mainWindow != null)
            {
                var title = Services.I18nService.Instance["Tutorial_Title"] ?? "教程提示";
                var message = Services.I18nService.Instance["Tutorial_ChartMigration"];
                bool dontRemind = await Views.TutorialDialog.ShowDialogAsync(mainWindow, title, message);
                if (dontRemind)
                {
                    _configService.Config.SuppressChartMigrationTutorial = true;
                    await _configService.SaveAsync();
                }
            }
        }

        // 3. 教程关闭或跳过后，检测并串行弹出谱面分类提示弹窗
        if (mainWindow != null && !_configService.Config.SuppressCustomAlbumsWarning)
        {
            await Views.CustomAlbumsWarningDialog.ShowDialogAsync(mainWindow, _configService);
        }

        await Task.Run(() => Reload(), ct);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await Task.Run(() => Reload());

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ToggleBatchMode()
    {
        IsBatchMode = !IsBatchMode;
        if (!IsBatchMode)
            ClearAllSelections();
    }

    [RelayCommand]
    private void ToggleSelectChart(ChartInfo chart)
    {
        chart.IsSelected = !chart.IsSelected;
        UpdateSelectedCount();
        UpdateIsAllSelected();
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var targetState = !IsAllSelected;
        foreach (var chart in Charts)
            chart.IsSelected = targetState;
        UpdateSelectedCount();
        UpdateIsAllSelected();
    }

    [RelayCommand]
    private async Task DeleteSelectedCharts()
    {
        var toDelete = _allCharts.Where(c => c.IsSelected).ToList();
        if (toDelete.Count == 0) return;

        // 游戏运行时限制非未分类谱面删除
        if (IsGameRunning() && toDelete.Any(c => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(c.FilePath)) != "Custom_Albums"))
        {
            var app = Avalonia.Application.Current;
            var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow != null)
            {
                await MessageBox.ShowDialogAsync(mainWindow, Services.I18nService.Instance["Str_442"]);
            }
            return;
        }

        StopCurrentPlayback();
        foreach (var chart in toDelete)
        {
            try
            {
                _chartIndexService.RemoveFromIndex(chart.FilePath);
                _chartService.DeleteChart(chart);
                chart.CleanupCoverResources();
                _allCharts.Remove(chart);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChartManager] Batch delete error: {ex.Message}");
            }
        }

        IsBatchMode = false;
        ApplyFilter();
        RefreshDuplicateGroupsIfNecessary();
    }

    private void ClearAllSelections()
    {
        foreach (var chart in _allCharts)
            chart.IsSelected = false;
        SelectedCount = 0;
        IsAllSelected = false;
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = _allCharts.Count(c => c.IsSelected);
    }

    private void UpdateIsAllSelected()
    {
        IsAllSelected = Charts.Count > 0 && Charts.All(c => c.IsSelected);
    }

    private void Reload(int? requestedPage = null)
    {
        StopCurrentPlayback();
        foreach (var existingChart in _allCharts.ToList())
            existingChart.CleanupCoverResources();

        var targetPage = requestedPage.GetValueOrDefault(1);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _allCharts.Clear();
            _filteredCharts.Clear();
            Charts.Clear();
            IsEmpty = true;
            HasNoSearchResults = false;
            HasVisibleCharts = false;
            IsCustomAlbumsMissing = false;
            CurrentPage = Math.Max(1, targetPage);
            TotalPages = 1;
            IsEditingPageNumber = false;
            IsBatchMode = false;
            SelectedCount = 0;
            IsAllSelected = false;
            IsLoading = true;
            StatusMessage = Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Loading..." : "正在加载...";
        });

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsLoading = false;
                StatusMessage = Services.I18nService.Instance.CurrentLanguage == "en-US"
                    ? "Game path not set, please configure the game directory in settings first"
                    : "游戏路径未设置，请先在设置中配置游戏目录";
            });
            return;
        }

        var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
        var libraryDir = System.IO.Path.Combine(gamePath, "CustomAlbums_Library");
        bool isCustomAlbumsMissing = !System.IO.Directory.Exists(albumsDir) && !System.IO.Directory.Exists(libraryDir);
        bool hasCustomAlbums = System.IO.Directory.Exists(albumsDir);

        var categories = new List<string> { "全部", CandidateCategoryKey };
        bool hasRootCharts = false;
        if (hasCustomAlbums)
        {
            if (System.IO.Directory.GetFiles(albumsDir, "*.mdm").Length > 0)
            {
                hasRootCharts = true;
            }

            if (hasRootCharts)
            {
                categories.Insert(1, RootCategoryKey);
            }

            foreach (var subDir in System.IO.Directory.GetDirectories(albumsDir))
            {
                var folderName = System.IO.Path.GetFileName(subDir);
                if (System.IO.File.Exists(System.IO.Path.Combine(subDir, "pack.json")))
                {
                    categories.Add(folderName);
                }
            }
        }

        if (System.IO.Directory.Exists(libraryDir))
        {
            foreach (var subDir in System.IO.Directory.EnumerateDirectories(libraryDir, "*", System.IO.SearchOption.AllDirectories))
            {
                var relative = System.IO.Path.GetRelativePath(libraryDir, subDir);
                if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
                    continue;

                categories.Add(GetCandidateSubCategoryKey(relative.Replace(System.IO.Path.DirectorySeparatorChar, '/').Replace(System.IO.Path.AltDirectorySeparatorChar, '/')));
            }
        }

        var charts = _chartService.LoadCharts(gamePath, _downloadManagerService.SessionDownloadedFiles)
            .OrderByDescending(chart => chart.IsNewDownload)
            .ThenBy(chart => chart.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(chart => System.IO.Path.GetFileName(chart.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var subCategory in charts
                     .Where(chart => chart.IsLibraryCandidate && !string.IsNullOrWhiteSpace(chart.CandidateSubCategory))
                     .Select(chart => GetCandidateSubCategoryKey(chart.CandidateSubCategory))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            categories.Add(subCategory);
        }

        categories = categories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsCustomAlbumsMissing = isCustomAlbumsMissing;

            var prevSelected = SelectedCategory;
            if (string.IsNullOrEmpty(prevSelected))
                prevSelected = "全部";

            Categories.Clear();
            CategoryItems.Clear();
            if (!categories.Contains(prevSelected))
                prevSelected = "全部";

            foreach (var cat in categories)
            {
                Categories.Add(cat);
                CategoryItems.Add(new CategoryItem { Name = cat, IsActive = (cat == prevSelected) });
            }

            OnPropertyChanged(nameof(MoveCategories));
            OnPropertyChanged(nameof(VisibleCategoryItems));
            OnPropertyChanged(nameof(SelectedCategoriesForDeletionCount));
            OnPropertyChanged(nameof(HasSelectedCategoriesForDeletion));

            if (Categories.Contains(prevSelected))
                SelectedCategory = prevSelected;
            else
                SelectedCategory = "全部";

            foreach (var chart in charts)
                _allCharts.Add(chart);

            // 构建自制谱内存快速哈希索引结构
            _chartIndexService.IndexAll(_allCharts);

            ApplyFilter();
            OnPropertyChanged(nameof(ToolboxStatusString));
            IsLoading = false;
        });
    }

    private void ApplyFilter()
    {
        var search = SearchText?.Trim();
        _filteredCharts.Clear();

        var enableFuzzy = _configService.Config.EnableFuzzySearch;

        foreach (var chart in _allCharts)
        {
            if (ShowOnlyCandidateRootCharts &&
                (!chart.IsLibraryCandidate || !string.IsNullOrWhiteSpace(chart.CandidateSubCategory)))
            {
                continue;
            }

            // 分类过滤
            if (!IsChartInSelectedCategory(chart))
                continue;

            // 搜索过滤
            if (string.IsNullOrEmpty(search)
                || MdModManager.Helpers.SearchHelper.IsMatch(chart.Name, search, enableFuzzy)
                || MdModManager.Helpers.SearchHelper.IsMatch(chart.MusicAuthor, search, enableFuzzy)
                || MdModManager.Helpers.SearchHelper.IsMatch(chart.ChartAuthor, search, enableFuzzy))
            {
                _filteredCharts.Add(chart);
            }
        }

        // 按照选定的规则排序数据
        var sorted = _filteredCharts.OrderByDescending(c => c.IsNewDownload);
        IOrderedEnumerable<ChartInfo> finalSorted;
        if (SelectedSortIndex == 1)
        {
            finalSorted = sorted
                .ThenBy(c => c.CategoryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
        }
        else if (SelectedSortIndex == 2)
        {
            finalSorted = sorted
                .ThenByDescending(c => GetMaxDifficulty(c))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
        }
        else if (SelectedSortIndex == 3)
        {
            finalSorted = sorted
                .ThenBy(c => GetMaxDifficulty(c))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            finalSorted = sorted
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
        }

        var sortedList = finalSorted.ToList();
        _filteredCharts.Clear();
        foreach (var c in sortedList)
        {
            _filteredCharts.Add(c);
        }

        TotalPages = Math.Max(1, (int)Math.Ceiling((double)_filteredCharts.Count / PageSize));
        if (CurrentPage > TotalPages)
            CurrentPage = TotalPages;

        RefreshPagedCharts();
        UpdateStateAndStatus();
    }

    private void RefreshPagedCharts()
    {
        var nextCharts = _filteredCharts
                     .Skip((CurrentPage - 1) * PageSize)
                     .Take(PageSize)
                     .ToList();

        foreach (var chart in Charts.ToList())
        {
            if (!nextCharts.Contains(chart))
            {
                chart.CleanupCoverResources();
            }
        }

        Charts.Clear();

        foreach (var chart in nextCharts)
        {
            Charts.Add(chart);
        }

        HasVisibleCharts = Charts.Count > 0;
        RequestedScrollY = 0;
        // 数据刷新后更新全选状态
        UpdateIsAllSelected();

        _ = EnsureCurrentPageCoversLoadedAsync();
    }

    private async Task EnsureCurrentPageCoversLoadedAsync()
    {
        int myId = System.Threading.Interlocked.Increment(ref _currentLoadId);
        var snapshot = Charts.ToList();

        foreach (var chart in snapshot)
        {
            if (myId != _currentLoadId) break;
            if (chart.HasAnyCover) continue;

            await _coverSemaphore.WaitAsync();
            try
            {
                if (myId != _currentLoadId) break;
                if (!chart.HasAnyCover)
                {
                    await _chartService.LoadCoverAsync(chart);
                }
            }
            finally
            {
                _coverSemaphore.Release();
            }
        }
    }

    private void UpdateStateAndStatus()
    {
        IsEmpty = _allCharts.Count == 0;
        HasNoSearchResults = _allCharts.Count > 0 && _filteredCharts.Count == 0;

        if (IsCustomAlbumsMissing)
        {
            StatusMessage = "请先创建 Custom_Albums 文件夹";
            return;
        }

        if (IsEmpty)
        {
            StatusMessage = "未找到谱面（Custom_Albums 目录为空）";
            return;
        }

        int categoryTotal = _allCharts.Count(IsChartInSelectedCategory);

        if (SelectedCategory == "全部")
        {
            StatusMessage = string.Format(Services.I18nService.Instance["Str_344"], CurrentPage, TotalPages, _allCharts.Count);
        }
        else
        {
            StatusMessage = string.Format(Services.I18nService.Instance["Str_346"], categoryTotal);
        }
    }

    private static bool IsCandidateCategory(string category)
    {
        return category == CandidateCategoryKey || category.StartsWith(CandidateCategoryKey + "/", StringComparison.Ordinal);
    }

    public static bool CanManageCategoryName(string category)
    {
        return !string.IsNullOrEmpty(category)
            && category != "全部"
            && category != "未分类"
            && category != "Uncategorized"
            && category != RootCategoryKey
            && category != CandidateCategoryKey;
    }

    private static string GetCandidateSubCategoryKey(string subCategory)
    {
        return string.IsNullOrWhiteSpace(subCategory)
            ? CandidateCategoryKey
            : CandidateCategoryKey + "/" + subCategory.Trim().Replace('\\', '/');
    }

    private static string GetCandidateSubCategoryFromKey(string category)
    {
        return category.StartsWith(CandidateCategoryKey + "/", StringComparison.Ordinal)
            ? category[(CandidateCategoryKey.Length + 1)..]
            : string.Empty;
    }

    public static string GetCategoryDisplayName(string category)
    {
        if (category == RootCategoryKey)
            return MdModManager.Services.I18nService.Instance.CurrentLanguage == "zh-CN" ? "未分类" : "Uncategorized";
        if (category == CandidateCategoryKey)
            return CandidateCategoryName;
        if (category.StartsWith(CandidateCategoryKey + "/", StringComparison.Ordinal))
            return CandidateCategoryName + " / " + GetCandidateSubCategoryFromKey(category);
        return category;
    }

    private bool IsChartInSelectedCategory(ChartInfo chart)
    {
        if (SelectedCategory == "全部") return true;
        if (SelectedCategory == CandidateCategoryKey) return chart.IsLibraryCandidate;
        if (SelectedCategory.StartsWith(CandidateCategoryKey + "/", StringComparison.Ordinal))
            return chart.IsLibraryCandidate && string.Equals(chart.CandidateSubCategory, GetCandidateSubCategoryFromKey(SelectedCategory), StringComparison.OrdinalIgnoreCase);
        if (chart.IsLibraryCandidate) return false;
        if (SelectedCategory == RootCategoryKey)
        {
            var parentName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(chart.FilePath));
            return parentName == "Custom_Albums";
        }

        var normalParentName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(chart.FilePath));
        return normalParentName == SelectedCategory;
    }
    [RelayCommand]
    private void TogglePreview(ChartInfo chart)
    {
        if (_playingChart == chart)
        {
            StopCurrentPlayback();
            return;
        }

        StopCurrentPlayback();
        PlayDemo(chart);
    }

    private void PlayDemo(ChartInfo chart)
    {
        var stream = _chartService.OpenDemoStream(chart);
        if (stream == null)
        {
            // 该谱面文件内没有音频文件
            StatusMessage = $"《{chart.Name}》没有试听文件";
            return;
        }

        try
        {
            var ext = System.IO.Path.GetExtension(chart.DemoEntryName ?? "").ToLowerInvariant();

            // 根据文件扩展名选择解码器
            IWaveProvider waveProvider;
            if (ext == ".ogg")
            {
                waveProvider = new VorbisWaveReader(stream);
            }
            else if (ext == ".mp3")
            {
                waveProvider = new Mp3FileReader(stream);
            }
            else
            {
                // .wav 或其他由 WaveFileReader 支持的格式
                waveProvider = new WaveFileReader(stream);
            }

            _waveOut = new WaveOutEvent();
            _waveOut.Init(waveProvider);
            _waveOut.Volume = (float)_configService.Config.ChartPreviewVolume;

            _stopCts = new CancellationTokenSource();
            var cts = _stopCts;
            var provider = waveProvider; // capture for lambda

            _waveOut.PlaybackStopped += (_, _) =>
            {
                if (!cts.IsCancellationRequested)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        chart.IsPlaying = false;
                        if (_playingChart == chart) _playingChart = null;
                    });
                }
                if (provider is IDisposable d) d.Dispose();
                stream.Dispose();
            };

            _waveOut.Play();
            _playingChart = chart;
            chart.IsPlaying = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChartManager] Playback error: {ex.Message}");
            StatusMessage = $"试听出错: {ex.Message}";
            stream.Dispose();
        }
    }

    private void StopCurrentPlayback()
    {
        _stopCts?.Cancel();
        _stopCts = null;

        if (_playingChart != null)
        {
            _playingChart.IsPlaying = false;
            _playingChart = null;
        }

        if (_waveOut != null)
        {
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
        }
    }

    [RelayCommand]
    private async Task DeleteChart(ChartInfo chart)
    {
        // 游戏运行时限制非未分类谱面删除
        if (IsGameRunning())
        {
            var parentName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(chart.FilePath));
            if (parentName != "Custom_Albums")
            {
                var app = Avalonia.Application.Current;
                var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (mainWindow != null)
                {
                    await MessageBox.ShowDialogAsync(mainWindow, Services.I18nService.Instance["Str_442"]);
                }
                return;
            }
        }

        if (_playingChart == chart)
            StopCurrentPlayback();

        try
        {
            _chartIndexService.RemoveFromIndex(chart.FilePath);
            _chartService.DeleteChart(chart);
            chart.CleanupCoverResources();
            _allCharts.Remove(chart);
            ApplyFilter();
            RefreshDuplicateGroupsIfNecessary();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChartManager] Delete error: {ex.Message}");
        }
    }

    public async Task ImportChartAsync(string sourceFile)
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Game path not set, cannot import" : "游戏路径未设置，无法导入";
            return;
        }

        try
        {
            var targetCategory = GetImportTargetCategory();
            var targetDir = GetTargetFolder(gamePath, targetCategory);
            if (!System.IO.Directory.Exists(targetDir))
                System.IO.Directory.CreateDirectory(targetDir);

            string destFileName = System.IO.Path.GetFileName(sourceFile);
            
            // Handle ZIP conversion
            if (sourceFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Validate ZIP content
                bool isValidChart = false;
                using (var zip = ZipFile.OpenRead(sourceFile))
                {
                    isValidChart = zip.Entries.Any(e => 
                        e.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase) || 
                        e.Name.Equals("map.json", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".epk", StringComparison.OrdinalIgnoreCase));
                }

                if (!isValidChart)
                {
                    StatusMessage = "导入失败: 该压缩包内未找到谱面信息 (info.json/map.json)";
                    return;
                }

                // Change extension to .mdm
                destFileName = System.IO.Path.GetFileNameWithoutExtension(sourceFile) + ".mdm";
            }

            var destFile = System.IO.Path.Combine(targetDir, destFileName);
            System.IO.File.Copy(sourceFile, destFile, true);
            ChartService.ConvertEpkToInfoJsonInPlace(destFile);
            _downloadManagerService.SessionDownloadedFiles.Add(System.IO.Path.GetFullPath(destFile));

            StatusMessage = $"导入成功: {destFileName}";
            Reload();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChartManager] Import error: {ex.Message}");
            StatusMessage = "导入失败: " + ex.Message;
        }
    }

    private string GetImportTargetCategory()
    {
        if (ShowOnlyCandidateRootCharts)
            return CandidateCategoryKey;

        if (string.IsNullOrWhiteSpace(SelectedCategory) || SelectedCategory == "全部")
            return RootCategoryKey;

        return SelectedCategory;
    }

    public void Dispose()
    {
        StopCurrentPlayback();
        foreach (var c in _allCharts)
            c.CleanupCoverResources();
    }

    [RelayCommand]
    private void LoadFirstPage() => ChangePage(1);

    [RelayCommand]
    private void LoadPrevPage()
    {
        if (CanLoadPrev)
            ChangePage(CurrentPage - 1);
    }

    [RelayCommand]
    private void LoadNextPage()
    {
        if (CanLoadNext)
            ChangePage(CurrentPage + 1);
    }

    [RelayCommand]
    private void LoadLastPage() => ChangePage(TotalPages);

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
    private void JumpPage()
    {
        if (!IsEditingPageNumber)
            return;

        IsEditingPageNumber = false;
        if (int.TryParse(JumpPageText.Trim(), out int page))
        {
            ChangePage(Math.Clamp(page, 1, TotalPages));
            return;
        }

        JumpPageText = CurrentPage.ToString();
    }

    private void ChangePage(int page)
    {
        if (page < 1 || page > TotalPages)
            return;

        CurrentPage = page;
        RefreshPagedCharts();
        UpdateStateAndStatus();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Open failed: Game path not set" : "打开失败: 游戏路径未设置";
            return;
        }

        var customAlbumsPath = System.IO.Path.Combine(gamePath, "Custom_Albums");
        if (!System.IO.Directory.Exists(customAlbumsPath))
        {
            try
            {
                System.IO.Directory.CreateDirectory(customAlbumsPath);
            }
            catch (Exception ex)
            {
                StatusMessage = $"打开失败: 无法创建 Custom_Albums 文件夹: {ex.Message}";
                return;
            }
        }

        try
        {
            MdModManager.Helpers.ProcessHelper.OpenFolder(customAlbumsPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开失败: {ex.Message}";
        }
    }

    // 打开分类管理面板
    [RelayCommand]
    private void OpenCategoryManagerPanel()
    {
        _categoryPanelSource = IsCandidateCategory(SelectedCategory)
            ? CategoryPanelSource.Candidate
            : CategoryPanelSource.CustomAlbums;
        NotifyCategoryPanelSourceChanged();

        IsCategoryManagerPanelOpen = true;
        IsCategoryDeleteMode = false;
        foreach (var item in CategoryItems)
        {
            item.IsSelectedForDeletion = false;
        }
        OnPropertyChanged(nameof(SelectedCategoriesForDeletionCount));
        OnPropertyChanged(nameof(HasSelectedCategoriesForDeletion));
    }

    [RelayCommand]
    private void ToggleCategoryPanelSource()
    {
        _categoryPanelSource = _categoryPanelSource == CategoryPanelSource.Candidate
            ? CategoryPanelSource.CustomAlbums
            : CategoryPanelSource.Candidate;

        IsCategoryDeleteMode = false;
        foreach (var item in CategoryItems)
        {
            item.IsSelectedForDeletion = false;
        }

        NotifyCategoryPanelSourceChanged();
        OnPropertyChanged(nameof(SelectedCategoriesForDeletionCount));
        OnPropertyChanged(nameof(HasSelectedCategoriesForDeletion));
    }

    private void NotifyCategoryPanelSourceChanged()
    {
        OnPropertyChanged(nameof(CategoryPanelSourceDisplay));
        OnPropertyChanged(nameof(CategoryPanelSourceToggleTip));
        OnPropertyChanged(nameof(IsCategoryPanelCustomAlbumsSource));
        OnPropertyChanged(nameof(CanShowCategoryDeleteEntry));
        OnPropertyChanged(nameof(CanShowCategoryDeleteControls));
        OnPropertyChanged(nameof(VisibleCategoryItems));
    }

    // 关闭分类管理面板
    [RelayCommand]
    private void CloseCategoryManagerPanel()
    {
        IsCategoryManagerPanelOpen = false;
        IsCategoryDeleteMode = false;
    }

    // 切换分类批量删除模式
    [RelayCommand]
    private void ToggleCategoryDeleteMode()
    {
        IsCategoryDeleteMode = !IsCategoryDeleteMode;
        if (!IsCategoryDeleteMode)
        {
            foreach (var item in CategoryItems)
            {
                item.IsSelectedForDeletion = false;
            }
        }
        OnPropertyChanged(nameof(SelectedCategoriesForDeletionCount));
        OnPropertyChanged(nameof(HasSelectedCategoriesForDeletion));
    }

    // 切换特定分类的勾选状态
    [RelayCommand]
    private void ToggleCategorySelectionForDeletion(CategoryItem item)
    {
        if (item != null && item.CanManage)
        {
            item.IsSelectedForDeletion = !item.IsSelectedForDeletion;
            OnPropertyChanged(nameof(SelectedCategoriesForDeletionCount));
            OnPropertyChanged(nameof(HasSelectedCategoriesForDeletion));
        }
    }

    // 全选自定义分类
    [RelayCommand]
    private void SelectAllCategoriesForDeletion()
    {
        foreach (var item in VisibleCategoryItems)
        {
            if (item.CanManage)
            {
                item.IsSelectedForDeletion = true;
            }
        }
        OnPropertyChanged(nameof(SelectedCategoriesForDeletionCount));
        OnPropertyChanged(nameof(HasSelectedCategoriesForDeletion));
    }

    // 反全选自定义分类
    [RelayCommand]
    private void InvertCategorySelectionForDeletion()
    {
        foreach (var item in VisibleCategoryItems)
        {
            if (item.CanManage)
            {
                item.IsSelectedForDeletion = !item.IsSelectedForDeletion;
            }
        }
        OnPropertyChanged(nameof(SelectedCategoriesForDeletionCount));
        OnPropertyChanged(nameof(HasSelectedCategoriesForDeletion));
    }

    // 处理分类项被点击逻辑
    [RelayCommand]
    private void OnCategoryItemClick(CategoryItem item)
    {
        if (item == null) return;
        if (IsCategoryDeleteMode)
        {
            ToggleCategorySelectionForDeletion(item);
        }
        else
        {
            SelectedCategory = item.Name;
            IsCategoryManagerPanelOpen = false;
        }
    }

    // 重命名指定名称的自定义分类
    [RelayCommand]
    private async Task RenameSpecificCategoryAsync(string oldName)
    {
        if (!CanManageCategoryName(oldName)) return;

        // 游戏运行时限制分类重命名
        if (IsGameRunning())
        {
            var curApp = Avalonia.Application.Current;
            var curWindow = (curApp?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (curWindow != null)
            {
                await MessageBox.ShowDialogAsync(curWindow, Services.I18nService.Instance["Str_442"]);
            }
            return;
        }

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Game path not set, cannot rename" : "游戏路径未设置，无法重命名";
            return;
        }

        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var isCandidate = IsCandidateCategory(oldName);
        var oldDisplayName = GetCategoryDisplayName(oldName);
        var oldInputName = isCandidate ? GetCandidateSubCategoryFromKey(oldName) : oldName;
        var newName = await InputDialog.ShowDialogAsync(mainWindow, "重命名分类", "请输入新的分类名称：", oldInputName);
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName)
            return;

        newName = SanitizeCategoryName(newName, isCandidate);
        if (string.IsNullOrWhiteSpace(newName) || newName == oldInputName)
            return;

        var targetCategory = isCandidate ? GetCandidateSubCategoryKey(newName) : newName;
        var sourceDir = GetTargetFolder(gamePath, oldName);
        var targetDir = GetTargetFolder(gamePath, targetCategory);

        if (System.IO.Directory.Exists(targetDir))
        {
            await MessageBox.ShowDialogAsync(mainWindow, "分类已存在");
            return;
        }

        StopCurrentPlayback();
        foreach (var existingChart in _allCharts)
        {
            existingChart.CleanupCoverResources();
        }

        try
        {
            System.IO.Directory.Move(sourceDir, targetDir);

            if (!isCandidate)
            {
                var packJsonPath = System.IO.Path.Combine(targetDir, "pack.json");
                if (System.IO.File.Exists(packJsonPath))
                {
                    var packData = new
                    {
                        Title = newName,
                        TitleColorHex = "#ffffff",
                        LongTextScroll = false
                    };
                    var jsonStr = JsonSerializer.Serialize(packData, new JsonSerializerOptions { WriteIndented = true });
                    await System.IO.File.WriteAllTextAsync(packJsonPath, jsonStr, System.Text.Encoding.UTF8);
                }
            }

            StatusMessage = $"分类《{oldDisplayName}》已重命名为《{GetCategoryDisplayName(targetCategory)}》";
            if (SelectedCategory == oldName)
            {
                SelectedCategory = targetCategory;
            }
            Reload();
        }
        catch (Exception ex)
        {
            StatusMessage = $"重命名分类失败: {ex.Message}";
        }
    }

    // 删除指定名称的自定义分类
    [RelayCommand]
    private async Task DeleteSpecificCategoryAsync(string oldName)
    {
        if (!CanManageCategoryName(oldName)) return;

        // 游戏运行时限制分类删除
        if (IsGameRunning())
        {
            var curApp = Avalonia.Application.Current;
            var curWindow = (curApp?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (curWindow != null)
            {
                await MessageBox.ShowDialogAsync(curWindow, Services.I18nService.Instance["Str_442"]);
            }
            return;
        }

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Game path not set, cannot delete" : "游戏路径未设置，无法删除";
            return;
        }

        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var isCandidate = IsCandidateCategory(oldName);
        var oldDisplayName = GetCategoryDisplayName(oldName);
        var confirmMsg = Services.I18nService.Instance.CurrentLanguage == "en-US"
            ? $"Are you sure you want to delete the category \"{oldDisplayName}\"?\nDeleting this category will move all of its charts back to \"Uncategorized\"."
            : $"确定要删除分类《{oldDisplayName}》吗？\n删除分类将把其中的所有谱面文件移回{(isCandidate ? "候选区" : "未分类")}。";
        var confirmed = await MessageBox.ShowDialogAsync(mainWindow, confirmMsg, true);
        if (!confirmed) return;

        StopCurrentPlayback();
        foreach (var existingChart in _allCharts)
        {
            existingChart.CleanupCoverResources();
        }

        try
        {
            var rootDir = System.IO.Path.Combine(gamePath, isCandidate ? "CustomAlbums_Library" : "Custom_Albums");
            var sourceDir = GetTargetFolder(gamePath, oldName);

            if (System.IO.Directory.Exists(sourceDir))
            {
                var searchOption = isCandidate ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;
                foreach (var file in System.IO.Directory.GetFiles(sourceDir, "*.mdm", searchOption))
                {
                    var destFile = isCandidate
                        ? GetAvailableChartPath(rootDir, System.IO.Path.GetFileName(file))
                        : System.IO.Path.Combine(rootDir, System.IO.Path.GetFileName(file));

                    if (!isCandidate && System.IO.File.Exists(destFile))
                        System.IO.File.Delete(destFile);

                    System.IO.File.Move(file, destFile);
                }

                System.IO.Directory.Delete(sourceDir, true);
            }


            StatusMessage = $"分类《{oldDisplayName}》删除成功，谱面已移回{(isCandidate ? "候选区" : "未分类")}";
            if (SelectedCategory == oldName)
            {
                SelectedCategory = isCandidate ? CandidateCategoryKey : "全部";
            }
            Reload();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除分类失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CleanUpBrokenChartsAsync()
    {
        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var brokenCharts = _chartService.BrokenCharts;
        if (brokenCharts == null || brokenCharts.Count == 0)
        {
            await MessageBox.ShowDialogAsync(mainWindow, MdModManager.Services.I18nService.Instance["Str_408"] ?? "没有发现损坏的谱面文件。");
            return;
        }

        string fileList = string.Join("\n", brokenCharts.Select(System.IO.Path.GetFileName));
        if (brokenCharts.Count > 10)
        {
            string moreText = MdModManager.Services.I18nService.Instance.CurrentLanguage == "zh-CN" ? $"\n...等共 {brokenCharts.Count} 个文件" : $"\n...and others ({brokenCharts.Count} in total)";
            fileList = string.Join("\n", brokenCharts.Take(10).Select(System.IO.Path.GetFileName)) + moreText;
        }

        string template = MdModManager.Services.I18nService.Instance["Str_409"] ?? "发现 {0} 个无法识别的损坏谱面文件，是否确认删除？\n\n{1}";
        var confirmed = await MessageBox.ShowDialogAsync(mainWindow, string.Format(template, brokenCharts.Count, fileList), true);
        if (!confirmed) return;

        int deletedCount = 0;
        foreach (var file in brokenCharts)
        {
            try
            {
                if (System.IO.File.Exists(file))
                {
                    System.IO.File.Delete(file);
                    deletedCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChartManager] Failed to delete broken chart {file}: {ex.Message}");
            }
        }

        string successTemplate = MdModManager.Services.I18nService.Instance["Str_410"] ?? "成功清理了 {0} 个损坏的谱面文件。";
        StatusMessage = string.Format(successTemplate, deletedCount);
        Reload();
    }

    [RelayCommand]
    private async Task MoveCustomAlbumsToLibraryAsync()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) || !System.IO.Directory.Exists(gamePath))
        {
            StatusMessage = Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Game path not set" : "游戏路径未设置";
            return;
        }

        StopCurrentPlayback();
        StatusMessage = "正在转移 Custom_Albums 谱面到候选区...";

        var result = await Task.Run(() => MoveCustomAlbumChartsToLibrary(gamePath));
        if (result.MovedCount <= 0 && result.FailedCount <= 0)
        {
            StatusMessage = "Custom_Albums 中没有可转移的谱面";
            return;
        }

        StatusMessage = result.FailedCount > 0
            ? $"已转移 {result.MovedCount} 张谱面到候选区，{result.FailedCount} 张失败"
            : $"已转移 {result.MovedCount} 张谱面到候选区";

        Reload();
    }

    private static TransferCustomAlbumResult MoveCustomAlbumChartsToLibrary(string gamePath)
    {
        var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
        var libraryDir = System.IO.Path.Combine(gamePath, "CustomAlbums_Library");
        if (!System.IO.Directory.Exists(albumsDir))
            return new TransferCustomAlbumResult(0, 0);

        System.IO.Directory.CreateDirectory(libraryDir);

        var movedCount = 0;
        var failedCount = 0;
        foreach (var sourcePath in System.IO.Directory.EnumerateFiles(albumsDir, "*.mdm", System.IO.SearchOption.AllDirectories))
        {
            try
            {
                var sourceDir = System.IO.Path.GetDirectoryName(sourcePath) ?? albumsDir;
                var relativeDir = System.IO.Path.GetRelativePath(albumsDir, sourceDir);
                if (relativeDir == "." || relativeDir.StartsWith("..", StringComparison.Ordinal))
                    relativeDir = string.Empty;

                var destinationDir = string.IsNullOrWhiteSpace(relativeDir)
                    ? libraryDir
                    : System.IO.Path.Combine(libraryDir, relativeDir);
                System.IO.Directory.CreateDirectory(destinationDir);

                var destinationPath = GetAvailableChartPath(destinationDir, System.IO.Path.GetFileName(sourcePath));
                System.IO.File.Move(sourcePath, destinationPath);
                movedCount++;
            }
            catch (Exception ex)
            {
                failedCount++;
                Console.WriteLine($"[ChartManager] Move custom album chart failed: {sourcePath} -> {ex.Message}");
            }
        }

        RemoveEmptyDirectories(albumsDir);
        return new TransferCustomAlbumResult(movedCount, failedCount);
    }

    private static string GetAvailableChartPath(string destinationDir, string fileName)
    {
        var candidate = System.IO.Path.Combine(destinationDir, fileName);
        if (!System.IO.File.Exists(candidate))
            return candidate;

        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var extension = System.IO.Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            candidate = System.IO.Path.Combine(destinationDir, $"{name}_{i}{extension}");
            if (!System.IO.File.Exists(candidate))
                return candidate;
        }
    }

    private static void RemoveEmptyDirectories(string rootDirectory)
    {
        foreach (var directory in System.IO.Directory.EnumerateDirectories(rootDirectory, "*", System.IO.SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
                    System.IO.Directory.Delete(directory);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChartManager] Remove empty category directory failed: {directory} -> {ex.Message}");
            }
        }
    }

    // 批量删除所有勾选的分类
    [RelayCommand]
    private async Task DeleteSelectedCategoriesAsync()
    {
        var toDelete = CategoryItems.Where(c => c.IsSelectedForDeletion && c.CanManage).Select(c => c.Name).ToList();
        if (toDelete.Count == 0) return;

        // 游戏运行时限制批量删除分类
        if (IsGameRunning())
        {
            var curApp = Avalonia.Application.Current;
            var curWindow = (curApp?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (curWindow != null)
            {
                await MessageBox.ShowDialogAsync(curWindow, Services.I18nService.Instance["Str_442"]);
            }
            return;
        }

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Game path not set, cannot delete" : "游戏路径未设置，无法删除";
            return;
        }

        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var confirmMsg = Services.I18nService.Instance.CurrentLanguage == "en-US"
            ? $"Are you sure you want to delete these {toDelete.Count} selected categories?\nDeleting categories will move all of their charts back to \"Uncategorized\"."
            : $"确定要删除选中的 {toDelete.Count} 个分类吗？\n删除分类将把其中的所有谱面文件移动回“未分类”。";
        var confirmed = await MessageBox.ShowDialogAsync(mainWindow, confirmMsg, true);
        if (!confirmed) return;

        StopCurrentPlayback();
        foreach (var existingChart in _allCharts)
        {
            existingChart.CleanupCoverResources();
        }

        try
        {
            int deletedCount = 0;

            foreach (var oldName in toDelete)
            {
                var isCandidate = IsCandidateCategory(oldName);
                var rootDir = System.IO.Path.Combine(gamePath, isCandidate ? "CustomAlbums_Library" : "Custom_Albums");
                var sourceDir = GetTargetFolder(gamePath, oldName);
                if (System.IO.Directory.Exists(sourceDir))
                {
                    var searchOption = isCandidate ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly;
                    foreach (var file in System.IO.Directory.GetFiles(sourceDir, "*.mdm", searchOption))
                    {
                        var destFile = isCandidate
                            ? GetAvailableChartPath(rootDir, System.IO.Path.GetFileName(file))
                            : System.IO.Path.Combine(rootDir, System.IO.Path.GetFileName(file));

                        if (!isCandidate && System.IO.File.Exists(destFile))
                            System.IO.File.Delete(destFile);

                        System.IO.File.Move(file, destFile);
                    }

                    System.IO.Directory.Delete(sourceDir, true);
                    deletedCount++;
                }
            }


            StatusMessage = $"成功删除 {deletedCount} 个分类，谱面已移回未分类";
            IsCategoryDeleteMode = false;

            if (toDelete.Contains(SelectedCategory))
            {
                SelectedCategory = IsCandidateCategory(SelectedCategory) ? CandidateCategoryKey : "全部";
            }

            Reload();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除分类失败: {ex.Message}";
        }
    }

    // 打开单张谱面移动面板
    [RelayCommand]
    private void OpenSingleMovePanel(ChartInfo chart)
    {
        CurrentMovingChart = chart;
        _movePanelSource = chart.IsLibraryCandidate ? CategoryPanelSource.Candidate : CategoryPanelSource.CustomAlbums;
        NotifyMovePanelSourceChanged();
        IsMovePanelOpen = true;
    }

    // 打开批量移动面板
    [RelayCommand]
    private void OpenBatchMovePanel()
    {
        CurrentMovingChart = null;
        _movePanelSource = IsCandidateCategory(SelectedCategory) ? CategoryPanelSource.Candidate : CategoryPanelSource.CustomAlbums;
        NotifyMovePanelSourceChanged();
        IsMovePanelOpen = true;
    }

    [RelayCommand]
    private void ToggleMovePanelSource()
    {
        _movePanelSource = _movePanelSource == CategoryPanelSource.Candidate
            ? CategoryPanelSource.CustomAlbums
            : CategoryPanelSource.Candidate;
        NotifyMovePanelSourceChanged();
    }

    private void NotifyMovePanelSourceChanged()
    {
        OnPropertyChanged(nameof(MovePanelSourceDisplay));
        OnPropertyChanged(nameof(MovePanelSourceToggleTip));
        OnPropertyChanged(nameof(MoveCategories));
    }

    // 关闭移动面板
    [RelayCommand]
    private void CloseMovePanel()
    {
        CurrentMovingChart = null;
        IsMovePanelOpen = false;
    }

    // 确认移动谱面至目标分类
    [RelayCommand]
    private async Task ConfirmMoveToCategoryAsync(MoveCategoryItem item)
    {
        if (item == null) return;

        IsMovePanelOpen = false;

        if (item.IsCreateNew)
        {
            await CreateCategoryAndMoveAsync(_movePanelSource == CategoryPanelSource.Candidate
                ? CategoryCreateScope.Candidate
                : CategoryCreateScope.Normal);
        }
        else
        {
            var targetCategory = item.Name;
            if (string.IsNullOrEmpty(targetCategory)) return;

            if (CurrentMovingChart != null)
            {
                await MoveSingleChartToCategoryAsync(CurrentMovingChart, targetCategory);
            }
            else
            {
                await MoveSelectedToCategoryAsync(targetCategory);
            }
        }
    }

    private async Task<CategoryCreateScope?> SelectCategoryCreateScopeAsync(Avalonia.Controls.Window mainWindow)
    {
        return await CategoryTypeDialog.ShowDialogAsync(mainWindow);
    }

    private static string SanitizeCategoryName(string value, bool allowPath)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');

        value = value.Trim();
        return allowPath
            ? value.Replace('\\', '/').Trim('/')
            : value.Replace('\\', '_').Replace('/', '_');
    }

    private static async Task WritePackJsonAsync(string targetDir, string catName)
    {
        var packJsonPath = System.IO.Path.Combine(targetDir, "pack.json");
        var packData = new
        {
            Title = catName,
            TitleColorHex = "#ffffff",
            LongTextScroll = false
        };
        var jsonStr = JsonSerializer.Serialize(packData, new JsonSerializerOptions { WriteIndented = true });
        await System.IO.File.WriteAllTextAsync(packJsonPath, jsonStr, System.Text.Encoding.UTF8);
    }

    private async Task<string?> CreateCategoryCoreAsync(CategoryCreateScope scope, Avalonia.Controls.Window mainWindow)
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = scope == CategoryCreateScope.Candidate
                ? "游戏路径未设置，无法创建候选区分类"
                : Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Game path not set, cannot create category" : "游戏路径未设置，无法创建分类";
            return null;
        }

        var isCandidate = scope == CategoryCreateScope.Candidate;
        var catName = await InputDialog.ShowDialogAsync(
            mainWindow,
            isCandidate ? "新建候选区分类" : "新建普通分类",
            isCandidate ? "请输入候选区内的新分类名称：" : "请输入普通分类名称（会创建游戏内常驻分类）：");
        if (string.IsNullOrWhiteSpace(catName))
            return null;

        catName = SanitizeCategoryName(catName, isCandidate);
        if (string.IsNullOrWhiteSpace(catName))
            return null;

        var targetCategory = isCandidate ? GetCandidateSubCategoryKey(catName) : catName;
        var targetDir = GetTargetFolder(gamePath, targetCategory);

        if (System.IO.Directory.Exists(targetDir))
        {
            await MessageBox.ShowDialogAsync(mainWindow, isCandidate ? "候选区分类已存在" : "分类已存在");
            return null;
        }

        try
        {
            System.IO.Directory.CreateDirectory(targetDir);
            if (!isCandidate)
            {
                await WritePackJsonAsync(targetDir, catName);
            }

            StatusMessage = isCandidate
                ? $"候选区分类《{catName}》创建成功"
                : $"分类《{catName}》创建成功";
            return targetCategory;
        }
        catch (Exception ex)
        {
            StatusMessage = isCandidate
                ? $"创建候选区分类失败: {ex.Message}"
                : $"创建分类失败: {ex.Message}";
            return null;
        }
    }

    // 当场新建分类并移动谱面
    private async Task CreateCategoryAndMoveAsync(CategoryCreateScope scope)
    {
        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var targetCategory = await CreateCategoryCoreAsync(scope, mainWindow);
        if (string.IsNullOrEmpty(targetCategory)) return;

        if (CurrentMovingChart != null)
        {
            await MoveSingleChartToCategoryAsync(CurrentMovingChart, targetCategory);
        }
        else
        {
            await MoveSelectedToCategoryAsync(targetCategory);
        }
    }

    private static string GetTargetFolder(string gamePath, string targetCategory)
    {
        if (targetCategory == CandidateCategoryKey)
            return System.IO.Path.Combine(gamePath, "CustomAlbums_Library");
        if (targetCategory.StartsWith(CandidateCategoryKey + "/", StringComparison.Ordinal))
            return System.IO.Path.Combine(gamePath, "CustomAlbums_Library", GetCandidateSubCategoryFromKey(targetCategory));

        var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
        return targetCategory == RootCategoryKey
            ? albumsDir
            : System.IO.Path.Combine(albumsDir, targetCategory);
    }
    // 移动单张谱面至特定分类
    public async Task MoveSingleChartToCategoryAsync(ChartInfo chart, string targetCategory)
    {
        if (chart == null || string.IsNullOrEmpty(targetCategory) || targetCategory == "全部") return;

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) return;

        string destFolder = GetTargetFolder(gamePath, targetCategory);

        if (!System.IO.Directory.Exists(destFolder))
        {
            System.IO.Directory.CreateDirectory(destFolder);
        }

        if (_playingChart == chart)
            StopCurrentPlayback();

        try
        {
            var destPath = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(chart.FilePath));
            if (chart.FilePath != destPath)
            {
                chart.CleanupCoverResources();
                if (System.IO.File.Exists(destPath))
                    System.IO.File.Delete(destPath);
                System.IO.File.Move(chart.FilePath, destPath);
    
                StatusMessage = $"成功移动谱面至分类《{targetCategory}》";
                Reload(CurrentPage);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChartManager] Move single error: {ex.Message}");
            StatusMessage = $"移动失败: {ex.Message}";
        }
    }

    // 批量移动选中的谱面至特定分类
    [RelayCommand]
    private async Task MoveSelectedToCategoryAsync(string targetCategory)
    {
        if (string.IsNullOrEmpty(targetCategory) || targetCategory == "全部") return;

        var toMove = _allCharts.Where(c => c.IsSelected).ToList();
        if (toMove.Count == 0) return;

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) return;

        string destFolder = GetTargetFolder(gamePath, targetCategory);

        if (!System.IO.Directory.Exists(destFolder))
        {
            System.IO.Directory.CreateDirectory(destFolder);
        }

        StopCurrentPlayback();

        int successCount = 0;
        foreach (var chart in toMove)
        {
            try
            {
                var destPath = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(chart.FilePath));
                if (chart.FilePath != destPath)
                {
                    chart.CleanupCoverResources();
                    if (System.IO.File.Exists(destPath))
                        System.IO.File.Delete(destPath);
                    System.IO.File.Move(chart.FilePath, destPath);
                    successCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChartManager] Move error: {ex.Message}");
            }
        }


        StatusMessage = $"成功移动 {successCount} 张谱面至分类《{targetCategory}》";
        Reload(CurrentPage);
    }

    // 新建分类的逻辑
    [RelayCommand]
    private async Task CreateCategoryAsync()
    {
        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var scope = await SelectCategoryCreateScopeAsync(mainWindow);
        if (scope == null) return;

        var targetCategory = await CreateCategoryCoreAsync(scope.Value, mainWindow);
        if (!string.IsNullOrEmpty(targetCategory))
        {
            SelectedCategory = targetCategory;
            Reload();
        }
    }

    private bool _isUpdatingSortOptions = false;

    // 动态刷新本地化排序文本列表
    private void UpdateSortOptions()
    {
        int currentIndex = SelectedSortIndex;
        _isUpdatingSortOptions = true;
        try
        {
            SortOptions.Clear();
            SortOptions.Add(Services.I18nService.Instance["Str_343"] ?? "按名称排序");
            SortOptions.Add(Services.I18nService.Instance["Str_352"] ?? "按分类排序");
            SortOptions.Add(Services.I18nService.Instance["Str_402"] ?? "难度从高到低");
            SortOptions.Add(Services.I18nService.Instance["Str_403"] ?? "难度从低到高");
        }
        finally
        {
            _isUpdatingSortOptions = false;
        }

        if (currentIndex >= 0 && currentIndex < SortOptions.Count)
        {
            SelectedSortIndex = currentIndex;
        }
        else
        {
            SelectedSortIndex = 0;
        }
    }

    // 解析单项难度数值以供比较
    private static bool TryParseDifficulty(string? diff, out int level)
    {
        level = 0;
        if (string.IsNullOrWhiteSpace(diff)) return false;

        string part = diff;
        int colonIdx = diff.LastIndexOf(':');
        if (colonIdx >= 0 && colonIdx < diff.Length - 1)
        {
            part = diff.Substring(colonIdx + 1);
        }

        var digits = new string(part.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (string.IsNullOrEmpty(digits)) return false;

        if (double.TryParse(digits, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            level = (int)Math.Floor(d);
            return true;
        }
        return false;
    }

    // 获取当前谱面所包含的最高难度
    private static int GetMaxDifficulty(ChartInfo chart)
    {
        if (chart.Difficulties == null || chart.Difficulties.Count == 0) return 0;
        int maxLevel = 0;
        foreach (var diff in chart.Difficulties)
        {
            if (TryParseDifficulty(diff, out int level))
            {
                if (level > maxLevel)
                    maxLevel = level;
            }
        }
        return maxLevel;
    }

    [RelayCommand]
    private void ToggleToolbox()
    {
        IsToolboxPanelOpen = !IsToolboxPanelOpen;
        if (IsToolboxPanelOpen)
        {
            IsToolboxMinimized = false;
            IsShowingDeduplication = false;
        }
    }

    [RelayCommand]
    private void MinimizeToolbox()
    {
        IsToolboxPanelOpen = false;
        IsToolboxMinimized = true;
    }

    [RelayCommand]
    private void CloseToolbox()
    {
        IsToolboxPanelOpen = false;
        IsToolboxMinimized = false;
    }

    [RelayCommand]
    private void RestoreToolbox()
    {
        IsToolboxPanelOpen = true;
        IsToolboxMinimized = false;
    }

    [RelayCommand]
    private void GoBackToToolbox()
    {
        IsShowingDeduplication = false;
    }

    [RelayCommand]
    private void ClickDuplicateGroup(DuplicateGroupItem item)
    {
        if (item == null) return;
        SearchText = item.Name;
        IsToolboxPanelOpen = false;
        IsToolboxMinimized = true;
    }

    [RelayCommand]
    private async Task RunDeduplicationAsync()
    {
        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var scope = await DeduplicationScopeDialog.ShowDialogAsync(mainWindow);
        if (scope == null) return;

        await RunDeduplicationForScopeAsync(scope.Value);
    }

    private async Task RunDeduplicationForScopeAsync(DeduplicationScanScope scope)
    {
        _currentDuplicateScanScope = scope;
        IsDeduplicationRunning = true;
        StatusMessage = $"{Services.I18nService.Instance["Str_420"] ?? "正在扫描重复谱面..."} ({GetDuplicateScanScopeLabel(scope)})";
        IsShowingDeduplication = true;

        var scanCharts = GetDuplicateScopeCharts(scope).ToList();
        await Task.Run(() =>
        {
            var groups = new Dictionary<string, List<ChartInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var chart in scanCharts)
            {
                if (chart == null || string.IsNullOrEmpty(chart.Name)) continue;

                var normTitle = NormalizeText(chart.Name);
                if (!groups.TryGetValue(normTitle, out var list))
                {
                    list = new List<ChartInfo>();
                    groups[normTitle] = list;
                }
                list.Add(chart);
            }

            var duplicateGroupItems = new List<DuplicateGroupItem>();
            foreach (var kvp in groups)
            {
                if (kvp.Value.Count > 1)
                {
                    var displayName = kvp.Value[0].Name;
                    duplicateGroupItems.Add(new DuplicateGroupItem
                    {
                        Name = displayName,
                        Charts = kvp.Value
                    });
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                DuplicateGroups.Clear();
                foreach (var item in duplicateGroupItems)
                {
                    DuplicateGroups.Add(item);
                }

                IsDeduplicationRunning = false;

                string resultPattern = Services.I18nService.Instance["Str_416"] ?? "扫描完成，发现 {0} 组共 {1} 个重复谱面。";
                int totalDupFiles = duplicateGroupItems.Sum(g => g.Charts.Count);
                StatusMessage = $"{GetDuplicateScanScopeLabel(scope)}：{string.Format(resultPattern, duplicateGroupItems.Count, totalDupFiles)}";
            });
        });
    }

    private void RefreshDuplicateGroupsIfNecessary()
    {
        if (DuplicateGroups.Count == 0 && !IsShowingDeduplication) return;

        var scope = _currentDuplicateScanScope;
        var scanCharts = GetDuplicateScopeCharts(scope).ToList();
        System.Threading.Tasks.Task.Run(() =>
        {
            var groups = new Dictionary<string, List<ChartInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var chart in scanCharts)
            {
                if (chart == null || string.IsNullOrEmpty(chart.Name)) continue;

                var normTitle = NormalizeText(chart.Name);
                if (!groups.TryGetValue(normTitle, out var list))
                {
                    list = new List<ChartInfo>();
                    groups[normTitle] = list;
                }
                list.Add(chart);
            }

            var duplicateGroupItems = new List<DuplicateGroupItem>();
            foreach (var kvp in groups)
            {
                if (kvp.Value.Count > 1)
                {
                    var displayName = kvp.Value[0].Name;
                    duplicateGroupItems.Add(new DuplicateGroupItem
                    {
                        Name = displayName,
                        Charts = kvp.Value
                    });
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                DuplicateGroups.Clear();
                foreach (var item in duplicateGroupItems)
                {
                    DuplicateGroups.Add(item);
                }

                if (IsShowingDeduplication)
                {
                    string resultPattern = Services.I18nService.Instance["Str_416"] ?? "扫描完成，发现 {0} 组共 {1} 个重复谱面。";
                    int totalDupFiles = duplicateGroupItems.Sum(g => g.Charts.Count);
                    StatusMessage = $"{GetDuplicateScanScopeLabel(scope)}：{string.Format(resultPattern, duplicateGroupItems.Count, totalDupFiles)}";
                }
                
                OnPropertyChanged(nameof(ToolboxStatusString));
            });
        });
    }

    [RelayCommand]
    private async Task AutoDeleteExactDuplicatesAsync()
    {
        if (DuplicateGroups.Count == 0 && !IsShowingDeduplication) return;

        var toDelete = new List<ChartInfo>();

        foreach (var group in DuplicateGroups)
        {
            var authorGroups = group.Charts.GroupBy(c => NormalizeText(c.ChartAuthor));
            
            foreach (var authorGroup in authorGroups)
            {
                var list = authorGroup.ToList();
                if (list.Count > 1)
                {
                    for (int i = 1; i < list.Count; i++)
                    {
                        toDelete.Add(list[i]);
                    }
                }
            }
        }

        if (toDelete.Count == 0)
        {
            StatusMessage = Services.I18nService.Instance["Str_444"] ?? "没有发现谱师和谱名完全相同的重复谱面。";
            return;
        }

        // 游戏运行时限制非未分类的重复谱面删除
        if (IsGameRunning() && toDelete.Any(c => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(c.FilePath)) != "Custom_Albums"))
        {
            var curApp = Avalonia.Application.Current;
            var curWindow = (curApp?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (curWindow != null)
            {
                await MessageBox.ShowDialogAsync(curWindow, Services.I18nService.Instance["Str_442"]);
            }
            return;
        }

        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        string confirmMsg = string.Format(Services.I18nService.Instance["Str_445"] ?? "是否确认自动删除 {0} 个谱名和谱师完全相同的重复谱面？\n此操作不可撤销！", toDelete.Count);
            
        var confirmed = await MessageBox.ShowDialogAsync(mainWindow, confirmMsg, true);
        if (!confirmed) return;

        int deletedCount = 0;
        foreach (var chart in toDelete)
        {
            try
            {
                if (System.IO.File.Exists(chart.FilePath))
                {
                    System.IO.File.Delete(chart.FilePath);
                    deletedCount++;
                }

                _chartIndexService.RemoveFromIndex(chart.FilePath);
                _allCharts.Remove(chart);
                _filteredCharts.Remove(chart);
                Charts.Remove(chart);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Toolbox] Failed to delete exact duplicate file {chart.FilePath}: {ex.Message}");
            }
        }

        string successMsg = string.Format(Services.I18nService.Instance["Str_446"] ?? "成功删除了 {0} 个完全重复的谱面文件。", deletedCount);
        StatusMessage = successMsg;

        await RunDeduplicationForScopeAsync(_currentDuplicateScanScope);
    }

    private IEnumerable<ChartInfo> GetDuplicateScopeCharts(DeduplicationScanScope scope)
    {
        return scope switch
        {
            DeduplicationScanScope.Library => _allCharts.Where(chart => chart.IsLibraryCandidate),
            DeduplicationScanScope.All => _allCharts,
            _ => _allCharts.Where(chart => !chart.IsLibraryCandidate)
        };
    }

    private static string GetDuplicateScanScopeLabel(DeduplicationScanScope scope)
    {
        var isEn = Services.I18nService.Instance.CurrentLanguage == "en-US";
        return scope switch
        {
            DeduplicationScanScope.Library => isEn ? "Candidate Library" : "候选区 Library",
            DeduplicationScanScope.All => isEn ? "All Areas" : "全部区域",
            _ => isEn ? "Active Custom_Albums" : "正式区 Custom_Albums"
        };
    }

    private string NormalizeText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var chars = s.ToLowerInvariant()
                     .Where(c => char.IsLetterOrDigit(c))
                     .ToArray();
        return new string(chars);
    }

    [RelayCommand]
    private void AutoSelectDuplicates(string strategy)
    {
        if (LocalDuplicatesList.Count == 0) return;

        var groups = LocalDuplicatesList.GroupBy(item => item.GroupKey);

        foreach (var grp in groups)
        {
            var itemsList = grp.ToList();
            if (itemsList.Count <= 1) continue;

            DuplicateCheckItem keepItem = itemsList[0];

            if (strategy == "keep_newest")
            {
                keepItem = itemsList.OrderByDescending(i => i.LastWriteTime).First();
            }
            else if (strategy == "keep_smallest")
            {
                keepItem = itemsList.OrderBy(i => i.FileSize).First();
            }
            else if (strategy == "keep_largest")
            {
                keepItem = itemsList.OrderByDescending(i => i.FileSize).First();
            }

            foreach (var item in itemsList)
            {
                item.IsRedundant = (item != keepItem);
            }
        }

        var temp = LocalDuplicatesList.ToList();
        LocalDuplicatesList.Clear();
        foreach (var item in temp)
        {
            LocalDuplicatesList.Add(item);
        }
    }

    [RelayCommand]
    private void ClickDuplicateChart(DuplicateCheckItem item)
    {
        if (item == null) return;

        SearchText = item.Name;
        IsToolboxPanelOpen = false;
        IsToolboxMinimized = true;
    }

    [RelayCommand]
    private async Task DeleteSelectedDuplicatesAsync()
    {
        var toDelete = LocalDuplicatesList.Where(i => i.IsRedundant).ToList();
        if (toDelete.Count == 0) return;

        // 游戏运行时限制非未分类的重复谱面删除
        if (IsGameRunning() && toDelete.Any(i => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(i.FilePath)) != "Custom_Albums"))
        {
            var curApp = Avalonia.Application.Current;
            var curWindow = (curApp?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (curWindow != null)
            {
                await MessageBox.ShowDialogAsync(curWindow, Services.I18nService.Instance["Str_442"]);
            }
            return;
        }

        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        string confirmTemplate = Services.I18nService.Instance["Str_421"] ?? "是否确认删除选中的 {0} 个重复谱面？\n此操作不可撤销！";
        var confirmed = await MessageBox.ShowDialogAsync(mainWindow, string.Format(confirmTemplate, toDelete.Count), true);
        if (!confirmed) return;

        int deletedCount = 0;
        foreach (var item in toDelete)
        {
            try
            {
                if (System.IO.File.Exists(item.FilePath))
                {
                    System.IO.File.Delete(item.FilePath);
                    deletedCount++;
                }

                _chartIndexService.RemoveFromIndex(item.FilePath);
                _allCharts.Remove(item.Chart);
                _filteredCharts.Remove(item.Chart);
                Charts.Remove(item.Chart);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Toolbox] Failed to delete duplicate file {item.FilePath}: {ex.Message}");
            }
        }

        string successTemplate = Services.I18nService.Instance["Str_422"] ?? "成功删除了 {0} 个重复的谱面文件。";
        StatusMessage = string.Format(successTemplate, deletedCount);

        LocalDuplicatesList.Clear();
        await RunDeduplicationForScopeAsync(_currentDuplicateScanScope);
    }
}

internal readonly record struct TransferCustomAlbumResult(int MovedCount, int FailedCount);

// 分类项目数据模型包装类
public class CategoryItem : ObservableObject
{
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string DisplayName => Name == "全部" ? MdModManager.Services.I18nService.Instance["Str_389"] : ChartManagerViewModel.GetCategoryDisplayName(Name);

    public bool IsGlobal => Name == "全部";

    public bool IsCandidate => Name.StartsWith(ChartManagerViewModel.CandidateCategoryKey, StringComparison.Ordinal);

    public bool IsCustomAlbums => Name == ChartManagerViewModel.RootCategoryKey || IsCustom;

    public bool IsCustom => Name != "全部" && Name != ChartManagerViewModel.RootCategoryKey && !Name.StartsWith(ChartManagerViewModel.CandidateCategoryKey, StringComparison.Ordinal);

    public bool CanManage => ChartManagerViewModel.CanManageCategoryName(Name);

    private bool _isSelectedForDeletion;
    public bool IsSelectedForDeletion
    {
        get => _isSelectedForDeletion;
        set => SetProperty(ref _isSelectedForDeletion, value);
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

// 移动目标分类项目包装类
public class MoveCategoryItem
{
    public string Name { get; set; } = string.Empty;
    public bool IsCreateNew { get; set; }

    public string DisplayName => Name == "新建分类" ? MdModManager.Services.I18nService.Instance["Str_391"] :
                                 (Name == "全部" ? MdModManager.Services.I18nService.Instance["Str_389"] : ChartManagerViewModel.GetCategoryDisplayName(Name));
}

// 重复谱面分组包装实体
public class DuplicateGroupItem
{
    // 分组名称歌曲名
    public string Name { get; set; } = string.Empty;

    // 分组内所包含的谱面列表
    public List<ChartInfo> Charts { get; set; } = new();
}









