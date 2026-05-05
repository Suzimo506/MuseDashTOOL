using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MdModManager.Helpers;
using MdModManager.Models;

namespace MdModManager.Services;

public class DownloadManagerService : IDownloadManagerService, IDisposable
{
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    // 默认客户端用于界面小文件（如封面），超时较短以保证响应速度
    private readonly HttpClient _http = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(4));
    // 下载专用客户端，超时放宽到 15 秒以应对慢速网络
    private readonly HttpClient _downloadHttp = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(15));
    // 并发控制器：最多同时下载 10 个谱面
    private readonly SemaphoreSlim _concurrencySemaphore = new(10, 10);

    public ObservableCollection<DownloadTaskItem> Tasks { get; } = new();
    public HashSet<string> SessionDownloadedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

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
            item.DestinationPath = GetUniqueDestinationPath(chart);

            Tasks.Add(item);

            if (item.Chart.CoverImage == null && !string.IsNullOrEmpty(item.Chart.CoverUrl))
            {
                _ = LoadCoverAsync(item);
            }

            _ = ProcessDownloadAsync(item);
        });
    }

    private string GetUniqueDestinationPath(MdmcChart chart)
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) return string.Empty;

        var albumsDir = Path.Combine(gamePath, "Custom_Albums");
        if (!Directory.Exists(albumsDir)) Directory.CreateDirectory(albumsDir);

        static string Safe(string s) => string.Join("_", (s ?? "Unknown").Split(Path.GetInvalidFileNameChars()));
        var baseName = $"{Safe(chart.Title)} - {Safe(chart.Artist)}";
        var finalPath = Path.Combine(albumsDir, $"{baseName}.mdm");

        int count = 1;
        // 避让队列冲突或会话已下载路径
        while (Tasks.Any(t => t.DestinationPath == finalPath) || SessionDownloadedFiles.Contains(Path.GetFullPath(finalPath)))
        {
            finalPath = Path.Combine(albumsDir, $"{baseName} ({count++}).mdm");
        }
        return finalPath;
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
        catch
        {
        }
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

        TryDeleteFile(item.DestinationPath);
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
        item.Status = DownloadStatus.Waiting;
        UpdateDownloadInfo(item);

        bool acquired = false;
        try
        {
            if (item.Cts == null)
                item.Cts = new CancellationTokenSource();

            var ct = item.Cts.Token;

            await _concurrencySemaphore.WaitAsync(ct);
            acquired = true;

            item.Status = DownloadStatus.Downloading;
            UpdateDownloadInfo(item);

            int retryCount = 0;
            var useOptimizedIpStrategy = UsesOptimizedIpStrategy(item.Chart.DownloadUrl);
            var maxRetryCount = useOptimizedIpStrategy ? 10 : 3;

            while (retryCount <= maxRetryCount)
            {
                try
                {
                    if (ct.IsCancellationRequested)
                        return;

                    if (string.IsNullOrEmpty(item.DestinationPath))
                        item.DestinationPath = GetUniqueDestinationPath(item.Chart);

                    if (string.IsNullOrEmpty(item.DestinationPath))
                        throw new Exception("无法确定下载路径，请检查游戏目录设置");

                    RuntimeLog.Write("DownloadManager", $"Download start/resume: title='{item.Chart.Title}', url='{item.Chart.DownloadUrl}', downloaded={item.DownloadedBytes}, retry={retryCount}");

                    await DownloadToFileAsync(item, ct);

                    var validationError = await ValidateMdmIntegrityAsync(item.DestinationPath, ct);
                    if (validationError != null)
                    {
                        retryCount++;
                        RuntimeLog.Write("DownloadManager", $"Downloaded mdm failed integrity check: title='{item.Chart.Title}', retry={retryCount}, error='{validationError}'");

                        ResetPartialDownload(item);
                        UpdateDownloadInfo(item);

                        if (retryCount <= maxRetryCount)
                        {
                            _notificationService.ShowInfo("文件损坏，尝试重新下载中", 3000);
                            await DelayBeforeRetryAsync(useOptimizedIpStrategy, ct);
                            continue;
                        }

                        HandleFinalFailure(item, useOptimizedIpStrategy, $"文件完整性校验失败: {validationError}");
                        return;
                    }

                    item.Status = DownloadStatus.Completed;
                    item.Progress = 100;
                    _notificationService.ShowSuccess($"《{item.Chart.Title}》下载完成");

                    SessionDownloadedFiles.Add(Path.GetFullPath(item.DestinationPath));
                    Dispatcher.UIThread.Post(() => Tasks.Remove(item));
                    return;
                }
                catch (OperationCanceledException)
                {
                    if (item.Status == DownloadStatus.Canceled)
                        return;

                    if (item.Cts != null && item.Cts.IsCancellationRequested)
                    {
                        item.Status = DownloadStatus.Paused;
                        UpdateDownloadInfo(item);
                        return;
                    }

                    retryCount++;
                    RuntimeLog.Write("DownloadManager", $"Download internal timeout (watchdog): title='{item.Chart.Title}', retry={retryCount}");

                    if (retryCount <= maxRetryCount)
                    {
                        await DelayBeforeRetryAsync(useOptimizedIpStrategy, CancellationToken.None);
                        continue;
                    }

                    HandleFinalFailure(item, useOptimizedIpStrategy, "下载超时");
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    retryCount++;
                    RuntimeLog.Write("DownloadManager", $"Download attempt {retryCount} failed: title='{item.Chart.Title}', url='{item.Chart.DownloadUrl}', error='{ex.Message}'");

                    if (retryCount <= maxRetryCount)
                    {
                        await DelayBeforeRetryAsync(useOptimizedIpStrategy, CancellationToken.None);
                        continue;
                    }

                    HandleFinalFailure(item, useOptimizedIpStrategy, ex.Message);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (item.Status == DownloadStatus.Canceled)
                return;

            if (item.Cts != null && item.Cts.IsCancellationRequested)
            {
                item.Status = DownloadStatus.Paused;
                UpdateDownloadInfo(item);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("DownloadManager", $"Uncaught download error: {ex.Message}");
            item.Status = DownloadStatus.Error;
            item.ErrorMessage = TranslateDownloadErrorMessage(ex.Message);
        }
        finally
        {
            if (acquired)
                _concurrencySemaphore.Release();

            item.Cts?.Dispose();
            item.Cts = null;
        }
    }

    private async Task DownloadToFileAsync(DownloadTaskItem item, CancellationToken ct)
    {
        var fileMode = item.DownloadedBytes > 0 ? FileMode.Append : FileMode.Create;
        using var dst = new FileStream(item.DestinationPath, fileMode, FileAccess.Write, FileShare.None, 81920, true);
        using var request = new HttpRequestMessage(HttpMethod.Get, item.Chart.DownloadUrl);

        if (item.DownloadedBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(item.DownloadedBytes, null);
            var ifRange = CreateIfRangeHeader(item);
            if (ifRange != null)
                request.Headers.IfRange = ifRange;
        }

        using var response = await _downloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        RuntimeLog.Write("DownloadManager", $"Download response: title='{item.Chart.Title}', status={(int)response.StatusCode} {response.StatusCode}, url='{item.Chart.DownloadUrl}'");

        if (item.DownloadedBytes > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            RuntimeLog.Write("DownloadManager", $"Server returned 200 OK instead of 206, resetting download progress for '{item.Chart.Title}'");
            item.DownloadedBytes = 0;
            item.TotalBytes = 0;
            dst.SetLength(0);
            dst.Seek(0, SeekOrigin.Begin);
        }

        response.EnsureSuccessStatusCode();
        CaptureResumeValidators(item, response);

        if (item.TotalBytes == 0)
        {
            item.TotalBytes = response.Content.Headers.ContentLength ?? 0;
            if (item.DownloadedBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                item.TotalBytes += item.DownloadedBytes;
        }

        UpdateDownloadInfo(item);

        using var src = await response.Content.ReadAsStreamAsync(ct);
        var buf = new byte[81920];
        var lastTime = DateTime.UtcNow;
        var lastBytes = item.DownloadedBytes;

        int n;
        while ((n = await src.ReadAsync(buf, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            item.DownloadedBytes += n;

            if (item.TotalBytes > 0)
                item.Progress = (double)item.DownloadedBytes / item.TotalBytes * 100;

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

        await dst.FlushAsync(ct);
    }

    private static RangeConditionHeaderValue? CreateIfRangeHeader(DownloadTaskItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ResumeEntityTag) &&
            EntityTagHeaderValue.TryParse(item.ResumeEntityTag, out var entityTag) &&
            !entityTag.IsWeak)
        {
            return new RangeConditionHeaderValue(entityTag);
        }

        if (item.ResumeLastModified.HasValue)
            return new RangeConditionHeaderValue(item.ResumeLastModified.Value);

        return null;
    }

    private static void CaptureResumeValidators(DownloadTaskItem item, HttpResponseMessage response)
    {
        if (response.Headers.ETag is { IsWeak: false } etag)
            item.ResumeEntityTag = etag.ToString();
        else if (response.StatusCode == HttpStatusCode.OK)
            item.ResumeEntityTag = null;

        if (response.Content.Headers.LastModified.HasValue)
            item.ResumeLastModified = response.Content.Headers.LastModified.Value;
        else if (response.Headers.TryGetValues("Last-Modified", out var values) &&
                 DateTimeOffset.TryParse(values.FirstOrDefault(), out var lastModified))
            item.ResumeLastModified = lastModified;
        else if (response.StatusCode == HttpStatusCode.OK)
            item.ResumeLastModified = null;
    }

    private static async Task<string?> ValidateMdmIntegrityAsync(string filePath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(filePath))
                return "文件不存在";

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length <= 0)
                return "文件大小为 0";

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            if (zip.Entries.Count == 0)
                return "压缩包为空";

            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    continue;

                using var entryStream = entry.Open();
                while (await entryStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct) > 0)
                {
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ex.Message;
        }
    }

    private static void ResetPartialDownload(DownloadTaskItem item)
    {
        item.DownloadedBytes = 0;
        item.TotalBytes = 0;
        item.Progress = 0;
        item.ErrorMessage = string.Empty;
        item.ResumeEntityTag = null;
        item.ResumeLastModified = null;
        TryDeleteFile(item.DestinationPath);
    }

    private static bool UsesOptimizedIpStrategy(string? downloadUrl)
    {
        if (!HttpHelper.UseOptimizedIps || string.IsNullOrWhiteSpace(downloadUrl))
            return false;

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
            return false;

        return HttpHelper.IsOptimizedAccelerationHost(uri.Host);
    }

    private static async Task DelayBeforeRetryAsync(bool useOptimizedIpStrategy, CancellationToken ct)
    {
        if (useOptimizedIpStrategy)
        {
            HttpHelper.InvalidateFastestIp();
            await Task.Delay(1000, ct);
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }

    private void HandleFinalFailure(DownloadTaskItem item, bool useOptimizedIpStrategy, string reason)
    {
        var translatedReason = TranslateDownloadErrorMessage(reason);
        item.Status = DownloadStatus.Error;
        item.ErrorMessage = useOptimizedIpStrategy
            ? translatedReason
            : "当前下载源连续失败，请切换下载源或手动下载。";
        UpdateDownloadInfo(item);
        _notificationService.ShowFailure("下载失败", item.ErrorMessage);
    }

    private static string TranslateDownloadErrorMessage(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "下载失败";

        var message = reason.Trim();

        if (message.Contains("Response status code does not indicate success", StringComparison.OrdinalIgnoreCase))
        {
            var statusCode = TryExtractHttpStatusCode(message);
            return statusCode switch
            {
                400 => "下载失败：请求无效（400）",
                401 => "下载失败：未通过身份验证（401）",
                403 => "下载失败：服务器拒绝访问（403）",
                404 => "下载失败：资源不存在（404）",
                408 => "下载失败：请求超时（408）",
                416 => "下载失败：断点续传范围无效（416）",
                429 => "下载失败：请求过于频繁（429）",
                >= 500 and <= 599 => $"下载失败：服务器暂时不可用（HTTP {statusCode}）",
                int code when code > 0 => $"下载失败（HTTP {code}）",
                _ => "下载失败：服务器返回了错误响应"
            };
        }

        if (message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("A task was canceled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "下载超时";
        }

        if (message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Temporary failure in name resolution", StringComparison.OrdinalIgnoreCase))
        {
            return "下载失败：无法解析服务器地址";
        }

        if (message.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return "下载失败：服务器拒绝连接";
        }

        if (message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Connection reset", StringComparison.OrdinalIgnoreCase))
        {
            return "下载失败：连接被服务器中断";
        }

        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("secure connection", StringComparison.OrdinalIgnoreCase))
        {
            return "下载失败：安全连接建立失败";
        }

        return message;
    }

    private static int TryExtractHttpStatusCode(string message)
    {
        var parts = message.Split([' ', ':', '(', ')', '.', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length == 3 && int.TryParse(part, out var code))
                return code;
        }

        return 0;
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private void UpdateDownloadInfo(DownloadTaskItem item, double speedBps = -1)
    {
        if (item.Status == DownloadStatus.Downloading)
        {
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
