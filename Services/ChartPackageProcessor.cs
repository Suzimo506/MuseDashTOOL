using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IChartPackageProcessor
{
    Task<PreparedChartUpload> PrepareUploadAsync(string filePath);
}

public sealed class ChartPackageProcessor : IChartPackageProcessor
{
    private static readonly string[] AudioExtensions = [".ogg", ".mp3", ".wav", ".flac", ".m4a", ".aac"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    public async Task<PreparedChartUpload> PrepareUploadAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("未找到要上传的谱面文件。", filePath);

        try
        {
            using var stream = File.OpenRead(filePath);
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Update, leaveOpen: true);

            var entries = archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .ToList();

            if (entries.Count == 0)
                throw new InvalidOperationException("压缩包里没有可用文件。");

            var coverEntry = FindPreferredEntry(entries, ImageExtensions, ["cover", "album", "illustration"]);
            var demoEntry = FindPreferredEntry(entries, AudioExtensions, ["demo", "preview", "music"]);
            var infoEntry = FindInfoEntry(entries);

            if (coverEntry is null)
                throw new InvalidOperationException("请放入正确的谱面文件！");

            if (demoEntry is null)
                throw new InvalidOperationException("请放入正确的谱面文件！");

            if (infoEntry is null)
                throw new InvalidOperationException("请放入正确的谱面文件！");

            var mapEntry = entries.FirstOrDefault(e => e.Name.EndsWith(".bms", StringComparison.OrdinalIgnoreCase)
                                                    || e.Name.EndsWith(".bme", StringComparison.OrdinalIgnoreCase)
                                                    || e.Name.EndsWith(".bml", StringComparison.OrdinalIgnoreCase));
            if (mapEntry is null)
                throw new InvalidOperationException("请放入正确的谱面文件！");

            var info = ReadPackageInfo(infoEntry);
            var id = GenerateUniqueId();
            var coverExtension = NormalizeExtension(Path.GetExtension(coverEntry.Name), ".png");
            var demoExtension = NormalizeExtension(Path.GetExtension(demoEntry.Name), ".ogg");

            return new PreparedChartUpload
            {
                SourceFilePath = filePath,
                SourceFileName = Path.GetFileName(filePath),
                Id = id,
                OriginalId = info.OriginalId,
                Artist = info.Artist,
                Charter = info.Charter,
                Bpm = info.Bpm,
                Difficulties = info.Difficulties,
                MdmFileName = $"{id}.mdm",
                MdmContent = File.ReadAllBytes(filePath),
                CoverFileName = $"{id}{coverExtension}",
                CoverContent = ReadEntryBytes(coverEntry),
                DemoFileName = $"{id}{demoExtension}",
                DemoContent = ReadEntryBytes(demoEntry)
            };
        }
        catch (Exception ex) when (ex is InvalidDataException ||
                                   ex.Message.Contains("End of Central Directory", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("压缩包目录信息损坏，无法读取。这个文件虽然像 zip/mdm，但不是完整可用的压缩包。", ex);
        }
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        return memory.ToArray();
    }

    private static ZipArchiveEntry? FindPreferredEntry(
        IEnumerable<ZipArchiveEntry> entries,
        IEnumerable<string> supportedExtensions,
        IEnumerable<string> preferredKeywords)
    {
        var supportedSet = new HashSet<string>(supportedExtensions, StringComparer.OrdinalIgnoreCase);
        var candidates = entries
            .Where(entry => supportedSet.Contains(Path.GetExtension(entry.Name)))
            .ToList();

        if (candidates.Count == 0)
            return null;

        foreach (var keyword in preferredKeywords)
        {
            var matched = candidates.FirstOrDefault(entry =>
                entry.FullName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            if (matched is not null)
                return matched;
        }

        return candidates[0];
    }

    private static ZipArchiveEntry? FindInfoEntry(IEnumerable<ZipArchiveEntry> entries)
    {
        var ordered = entries
            .Select(entry => new
            {
                Entry = entry,
                Score = GetInfoScore(entry)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ToList();

        return ordered.FirstOrDefault()?.Entry;
    }

    private static int GetInfoScore(ZipArchiveEntry entry)
    {
        var score = 0;
        var name = entry.Name;
        var fullName = entry.FullName;
        var extension = Path.GetExtension(name);

        if (name.Contains("info", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("info", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            score += 10;

        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ini", StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        if (score == 0 && string.IsNullOrWhiteSpace(extension))
            score = 1;

        return score;
    }

    private static ChartPackageInfo ReadPackageInfo(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        var bytes = memory.ToArray();

        var text = DecodeText(bytes);
        if (TryReadJsonInfo(text, out var jsonInfo))
            return jsonInfo;

        if (TryReadKeyValueInfo(text, out var kvInfo))
            return kvInfo;

        throw new InvalidOperationException("找到了 info 文件，但无法识别里面的字段格式。");
    }

    private static string DecodeText(byte[] bytes)
    {
        var utf8 = Encoding.UTF8.GetString(bytes);
        if (!utf8.Contains('�'))
            return utf8;

        return Encoding.Default.GetString(bytes);
    }

    private static bool TryReadJsonInfo(string text, out ChartPackageInfo info)
    {
        info = new ChartPackageInfo();

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var originalId = FindFirstString(root, "original_id", "originalId", "title", "song_name", "songName", "name");
            if (string.IsNullOrWhiteSpace(originalId))
                return false;

            info = new ChartPackageInfo
            {
                OriginalId = originalId,
                Artist = FindArtist(root),
                Charter = FindCharter(root),
                Bpm = FindFirstString(root, "bpm", "BPM"),
                Difficulties = FindDifficulties(root)
            };

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadKeyValueInfo(string text, out ChartPackageInfo info)
    {
        info = new ChartPackageInfo();
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//"))
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
                separatorIndex = line.IndexOf(':');

            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim().Trim('"');
            dict[key] = value;
        }

        if (dict.Count == 0)
            return false;

        var originalId = FindFirstString(dict, "original_id", "originalId", "title", "song_name", "songName", "name");
        if (string.IsNullOrWhiteSpace(originalId))
            return false;

        info = new ChartPackageInfo
        {
            OriginalId = originalId,
            Artist = FindArtist(dict),
            Charter = FindCharter(dict),
            Bpm = FindFirstString(dict, "bpm", "BPM"),
            Difficulties = FindDifficulties(dict)
        };

        return true;
    }

    private static string FindArtist(JsonElement root) =>
        FindFirstString(root, "artist", "artist_name", "artistName", "author");

    private static string FindArtist(IReadOnlyDictionary<string, string> values) =>
        FindFirstString(values, "artist", "artist_name", "artistName", "author");

    private static string FindCharter(JsonElement root) =>
        FindFirstNonEmptyString(
            root,
            "charter",
            "mapper",
            "designer",
            "levelDesigner",
            "levelDesigner1",
            "levelDesigner2",
            "levelDesigner3",
            "levelDesigner4");

    private static string FindCharter(IReadOnlyDictionary<string, string> values) =>
        FindFirstNonEmptyString(
            values,
            "charter",
            "mapper",
            "designer",
            "levelDesigner",
            "levelDesigner1",
            "levelDesigner2",
            "levelDesigner3",
            "levelDesigner4");

    private static string FindFirstString(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                    continue;

                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => property.Value.ToString(),
                    JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
                    JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
                    _ => string.Empty
                };
            }
        }

        return string.Empty;
    }

    private static string FindFirstNonEmptyString(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = FindFirstString(root, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static string FindFirstString(IReadOnlyDictionary<string, string> values, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (values.TryGetValue(propertyName, out var value))
                return value;
        }

        return string.Empty;
    }

    private static string FindFirstNonEmptyString(IReadOnlyDictionary<string, string> values, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = FindFirstString(values, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static List<string> FindDifficulties(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals("difficulties", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.Equals("difficulty", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.Equals("level", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var result = property.Value.EnumerateArray()
                    .Select(item => item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Number => item.ToString(),
                        _ => null
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToList();

                return AppendVideoFlagIfNeeded(root, result);
            }

            if (property.Value.ValueKind == JsonValueKind.String || property.Value.ValueKind == JsonValueKind.Number)
            {
                var parts = SplitDifficultyText(property.Value.ToString());
                return AppendVideoFlagIfNeeded(root, parts);
            }
        }

        var numberedDifficulties = FindNumberedDifficulties(name => FindFirstString(root, name));
        return AppendVideoFlagIfNeeded(root, numberedDifficulties);
    }

    private static List<string> FindDifficulties(IReadOnlyDictionary<string, string> values)
    {
        var result = SplitDifficultyText(FindFirstString(values, "difficulties", "difficulty", "level"));

        if (result.Count == 0)
            result = FindNumberedDifficulties(name => FindFirstString(values, name));

        if (values.TryGetValue("video", out var videoValue) &&
            IsTruthy(videoValue) &&
            !result.Contains("video", StringComparer.OrdinalIgnoreCase))
        {
            result.Insert(0, "video");
        }

        return result;
    }

    private static List<string> AppendVideoFlagIfNeeded(JsonElement root, List<string> difficulties)
    {
        if (TryGetBoolean(root, "video", out var hasVideo) &&
            hasVideo &&
            !difficulties.Contains("video", StringComparer.OrdinalIgnoreCase))
        {
            difficulties.Insert(0, "video");
        }

        return difficulties;
    }

    private static List<string> FindNumberedDifficulties(Func<string, string> getValue)
    {
        var result = new List<string>();

        for (var index = 1; index <= 4; index++)
        {
            var value = getValue($"difficulty{index}").Trim();
            if (string.IsNullOrWhiteSpace(value) || value == "0")
                continue;

            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
                result.Add(value);
        }

        return result;
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName, out bool value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                value = IsTruthy(property.Value.GetString());
                return true;
            }
        }

        value = false;
        return false;
    }

    private static List<string> SplitDifficultyText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split([',', '/', '|', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string extension, string fallback)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return fallback;

        return extension.StartsWith(".", StringComparison.Ordinal)
            ? extension.ToLowerInvariant()
            : $".{extension.ToLowerInvariant()}";
    }

    private static string GenerateUniqueId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLower(CultureInfo.InvariantCulture);
    }

    private sealed class ChartPackageInfo
    {
        public string OriginalId { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public string Charter { get; init; } = string.Empty;
        public string Bpm { get; init; } = string.Empty;
        public List<string> Difficulties { get; init; } = [];
    }
}
