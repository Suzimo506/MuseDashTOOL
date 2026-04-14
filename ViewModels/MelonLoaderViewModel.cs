using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.Helpers;

namespace MdModManager.ViewModels;

public partial class MelonLoaderViewModel : ObservableObject
{
    private readonly IMelonLoaderService _melonLoaderService;

    [ObservableProperty]
    private ObservableCollection<GitHubRelease> _releases = new();

    [ObservableProperty]
    private GitHubRelease? _selectedRelease;

    [ObservableProperty]
    private string _currentVersionText = "Checking...";

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _isActionProgressing;


    private CancellationTokenSource? _installCts;
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;

    public MelonLoaderViewModel(IMelonLoaderService melonLoaderService, IConfigService configService, INotificationService notificationService)
    {
        _melonLoaderService = melonLoaderService;
        _configService = configService;
        _notificationService = notificationService;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        RefreshCurrentVersion();
        
        try
        {
            var releases = await _melonLoaderService.GetReleasesAsync(cancellationToken);
            Releases = new ObservableCollection<GitHubRelease>(releases);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        
        if (Releases.Count > 0)
        {
            SelectedRelease = Releases[0];
        }
    }

    private void RefreshCurrentVersion()
    {
        var version = _melonLoaderService.GetCurrentVersion();
        CurrentVersionText = string.IsNullOrEmpty(version) ? "未安装" : version;
    }

    [RelayCommand]
    private void Stop()
    {
        _installCts?.Cancel();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (SelectedRelease == null) return;
        
        var asset = SelectedRelease.Assets.FirstOrDefault(a => a.Name != null && a.Name.Equals("MelonLoader.x64.zip", StringComparison.OrdinalIgnoreCase))
                    ?? SelectedRelease.Assets.FirstOrDefault(a => a.Name != null && a.Name.Equals("MelonLoader.x86.zip", StringComparison.OrdinalIgnoreCase))
                    ?? (SelectedRelease.Assets.Length > 0 ? SelectedRelease.Assets[0] : null);

        if (asset == null || string.IsNullOrEmpty(asset.DownloadUrl)) return;

        IsActionProgressing = true;
        DownloadProgress = 0;
        _installCts = new CancellationTokenSource();

        // 确保优选IP在 MelonLoader 下载期间始终启用
        var previousUseOptimized = HttpHelper.UseOptimizedIps;

        try
        {
            var progress = new Progress<double>(p => DownloadProgress = p);

            // MelonLoader 强制通过 suzimo.site 代理加速下载，使用优选IP
            var suzimoHost = MirrorDomainRegistry.SuzimoHost;
            if (string.IsNullOrWhiteSpace(suzimoHost))
                suzimoHost = "suzimo.site";
            var downloadUrl = GitHubMirrorHelper.ApplyCustomMirror(asset.DownloadUrl, suzimoHost);

            HttpHelper.UseOptimizedIps = true;
            await _melonLoaderService.InstallAsync(downloadUrl, progress, _installCts.Token);
            RefreshCurrentVersion();
            _notificationService.ShowSuccess("MelonLoader 安装成功");
        }
        catch (OperationCanceledException)
        {
            _notificationService.ShowInfo("已中止下载");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Install failed: {ex.Message}");
            _notificationService.ShowFailure("安装失败", ex.Message);
        }
        finally
        {
            // 恢复用户原有的优选IP设置
            HttpHelper.UseOptimizedIps = previousUseOptimized;
            IsActionProgressing = false;
            DownloadProgress = 0;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        IsActionProgressing = true;
        try
        {
            await _melonLoaderService.UninstallAsync();
            RefreshCurrentVersion();
            _notificationService.ShowSuccess("MelonLoader 卸载成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Uninstall failed: {ex.Message}");
            _notificationService.ShowFailure("卸载失败", ex.Message);
        }
        finally
        {
            IsActionProgressing = false;
        }
    }
}
