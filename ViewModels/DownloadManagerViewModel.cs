using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;
using System.Collections.ObjectModel;

namespace MdModManager.ViewModels;

public partial class DownloadManagerViewModel : ObservableObject
{
    private readonly IDownloadManagerService _downloadManagerService;

    [ObservableProperty]
    private string? _previousPageType;

    public event Action? OnRequestBack;

    public ObservableCollection<DownloadTaskItem> Tasks => _downloadManagerService.Tasks;

    public DownloadManagerViewModel(IDownloadManagerService downloadManagerService)
    {
        _downloadManagerService = downloadManagerService;
    }

    [RelayCommand]
    private void Pause(DownloadTaskItem item)
    {
        _downloadManagerService.PauseDownload(item);
    }

    [RelayCommand]
    private void Resume(DownloadTaskItem item)
    {
        _downloadManagerService.ResumeDownload(item);
    }

    [RelayCommand]
    private void Cancel(DownloadTaskItem item)
    {
        _downloadManagerService.CancelDownload(item);
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        _downloadManagerService.ClearCompletedAndCanceled();
    }

    [RelayCommand]
    private void GoBack()
    {
        OnRequestBack?.Invoke();
    }
}
