using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MdModManager.Models;
using System.Collections.Generic; // Added for HashSet

namespace MdModManager.Services;

public class DownloadManagerService : IDownloadManagerService, IDisposable
{
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

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
        try
        {
            if (item.Status == DownloadStatus.Canceled) return;

            item.Status = DownloadStatus.Downloading;
            item.Cts = new CancellationTokenSource();
            
            var ct = item.Cts.Token;
            
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

            RuntimeLog.Write("DownloadManager", $"Download start: title='{item.Chart.Title}', url='{item.Chart.DownloadUrl}', dest='{item.DestinationPath}'");
            
            var fileMode = item.DownloadedBytes > 0 ? FileMode.Append : FileMode.Create;
            using var dst = new FileStream(item.DestinationPath, fileMode, FileAccess.Write, FileShare.None, 81920, true);

            using var request = new HttpRequestMessage(HttpMethod.Get, item.Chart.DownloadUrl);
            if (item.DownloadedBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(item.DownloadedBytes, null);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            RuntimeLog.Write("DownloadManager", $"Download response: title='{item.Chart.Title}', status={(int)response.StatusCode} {response.StatusCode}");
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
            
            UpdateFileSizeInfo(item);

            using var src = await response.Content.ReadAsStreamAsync(ct);
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                item.DownloadedBytes += n;
                
                if (item.TotalBytes > 0)
                {
                    item.Progress = (double)item.DownloadedBytes / item.TotalBytes * 100;
                }
                UpdateFileSizeInfo(item);
            }

            item.Status = DownloadStatus.Completed;
            item.Progress = 100;
            _notificationService.ShowSuccess($"《{item.Chart.Title}》下载完成");
            
            // Add to session downloaded files list
            SessionDownloadedFiles.Add(Path.GetFullPath(item.DestinationPath));

            // Auto remove after completion
            Dispatcher.UIThread.Post(() => Tasks.Remove(item));
        }
        catch (OperationCanceledException)
        {
            if (item.Status != DownloadStatus.Canceled)
            {
                item.Status = DownloadStatus.Paused;
            }
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Error;
            item.ErrorMessage = ex.Message;
            RuntimeLog.Write("DownloadManager", $"Download failed: title='{item.Chart.Title}', error='{ex}'");
            _notificationService.ShowFailure("下载失败", ex.Message);
        }
        finally
        {
            item.Cts?.Dispose();
            item.Cts = null;
        }
    }

    private void UpdateFileSizeInfo(DownloadTaskItem item)
    {
        double downloadedMb = item.DownloadedBytes / 1024.0 / 1024.0;
        if (item.TotalBytes > 0)
        {
            double totalMb = item.TotalBytes / 1024.0 / 1024.0;
            item.FileSizeInfo = $"{downloadedMb:F2} MB / {totalMb:F2} MB";
        }
        else
        {
            item.FileSizeInfo = $"{downloadedMb:F2} MB / 未知大小";
        }
    }

    public void Dispose()
    {
        foreach (var task in Tasks)
        {
            task.Cts?.Cancel();
        }
        _http.Dispose();
    }
}
