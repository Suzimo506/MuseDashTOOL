using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MdModManager.Models;

namespace MdModManager.Helpers;

public static class ChartCoverSourceResolver
{
    private static readonly string[] MdmcCoverExtensions = [".gif", ".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    private static readonly HttpClient ProbeHttp = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(8));
    private static readonly ConcurrentDictionary<string, string> ResolvedCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ResolveLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> AnimatedTempCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AnimatedTempLocks = new(StringComparer.OrdinalIgnoreCase);

    static ChartCoverSourceResolver()
    {
        ProbeHttp.DefaultRequestHeaders.Remove("User-Agent");
        ProbeHttp.DefaultRequestHeaders.Add("User-Agent", "MuseDashTOOL-CoverResolver");
    }

    public static void ApplyDirectCoverSource(MdmcChart chart)
    {
        if (chart == null || !string.IsNullOrWhiteSpace(chart.ResolvedCoverSource) || string.IsNullOrWhiteSpace(chart.CustomCoverUrl))
            return;

        // GIF 封面由详情页的 EnableAnimatedCoversDeferredAsync 异步处理
        if (!LooksLikeGifSource(chart.CustomCoverUrl))
            SetResolvedCoverSource(chart, chart.CustomCoverUrl);
    }

    public static async Task<string?> EnsureResolvedAsync(MdmcChart chart, CancellationToken ct = default)
    {
        ApplyDirectCoverSource(chart);
        if (!string.IsNullOrWhiteSpace(chart.ResolvedCoverSource))
            return chart.ResolvedCoverSource;

        if (!string.IsNullOrWhiteSpace(chart.CustomCoverUrl))
        {
            var directResolved = await PrepareDisplaySourceAsync(chart.CustomCoverUrl, ct);
            if (!string.IsNullOrWhiteSpace(directResolved))
                chart.ResolvedCoverSource = directResolved;
            return directResolved;
        }

        if (string.IsNullOrWhiteSpace(chart.Id))
            return null;

        var cacheKey = $"mdmc:{chart.Id.Trim()}";
        if (ResolvedCache.TryGetValue(cacheKey, out var cached))
        {
            if (!string.IsNullOrWhiteSpace(cached))
                await SetResolvedCoverSourceAsync(chart, cached);
            return string.IsNullOrWhiteSpace(cached) ? null : cached;
        }

        var gate = ResolveLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (ResolvedCache.TryGetValue(cacheKey, out cached))
            {
                if (!string.IsNullOrWhiteSpace(cached))
                    await SetResolvedCoverSourceAsync(chart, cached);
                return string.IsNullOrWhiteSpace(cached) ? null : cached;
            }

            var resolved = await ResolveMdmcCoverUrlAsync(chart.Id, ct);
            ResolvedCache[cacheKey] = resolved ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(resolved))
                await SetResolvedCoverSourceAsync(chart, resolved);

            return resolved;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string?> ResolveMdmcCoverUrlAsync(string chartId, CancellationToken ct)
    {
        var baseUrl = $"https://cdn.mdmc.moe/charts/{Uri.EscapeDataString(chartId)}/cover";
        foreach (var ext in MdmcCoverExtensions)
        {
            var candidate = baseUrl + ext;
            if (await ProbeUrlAsync(candidate, ct))
                return await PrepareDisplaySourceAsync(candidate, ct);
        }

        return null;
    }

    /// <summary>
    /// 将远程 GIF 下载到本地临时文件，返回 file:// URI。
    /// 非 GIF 源返回 null。供详情页在后台预下载后再启用动画播放。
    /// </summary>
    public static async Task<string?> PrepareAnimatedSourceAsync(string? source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source) || !LooksLikeGifSource(source))
            return null;

        return await PrepareDisplaySourceAsync(source, ct);
    }

    private static async Task<string> PrepareDisplaySourceAsync(string source, CancellationToken ct)
    {
        if (!LooksLikeGifSource(source))
            return source;

        if (AnimatedTempCache.TryGetValue(source, out var cachedPath))
            return cachedPath;

        var gate = AnimatedTempLocks.GetOrAdd(source, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (AnimatedTempCache.TryGetValue(source, out cachedPath))
                return cachedPath;

            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                AnimatedTempCache[source] = source;
                return source;
            }

            var tempFile = Path.Combine(Path.GetTempPath(), $"mdm_anim_cover_{Guid.NewGuid():N}.gif");
            using var response = await ProbeHttp.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(tempFile);
            await input.CopyToAsync(output, ct);

            var localUri = new Uri(tempFile).AbsoluteUri;
            AnimatedTempCache[source] = localUri;
            return localUri;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<bool> ProbeUrlAsync(string url, CancellationToken ct)
    {
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await ProbeHttp.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (headResponse.IsSuccessStatusCode)
                return true;

            if (headResponse.StatusCode != HttpStatusCode.MethodNotAllowed &&
                headResponse.StatusCode != HttpStatusCode.NotImplemented)
            {
                return false;
            }
        }
        catch (HttpRequestException)
        {
            return false;
        }

        try
        {
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var getResponse = await ProbeHttp.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            return getResponse.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static bool LooksLikeGifSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
            return string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(Path.GetExtension(source), ".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetResolvedCoverSource(MdmcChart chart, string resolvedSource)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            chart.ResolvedCoverSource = resolvedSource;
            return;
        }

        Dispatcher.UIThread.Post(() => chart.ResolvedCoverSource = resolvedSource);
    }

    private static async Task SetResolvedCoverSourceAsync(MdmcChart chart, string resolvedSource)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            chart.ResolvedCoverSource = resolvedSource;
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => chart.ResolvedCoverSource = resolvedSource);
    }
}
