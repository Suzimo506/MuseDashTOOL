using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Models;
using MdModManager.Helpers;

namespace MdModManager.Services;

public interface IChartDownloadService
{
    Task<(IList<MdmcChart> charts, int totalPages)> FetchChartsAsync(
        int page, string sort, string order,
        string query, bool rankedOnly,
        CancellationToken ct = default);

    Task DownloadChartAsync(
        MdmcChart chart, string destinationFolder,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

public class ChartDownloadService : IChartDownloadService
{
    private static readonly HttpClient _http = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(30));

    public async Task<(IList<MdmcChart> charts, int totalPages)> FetchChartsAsync(
        int page, string sort, string order,
        string query, bool rankedOnly,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.mdmc.moe/v3/charts?page={page}&pageSize=20&sort={sort}&order={order}";
            if (!string.IsNullOrWhiteSpace(query))
                url += $"&q={Uri.EscapeDataString(query)}";
            if (rankedOnly)
                url += "&rankedOnly=true";

            var result = await _http.GetFromJsonAsync<MdmcChartListResponse>(url, ct);
            if (result != null)
                return (result.Charts, result.TotalPages);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChartDownloadService] FetchCharts error: {ex.Message}");
        }
        return (new List<MdmcChart>(), 0);
    }

    public async Task DownloadChartAsync(
        MdmcChart chart, string destinationFolder,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // Clean filename
        static string Safe(string s) => string.Join("_", s.Split(Path.GetInvalidFileNameChars()));
        var fileName = $"{Safe(chart.Title)} - {Safe(chart.Artist)}.mdm";
        var destPath = Path.Combine(destinationFolder, fileName);

        using var response = await _http.GetAsync(chart.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        using var src  = await response.Content.ReadAsStreamAsync(ct);
        using var dst  = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                                        FileShare.None, 81920, true);

        var buf = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (totalBytes > 0)
                progress?.Report((double)read / totalBytes * 100);
        }
    }
}
