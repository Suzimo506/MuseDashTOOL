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
    }

    public async Task InitializeAsync(string categoryName, string repoUrl = "")
    {
        CategoryName = categoryName;
        // 根据用户设置的下载源，对仓库地址进行镜像加速
        RepoUrl = GitHubMirrorHelper.ApplyMirror(repoUrl, _configService.Config.DownloadSource);
        CurrentPage = 1;
        SearchText = string.Empty;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsLoading = true;
        IsEmpty = false;
        StatusMessage = "正在加载谱面列表...";
        Charts.Clear();

        // 模拟加载延迟
        await Task.Delay(500);

        try
        {
            // TODO: 这里将来接入真实的 GitHub Release 获取逻辑
            // 目前按照用户要求使用占位符
            var totalCount = 100; // 模拟总数
            var pageSize = 12; // 每页 12 个 (4x3)
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize);

            for (int i = 0; i < pageSize; i++)
            {
                var index = (CurrentPage - 1) * pageSize + i + 1;
                Charts.Add(new MdmcChart
                {
                    Id = $"community_{index}",
                    Title = $"{CategoryName} 占位谱面 {index}",
                    Artist = "Placeholder Artist",
                    Charter = "Community Member",
                    Bpm = "120.000",
                    Ranked = true,
                    LikesCount = new Random().Next(0, 500),
                    Sheets = new List<MdmcSheet> { new MdmcSheet { Difficulty = "10" } },
                    SearchText = SearchText
                });
            }

            IsEmpty = Charts.Count == 0;
            StatusMessage = $"第 {CurrentPage} / {TotalPages} 页，共 {totalCount} 张谱面";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            _ = LoadCoversAsync();
        }
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

    private async Task LoadCoversAsync()
    {
        // 占位逻辑：目前没有封面图，之后接入 GitHub 时可以使用占位图或默认图
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            _chartDownloadViewModel.StopPlayback();
            var vm = Ioc.Default.GetRequiredService<AlbumCollectionViewModel>();
            mainVm.CurrentPage = vm;
        }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        Charts.Clear();
    }
}
