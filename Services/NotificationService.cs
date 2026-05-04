using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MdModManager.Services;

public interface INotificationService
{
    ObservableCollection<DownloadNotification> Notifications { get; }
    void ShowSuccess(string message);
    void ShowFailure(string message, string reason);
    void ShowInfo(string message, int durationMs = 1500);
    DownloadNotification ShowPersistentProgress(string message);
    void RemoveNotification(DownloadNotification notification);
    void ClearPersistentNotifications();
}

public partial class DownloadNotification : ObservableObject
{
    [ObservableProperty]
    private string _message = "";
    
    public bool IsSuccess { get; set; }
    public bool IsInfo { get; set; }
    public int DurationMs { get; set; } = 1500;

    [ObservableProperty]
    private double _opacity = 1.0;
    
    [ObservableProperty]
    private bool _showProgress = false;
    
    [ObservableProperty]
    private double _progressValue = 0.0;
}

public class NotificationService : INotificationService
{
    // 最多同时显示 2 条
    private const int MaxNotifications = 2;

    public ObservableCollection<DownloadNotification> Notifications { get; } = new();

    public void ShowSuccess(string message) =>
        ShowNotification(new DownloadNotification { Message = message, IsSuccess = true });

    public void ShowFailure(string message, string reason) =>
        ShowNotification(new DownloadNotification { Message = $"失败：{reason}", IsSuccess = false });

    public void ShowInfo(string message, int durationMs = 1500) =>
        ShowNotification(new DownloadNotification { Message = message, IsInfo = true, DurationMs = durationMs });

    public DownloadNotification ShowPersistentProgress(string message)
    {
        var notif = new DownloadNotification { Message = message, IsInfo = true, DurationMs = 0, ShowProgress = true };
        ShowNotification(notif);
        return notif;
    }

    public void RemoveNotification(DownloadNotification notification)
    {
        Dispatcher.UIThread.Post(() => Notifications.Remove(notification));
    }

    public void ClearPersistentNotifications()
    {
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = Notifications.Count - 1; i >= 0; i--)
            {
                if (Notifications[i].DurationMs <= 0)
                {
                    Notifications.RemoveAt(i);
                }
            }
        });
    }

    private void ShowNotification(DownloadNotification notification)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 超出上限时移除最老的
            while (Notifications.Count >= MaxNotifications)
                Notifications.RemoveAt(0);

            Notifications.Add(notification);

            // 如果 DurationMs <= 0，则不自动移除
            if (notification.DurationMs > 0)
            {
                _ = FadeOutAndRemoveAsync(notification);
            }
        });
    }

    private async Task FadeOutAndRemoveAsync(DownloadNotification notification)
    {
        // 显示停留至指定时间后开始淡出
        await Task.Delay(notification.DurationMs);

        // 淡出动画：20 步 × 10ms = 200ms，共约 1 秒
        const int steps = 20;
        const int stepMs = 10;
        for (int i = steps; i >= 0; i--)
        {
            notification.Opacity = (double)i / steps;
            await Task.Delay(stepMs);
        }

        Dispatcher.UIThread.Post(() => Notifications.Remove(notification));
    }
}
