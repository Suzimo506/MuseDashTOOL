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
using NAudio.Vorbis;
using NAudio.Wave;

namespace MdModManager.ViewModels;

public partial class ChartManagerViewModel : ObservableObject, IDisposable
{
    private readonly IChartService _chartService;
    private readonly IConfigService _configService;
    private readonly IDownloadManagerService _downloadManagerService;
    private const int PageSize = 16;

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

    partial void OnSearchTextChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilter();
    }

    partial void OnCurrentPageChanged(int value)
    {
        JumpPageText = value.ToString();
    }

    /// <summary>是否启用谱面名称滚动</summary>
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

        var charts = _chartService.LoadCharts(gamePath)
            .OrderByDescending(chart => chart.IsNewDownload)
            .ThenBy(chart => chart.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(chart => System.IO.Path.GetFileName(chart.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToList();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsCustomAlbumsMissing = isCustomAlbumsMissing;
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

        foreach (var chart in _allCharts)
        {
            if (string.IsNullOrEmpty(search)
                || chart.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (chart.MusicAuthor?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
                || (chart.ChartAuthor?.Contains(search, StringComparison.OrdinalIgnoreCase) == true))
            {
                _filteredCharts.Add(chart);
            }
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

        if (HasNoSearchResults)
        {
            StatusMessage = $"没有找到匹配的谱面（共 {_allCharts.Count} 张）";
            return;
        }

        StatusMessage = string.IsNullOrWhiteSpace(SearchText)
            ? $"第 {CurrentPage} / {TotalPages} 页，共 {_allCharts.Count} 张谱面"
            : $"第 {CurrentPage} / {TotalPages} 页，筛选结果 {_filteredCharts.Count} 张（总 {_allCharts.Count} 张）";
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
                        e.Name.Equals("map.json", StringComparison.OrdinalIgnoreCase));
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
}
