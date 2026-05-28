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
    private const int PageSize = 16;
    public const string RootCategoryKey = "Root_Uncategorized";

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

    // 是否打开分类管理面板
    [ObservableProperty]
    private bool _isCategoryManagerPanelOpen;

    // 是否处于分类删除模式
    [ObservableProperty]
    private bool _isCategoryDeleteMode;

    // 当前准备移动的谱面
    [ObservableProperty]
    private ChartInfo? _currentMovingChart;

    [ObservableProperty]
    private ObservableCollection<string> _categories = new() { "全部", RootCategoryKey };

    // 可用于移动的目标分类列表
    public IEnumerable<MoveCategoryItem> MoveCategories
    {
        get
        {
            var list = new List<MoveCategoryItem>
            {
                new MoveCategoryItem { Name = "新建分类", IsCreateNew = true }
            };
            list.AddRange(Categories.Where(c => c != "全部" && c != RootCategoryKey).Select(c => new MoveCategoryItem { Name = c, IsCreateNew = false }));
            return list;
        }
    }

    // 分类包装项列表用于管理界面展示
    public ObservableCollection<CategoryItem> CategoryItems { get; } = new();

    // 勾选待删除分类的计数
    public int SelectedCategoriesForDeletionCount => CategoryItems.Count(c => c.IsSelectedForDeletion);

    // 是否有选中的分类准备被删除
    public bool HasSelectedCategoriesForDeletion => SelectedCategoriesForDeletionCount > 0;

    [ObservableProperty]
    private ObservableCollection<string> _sortOptions = new() { Services.I18nService.Instance["Str_343"], Services.I18nService.Instance["Str_352"] };

    [ObservableProperty]
    private int _selectedSortIndex = 0;

    partial void OnSelectedSortIndexChanged(int value)
    {
        CurrentPage = 1;
        ApplyFilter();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategorySelected))]
    [NotifyPropertyChangedFor(nameof(SelectedCategoryDisplay))]
    private string _selectedCategory = "全部";

    // 选中自定义分类
    public bool IsCategorySelected => SelectedCategory != "全部" && SelectedCategory != RootCategoryKey;

    // 限制分类名显示字数
    public string SelectedCategoryDisplay
    {
        get
        {
            string displayName = string.IsNullOrEmpty(SelectedCategory) || SelectedCategory == "全部" ? Services.I18nService.Instance["Str_389"] : 
                                 (SelectedCategory == RootCategoryKey ? (MdModManager.Services.I18nService.Instance.CurrentLanguage == "zh-CN" ? "未分类" : "Uncategorized") : SelectedCategory);
            return displayName.Length > 6 ? displayName.Substring(0, 6) + ".." : displayName;
        }
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
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

    partial void OnCurrentPageChanged(int value)
    {
        JumpPageText = value.ToString();
        UpdateIsAllSelected();
    }

    public bool EnableMarquee => _configService.Config.EnableChartNameMarquee;

    public bool CanLoadNext => CurrentPage < TotalPages && !IsLoading;
    public bool CanLoadPrev => CurrentPage > 1 && !IsLoading;

    // Audio playback
    private WaveOutEvent? _waveOut;
    private ChartInfo? _playingChart;
    private CancellationTokenSource? _stopCts;

    public ChartManagerViewModel(IChartService chartService, IConfigService configService, IDownloadManagerService downloadManagerService)
    {
        _chartService = chartService;
        _configService = configService;
        _downloadManagerService = downloadManagerService;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
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
    private void DeleteSelectedCharts()
    {
        var toDelete = _allCharts.Where(c => c.IsSelected).ToList();
        if (toDelete.Count == 0) return;

        StopCurrentPlayback();
        foreach (var chart in toDelete)
        {
            try
            {
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

    private void Reload()
    {
        StopCurrentPlayback();
        foreach (var existingChart in _allCharts.ToList())
            existingChart.CleanupCoverResources();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _allCharts.Clear();
            _filteredCharts.Clear();
            Charts.Clear();
            IsEmpty = true;
            HasNoSearchResults = false;
            HasVisibleCharts = false;
            IsCustomAlbumsMissing = false;
            CurrentPage = 1;
            TotalPages = 1;
            IsEditingPageNumber = false;
            IsBatchMode = false;
            SelectedCount = 0;
            IsAllSelected = false;
            IsLoading = true;
            StatusMessage = "正在加载...";
        });

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsLoading = false;
                StatusMessage = "游戏路径未设置，请先在设置中配置游戏目录";
            });
            return;
        }

        var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
        bool isCustomAlbumsMissing = !System.IO.Directory.Exists(albumsDir);

        var categories = new List<string> { "全部" };
        bool hasRootCharts = false;
        if (!isCustomAlbumsMissing)
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

        var charts = _chartService.LoadCharts(gamePath, _downloadManagerService.SessionDownloadedFiles)
            .OrderByDescending(chart => chart.IsNewDownload)
            .ThenBy(chart => chart.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(chart => System.IO.Path.GetFileName(chart.FilePath), StringComparer.OrdinalIgnoreCase)
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
            OnPropertyChanged(nameof(SelectedCategoriesForDeletionCount));
            OnPropertyChanged(nameof(HasSelectedCategoriesForDeletion));

            if (Categories.Contains(prevSelected))
                SelectedCategory = prevSelected;
            else
                SelectedCategory = "全部";

            foreach (var chart in charts)
                _allCharts.Add(chart);

            ApplyFilter();
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
            // 分类过滤
            if (SelectedCategory == RootCategoryKey)
            {
                var parentName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(chart.FilePath));
                if (parentName != "Custom_Albums")
                    continue;
            }
            else if (SelectedCategory != "全部")
            {
                var parentName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(chart.FilePath));
                if (parentName != SelectedCategory)
                    continue;
            }

            // 搜索过滤
            if (string.IsNullOrEmpty(search)
                || MdModManager.Helpers.SearchHelper.IsMatch(chart.Name, search, enableFuzzy)
                || MdModManager.Helpers.SearchHelper.IsMatch(chart.MusicAuthor, search, enableFuzzy)
                || MdModManager.Helpers.SearchHelper.IsMatch(chart.ChartAuthor, search, enableFuzzy))
            {
                _filteredCharts.Add(chart);
            }
        }

        // 排序规则
        var sorted = _filteredCharts.OrderByDescending(c => c.IsNewDownload);
        IOrderedEnumerable<ChartInfo> finalSorted;
        if (SelectedSortIndex == 1)
        {
            finalSorted = sorted
                .ThenBy(c => c.CategoryName, StringComparer.OrdinalIgnoreCase)
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
        Charts.Clear();

        foreach (var chart in _filteredCharts
                     .Skip((CurrentPage - 1) * PageSize)
                     .Take(PageSize))
        {
            Charts.Add(chart);
        }

        HasVisibleCharts = Charts.Count > 0;
        RequestedScrollY = 0;
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

        int categoryTotal = _allCharts.Count(c => {
            if (SelectedCategory == RootCategoryKey)
            {
                var parentName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(c.FilePath));
                return parentName == "Custom_Albums";
            }
            else if (SelectedCategory != "全部")
            {
                var parentName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(c.FilePath));
                return parentName == SelectedCategory;
            }
            return true;
        });

        if (SelectedCategory == "全部")
        {
            StatusMessage = string.Format(Services.I18nService.Instance["Str_344"], CurrentPage, TotalPages, _allCharts.Count);
        }
        else
        {
            StatusMessage = string.Format(Services.I18nService.Instance["Str_346"], categoryTotal);
        }
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
    private void DeleteChart(ChartInfo chart)
    {
        if (_playingChart == chart)
            StopCurrentPlayback();

        try
        {
            _chartService.DeleteChart(chart);
            chart.CleanupCoverResources();
            _allCharts.Remove(chart);
            ApplyFilter();
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
            StatusMessage = "游戏路径未设置，无法导入";
            return;
        }

        try
        {
            var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
            if (!System.IO.Directory.Exists(albumsDir))
                System.IO.Directory.CreateDirectory(albumsDir);

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

            var destFile = System.IO.Path.Combine(albumsDir, destFileName);
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
            StatusMessage = "打开失败: 游戏路径未设置";
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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = customAlbumsPath,
                UseShellExecute = true,
                Verb = "open"
            });
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
        IsCategoryManagerPanelOpen = true;
        IsCategoryDeleteMode = false;
        foreach (var item in CategoryItems)
        {
            item.IsSelectedForDeletion = false;
        }
        OnPropertyChanged(nameof(SelectedCategoriesForDeletionCount));
        OnPropertyChanged(nameof(HasSelectedCategoriesForDeletion));
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
        if (item != null && item.IsCustom)
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
        foreach (var item in CategoryItems)
        {
            if (item.IsCustom)
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
        foreach (var item in CategoryItems)
        {
            if (item.IsCustom)
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
        if (string.IsNullOrEmpty(oldName) || oldName == "全部" || oldName == "未分类" || oldName == "Uncategorized" || oldName == RootCategoryKey) return;

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = "游戏路径未设置，无法重命名";
            return;
        }

        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var newName = await InputDialog.ShowDialogAsync(mainWindow, "重命名分类", "请输入新的分类名称：", oldName);
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName)
            return;

        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            newName = newName.Replace(c, '_');

        var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
        var sourceDir = System.IO.Path.Combine(albumsDir, oldName);
        var targetDir = System.IO.Path.Combine(albumsDir, newName);

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

            StatusMessage = $"分类《{oldName}》已重命名为《{newName}》";
            if (SelectedCategory == oldName)
            {
                SelectedCategory = newName;
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
        if (string.IsNullOrEmpty(oldName) || oldName == "全部" || oldName == "未分类" || oldName == "Uncategorized" || oldName == RootCategoryKey) return;

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = "游戏路径未设置，无法删除";
            return;
        }

        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var confirmed = await MessageBox.ShowDialogAsync(mainWindow, $"确定要删除分类《{oldName}》吗？\n删除分类将把其中的所有谱面文件移动回“未分类”。", true);
        if (!confirmed) return;

        StopCurrentPlayback();
        foreach (var existingChart in _allCharts)
        {
            existingChart.CleanupCoverResources();
        }

        try
        {
            var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
            var sourceDir = System.IO.Path.Combine(albumsDir, oldName);

            if (System.IO.Directory.Exists(sourceDir))
            {
                foreach (var file in System.IO.Directory.GetFiles(sourceDir, "*.mdm"))
                {
                    var destFile = System.IO.Path.Combine(albumsDir, System.IO.Path.GetFileName(file));
                    if (System.IO.File.Exists(destFile))
                        System.IO.File.Delete(destFile);
                    System.IO.File.Move(file, destFile);
                }

                System.IO.Directory.Delete(sourceDir, true);
            }


            StatusMessage = $"分类《{oldName}》删除成功，谱面已移回未分类";
            if (SelectedCategory == oldName)
            {
                SelectedCategory = "全部";
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

    // 批量删除所有勾选的分类
    [RelayCommand]
    private async Task DeleteSelectedCategoriesAsync()
    {
        var toDelete = CategoryItems.Where(c => c.IsSelectedForDeletion && c.IsCustom).Select(c => c.Name).ToList();
        if (toDelete.Count == 0) return;

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = "游戏路径未设置，无法删除";
            return;
        }

        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var confirmed = await MessageBox.ShowDialogAsync(mainWindow, $"确定要删除选中的 {toDelete.Count} 个分类吗？\n删除分类将把其中的所有谱面文件移动回“未分类”。", true);
        if (!confirmed) return;

        StopCurrentPlayback();
        foreach (var existingChart in _allCharts)
        {
            existingChart.CleanupCoverResources();
        }

        try
        {
            var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
            int deletedCount = 0;

            foreach (var oldName in toDelete)
            {
                var sourceDir = System.IO.Path.Combine(albumsDir, oldName);
                if (System.IO.Directory.Exists(sourceDir))
                {
                    foreach (var file in System.IO.Directory.GetFiles(sourceDir, "*.mdm"))
                    {
                        var destFile = System.IO.Path.Combine(albumsDir, System.IO.Path.GetFileName(file));
                        if (System.IO.File.Exists(destFile))
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
                SelectedCategory = "全部";
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
        IsMovePanelOpen = true;
    }

    // 打开批量移动面板
    [RelayCommand]
    private void OpenBatchMovePanel()
    {
        CurrentMovingChart = null;
        IsMovePanelOpen = true;
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
            await CreateCategoryAndMoveAsync();
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

    // 当场新建分类并移动谱面
    private async Task CreateCategoryAndMoveAsync()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = "游戏路径未设置，无法创建分类";
            return;
        }

        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var catName = await InputDialog.ShowDialogAsync(mainWindow, "新建分类", "请输入新建分类的名称（将创建对应的合集文件夹）：");
        if (string.IsNullOrWhiteSpace(catName))
            return;

        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            catName = catName.Replace(c, '_');

        var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
        var targetDir = System.IO.Path.Combine(albumsDir, catName);

        if (System.IO.Directory.Exists(targetDir))
        {
            await MessageBox.ShowDialogAsync(mainWindow, "分类已存在");
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(targetDir);
            var packJsonPath = System.IO.Path.Combine(targetDir, "pack.json");
            var packData = new
            {
                Title = catName,
                TitleColorHex = "#ffffff",
                LongTextScroll = false
            };
            var jsonStr = JsonSerializer.Serialize(packData, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(packJsonPath, jsonStr, System.Text.Encoding.UTF8);

            StatusMessage = $"分类《{catName}》创建成功";

            if (CurrentMovingChart != null)
            {
                await MoveSingleChartToCategoryAsync(CurrentMovingChart, catName);
            }
            else
            {
                await MoveSelectedToCategoryAsync(catName);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"创建分类失败: {ex.Message}";
        }
    }

    // 移动单张谱面至特定分类
    public async Task MoveSingleChartToCategoryAsync(ChartInfo chart, string targetCategory)
    {
        if (chart == null || string.IsNullOrEmpty(targetCategory) || targetCategory == "全部") return;

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) return;

        var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
        string destFolder = targetCategory == RootCategoryKey 
            ? albumsDir 
            : System.IO.Path.Combine(albumsDir, targetCategory);

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
                Reload();
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

        var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
        string destFolder = targetCategory == RootCategoryKey 
            ? albumsDir 
            : System.IO.Path.Combine(albumsDir, targetCategory);

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
        Reload();
    }

    // 新建分类的逻辑
    [RelayCommand]
    private async Task CreateCategoryAsync()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            StatusMessage = "游戏路径未设置，无法创建分类";
            return;
        }

        var app = Avalonia.Application.Current;
        var mainWindow = (app?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var catName = await InputDialog.ShowDialogAsync(mainWindow, "新建分类", "请输入新建分类的名称（将创建对应的合集文件夹）：");
        if (string.IsNullOrWhiteSpace(catName))
            return;

        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            catName = catName.Replace(c, '_');

        var albumsDir = System.IO.Path.Combine(gamePath, "Custom_Albums");
        var targetDir = System.IO.Path.Combine(albumsDir, catName);

        if (System.IO.Directory.Exists(targetDir))
        {
            await MessageBox.ShowDialogAsync(mainWindow, "分类已存在");
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(targetDir);
            var packJsonPath = System.IO.Path.Combine(targetDir, "pack.json");
            var packData = new
            {
                Title = catName,
                TitleColorHex = "#ffffff",
                LongTextScroll = false
            };
            var jsonStr = JsonSerializer.Serialize(packData, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(packJsonPath, jsonStr, System.Text.Encoding.UTF8);

            StatusMessage = $"分类《{catName}》创建成功";
            Reload();
        }
        catch (Exception ex)
        {
            StatusMessage = $"创建分类失败: {ex.Message}";
        }
    }
}

// 分类项目数据模型包装类
public class CategoryItem : ObservableObject
{
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string DisplayName => Name == "全部" ? MdModManager.Services.I18nService.Instance["Str_389"] :
                                 (Name == ChartManagerViewModel.RootCategoryKey ? (MdModManager.Services.I18nService.Instance.CurrentLanguage == "zh-CN" ? "未分类" : "Uncategorized") : Name);

    public bool IsCustom => Name != "全部" && Name != ChartManagerViewModel.RootCategoryKey;

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
                                 (Name == "全部" ? MdModManager.Services.I18nService.Instance["Str_389"] :
                                 (Name == ChartManagerViewModel.RootCategoryKey ? (MdModManager.Services.I18nService.Instance.CurrentLanguage == "zh-CN" ? "未分类" : "Uncategorized") : Name));
}
