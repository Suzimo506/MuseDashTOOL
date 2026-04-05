using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;

namespace MdModManager.ViewModels;

public partial class ChartUploadViewModel : ObservableObject
{
    private readonly IChartPackageProcessor _packageProcessor;
    private readonly IChartUploadService _uploadService;
    private readonly IChartUploadConfigService _uploadConfigService;
    private readonly INotificationService _notificationService;

    private MuseDashAccountInfo? _accountInfo;
    private ChartUploadConfig _currentConfig = ChartUploadConfig.CreateDefault();

    public ObservableCollection<ChartUploadQueueItem> UploadItems { get; } = new();

    [ObservableProperty]
    private string _uploadStatus = "等待选择谱面文件...";

    [ObservableProperty]
    private string _accountSummary = "正在读取喵斯快跑账号...";

    [ObservableProperty]
    private string _configNotice = string.Empty;

    [ObservableProperty]
    private string _burl = string.Empty;



    public bool CanEditQueue => !IsBusy;
    public string UploadButtonText => IsBusy ? "上传中..." : "开始上传";

    public string DropZoneTitle
    {
        get
        {
            var item = UploadItems.FirstOrDefault();
            return item != null
                ? $"已选择：{item.Title} - {item.Artist}"
                : "拖入 `.mdm` 或 `.zip` 文件，或者点击右侧图标选择文件";
        }
    }

    public ChartUploadViewModel(
        IChartPackageProcessor packageProcessor,
        IChartUploadService uploadService,
        IChartUploadConfigService uploadConfigService,
        INotificationService notificationService)
    {
        _packageProcessor = packageProcessor;
        _uploadService = uploadService;
        _uploadConfigService = uploadConfigService;
        _notificationService = notificationService;

        UploadItems.CollectionChanged += OnUploadItemsCollectionChanged;
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await LoadUploadConfigAsync(token);
        await LoadAccountAsync();
    }

    public async Task AddFilesAsync(IEnumerable<string> filePaths)
    {
        var normalizedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Where(path => path.EndsWith(".mdm", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
            return;

        // 一次仅允许上传一个文件
        if (normalizedPaths.Count > 1)
        {
            normalizedPaths = normalizedPaths.Take(1).ToList();
        }

        var added = 0;
        foreach (var filePath in normalizedPaths)
        {
            if (UploadItems.Any(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var prepared = await _packageProcessor.PrepareUploadAsync(filePath);
                
                // 更换新文件时自动清空之前的（既然一次仅允许上传一个）
                UploadItems.Clear();
                
                UploadItems.Add(new ChartUploadQueueItem
                {
                    Package = prepared,
                    StatusText = "等待上传"
                });
                added++;
            }
            catch (Exception)
            {
                _notificationService.ShowFailure("解析失败", "请放入正确的谱面文件！");
            }
        }

        if (added > 0)
        {
            UploadStatus = $"已加入 {added} 个待上传谱面。";
            _notificationService.ShowInfo($"已加入 {added} 个谱面");
        }
        else if (normalizedPaths.Count > 0)
        {
            UploadStatus = "没有新增可处理的谱面文件。";
        }
    }

    [RelayCommand]
    private void RemoveItem(ChartUploadQueueItem? item)
    {
        if (item == null || IsBusy)
            return;

        UploadItems.Remove(item);
        UploadStatus = UploadItems.Count == 0 ? "等待选择谱面文件..." : $"当前还有 {UploadItems.Count} 个待上传谱面。";
    }

    [RelayCommand]
    private void ClearQueue()
    {
        if (IsBusy)
            return;

        UploadItems.Clear();
        UploadStatus = "待上传列表已清空。";
    }

    [RelayCommand]
    private async Task UploadAllAsync()
    {
        if (!CanUpload || _accountInfo == null)
            return;

        IsBusy = true;
        var successCount = 0;
        var failureCount = 0;
        var itemsToUpload = UploadItems.Where(item => !item.IsUploaded).ToList();

        try
        {
            for (var index = 0; index < itemsToUpload.Count; index++)
            {
                var item = itemsToUpload[index];
                item.StatusText = $"上传中 {index + 1}/{itemsToUpload.Count}";
                UploadStatus = $"正在上传：{item.Title} ({index + 1}/{itemsToUpload.Count})";

                var progressIndicator = new Progress<double>(p => 
                {
                    UploadProgress = p;
                });

                var result = await _uploadService.UploadAsync(
                    item.Package,
                    _accountInfo,
                    Burl,
                    progressIndicator);

                if (result.Success)
                {
                    item.IsUploaded = true;
                    item.StatusText = string.IsNullOrWhiteSpace(result.UploadId)
                        ? "上传成功"
                        : $"上传成功 #{result.UploadId}";
                    successCount++;
                }
                else
                {
                    item.IsUploaded = false;
                    item.StatusText = $"上传失败：{result.Message}";
                    failureCount++;
                }
            }

            UploadStatus = failureCount == 0
                ? $"全部上传完成，共 {successCount} 个。"
                : $"上传结束，成功 {successCount} 个，失败 {failureCount} 个。";

            if (failureCount == 0)
                _notificationService.ShowSuccess($"谱面上传完成，共 {successCount} 个");
            else
                _notificationService.ShowInfo($"上传结束，成功 {successCount} 个，失败 {failureCount} 个", 2500);
        }
        finally
        {
            IsBusy = false;
            UploadProgress = 0;
        }
    }

    private async Task LoadUploadConfigAsync(CancellationToken token)
    {
        _currentConfig = await _uploadConfigService.GetConfigAsync(token);


        // ConfigNotice moved to LoadAccountAsync
        IsUploadEnabled = _currentConfig.Enabled && _currentConfig.GetApiCandidates().Count > 0;

        if (!IsUploadEnabled)
        {
            UploadStatus = "上传服务当前不可用，请先配置远程上传接口。";
        }
    }

    private async Task LoadAccountAsync()
    {
        _accountInfo = MuseDashAccountService.CachedAccountInfo
            ?? await Task.Run(MuseDashAccountService.ReadAccountInfo);

        if (_accountInfo == null || string.IsNullOrWhiteSpace(_accountInfo.Uid))
        {
            IsLoggedIn = false;
            AccountSummary = "未读取到喵斯快跑账号，请先打开游戏并登录。";
            return;
        }

        var profile = MuseDashAccountService.CachedProfile;
        string displayName;

        if (profile != null && !string.IsNullOrWhiteSpace(profile.Nickname))
        {
            displayName = profile.Nickname;
        }
        else
        {
            displayName = _accountInfo.Nickname ?? _accountInfo.Username ?? _accountInfo.Uid ?? "玩家";
        }

        AccountSummary = displayName;
        ConfigNotice = $"UID: {_accountInfo.Uid}";
        IsLoggedIn = true;
    }

    private void OnUploadItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<ChartUploadQueueItem>())
                item.PropertyChanged += OnQueueItemPropertyChanged;
        }

        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<ChartUploadQueueItem>())
                item.PropertyChanged -= OnQueueItemPropertyChanged;
        }

        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(DropZoneTitle));
    }

    private void OnQueueItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartUploadQueueItem.IsUploaded))
            OnPropertyChanged(nameof(CanUpload));
    }

    [ObservableProperty]
    private bool _isUploadEnabled;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditQueue))]
    [NotifyPropertyChangedFor(nameof(UploadButtonText))]
    private bool _isBusy;

    [ObservableProperty]
    private double _uploadProgress;

    public bool CanUpload => !IsBusy &&
                             IsUploadEnabled &&
                             IsLoggedIn &&
                             UploadItems.Any(item => !item.IsUploaded);

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUpload));
    }

    partial void OnIsUploadEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUpload));
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUpload));
    }
}
