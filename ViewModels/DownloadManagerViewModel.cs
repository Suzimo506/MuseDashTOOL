using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace MdModManager.ViewModels;

public partial class DownloadManagerViewModel : ObservableObject
{
    private readonly IDownloadManagerService _downloadManagerService;

    [ObservableProperty]
    private string? _previousPageType;

    public event Action? OnRequestBack;

    public ObservableCollection<DownloadTaskItem> Tasks => _downloadManagerService.Tasks;

    public string PauseResumeAllText => Tasks.Any(t => t.Status == DownloadStatus.Waiting || t.Status == DownloadStatus.Downloading)
        ? "全部暂停"
        : "全部开始";

    public bool IsBulkPauseMode => Tasks.Any(t => t.Status == DownloadStatus.Waiting || t.Status == DownloadStatus.Downloading);

    public DownloadManagerViewModel(IDownloadManagerService downloadManagerService)
    {
        _downloadManagerService = downloadManagerService;
        Tasks.CollectionChanged += OnTasksCollectionChanged;

        foreach (var item in Tasks)
            item.StatusChanged += OnTaskStatusChanged;
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
        OnBulkCommandStateChanged();
    }

    [RelayCommand]
    private void TogglePauseResumeAll()
    {
        _downloadManagerService.TogglePauseResumeAll();
        OnBulkCommandStateChanged();
    }

    [RelayCommand]
    private void CancelAll()
    {
        _downloadManagerService.CancelAllDownloads();
        OnBulkCommandStateChanged();
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        _downloadManagerService.ClearCompletedAndCanceled();
        OnBulkCommandStateChanged();
    }

    [RelayCommand]
    private void GoBack()
    {
        OnRequestBack?.Invoke();
    }

    private void OnBulkCommandStateChanged()
    {
        OnPropertyChanged(nameof(PauseResumeAllText));
        OnPropertyChanged(nameof(IsBulkPauseMode));
    }

    private void OnTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (DownloadTaskItem item in e.OldItems)
                item.StatusChanged -= OnTaskStatusChanged;
        }

        if (e.NewItems != null)
        {
            foreach (DownloadTaskItem item in e.NewItems)
                item.StatusChanged += OnTaskStatusChanged;
        }

        OnBulkCommandStateChanged();
    }

    private void OnTaskStatusChanged(object? sender, EventArgs e)
    {
        OnBulkCommandStateChanged();
    }
}
