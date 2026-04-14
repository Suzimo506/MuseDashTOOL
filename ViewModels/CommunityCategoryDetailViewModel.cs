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

    // ── 搜索功能 ────────────────────────────────────────────────────────────
    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        CurrentPage = 1;
        _ = ReloadAsync();
    }

    // ── 排序 ──────────────────────────────────────────────────────────────────
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

    // ── 命令转发 ────────────────────────────────────────────────────────────
    public IAsyncRelayCommand<MdmcChart> TogglePreviewCommand => _chartDownloadViewModel.TogglePreviewCommand;
    public IAsyncRelayCommand<MdmcChart> DownloadChartCommand => _chartDownloadViewModel.DownloadChartCommand;
    public bool EnableMarquee => _chartDownloadViewModel.EnableMarquee;

    // ── 5 页 LRU 缓存 ────────────────────────────────────────────────────────
    private readonly Dictionary<string, (IList<MdmcChart> charts, int totalPages)> _pageCache = new();
    private readonly List<string> _cacheKeys = new();
    private const int MaxCachedPages = 5;

    // ── 全量索引数据 ──────────────────────────────────────────────────────────
    private List<MdmcChart> _allFullIndex = new();
    private List<MdmcChart> _filteredIndex = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private string _rawBaseUrl = ""; // e.g. https://raw.githubusercontent.com/user/repo/main
    private string _githubRepoUrl = ""; // e.g. https://github.com/user/repo
    private List<string> _defaultReleaseTags = new();

    private class CommunityIndexWrapper
    {
        [JsonPropertyName("release_tag")] public JsonElement ReleaseTag { get; set; }
        [JsonPropertyName("release_tags")] public JsonElement ReleaseTags { get; set; }
        [JsonPropertyName("charts")] public List<CommunityIndexItem> Charts { get; set; } = new();
    }

    private class CommunityIndexItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("original_id")] public string OriginalId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("artist")] public string Artist { get; set; } = "";
        [JsonPropertyName("charter")] public string Charter { get; set; } = "";
        [JsonPropertyName("bpm")] public string Bpm { get; set; } = "";
        [JsonPropertyName("scene")] public string Scene { get; set; } = "";
        [JsonPropertyName("difficulty")] public string Difficulty { get; set; } = "";
        [JsonPropertyName("difficulties")] public List<string>? Difficulties { get; set; }
        [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = "";
        [JsonPropertyName("demo_url")] public string DemoUrl { get; set; } = "";
        [JsonPropertyName("demo_mp3_url")] public string DemoMp3Url { get; set; } = "";
        [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("release_tag")] public JsonElement ReleaseTag { get; set; }
        [JsonPropertyName("release_tags")] public JsonElement ReleaseTags { get; set; }
        [JsonPropertyName("upload_time")] public string UploadTime { get; set; } = "";
    }

    // ── 滚动位置信号 ─────────────────────────────────────────────────────────
    [ObservableProperty] private double? _requestedScrollY;

    private string GetCacheKey(int page, int sortIndex, bool ascending, string query)
        => $"{CategoryName}|{sortIndex}|{ascending}|{query.Trim()}|{page}";

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
            RuntimeLog.Write("CommunityDetailVM", $"Cache full, evicted: {oldKey}");
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
        if (e.PropertyName == nameof(ChartDownloadViewModel.PreviewStatusText))
        {
            UpdateStatusMessage();
        }
    }

    public async Task InitializeAsync(string categoryName, string repoUrl = "")
    {
        // 重新建立监听
        _chartDownloadViewModel.PropertyChanged -= OnDownloadViewModelPropertyChanged;
        _chartDownloadViewModel.PropertyChanged += OnDownloadViewModelPropertyChanged;

        CategoryName = categoryName;
        _githubRepoUrl = repoUrl; 
        
        if (repoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            _rawBaseUrl = repoUrl.Replace("github.com", "raw.githubusercontent.com") + "/main";
        }
        else
        {
            // 对于 R2 存储 (download.suzimo.site)，repoUrl 已经是 Raw 基础地址
            _rawBaseUrl = repoUrl.TrimEnd('/');
        }
        
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
        var rawIndexUrl = _rawBaseUrl + $"/index.json?t={timestamp}";
        RepoUrl = GitHubMirrorHelper.ApplyMirror(rawIndexUrl, _configService.Config.DownloadSource);
        
        ClearPageCache();
        _allFullIndex.Clear();
        _filteredIndex.Clear();
        _defaultReleaseTags.Clear();
        CurrentPage = 1;
        SearchText = string.Empty;
        
        await FetchIndexAsync();
        await ReloadAsync();
    }

    private async Task FetchIndexAsync()
    {
        IsLoading = true;
        StatusMessage = "正在获取远程索引...";
        try
        {
            string json;
            var localPath = FindLocalIndexPath();
            
            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            {
                json = await File.ReadAllTextAsync(localPath);
                RuntimeLog.Write("CommunityDetailVM", $"Using local override index: {localPath}");
            }
            else
            {
                json = await _httpClient.GetStringAsync(RepoUrl);
                RuntimeLog.Write("CommunityDetailVM", $"Fetched remote index from {RepoUrl}");
            }

            List<CommunityIndexItem>? items = null;

            // 尝试解析新格式 { "release_tag": "..."/["..."], "charts": [...] }
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                var wrapper = JsonSerializer.Deserialize<CommunityIndexWrapper>(json, options);
                if (wrapper != null && wrapper.Charts != null && wrapper.Charts.Count > 0)
                {
                    _defaultReleaseTags = CommunityReleaseHelper.MergeReleaseTags(
                        null,
                        wrapper.ReleaseTag,
                        wrapper.ReleaseTags);
                    items = wrapper.Charts;
                }
            }
            catch (Exception ex1)
            {
                // 尝试解析旧格式：纯数组 [...]
                try 
                {
                    var optionsFallback = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                    items = JsonSerializer.Deserialize<List<CommunityIndexItem>>(json, optionsFallback);
                    _defaultReleaseTags.Clear();
                }
                catch (Exception ex2)
                {
                    RuntimeLog.Write("CommunityDetailVM", $"Parse failed... WrapperEx: {ex1.Message}, ArrayEx: {ex2.Message}");
                }
            }

            if (items != null)
            {
                _allFullIndex = items.Select(MapToIndexChart).ToList();

                // 未经审查：按 upload_time 倒序排列（最新上传的在前）
                if (CategoryName == "未经审查")
                {
                    _allFullIndex = _allFullIndex
                        .OrderByDescending(c => c.UploadedAt ?? DateTime.MinValue)
                        .ToList();
                }

                RuntimeLog.Write("CommunityDetailVM", $"Loaded {_allFullIndex.Count} charts (tags='{string.Join(", ", _defaultReleaseTags)}')");
            }
            else
            {
                throw new Exception("索引文件格式错误或为空");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("CommunityDetailVM", $"Failed to fetch index: {ex.Message}");
            StatusMessage = $"索引获取失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private string? FindLocalIndexPath()
    {
        var candidates = new List<string>();
        
        // 尝试的环境变量和基础路径
        var basePaths = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        
        foreach (var bp in basePaths)
        {
            candidates.Add(Path.Combine(bp, "SongRepository", CategoryName, "repo", "index.json"));
            candidates.Add(Path.Combine(bp, "SongRepository", CategoryName, "index.json"));
            
            // 向上查找 5 层父目录 (开发环境下 bin/Debug/... 结构)
            var current = new DirectoryInfo(bp);
            for (var depth = 0; depth < 5 && current != null; depth++, current = current.Parent)
            {
                candidates.Add(Path.Combine(current.FullName, "SongRepository", CategoryName, "repo", "index.json"));
                candidates.Add(Path.Combine(current.FullName, "SongRepository", CategoryName, "index.json"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private MdmcChart MapToIndexChart(CommunityIndexItem item)
    {
        // 如果 cover_url/demo_url/download_url 是相对文件名，补全为完整 raw URL
        var coverUrl = item.CoverUrl;
        if (!string.IsNullOrEmpty(coverUrl) && !coverUrl.StartsWith("http"))
            coverUrl = _rawBaseUrl + "/covers/" + coverUrl;

        var demoUrl = item.DemoUrl;
        if (!string.IsNullOrEmpty(demoUrl) && !demoUrl.StartsWith("http"))
            demoUrl = _rawBaseUrl + "/demos/" + demoUrl;

        var demoMp3Url = item.DemoMp3Url;
        if (!string.IsNullOrEmpty(demoMp3Url) && !demoMp3Url.StartsWith("http"))
            demoMp3Url = _rawBaseUrl + "/demos/" + demoMp3Url;

        var downloadUrl = item.DownloadUrl;
        if (_githubRepoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var candidateTags = CommunityReleaseHelper.MergeReleaseTags(_defaultReleaseTags, item.ReleaseTag, item.ReleaseTags);
            downloadUrl = CommunityReleaseHelper.ResolveReleaseDownloadUrl(downloadUrl, _githubRepoUrl, candidateTags);
        }
        else if (!string.IsNullOrEmpty(downloadUrl) && !downloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // 对于 R2 存储，谱面文件统一存放在 mdm/ 目录下
            downloadUrl = _rawBaseUrl + "/mdm/" + downloadUrl;
        }

        // 应用镜像/加速逻辑
        var source = _configService.Config.DownloadSource;
        coverUrl = GitHubMirrorHelper.ApplyMirror(coverUrl, source);
        demoUrl = GitHubMirrorHelper.ApplyMirror(demoUrl, source);
        downloadUrl = GitHubMirrorHelper.ApplyMirror(downloadUrl, source);

        // 兼容 difficulty (单值) 和 difficulties (列表) 两种格式
        var sheets = new List<MdmcSheet>();
        if (item.Difficulties != null && item.Difficulties.Count > 0)
        {
            sheets = item.Difficulties.Select(d => new MdmcSheet { Difficulty = d }).ToList();
        }
        else if (!string.IsNullOrEmpty(item.Difficulty) && item.Difficulty != "0" && item.Difficulty != "?")
        {
            var parts = item.Difficulty.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                sheets.Add(new MdmcSheet { Difficulty = p.Trim() });
            }
        }
        
        // 如果依然没有难度，尝试从 OriginalId (文件名) 中提取
        if (sheets.Count == 0 && !string.IsNullOrEmpty(item.OriginalId))
        {
            var diffMatch = Regex.Match(item.OriginalId, @"\[(?:Lv|LV|lv)[.\s]?\s*([0-9.,&里?？\s\-\+]+)\]", RegexOptions.IgnoreCase);
            if (diffMatch.Success)
            {
                var rawDiff = diffMatch.Groups[1].Value;
                // 提取所有数字（支持 10+ 格式）
                var nums = Regex.Matches(rawDiff, @"(\d+\+?)")
                    .Cast<Match>()
                    .Select(m => m.Value)
                    .Distinct();
                foreach (var n in nums) sheets.Add(new MdmcSheet { Difficulty = n });
            }
            else
            {
                // 尝试匹配开头的 Lv.x (无括号)
                var standaloneMatch = Regex.Match(item.OriginalId, @"^(?:Lv|LV|lv)[.\s]?\s*(\d+)", RegexOptions.IgnoreCase);
                if (standaloneMatch.Success) sheets.Add(new MdmcSheet { Difficulty = standaloneMatch.Groups[1].Value });
            }
        }

        // 清理标题：仅去掉名字最前面的 [Lv.x] 或 LVx 前缀，保留之后的所有信息（如括号等）
        // 优先使用 OriginalId，因为它保留了文件名中的完整信息
        string cleanTitle = item.OriginalId;
        if (string.IsNullOrEmpty(cleanTitle)) cleanTitle = item.Title ?? "";
        
        // 只匹配开头的等级前缀并移除
        cleanTitle = Regex.Replace(cleanTitle, @"^\[(?:Lv|LV|lv)[.\s]?\s*[^\]]+\]\s*", "", RegexOptions.IgnoreCase);
        cleanTitle = Regex.Replace(cleanTitle, @"^(?:Lv|LV|lv)[.\s]?\s*\d+\s*", "", RegexOptions.IgnoreCase);
        
        // 去掉可能的 .mdm 后缀
        if (cleanTitle.EndsWith(".mdm", StringComparison.OrdinalIgnoreCase))
            cleanTitle = cleanTitle.Substring(0, cleanTitle.Length - 4);

        cleanTitle = cleanTitle.Trim();

        var chart = new MdmcChart
        {
            Id = item.Id,
            Title = cleanTitle,
            Artist = item.Artist,
            Charter = item.Charter,
            Bpm = item.Bpm,
            CustomCoverUrl = coverUrl,
            CustomDemoUrl = demoUrl,
            CustomDemoMp3Url = demoMp3Url,
            CustomDownloadUrl = downloadUrl,
            Sheets = sheets,
            UploadedAt = DateTime.TryParseExact(item.UploadTime, "yyyy/MM/dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt) ? dt : null
        };
        return chart;
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
            foreach (var c in cached.charts) Charts.Add(c);
            _cacheKeys.Remove(cacheKey);
            _cacheKeys.Add(cacheKey);
            IsEmpty = Charts.Count == 0;
            IsLoading = false;
            UpdateStatusMessage();
            return;
        }

        // 1. 过滤与排序
        _filteredIndex = _allFullIndex;
        if (!string.IsNullOrWhiteSpace(query))
        {
            _filteredIndex = _allFullIndex.Where(c => 
                c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.Charter.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        // 目前 index.json 里没有上传时间，默认按标题排序
        if (IsSortByName)
        {
            _filteredIndex = IsAscending 
                ? _filteredIndex.OrderBy(c => c.Title).ToList() 
                : _filteredIndex.OrderByDescending(c => c.Title).ToList();
        }

        // 2. 分页处理
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

        // 翻页后滚动到顶部
        RequestedScrollY = 0;

        // 3. 异步加载封面
        _ = LoadCoversAsync(pageCharts);
    }

    private void UpdateStatusMessage()
    {
        // 只有正在缓冲或播放时显示试听状态
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

    [RelayCommand(CanExecute = nameof(CanLoadNext))]
    private async Task LoadNextPage()
    {
        if (CanLoadNext)
        {
            CurrentPage++;
            await ReloadAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadPrev))]
    private async Task LoadPrevPage()
    {
        if (CanLoadPrev)
        {
            CurrentPage--;
            await ReloadAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadPrev))]
    private async Task LoadFirstPageAsync()
    {
        CurrentPage = 1;
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanLoadNext))]
    private async Task LoadLastPageAsync()
    {
        CurrentPage = TotalPages;
        await ReloadAsync();
    }

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
        if (!IsEditingPageNumber) return;

        var text = JumpPageText;
        IsEditingPageNumber = false;

        if (string.IsNullOrWhiteSpace(text)) return;

        if (int.TryParse(text, out int targetPage))
        {
            CurrentPage = Math.Clamp(targetPage, 1, TotalPages);
            await ReloadAsync();
        }
    }

    private async Task LoadCoversAsync(IEnumerable<MdmcChart> pageCharts)
    {
        var tasks = pageCharts.Select(async chart =>
        {
            if (chart.CoverImage != null || string.IsNullOrEmpty(chart.CoverUrl)) return;

            await _coverSemaphore.WaitAsync();
            try
            {
                var url = GitHubMirrorHelper.ApplyMirror(chart.CoverUrl, _configService.Config.DownloadSource);
                var bytes = await _httpClient.GetByteArrayAsync(url);
                using var stream = new MemoryStream(bytes);
                chart.CoverImage = new Avalonia.Media.Imaging.Bitmap(stream);
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("CommunityDetailVM", $"Failed to load cover for {chart.Id}: {ex.Message}");
            }
            finally
            {
                _coverSemaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            _chartDownloadViewModel.StopPlayback();
            _chartDownloadViewModel.PropertyChanged -= OnDownloadViewModelPropertyChanged;
            var vm = Ioc.Default.GetRequiredService<AlbumCollectionViewModel>();
            mainVm.CurrentPage = vm;
        }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _chartDownloadViewModel.PropertyChanged -= OnDownloadViewModelPropertyChanged;
        ClearPageCache(); 
        _allFullIndex.Clear();
        _filteredIndex.Clear();
        Charts.Clear();
        _httpClient.Dispose();
    }
}
