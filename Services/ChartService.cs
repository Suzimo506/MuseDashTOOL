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
    IReadOnlyList<string> BrokenCharts { get; }
    IEnumerable<ChartInfo> LoadCharts(string gamePath, IReadOnlySet<string>? sessionDownloadedFiles = null);
    void DeleteChart(ChartInfo chart);
    Stream? OpenDemoStream(ChartInfo chart);
    ChartInfo? LoadSingleChart(string filePath);
    System.Threading.Tasks.Task LoadCoverAsync(ChartInfo chart);
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

    public IReadOnlyList<string> BrokenCharts => _brokenCharts;
    private readonly List<string> _brokenCharts = new();

    public IEnumerable<ChartInfo> LoadCharts(string gamePath, IReadOnlySet<string>? sessionDownloadedFiles = null)
    {
        _brokenCharts.Clear();
        var albumsDir = Path.Combine(gamePath, "Custom_Albums");
        var libraryDir = Path.Combine(gamePath, "CustomAlbums_Library");
        var allFiles = new List<string>();
        var candidateFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(albumsDir))
        {
            // 1. Root .mdm files (Unclassified)
            allFiles.AddRange(Directory.GetFiles(albumsDir, "*.mdm"));

            // 2. Subdirectories with pack.json (Classifications)
            foreach (var subDir in Directory.GetDirectories(albumsDir))
            {
                if (File.Exists(Path.Combine(subDir, "pack.json")))
                {
                    allFiles.AddRange(Directory.GetFiles(subDir, "*.mdm"));
                }
            }
        }

        if (Directory.Exists(libraryDir))
        {
            foreach (var file in Directory.GetFiles(libraryDir, "*.mdm", SearchOption.AllDirectories))
            {
                allFiles.Add(file);
                candidateFiles.Add(file);
            }
        }

        if (allFiles.Count == 0)
            yield break;

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

        // 读取轻量化持久磁盘缓存索引
        var indexFile = Path.Combine(gamePath, ".chart_manager_index.json");
        var cachedEntries = new Dictionary<string, MdModManager.Services.ChartIndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(indexFile))
        {
            try
            {
                var json = File.ReadAllText(indexFile);
                var entries = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListChartIndexEntry);
                if (entries != null)
                {
                    foreach (var entry in entries)
                    {
                        if (entry != null && !string.IsNullOrEmpty(entry.FilePath))
                        {
                            cachedEntries[entry.FilePath] = entry;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChartService] Failed to load index file: {ex.Message}");
            }
        }

        var updatedEntries = new List<MdModManager.Services.ChartIndexEntry>();
        bool isIndexChanged = false;

        foreach (var file in allFiles)
        {
            var fileInfo = new FileInfo(file);
            long size = fileInfo.Length;
            DateTime writeTime = fileInfo.LastWriteTime;

            ChartInfo? info = null;
            MdModManager.Services.ChartIndexEntry? entry = null;

            if (cachedEntries.TryGetValue(file, out var cached) && cached.FileSize == size && cached.LastWriteTime == writeTime)
            {
                entry = cached;
                info = new ChartInfo
                {
                    FilePath = entry.FilePath,
                    Name = entry.Name,
                    MusicAuthor = entry.MusicAuthor,
                    ChartAuthor = entry.ChartAuthor,
                    Difficulties = entry.Difficulties,
                    Bpm = entry.Bpm,
                    DemoEntryName = string.IsNullOrEmpty(entry.DemoEntryName) ? null : entry.DemoEntryName
                };
            }
            else
            {
                // 缓存未命中，重新完整解析压缩包并更新索引
                try
                {
                    info = ParseMdm(file);
                    if (info != null)
                    {
                        entry = new MdModManager.Services.ChartIndexEntry
                        {
                            FilePath = file,
                            Name = info.Name,
                            MusicAuthor = info.MusicAuthor,
                            ChartAuthor = info.ChartAuthor,
                            Difficulties = info.Difficulties,
                            Bpm = info.Bpm,
                            DemoEntryName = info.DemoEntryName ?? string.Empty,
                            FileSize = size,
                            LastWriteTime = writeTime
                        };
                        isIndexChanged = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ChartService] Failed to parse {file}: {ex.Message}");
                }
            }

            if (info != null)
            {
                if (entry != null)
                {
                    updatedEntries.Add(entry);
                }

                info.IsLibraryCandidate = candidateFiles.Contains(file);
                info.CandidateSubCategory = info.IsLibraryCandidate ? GetLibrarySubCategory(libraryDir, file) : string.Empty;

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
            else
            {
                _brokenCharts.Add(file);
            }
        }

        // 保存更新后的持久化缓存索引
        if (isIndexChanged || cachedEntries.Count != updatedEntries.Count)
        {
            try
            {
                var json = JsonSerializer.Serialize(updatedEntries, AppJsonContext.Default.ListChartIndexEntry);
                File.WriteAllText(indexFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChartService] Failed to save index: {ex.Message}");
            }
        }
    }


    private static string GetLibrarySubCategory(string libraryDir, string filePath)
    {
        try
        {
            var parent = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(parent)) return string.Empty;
            var relative = Path.GetRelativePath(libraryDir, parent);
            return relative == "." ? string.Empty : relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }
        catch
        {
            return string.Empty;
        }
    }
    // 解析单个自制谱文件并返回其元数据
    public ChartInfo? LoadSingleChart(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            return ParseMdm(filePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChartService] Failed to parse single chart {filePath}: {ex.Message}");
            return null;
        }
    }

    // 缓存命中的情况下，辅助快速单独读取压缩包内的封面，不解析其他无关元数据
    private static void PopulateCover(string filePath, ChartInfo chart)
    {
        try
        {
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
        }
        catch
        {
        }
    }

    public System.Threading.Tasks.Task LoadCoverAsync(ChartInfo chart)
    {
        if (chart.HasAnyCover || string.IsNullOrEmpty(chart.FilePath) || !File.Exists(chart.FilePath))
            return System.Threading.Tasks.Task.CompletedTask;

        return System.Threading.Tasks.Task.Run(() => PopulateCover(chart.FilePath, chart));
    }

    public static void ConvertEpkToInfoJsonInPlace(string filePath)
    {
        try
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
            {
                var epkEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".epk", StringComparison.OrdinalIgnoreCase));
                if (epkEntry != null)
                {
                    var infoEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase));
                    var hasInfoJson = infoEntry != null;
                    var hasVideo = archive.Entries.Any(e => e.Name.Equals("video.mp4", StringComparison.OrdinalIgnoreCase));
                    var hasCinemaJson = archive.Entries.Any(e => e.Name.Equals("cinema.json", StringComparison.OrdinalIgnoreCase));
                    var backgroundVideoOpacity = 1f;
                    if (infoEntry == null)
                    {
                        byte[] epkBytes;
                        using (var epkStream = epkEntry.Open())
                        using (var ms = new MemoryStream())
                        {
                            epkStream.CopyTo(ms);
                            epkBytes = ms.ToArray();
                        }
                        var jsonNode = MdModManager.Helpers.MsgPackDecoder.Decode(epkBytes);
                        if (jsonNode != null)
                        {
                            if (jsonNode is JsonObject rootObj && rootObj["meta"] is JsonObject metaObj)
                            {
                                if (metaObj["background_video_opacity"] is JsonValue opacityValue &&
                                    opacityValue.TryGetValue<float>(out var opacity))
                                {
                                    backgroundVideoOpacity = opacity;
                                }

                                var nameStr = metaObj["name"]?.ToString();
                                if (nameStr != null) rootObj["name"] = JsonValue.Create(nameStr);

                                var authorStr = metaObj["author"]?.ToString();
                                if (authorStr != null) rootObj["author"] = JsonValue.Create(authorStr);

                                var bpmStr = metaObj["bpm"]?.ToString();
                                if (bpmStr != null) rootObj["bpm"] = JsonValue.Create(bpmStr);

                                if (metaObj["maps"] is JsonObject mapsObj)
                                {
                                    var charters = new List<string>();
                                    for (int i = 1; i <= 5; i++)
                                    {
                                        if (mapsObj[$"map{i}"] is JsonObject mapX)
                                        {
                                            var ratingStr = mapX["rating"]?.ToString();
                                            if (ratingStr != null)
                                            {
                                                rootObj[$"difficulty{i}"] = JsonValue.Create(ratingStr);
                                            }
                                            if (mapX["charters"] is JsonArray chartersArr)
                                            {
                                                var mapCharters = new List<string>();
                                                foreach (var c in chartersArr)
                                                {
                                                    var cStr = c?.ToString();
                                                    if (!string.IsNullOrWhiteSpace(cStr))
                                                    {
                                                        if (!charters.Contains(cStr))
                                                        {
                                                            charters.Add(cStr);
                                                        }
                                                        if (!mapCharters.Contains(cStr))
                                                        {
                                                            mapCharters.Add(cStr);
                                                        }
                                                    }
                                                }
                                                if (mapCharters.Count > 0)
                                                {
                                                    rootObj[$"levelDesigner{i}"] = JsonValue.Create(string.Join(", ", mapCharters));
                                                }
                                            }
                                        }
                                    }
                                    if (charters.Count > 0)
                                    {
                                        var combinedCharters = string.Join(", ", charters);
                                        rootObj["charter"] = JsonValue.Create(combinedCharters);
                                        // 写入谱师信息以供游戏识别
                                        rootObj["levelDesigner"] = JsonValue.Create(combinedCharters);
                                    }
                                }
                            }
                            var jsonString = jsonNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                            var newInfoEntry = archive.CreateEntry("info.json");
                            using (var newInfoStream = newInfoEntry.Open())
                            using (var writer = new StreamWriter(newInfoStream, System.Text.Encoding.UTF8))
                            {
                                writer.Write(jsonString);
                            }
                            hasInfoJson = true;
                        }
                    }

                    if (hasVideo && !hasCinemaJson)
                    {
                        var cinemaEntry = archive.CreateEntry("cinema.json");
                        using var cinemaStream = cinemaEntry.Open();
                        using var cinemaWriter = new StreamWriter(cinemaStream, System.Text.Encoding.UTF8);
                        cinemaWriter.Write(new JsonObject
                        {
                            ["file_name"] = "video.mp4",
                            ["opacity"] = backgroundVideoOpacity
                        }.ToJsonString());
                    }

                    if (hasInfoJson)
                    {
                        epkEntry.Delete();
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static ChartInfo ParseMdm(string filePath)
    {
        bool needsConversion = false;
        try
        {
            using (var zipCheck = ZipFile.OpenRead(filePath))
            {
                var hasJson = zipCheck.Entries.Any(e => e.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase));
                var hasEpk = zipCheck.Entries.Any(e => e.Name.EndsWith(".epk", StringComparison.OrdinalIgnoreCase));
                if (!hasJson && hasEpk)
                {
                    needsConversion = true;
                }
            }
        }
        catch
        {
        }

        if (needsConversion)
        {
            ConvertEpkToInfoJsonInPlace(filePath);
        }

        var chart = new ChartInfo
        {
            FilePath = filePath,
            Name = Path.GetFileNameWithoutExtension(filePath)
        };

        using var zip = ZipFile.OpenRead(filePath);
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
                    ParseInfoJson(ms.ToArray(), chart);
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

    // 自动检测并使用合适的编码解码文本
    private static string DecodeText(byte[] bytes)
    {
        int offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            offset = 3;
        }

        var utf8 = System.Text.Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        if (utf8.StartsWith('\uFEFF'))
        {
            utf8 = utf8.Substring(1);
        }

        if (!utf8.Contains('\uFFFD'))
            return utf8;

        var gbk = System.Text.Encoding.Default.GetString(bytes);
        if (gbk.StartsWith('\uFEFF'))
        {
            gbk = gbk.Substring(1);
        }
        return gbk;
    }

    private static void ParseInfoJson(byte[] bytes, ChartInfo chart)
    {
        var text = DecodeText(bytes);
        var root = JsonNode.Parse(text);
        if (root == null) return;

        // Song name — prefer localised "name" field
        var name = root["name"]?.GetValue<string>()
                   ?? root["song_name"]?.GetValue<string>()
                   ?? root["title"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(name))
            chart.Name = name;

        // Music author
        var musicAuthorKeys = new[] { "author", "music_author", "artist", "composer" };
        foreach (var key in musicAuthorKeys)
        {
            var val = root[key]?.ToString();
            if (!string.IsNullOrWhiteSpace(val))
            {
                chart.MusicAuthor = val;
                break;
            }
        }

        // Chart/level designer
        var charterKeys = new[] { "levelDesigner", "levelDesigner1", "levelDesigner2", "levelDesigner3", "levelDesigner4", "levelDesigner5", "level_designer", "charter", "mapper" };
        foreach (var key in charterKeys)
        {
            var val = root[key]?.ToString();
            if (!string.IsNullOrWhiteSpace(val))
            {
                chart.ChartAuthor = val;
                break;
            }
        }

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


