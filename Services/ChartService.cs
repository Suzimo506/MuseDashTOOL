using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IChartService
{
    IEnumerable<ChartInfo> LoadCharts(string gamePath, IReadOnlySet<string>? sessionDownloadedFiles = null);
    void DeleteChart(ChartInfo chart);
    Stream? OpenDemoStream(ChartInfo chart);
}

public class ChartService : IChartService
{
    private static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ogg", ".wav", ".mp3", ".flac" };
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".gif", ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    private static readonly string[] PreferredCoverNames =
        ["cover.gif", "cover.png", "cover.jpg", "cover.jpeg", "cover.webp", "cover.bmp"];

    /// <summary>
    /// Filenames present at app startup. Files added after startup are "new".
    /// null = snapshot not yet taken.
    /// </summary>
    private static HashSet<string>? _snapshotFilenames = null;
    private static readonly object _snapshotLock = new();

    public IEnumerable<ChartInfo> LoadCharts(string gamePath, IReadOnlySet<string>? sessionDownloadedFiles = null)
    {
        var albumsDir = Path.Combine(gamePath, "Custom_Albums");
        if (!Directory.Exists(albumsDir))
            yield break;

        var allFiles = Directory.GetFiles(albumsDir, "*.mdm");

        // Take the startup snapshot on the very first call, then keep it forever
        bool takeSnapshot = false;
        lock (_snapshotLock)
        {
            if (_snapshotFilenames == null)
            {
                takeSnapshot = true;
                _snapshotFilenames = new HashSet<string>(
                    allFiles.Select(Path.GetFileName)!,
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        foreach (var file in allFiles)
        {
            ChartInfo? info = null;
            try
            {
                info = ParseMdm(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChartService] Failed to parse {file}: {ex.Message}");
            }

            if (info != null)
            {
                // Mark as new if it wasn't in the startup snapshot
                if (!takeSnapshot &&
                    !_snapshotFilenames!.Contains(Path.GetFileName(file)))
                {
                    info.IsNewDownload = true;
                }

                if (sessionDownloadedFiles != null &&
                    sessionDownloadedFiles.Contains(Path.GetFullPath(file)))
                {
                    info.IsNewDownload = true;
                }

                yield return info;
            }
        }
    }

    private static ChartInfo ParseMdm(string filePath)
    {
        var chart = new ChartInfo
        {
            FilePath = filePath,
            Name = Path.GetFileNameWithoutExtension(filePath)
        };

        using var zip = ZipFile.OpenRead(filePath);
        var coverEntry = FindPreferredCoverEntry(zip);
        if (coverEntry != null)
        {
            var coverExtension = Path.GetExtension(coverEntry.Name);
            if (string.Equals(coverExtension, ".gif", StringComparison.OrdinalIgnoreCase))
            {
                chart.CoverSource = ExtractCoverToTempFile(coverEntry);
                chart.HasTemporaryCoverFile = !string.IsNullOrWhiteSpace(chart.CoverSource);
            }
            else
            {
                try
                {
                    using var stream = coverEntry.Open();
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    chart.CoverImage = new Avalonia.Media.Imaging.Bitmap(ms);
                }
                catch
                {
                }
            }
        }

        foreach (var entry in zip.Entries)
        {
            var ext = Path.GetExtension(entry.Name);

            // Demo audio
            if (chart.DemoEntryName == null && AudioExtensions.Contains(ext))
            {
                chart.DemoEntryName = entry.FullName;
            }

            // info.json metadata
            if (entry.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var stream = entry.Open();
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    ParseInfoJson(ms, chart);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ChartService] Failed to parse info.json in {filePath}: {ex.Message}");
                }
            }
        }

        return chart;
    }

    private static ZipArchiveEntry? FindPreferredCoverEntry(ZipArchive zip)
    {
        foreach (var preferredName in PreferredCoverNames)
        {
            var exactMatch = zip.Entries.FirstOrDefault(entry =>
                string.Equals(entry.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
                return exactMatch;
        }

        return zip.Entries
            .Where(entry => ImageExtensions.Contains(Path.GetExtension(entry.Name)))
            .OrderBy(entry => string.Equals(Path.GetFileNameWithoutExtension(entry.Name), "cover", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(entry => entry.FullName.Length)
            .FirstOrDefault();
    }

    private static string? ExtractCoverToTempFile(ZipArchiveEntry entry)
    {
        try
        {
            var extension = Path.GetExtension(entry.Name);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".png";

            var tempPath = Path.Combine(Path.GetTempPath(), $"mdm_cover_{Guid.NewGuid():N}{extension}");
            using var source = entry.Open();
            using var destination = File.Create(tempPath);
            source.CopyTo(destination);
            return new Uri(tempPath).AbsoluteUri;
        }
        catch
        {
            return null;
        }
    }

    private static void ParseInfoJson(Stream jsonStream, ChartInfo chart)
    {
        var root = JsonNode.Parse(jsonStream);
        if (root == null) return;

        // Song name — prefer localised "name" field
        var name = root["name"]?.GetValue<string>()
                   ?? root["song_name"]?.GetValue<string>()
                   ?? root["title"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(name))
            chart.Name = name;

        // Music author
        chart.MusicAuthor = root["author"]?.GetValue<string>()
                            ?? root["music_author"]?.GetValue<string>()
                            ?? root["artist"]?.GetValue<string>()
                            ?? root["composer"]?.GetValue<string>();

        // Chart/level designer
        chart.ChartAuthor = root["levelDesigner"]?.GetValue<string>()
                            ?? root["level_designer"]?.GetValue<string>()
                            ?? root["charter"]?.GetValue<string>()
                            ?? root["mapper"]?.GetValue<string>();

        // BPM
        var bpm = root["bpm"]?.GetValue<string>()
                  ?? root["bpm"]?.ToString();
        chart.Bpm = bpm;

        // Difficulties — could be an array or object
        var diffNode = root["difficulties"] ?? root["difficulty"];
        if (diffNode is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var d = item?.ToString();
                if (!string.IsNullOrWhiteSpace(d)) chart.Difficulties.Add(d!);
            }
        }
        else if (diffNode is JsonObject diffObj)
        {
            foreach (var kv in diffObj)
            {
                if (kv.Value != null && !string.IsNullOrWhiteSpace(kv.Value.ToString()))
                    chart.Difficulties.Add($"{kv.Key}:{kv.Value}");
            }
        }
        else if (diffNode != null)
        {
            var d = diffNode.ToString();
            if (!string.IsNullOrWhiteSpace(d)) chart.Difficulties.Add(d);
        }

        // Fallback: scan for individual difficulty fields
        if (chart.Difficulties.Count == 0)
        {
            var diffs = new List<string>();
            // difficulty1 ~ difficulty4 are standard in Muse Dash custom charts
            for (int i = 1; i <= 5; i++)
            {
                var val = root[$"difficulty{i}"]?.ToString();
                if (!string.IsNullOrWhiteSpace(val) && val != "0")
                {
                    diffs.Add(val!);
                }
            }
            if (diffs.Count > 0)
            {
                chart.Difficulties.AddRange(diffs);
            }
            else
            {
                // Another fallback
                foreach (var key in new[] { "easy", "hard", "master", "ultimate", "Easy", "Hard", "Master", "Ultimate" })
                {
                    var val = root[key]?.ToString();
                    if (!string.IsNullOrWhiteSpace(val))
                        chart.Difficulties.Add($"{key}:{val}");
                }
            }
        }

        // Final fallback: scan ALL keys in root to find anything that looks like a difficulty indicator
        if (chart.Difficulties.Count == 0 && root is JsonObject rootObj)
        {
            foreach (var kvp in rootObj)
            {
                var k = kvp.Key.ToLowerInvariant();
                if (k.Contains("difficult") && kvp.Value != null)
                {
                    var valStr = kvp.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(valStr) && valStr != "0")
                    {
                        chart.Difficulties.Add($"{kvp.Key}:{valStr}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// 返回临时复制到磁盘的 demo 流（因 NAudio 通常需要随机访问）。
    /// 调用方负责删除临时文件。
    /// </summary>
    public Stream? OpenDemoStream(ChartInfo chart)
    {
        if (string.IsNullOrEmpty(chart.DemoEntryName))
            return null;

        using var zip = ZipFile.OpenRead(chart.FilePath);
        var entry = zip.GetEntry(chart.DemoEntryName);
        if (entry == null) return null;

        var tmpPath = Path.Combine(Path.GetTempPath(), $"mdm_demo_{Guid.NewGuid()}{Path.GetExtension(chart.DemoEntryName)}");
        using (var src = entry.Open())
        using (var dst = File.Create(tmpPath))
            src.CopyTo(dst);

        return new DeleteOnCloseStream(tmpPath, File.OpenRead(tmpPath));
    }

    public void DeleteChart(ChartInfo chart)
    {
        chart.CleanupCoverResources();
        if (File.Exists(chart.FilePath))
            File.Delete(chart.FilePath);
    }
}

/// <summary>流关闭时自动删除底层临时文件。</summary>
internal sealed class DeleteOnCloseStream : Stream
{
    private readonly string _path;
    private readonly Stream _inner;

    public DeleteOnCloseStream(string path, Stream inner)
    {
        _path = path;
        _inner = inner;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        _inner.Dispose();
        try { File.Delete(_path); } catch { /* best effort */ }
        base.Dispose(disposing);
    }
}
