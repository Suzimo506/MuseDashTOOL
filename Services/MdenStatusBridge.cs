using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MdModManager.Services;

internal static class MdenStatusBridge
{
    private const string PipeName = "MDEN-MuseDashTOOL-Status";
    private const int ConnectTimeoutMs = 300;
    private static readonly SemaphoreSlim SendLock = new(1, 1);

    public static void NotifyMissingChartDownloadStarted(string title, string? chartKey = null, int difficulty = 0)
    {
        SendMissingChartStatus("started", title, null, chartKey, difficulty);
    }

    public static void NotifyMissingChartDownloadCompleted(string title, string? chartKey = null, int difficulty = 0)
    {
        SendMissingChartStatus("completed", title, null, chartKey, difficulty);
    }

    public static void NotifyMissingChartDownloadFailed(string title, string? reason, string? chartKey = null, int difficulty = 0)
    {
        SendMissingChartStatus("failed", title, reason, chartKey, difficulty);
    }

    private static void SendMissingChartStatus(string status, string title, string? reason, string? chartKey, int difficulty)
    {
        var uri = BuildUri(status, title, reason, chartKey, difficulty);
        _ = Task.Run(() => SendAsync(uri));
    }

    private static async Task SendAsync(string uri)
    {
        try
        {
            await SendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                await client.ConnectAsync(ConnectTimeoutMs).ConfigureAwait(false);
                await using var writer = new StreamWriter(client, new UTF8Encoding(false));
                await writer.WriteAsync(uri).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                SendLock.Release();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("MdenStatusBridge", $"Status notify skipped: {ex.Message}");
        }
    }

    private static string BuildUri(string status, string title, string? reason, string? chartKey, int difficulty)
    {
        var parts = new List<string>
        {
            "status=" + Uri.EscapeDataString(status),
            "title=" + Uri.EscapeDataString(title ?? string.Empty)
        };

        if (!string.IsNullOrWhiteSpace(chartKey))
        {
            parts.Add("chartKey=" + Uri.EscapeDataString(chartKey));
        }

        if (difficulty > 0)
        {
            parts.Add("difficulty=" + difficulty);
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            parts.Add("reason=" + Uri.EscapeDataString(reason));
        }

        return "mden://missing-chart-download?" + string.Join("&", parts);
    }
}
