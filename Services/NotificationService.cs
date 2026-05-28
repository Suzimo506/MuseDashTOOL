using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MdModManager.Services;

public interface INotificationService
{
    ObservableCollection<DownloadNotification> Notifications { get; }
    void ShowSuccess(string message, int durationMs = 1500);
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

    public void ShowSuccess(string message, int durationMs = 1500) =>
        ShowNotification(new DownloadNotification { Message = message, IsSuccess = true, DurationMs = durationMs });

    public void ShowFailure(string message, string reason) =>
        ShowNotification(new DownloadNotification { Message = $"失败：{reason}", IsSuccess = false });

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
        if (I18nService.Instance.CurrentLanguage == "en-US")
        {
            notification.Message = TranslateMessage(notification.Message);
        }

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

    private string TranslateMessage(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return msg;

        // Basic replacements for common patterns
        var map = new System.Collections.Generic.Dictionary<string, string>
        {
            { "欢迎回来", "Welcome Back" },
            { "未检测到游戏，请手动选择路径", "Game not found, please select path manually" },
            { "正在拉起浏览器登录 Euterpe...", "Opening browser to login to Euterpe..." },
            { "运行日志已保存到桌面！", "Log saved to Desktop!" },
            { "复制成功", "Copied successfully" },
            { "复制失败", "Copy failed" },
            { "导入成功", "Import successful" },
            { "导入失败", "Import failed" },
            { "操作失败", "Operation failed" },
            { "删除成功", "Delete successful" },
            { "删除失败", "Delete failed" },
            { "跳转失败", "Navigation failed" },
            { "高级设置已保存", "Advanced settings saved" },
            { "自制谱安装包安装完成", "Custom chart installer finished" },
            { "已暂存，将在游戏关闭后自动安装", "Staged, will install after game closes" },
            { "将在游戏关闭后自动删除", "Will delete after game closes" },
            { "更新成功", "Update successful" },
            { "下载成功", "Download successful" },
            { "安装失败", "Install failed" },
            { "卸载失败", "Uninstall failed" },
            { "设置失败", "Setup failed" },
            { "环境检查", "Environment check" },
            { "一键安装自制谱", "One-click install custom charts" },
            { "无法退出", "Cannot exit" },
            { "打开失败", "Open failed" },
            { "游戏路径未设置", "Game path not set" },
            { "更新已下载完成，将在软件关闭后自动安装", "Update downloaded, will install after close" },
            { "更新已就绪，将在软件关闭后自动安装", "Update ready, will install after close" },
            { "失败：", "Failed: " },
            { "网络异常", "Network error" },
            { "未找到受支持的字体文件", "No supported font files found" },
            { "已成功注销 Euterpe 账号", "Euterpe account logged out successfully" },
            { "文件损坏，尝试重新下载中", "File corrupted, retrying download" },
            { "已中止下载", "Download aborted" },
            { "镜像域名已更新", "Mirror domain updated" },
            { "新版本下载完成", "New version downloaded" }
        };

        foreach (var kvp in map)
        {
            if (msg.Contains(kvp.Key))
                msg = msg.Replace(kvp.Key, kvp.Value);
        }

        // Deal with patterns like "已添加到下载列表: 《XXX》"
        if (msg.StartsWith("已添加到下载列表: 《") && msg.EndsWith("》"))
        {
            msg = msg.Replace("已添加到下载列表: 《", "Added to download queue: 《");
        }
        if (msg.StartsWith("已添加到下载队列: 《") && msg.EndsWith("》"))
        {
            msg = msg.Replace("已添加到下载队列: 《", "Added to download queue: 《");
        }
        if (msg.StartsWith("成功安装了") && msg.Contains("个字体"))
        {
            msg = System.Text.RegularExpressions.Regex.Replace(msg, @"成功安装了 (\d+) 个字体(.*)", "Successfully installed $1 fonts$2");
        }
        
        return msg;
    }
}
