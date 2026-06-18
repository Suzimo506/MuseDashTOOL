using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.Helpers;
using MdModManager.Views;
using NAudio.Vorbis;
using NAudio.Wave;

namespace MdModManager.ViewModels;

// 分发版本清单项实体
public class ManifestVersionEntry
{
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
}

// 分发资源清单项实体
public class ManifestSlugEntry
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("file_extension")]
    public string FileExtension { get; set; } = string.Empty;

    [JsonPropertyName("versions")]
    public Dictionary<string, ManifestVersionEntry> Versions { get; set; } = new();
}

// 谱面打包下载响应实体
public class BuildZipResponse
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;
}

// AOT 兼容的 JSON 序列化上下文
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EuterpeChart))]
[JsonSerializable(typeof(List<EuterpeChart>))]
[JsonSerializable(typeof(EuterpeSearchResponse))]
[JsonSerializable(typeof(MapSlotInfo))]
[JsonSerializable(typeof(List<MapSlotInfo>))]
[JsonSerializable(typeof(ManifestSlugEntry))]
[JsonSerializable(typeof(ManifestVersionEntry))]
[JsonSerializable(typeof(Dictionary<string, ManifestVersionEntry>))]
[JsonSerializable(typeof(BuildZipResponse))]
[JsonSerializable(typeof(EuterpeTag))]
[JsonSerializable(typeof(List<EuterpeTag>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class EuterpeChartJsonContext : JsonSerializerContext;

// 排序选项包装类
public class EuterpeSortOption
{
    public string Label { get; }
    public string Value { get; }

    public EuterpeSortOption(string label, string value)
    {
        Label = label;
        Value = value;
    }
}

public partial class EuterpeViewModel : ObservableObject, IDisposable
{
    private readonly IAuthService _authService;
    private readonly AuthState _authState;
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    private readonly IDownloadManagerService _downloadManagerService;
    private readonly HttpClient _httpClient;

    // 谱面集合
    public ObservableCollection<EuterpeChart> Charts { get; } = new();

    // 标签集合与选中标签
    public ObservableCollection<EuterpeTag> Tags { get; } = new();
    [ObservableProperty]
    private bool _isTagPanelOpen;
    [ObservableProperty]
    private EuterpeTag? _selectedTag;

    [RelayCommand]
    private void ToggleTagPanel()
    {
        IsTagPanelOpen = !IsTagPanelOpen;
    }

    [RelayCommand]
    private void SelectTag(EuterpeTag tag)
    {
        if (tag == null) return;

        foreach (var t in Tags)
        {
            t.IsSelected = (t == tag);
        }

        SelectedTag = tag;
        IsTagPanelOpen = false;

        _cursors.Clear();
        _cursors.Add(null);
        CurrentPage = 1;
        _ = ReloadAsync();
    }

    // 页面状态
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    [NotifyCanExecuteChangedFor(nameof(LoadNextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadPrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadFirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadLastPageCommand))]
    private bool _isLoading;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _statusMessage = "正在初始化…";
    [ObservableProperty] private string _previewStatusText = string.Empty;

    // 用户信息
    public string Nickname => _authState.CurrentUser?.Nickname ?? "未登录";
    public string Email => _authState.CurrentUser?.Email ?? string.Empty;
    public string AvatarUrl => _authState.AvatarUrl;

    // 歌名滚动显示配置映射
    public bool EnableMarquee => _configService.Config.EnableChartNameMarquee;

    // 搜索词
    [ObservableProperty] private string _searchDraftText = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;

    // 排序选项
    public EuterpeSortOption[] SortOptions { get; } = new[]
    {
        new EuterpeSortOption(I18nService.Instance["Str_398"] ?? "推荐排序", "recommended"),
        new EuterpeSortOption(I18nService.Instance["Str_399"] ?? "最新上传", "created_at"),
        new EuterpeSortOption(I18nService.Instance["Str_400"] ?? "最多点赞", "likes"),
        new EuterpeSortOption(I18nService.Instance["Str_401"] ?? "最多下载", "downloads"),
        new EuterpeSortOption(I18nService.Instance["Str_402"] ?? "难度从高到低", "rating_desc"),
        new EuterpeSortOption(I18nService.Instance["Str_403"] ?? "难度从低到高", "rating_asc")
    };

    private int _selectedSortIndex = 0;
    public int SelectedSortIndex
    {
        get => _selectedSortIndex;
        set
        {
            if (SetProperty(ref _selectedSortIndex, value))
            {
                _cursors.Clear();
                _cursors.Add(null);
                CurrentPage = 1;
                _ = ReloadAsync();
            }
        }
    }

    // 游标分页
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
    [NotifyCanExecuteChangedFor(nameof(LoadPrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadFirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadLastPageCommand))]
    private int _totalPages = 1;

    private readonly List<string?> _cursors = new() { null };

    public bool CanLoadNext => CurrentPage < TotalPages && !IsLoading;
    public bool CanLoadPrev => CurrentPage > 1 && !IsLoading;

    // 是否处于编辑页码状态
    [ObservableProperty]
    private bool _isEditingPageNumber;

    // 快速跳转文本
    [ObservableProperty]
    private string _jumpPageText = "1";

    // 页码变更同步文本
    partial void OnCurrentPageChanged(int value)
    {
        JumpPageText = value.ToString();
    }

    // 开始编辑页码
    [RelayCommand]
    private void StartEditPage()
    {
        JumpPageText = CurrentPage.ToString();
        IsEditingPageNumber = true;
    }

    // 取消编辑页码
    [RelayCommand]
    private void CancelEditPage()
    {
        JumpPageText = CurrentPage.ToString();
        IsEditingPageNumber = false;
    }

    // 执行快速跳转
    [RelayCommand]
    private async Task JumpPageAsync()
    {
        if (!IsEditingPageNumber) return;

        var text = JumpPageText;
        IsEditingPageNumber = false;

        if (string.IsNullOrWhiteSpace(text)) return;

        if (int.TryParse(text, out int targetPage))
        {
            targetPage = Math.Clamp(targetPage, 1, TotalPages);
            if (targetPage == CurrentPage)
            {
                JumpPageText = CurrentPage.ToString();
                return;
            }

            if (targetPage > _cursors.Count)
            {
                IsLoading = true;
                StatusMessage = "正在获取分页游标…";
                try
                {
                    while (_cursors.Count < targetPage)
                    {
                        var currentFetchCursor = _cursors.Last();
                        var sort = SortOptions[SelectedSortIndex].Value;
                        var query = SearchText.Trim();
                        var path = $"charts/search?size=15&sort={Uri.EscapeDataString(sort)}";
                        if (!string.IsNullOrEmpty(query))
                        {
                            path += $"&q={Uri.EscapeDataString(query)}";
                        }
                        if (SelectedTag != null && !string.IsNullOrEmpty(SelectedTag.TagId))
                        {
                            path += $"&tags={Uri.EscapeDataString(SelectedTag.TagId)}&tag_match=all";
                        }
                        if (!string.IsNullOrEmpty(currentFetchCursor))
                        {
                            path += $"&cursor={Uri.EscapeDataString(currentFetchCursor)}";
                        }

                        using var req = new HttpRequestMessage(HttpMethod.Get, path);
                        using var response = await _httpClient.SendAsync(req);
                        response.EnsureSuccessStatusCode();

                        var json = await response.Content.ReadAsStringAsync();
                        var result = JsonSerializer.Deserialize(json, EuterpeChartJsonContext.Default.EuterpeSearchResponse);

                        if (result != null && !string.IsNullOrEmpty(result.NextCursor))
                        {
                            _cursors.Add(result.NextCursor);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _notificationService.ShowFailure("获取分页游标失败", ex.Message);
                    IsLoading = false;
                    UpdateStatusMessage();
                    return;
                }
            }

            CurrentPage = Math.Min(targetPage, _cursors.Count);
            await ReloadAsync();
        }
    }

    // 滚动事件信号
    [ObservableProperty] private double? _requestedScrollY;

    // NAudio 播放状态
    private WaveOutEvent? _waveOut;
    private EuterpeChart? _playingChart;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _listCts;

    private readonly IChartIndexService _chartIndexService;

    [ObservableProperty]
    private bool _isDuplicateDialogOpen;

    [ObservableProperty]
    private EuterpeChart? _duplicateDialogTarget;

    [ObservableProperty]
    private List<ChartInfo> _duplicateDialogItems = new();

    public EuterpeViewModel(
        IAuthService authService,
        AuthState authState,
        IConfigService configService,
        INotificationService notificationService,
        IDownloadManagerService downloadManagerService,
        AuthHeaderHandler authHeaderHandler,
        IChartIndexService chartIndexService)
    {
        _authService = authService;
        _authState = authState;
        _configService = configService;
        _notificationService = notificationService;
        _downloadManagerService = downloadManagerService;
        _chartIndexService = chartIndexService;

        _httpClient = new HttpClient(authHeaderHandler) { BaseAddress = new Uri("https://euterpe-org.com/api/") };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MuseDashTOOL/1.4.8.3");
    }

    // 初始化加载
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _cursors.Clear();
        _cursors.Add(null);
        CurrentPage = 1;
        SearchDraftText = SearchText;

        // 加载 Euterpe 标签
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "tags");
            using var response = await _httpClient.SendAsync(req, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var fetchedTags = JsonSerializer.Deserialize(json, EuterpeChartJsonContext.Default.ListEuterpeTag);

            Tags.Clear();
            Tags.Add(new EuterpeTag(string.Empty, string.Empty, 0, true, 0, new Dictionary<string, string> { { "zh", "全部标签" } }));
            if (fetchedTags != null)
            {
                foreach (var tag in fetchedTags.Where(t => t.IsActive && t.Popularity > 0).OrderBy(t => t.Category).ThenBy(t => t.SortOrder))
                {
                    Tags.Add(tag);
                }
            }
            if (Tags.Count > 0)
            {
                SelectedTag = Tags[0];
                Tags[0].IsSelected = true;
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowFailure("加载标签失败", ex.Message);
        }

        await ReloadAsync(ct);
    }

    // 刷新命令
    [RelayCommand]
    private async Task RefreshAsync()
    {
        _cursors.Clear();
        _cursors.Add(null);
        CurrentPage = 1;
        await ReloadAsync(default, true);
    }

    // 执行搜索
    [RelayCommand]
    public async Task ApplySearchAsync()
    {
        var newQuery = SearchDraftText?.Trim() ?? string.Empty;
        if (string.Equals(newQuery, SearchText, StringComparison.Ordinal))
            return;

        SearchText = newQuery;
        _cursors.Clear();
        _cursors.Add(null);
        CurrentPage = 1;
        await ReloadAsync();
    }

    // 清除搜索
    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        SearchDraftText = string.Empty;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            SearchText = string.Empty;
            _cursors.Clear();
            _cursors.Add(null);
            CurrentPage = 1;
            await ReloadAsync();
        }
    }

    // 下一页命令
    [RelayCommand(CanExecute = nameof(CanLoadNext))]
    private async Task LoadNextPageAsync()
    {
        CurrentPage++;
        await ReloadAsync();
    }

    // 上一页命令
    [RelayCommand(CanExecute = nameof(CanLoadPrev))]
    private async Task LoadPrevPageAsync()
    {
        CurrentPage--;
        await ReloadAsync();
    }

    // 第一页命令
    [RelayCommand(CanExecute = nameof(CanLoadPrev))]
    private async Task LoadFirstPageAsync()
    {
        CurrentPage = 1;
        await ReloadAsync();
    }

    // 最末页命令
    [RelayCommand(CanExecute = nameof(CanLoadNext))]
    private async Task LoadLastPageAsync()
    {
        int targetPage = TotalPages;
        if (targetPage > _cursors.Count)
        {
            IsLoading = true;
            StatusMessage = "正在获取分页游标…";
            try
            {
                while (_cursors.Count < targetPage)
                {
                    string? currentFetchCursor = _cursors.Last();
                    string sort = SortOptions[SelectedSortIndex].Value;
                    string query = SearchText.Trim();
                    string path = $"charts/search?size=15&sort={Uri.EscapeDataString(sort)}";
                    if (!string.IsNullOrEmpty(query))
                    {
                        path += $"&q={Uri.EscapeDataString(query)}";
                    }
                    if (SelectedTag != null && !string.IsNullOrEmpty(SelectedTag.TagId))
                    {
                        path += $"&tags={Uri.EscapeDataString(SelectedTag.TagId)}&tag_match=all";
                    }
                    if (!string.IsNullOrEmpty(currentFetchCursor))
                    {
                        path += $"&cursor={Uri.EscapeDataString(currentFetchCursor)}";
                    }

                    using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, path);
                    using HttpResponseMessage response = await _httpClient.SendAsync(req);
                    response.EnsureSuccessStatusCode();

                    string json = await response.Content.ReadAsStringAsync();
                    EuterpeSearchResponse? result = JsonSerializer.Deserialize(json, EuterpeChartJsonContext.Default.EuterpeSearchResponse);

                    if (result != null && !string.IsNullOrEmpty(result.NextCursor))
                    {
                        _cursors.Add(result.NextCursor);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowFailure("获取分页游标失败", ex.Message);
                IsLoading = false;
                UpdateStatusMessage();
                return;
            }
        }

        CurrentPage = Math.Min(targetPage, _cursors.Count);
        await ReloadAsync();
    }

    // 谱面加载核心逻辑
    private async Task ReloadAsync(CancellationToken externalCt = default, bool force = false)
    {
        _listCts?.Cancel();
        _listCts = new CancellationTokenSource();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_listCts.Token, externalCt);
        var ct = linkedCts.Token;

        StopPlayback();
        IsLoading = true;
        StatusMessage = "正在加载谱面列表…";

        try
        {
            var sort = SortOptions[SelectedSortIndex].Value;
            var query = SearchText.Trim();
            var cursor = _cursors[CurrentPage - 1];

            // 构造请求 URL 编码
            var path = $"charts/search?size=15&sort={Uri.EscapeDataString(sort)}";
            if (!string.IsNullOrEmpty(query))
            {
                path += $"&q={Uri.EscapeDataString(query)}";
            }
            if (SelectedTag != null && !string.IsNullOrEmpty(SelectedTag.TagId))
            {
                path += $"&tags={Uri.EscapeDataString(SelectedTag.TagId)}&tag_match=all";
            }
            if (!string.IsNullOrEmpty(cursor))
            {
                path += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await _httpClient.SendAsync(req, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize(json, EuterpeChartJsonContext.Default.EuterpeSearchResponse);

            if (result != null)
            {
                Charts.Clear();
                foreach (var item in result.Items)
                {
                    Charts.Add(item);
                }

                // 从本地缓存恢复点赞状态
                var likedSet = new HashSet<long>(_configService.Config.EuterpeLikedCids);
                foreach (var c in Charts)
                {
                    if (likedSet.Contains(c.Cid))
                        c.IsLiked = true;
                }

                IsEmpty = Charts.Count == 0;

                // 游标历史追踪
                if (CurrentPage == _cursors.Count && !string.IsNullOrEmpty(result.NextCursor))
                {
                    _cursors.Add(result.NextCursor);
                }

                // 粗略计算总页数
                TotalPages = Math.Max(1, (int)Math.Ceiling((double)result.Total / 15));
                if (!string.IsNullOrEmpty(result.NextCursor) && TotalPages <= CurrentPage)
                {
                    TotalPages = CurrentPage + 1;
                }

                // 异步预载封面大图与标签列表
                _ = LoadCoversAsync(ct);
                _ = LoadTagsAsync(ct);
            }

            RequestedScrollY = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = "加载失败：" + ex.Message;
            _notificationService.ShowFailure("获取谱面失败", ex.Message);
        }
        finally
        {
            IsLoading = false;
            UpdateStatusMessage();
        }
    }

    // 异步拉取并缓冲本页所有谱面的封面
    private async Task LoadCoversAsync(CancellationToken ct)
    {
        var snapshot = Charts.ToList();
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "MuseDashTOOL/1.4.8.3");

        foreach (var chart in snapshot)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(chart.CoverUrl)) continue;

            try
            {
                var bytes = await client.GetByteArrayAsync(chart.CoverUrl, ct);
                using var ms = new MemoryStream(bytes);
                chart.CoverImage = new Bitmap(ms);
            }
            catch
            {
                // 封面加载失败时忽略，UI 将自动回退为默认提示图
            }
        }
    }

    // 异步拉取并缓冲本页所有谱面的标签
    private async Task LoadTagsAsync(CancellationToken ct)
    {
        var snapshot = Charts.ToList();

        foreach (var chart in snapshot)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"charts/{chart.Cid}/tags");
                using var response = await _httpClient.SendAsync(req, ct);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    var tags = JsonSerializer.Deserialize(json, EuterpeChartJsonContext.Default.ListEuterpeTag);
                    if (tags != null)
                    {
                        chart.Tags = tags;
                        chart.HasTags = tags.Count > 0;
                    }
                }
            }
            catch
            {
                // 加载标签失败时忽略
            }
        }
    }

    // 切换试听音频
    [RelayCommand]
    private async Task TogglePreviewAsync(EuterpeChart chart)
    {
        if (_playingChart == chart)
        {
            StopPlayback();
            return;
        }

        StopPlayback();
        await PlayDemoAsync(chart);
    }

    // 播放试听文件
    private async Task PlayDemoAsync(EuterpeChart chart)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
            PreviewStatusText = $"正在缓冲 {chart.Name} 试听音频";
            UpdateStatusMessage();

            // 获取令牌
            var token = await _authService.GetAccessTokenAsync();

            // 构造音频文件直连地址
            var previewUrl = $"https://dl.euterpe-org.com/files/charts/{chart.Cid}/demo.ogg";
            if (!string.IsNullOrEmpty(token))
            {
                previewUrl += $"?t={Uri.EscapeDataString(token)}";
            }

            // 请求音频文件字节数据
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "MuseDashTOOL/1.4.8.3");
            using var req = new HttpRequestMessage(HttpMethod.Get, previewUrl);
            using var response = await client.SendAsync(req, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            if (ct.IsCancellationRequested) return;

            var ms = new MemoryStream(bytes);
            var waveProvider = new VorbisWaveReader(ms);

            _waveOut = new WaveOutEvent();
            _waveOut.Init(waveProvider);
            _waveOut.Volume = (float)_configService.Config.ChartPreviewVolume;

            var cts = _stopCts = new CancellationTokenSource();

            _waveOut.PlaybackStopped += (_, _) =>
            {
                if (!cts.IsCancellationRequested)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (_playingChart == chart)
                        {
                            _playingChart = null;
                            PreviewStatusText = string.Empty;
                            UpdateStatusMessage();
                        }
                        chart.IsPlaying = false;
                    });
                }
                waveProvider.Dispose();
                ms.Dispose();
            };

            _waveOut.Play();
            _playingChart = chart;
            chart.IsPlaying = true;

            PreviewStatusText = $"正在播放 {chart.Name} 试听";
            UpdateStatusMessage();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notificationService.ShowFailure("试听加载失败", ex.Message);
            PreviewStatusText = string.Empty;
            UpdateStatusMessage();
        }
    }

    // 停止音频播放
    public void StopPlayback()
    {
        _loadCts?.Cancel();
        _loadCts = null;

        _stopCts?.Cancel();
        _stopCts = null;

        if (_playingChart != null)
        {
            _playingChart.IsPlaying = false;
            _playingChart = null;
        }

        PreviewStatusText = string.Empty;
        UpdateStatusMessage();

        var waveOut = _waveOut;
        _waveOut = null;
        if (waveOut != null)
        {
            Task.Run(() =>
            {
                try
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                }
                catch { /* 忽略底驱动延迟异常 */ }
            });
        }
    }

    // 点赞 / 取消点赞切换
    [RelayCommand]
    private async Task ToggleLikeAsync(EuterpeChart chart)
    {
        try
        {
            var isLiked = chart.IsLiked;

            // 构造请求
            var method = isLiked ? HttpMethod.Delete : HttpMethod.Post;
            using var req = new HttpRequestMessage(method, $"charts/{chart.Cid}/like");
            using var response = await _httpClient.SendAsync(req);
            response.EnsureSuccessStatusCode();

            chart.IsLiked = !isLiked;
            chart.LikeCount += isLiked ? -1 : 1;

            // 同步本地缓存
            var likedCids = _configService.Config.EuterpeLikedCids;
            if (chart.IsLiked)
            {
                if (!likedCids.Contains(chart.Cid))
                    likedCids.Add(chart.Cid);
            }
            else
            {
                likedCids.Remove(chart.Cid);
            }
            await _configService.SaveAsync();
        }
        catch (Exception ex)
        {
            _notificationService.ShowFailure("点赞交互失败", ex.Message);
        }
    }

    // 谱面安全映射并投递至下载器
    [RelayCommand]
    private async Task DownloadChartAsync(EuterpeChart chart)
    {
        if (!DotNetRuntimeHelper.IsDotNet6Installed())
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var mainWindow = desktop?.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                await mainWindow.ShowMessageBoxAsync(MdModManager.Services.I18nService.Instance["Str_404"] ?? "请先安装.net6环境！");
                return;
            }
        }

        if (_configService.Config.EnableDownloadDuplicateCheck)
        {
            var duplicates = _chartIndexService.FindDuplicatesByTitle(chart.Name);
            if (duplicates.Count > 0)
            {
                DuplicateDialogTarget = chart;
                DuplicateDialogItems = duplicates;
                IsDuplicateDialogOpen = true;
                return;
            }
        }

        await ExecuteDownloadAsync(chart);
    }

    private async Task ExecuteDownloadAsync(EuterpeChart chart)
    {
        try
        {
            var token = await _authService.GetAccessTokenAsync();

            var buildZipPath = $"workspace/charts/{chart.Cid}/build-zip";
            using var buildReq = new HttpRequestMessage(HttpMethod.Post, buildZipPath);
            using var buildResponse = await _httpClient.SendAsync(buildReq);
            buildResponse.EnsureSuccessStatusCode();

            var json = await buildResponse.Content.ReadAsStringAsync();
            var buildZipResult = JsonSerializer.Deserialize(json, EuterpeChartJsonContext.Default.BuildZipResponse);
            if (buildZipResult == null || string.IsNullOrEmpty(buildZipResult.Path))
            {
                throw new Exception("未找到可用的谱面下载版本");
            }

            var zipDownloadUrl = buildZipResult.Path;
            if (zipDownloadUrl.Contains("euterpe-org.com", StringComparison.OrdinalIgnoreCase) && !zipDownloadUrl.Contains("t="))
            {
                var connector = zipDownloadUrl.Contains('?') ? "&" : "?";
                zipDownloadUrl += $"{connector}t={Uri.EscapeDataString(token)}";
            }

            var mdmc = new MdmcChart
            {
                Id = chart.Cid.ToString(),
                Title = chart.Name,
                Artist = chart.Author,
                Bpm = chart.Bpm.ToString(),
                CustomCoverUrl = chart.CoverUrl,
                CustomDownloadUrl = zipDownloadUrl,
                SourceCategoryName = "Euterpe",
                IsCommunitySource = true,
                Sheets = chart.Maps.Select(m => new MdmcSheet
                {
                    Difficulty = m.Rating,
                    RankedDifficulty = int.TryParse(m.Rating, out var rd) ? rd : 0,
                    Charter = string.Join(", ", m.Charters)
                }).ToList()
            };

            _downloadManagerService.EnqueueDownload(mdmc);
            string successMsg = MdModManager.Services.I18nService.Instance.CurrentLanguage == "zh-CN"
                ? $"已添加到下载队列: 《{chart.Name}》"
                : $"Added to download queue: \"{chart.Name}\"";
            _notificationService.ShowSuccess(successMsg);
        }
        catch (Exception ex)
        {
            _notificationService.ShowFailure("投递下载失败", ex.Message);
        }
    }

    /// <summary>确认单谱面查重拦截后的操作</summary>
    [RelayCommand]
    private async Task ConfirmSingleDownloadActionAsync(string action)
    {
        IsDuplicateDialogOpen = false;
        var chart = DuplicateDialogTarget;
        if (chart == null) return;

        if (action == "overwrite")
        {
            foreach (var local in DuplicateDialogItems)
            {
                try
                {
                    if (System.IO.File.Exists(local.FilePath))
                    {
                        System.IO.File.Delete(local.FilePath);
                    }
                    _chartIndexService.RemoveFromIndex(local.FilePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EuterpeDownload] Failed to delete duplicate local chart {local.FilePath}: {ex.Message}");
                }
            }
            await ExecuteDownloadAsync(chart);
        }
        else if (action == "both")
        {
            await ExecuteDownloadAsync(chart);
        }

        DuplicateDialogTarget = null;
        DuplicateDialogItems.Clear();
    }

    // 退出登录，擦除本地 Token 并安全切回 Mod 管理首页
    [RelayCommand]
    private async Task LogoutAsync()
    {
        StopPlayback();
        await _authService.LogoutAsync();

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            mainVm.CleanupCurrentPage();
            mainVm.IsChartDownloadMenuExpanded = false;

            var homeVm = Ioc.Default.GetRequiredService<ModManagerViewModel>();
            mainVm.CurrentPage = homeVm;
            await homeVm.InitializeAsync(default);
            _notificationService.ShowSuccess("已成功注销 Euterpe 账号");
        }
    }

    // 打开官方网站
    [RelayCommand]
    private void OpenEuterpeWeb()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://euterpe-org.com") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _notificationService.ShowFailure("打开网页失败", ex.Message);
        }
    }


    // 获取清单中最新的版本信息
    private static ManifestVersionEntry? GetLatestVersion(Dictionary<string, ManifestVersionEntry>? versions)
    {
        if (versions == null || versions.Count == 0) return null;
        if (versions.Count == 1) return versions.Values.First();

        var parsedVersions = new List<(Version Ver, ManifestVersionEntry Entry)>();
        var nonParsedVersions = new List<(string Key, ManifestVersionEntry Entry)>();

        foreach (var kvp in versions)
        {
            var cleanKey = kvp.Key.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? kvp.Key.Substring(1) : kvp.Key;
            var dashIdx = cleanKey.IndexOf('-');
            if (dashIdx > 0)
            {
                cleanKey = cleanKey.Substring(0, dashIdx);
            }

            if (Version.TryParse(cleanKey, out var version))
            {
                parsedVersions.Add((version, kvp.Value));
            }
            else
            {
                nonParsedVersions.Add((kvp.Key, kvp.Value));
            }
        }

        if (parsedVersions.Count > 0)
        {
            return parsedVersions.OrderByDescending(v => v.Ver).First().Entry;
        }

        return nonParsedVersions.OrderByDescending(v => v.Key).FirstOrDefault().Entry;
    }

    private void UpdateStatusMessage()
    {
        if (!string.IsNullOrEmpty(PreviewStatusText))
        {
            StatusMessage = PreviewStatusText;
            return;
        }

        if (IsLoading)
        {
            StatusMessage = "正在加载谱面列表…";
            return;
        }

        if (IsEmpty)
        {
            StatusMessage = "未发现匹配当前筛选条件的 Euterpe 谱面";
            return;
        }

        StatusMessage = $"第 {CurrentPage} / {TotalPages} 页 | 优质谱面，尽在 Euterpe";
    }

    public void Dispose()
    {
        _listCts?.Cancel();
        _listCts?.Dispose();
        _listCts = null;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        StopPlayback();
        Charts.Clear();
    }
}
