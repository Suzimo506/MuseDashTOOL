using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MdModManager.Models;
using System;

namespace MdModManager.Models;

public enum DownloadStatus
{
    Waiting,
    Downloading,
    Paused,
    Completed,
    Canceled,
    Error
}

public partial class DownloadTaskItem : ObservableObject
{
    public MdmcChart Chart { get; }

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _downloadInfo = "0 MB / 0 MB";

    [ObservableProperty]
    private DownloadStatus _status = DownloadStatus.Waiting;

    partial void OnStatusChanged(DownloadStatus value)
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsCompleted));
    }

    public bool CanPause => Status == DownloadStatus.Downloading || Status == DownloadStatus.Waiting;
    public bool CanResume => Status == DownloadStatus.Paused || Status == DownloadStatus.Error;
    public bool IsError => Status == DownloadStatus.Error;
    public bool IsCompleted => Status == DownloadStatus.Completed;

    public string StatusText => Status switch
    {
        DownloadStatus.Waiting => "等待中",
        DownloadStatus.Downloading => "下载中",
        DownloadStatus.Paused => "已暂停",
        DownloadStatus.Completed => "已完成",
        DownloadStatus.Canceled => "已取消",
        DownloadStatus.Error => "错误",
        _ => "未知状态"
    };

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public CancellationTokenSource? Cts { get; set; }
    
    // 用于追踪已下载字节以支持断点续传
    public long DownloadedBytes { get; set; } = 0;
    public long TotalBytes { get; set; } = 0;
    public string? ResumeEntityTag { get; set; }
    public DateTimeOffset? ResumeLastModified { get; set; }

    public string DestinationPath { get; set; } = string.Empty;

    public DownloadTaskItem(MdmcChart chart)
    {
        Chart = chart;
    }
}
