using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MdModManager.Helpers;
using MdModManager.Models;
using System.Collections.Generic; // Added for HashSet

namespace MdModManager.Services;

public class DownloadManagerService : IDownloadManagerService, IDisposable
{
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    // 默认客户端用于界面小文件（如封面），超时较短以保证响应度
    private readonly HttpClient _http = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(4));
    // 下载专门客户端，超时放宽到 15 秒以应对慢速网络
    private readonly HttpClient _downloadHttp = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(15));
    // 并发控制器：最多同时下载 10 个谱面
    private readonly SemaphoreSlim _concurrencySemaphore = new(10, 10);

    public ObservableCollection<DownloadTaskItem> Tasks { get; } = new();
    public HashSet<string> SessionDownloadedFiles { get; } = new(StringComparer.OrdinalIgnoreCase); // Added property

    public DownloadManagerService(IConfigService configService, INotificationService notificationService)
    {
        _configService = configService;
        _notificationService = notificationService;
    }

    public void EnqueueDownload(MdmcChart chart)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Tasks.Any(t => t.Chart.Id == chart.Id && 
                (t.Status == DownloadStatus.Waiting || t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Paused)))
            {
                return;
            }

            var item = new DownloadTaskItem(chart);
            Tasks.Add(item);
            
            // If the chart in the list doesn't have a loaded cover (e.g. freshly passed in before list loaded its own), try to load it
            if (item.Chart.CoverImage == null && !string.IsNullOrEmpty(item.Chart.CoverUrl))
            {
                _ = LoadCoverAsync(item);
            }
            
            _ = ProcessDownloadAsync(item);
        });
    }

    private async Task LoadCoverAsync(DownloadTaskItem item)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(item.Chart.CoverUrl);
            using var ms = new MemoryStream(bytes);
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
            Dispatcher.UIThread.Post(() => item.Chart.CoverImage = bmp);
        }
        catch { /* ignore cover load error */ }
    }

    public void PauseDownload(DownloadTaskItem item)
    {
        if (item.Status == DownloadStatus.Downloading)
        {
            item.Cts?.Cancel();
            item.Status = DownloadStatus.Paused;
        }
    }

    public void ResumeDownload(DownloadTaskItem item)
    {
        if (item.Status == DownloadStatus.Paused || item.Status == DownloadStatus.Error)
        {
            item.Status = DownloadStatus.Waiting;
            _ = ProcessDownloadAsync(item);
        }
    }

    public void CancelDownload(DownloadTaskItem item)
    {
        item.Cts?.Cancel();
        item.Status = DownloadStatus.Canceled;
        
        try
        {
            if (!string.IsNullOrEmpty(item.DestinationPath) && File.Exists(item.DestinationPath))
            {
                File.Delete(item.DestinationPath);
            }
        }
        catch { /* ignored */ }

        Dispatcher.UIThread.Post(() => Tasks.Remove(item));
    }

    public void ClearCompletedAndCanceled()
    {
        var toRemove = Tasks.Where(t => t.Status == DownloadStatus.Completed || t.Status == DownloadStatus.Canceled).ToList();
        foreach (var item in toRemove)
        {
            Tasks.Remove(item);
        }
    }

    private async Task ProcessDownloadAsync(DownloadTaskItem item)
    {
        // 标记为等待中
        item.Status = DownloadStatus.Waiting;
        UpdateDownloadInfo(item);

        bool acquired = false;
        try
        {
            // 在入场前初始化一次 CTS，这样用户在“等待中”也能取消
            if (item.Cts == null) item.Cts = new CancellationTokenSource();
            var ct = item.Cts.Token;

            // 等待入场券
            await _concurrencySemaphore.WaitAsync(ct);
            acquired = true;

            // 拿到入场券，正式开始下载
            item.Status = DownloadStatus.Downloading;
            UpdateDownloadInfo(item);

            int retryCount = 0;
            const int maxRetries = 10;

            while (retryCount < maxRetries)
            {
                try
                {
                    if (ct.IsCancellationRequested) return;

                    var gamePath = _configService.Config.GamePath;
                    if (string.IsNullOrEmpty(gamePath))
                    {
                        throw new Exception("游戏路径未设置，请先在设置中配置游戏目录");
                    }

                    var albumsDir = Path.Combine(gamePath, "Custom_Albums");
                    if (!Directory.Exists(albumsDir))
                    {
                        Directory.CreateDirectory(albumsDir);
                    }

                    static string Safe(string s) => string.Join("_", s.Split(Path.GetInvalidFileNameChars()));
                    var fileName = $"{Safe(item.Chart.Title)} - {Safe(item.Chart.Artist)}.mdm";
                    if (string.IsNullOrEmpty(item.DestinationPath))
                    {
                        item.DestinationPath = Path.Combine(albumsDir, fileName);
                    }

                    RuntimeLog.Write("DownloadManager", $"Download start/resume: title='{item.Chart.Title}', url='{item.Chart.DownloadUrl}', downloaded={item.DownloadedBytes}, retry={retryCount}");
                    
                    var fileMode = item.DownloadedBytes > 0 ? FileMode.Append : FileMode.Create;
                    // Use FileShare.Write to allow resuming even if something else has it open for reading (though unlikely here)
                    using var dst = new FileStream(item.DestinationPath, fileMode, FileAccess.Write, FileShare.None, 81920, true);

                    using var request = new HttpRequestMessage(HttpMethod.Get, item.Chart.DownloadUrl);
                    if (item.DownloadedBytes > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(item.DownloadedBytes, null);
                    }

                    // 下载时使用 _downloadHttp
                    using var response = await _downloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                RuntimeLog.Write("DownloadManager", $"Download response: title='{item.Chart.Title}', status={(int)response.StatusCode} {response.StatusCode}");
                
                // If we requested a range but got 200 OK instead of 206 Partial Content, 
                // it means the server doesn't support range or ignored it. We must restart from 0.
                if (item.DownloadedBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    RuntimeLog.Write("DownloadManager", $"Server returned 200 OK instead of 206, resetting download progress for '{item.Chart.Title}'");
                    item.DownloadedBytes = 0;
                    dst.SetLength(0);
                    dst.Seek(0, SeekOrigin.Begin);
                }
                
                response.EnsureSuccessStatusCode();

                // Try to get total size from headers
                if (item.TotalBytes == 0)
                {
                    item.TotalBytes = response.Content.Headers.ContentLength ?? 0;
                    if (item.DownloadedBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                    {
                        item.TotalBytes += item.DownloadedBytes;
                    }
                }
                
                UpdateDownloadInfo(item);

                using var src = await response.Content.ReadAsStreamAsync(ct);
                var buf = new byte[81920];
                int n;

                var lastTime = DateTime.UtcNow;
                var lastBytes = item.DownloadedBytes;

                // 读取数据流，放宽超时至 15 秒以适应慢速网络
                while ((n = await src.ReadAsync(buf, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    item.DownloadedBytes += n;
                    
                    if (item.TotalBytes > 0)
                    {
                        item.Progress = (double)item.DownloadedBytes / item.TotalBytes * 100;
                    }

                    var now = DateTime.UtcNow;
                    var elapsed = (now - lastTime).TotalSeconds;
                    if (elapsed >= 1.0)
                    {
                        var speed = (item.DownloadedBytes - lastBytes) / elapsed;
                        UpdateDownloadInfo(item, speed);
                        lastTime = now;
                        lastBytes = item.DownloadedBytes;
                    }
                }

                item.Status = DownloadStatus.Completed;
                item.Progress = 100;
                _notificationService.ShowSuccess($"《{item.Chart.Title}》下载完成");
                
                // Add to session downloaded files list
                SessionDownloadedFiles.Add(Path.GetFullPath(item.DestinationPath));

                // Auto remove after completion
                Dispatcher.UIThread.Post(() => Tasks.Remove(item));
                return; // 成功完成
            }
            catch (OperationCanceledException)
            {
                // 如果是用户点击了“取消”按钮 (Status 会被设为 Canceled)
                if (item.Status == DownloadStatus.Canceled) return;

                // 核心修复点：只有在“用户主动点击暂停”时（即 item.Cts 被标记为已取消），才真正显示“已暂停”
                if (item.Cts != null && item.Cts.IsCancellationRequested)
                {
                    item.Status = DownloadStatus.Paused;
                    UpdateDownloadInfo(item); // 暂停时切换回文件大小
                    return;
                }

                // 否则，该异常是由内部超时（如 HttpHelper 的 4 秒头监控）触发的，应走重试逻辑
                retryCount++;
                RuntimeLog.Write("DownloadManager", $"Download internal timeout (watchdog): title='{item.Chart.Title}', retry={retryCount}");
                
                if (HttpHelper.UseOptimizedIps)
                {
                    HttpHelper.InvalidateFastestIp();
                    await Task.Delay(1000);
                }
                continue; // 进入重试循环，尝试下一个 IP
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 通用重试逻辑：无论是否开启“高速 DNS”，只要没超过最大重试次数，就继续尝试
                retryCount++;
                RuntimeLog.Write("DownloadManager", $"Download attempt {retryCount} failed: title='{item.Chart.Title}', error='{ex.Message}'");

                if (retryCount < maxRetries)
                {
                    // 如果启用了高速 DNS，尝试换 IP
                    if (HttpHelper.UseOptimizedIps)
                    {
                        HttpHelper.InvalidateFastestIp();
                        // 等待 1 秒给新竞速一点时间
                        await Task.Delay(1000);
                    }
                    else
                    {
                        // 普通模式下也稍微等一下再试
                        await Task.Delay(2000);
                    }
                    continue; // 只要次数没完，就继续重试
                }

                // 只有真的失败 10 次后，才设置状态为错误
                item.Status = DownloadStatus.Error;
                item.ErrorMessage = ex.Message;
                UpdateDownloadInfo(item);
                _notificationService.ShowFailure("下载失败", ex.Message);
                return;
            }
        }
    }
    catch (OperationCanceledException)
        {
            // 外部异常，通常是因为 WaitAsync 被取消
            if (item.Status == DownloadStatus.Canceled) return;
            if (item.Cts != null && item.Cts.IsCancellationRequested)
            {
                item.Status = DownloadStatus.Paused;
                UpdateDownloadInfo(item);
            }
        }
        catch (Exception ex)
        {
            // 其他未知异常
            RuntimeLog.Write("DownloadManager", $"Uncaught download error: {ex.Message}");
            item.Status = DownloadStatus.Error;
            item.ErrorMessage = ex.Message;
        }
        finally
        {
            if (acquired)
            {
                _concurrencySemaphore.Release();
            }
            item.Cts?.Dispose();
            item.Cts = null;
        }
    }

    private void UpdateDownloadInfo(DownloadTaskItem item, double speedBps = -1)
    {
        if (item.Status == DownloadStatus.Downloading)
        {
            // 开始下载且还没计算出速度时，默认显示 0 KB/s 而非文件大小
            double speed = speedBps < 0 ? 0 : speedBps;
            if (speed < 1024)
                item.DownloadInfo = $"{speed:F0} B/s";
            else if (speed < 1024 * 1024)
                item.DownloadInfo = $"{speed / 1024:F1} KB/s";
            else
                item.DownloadInfo = $"{speed / 1024 / 1024:F2} MB/s";
        }
        else
        {
            double downloadedMb = item.DownloadedBytes / 1024.0 / 1024.0;
            if (item.TotalBytes > 0)
            {
                double totalMb = item.TotalBytes / 1024.0 / 1024.0;
                item.DownloadInfo = $"{downloadedMb:F2} MB / {totalMb:F2} MB";
            }
            else
            {
                item.DownloadInfo = $"{downloadedMb:F2} MB / 未知大小";
            }
        }
    }

    public void Dispose()
    {
        foreach (var task in Tasks)
        {
            task.Cts?.Cancel();
        }
        _http.Dispose();
        _downloadHttp.Dispose();
    }
}
