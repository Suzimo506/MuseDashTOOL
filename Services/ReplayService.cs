using System.Text.Json;
using MdModManager.Models;

namespace MdModManager.Services;

public sealed class ReplayService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Size, long Stamp, ReplayInfo Info)> _cache = new(StringComparer.OrdinalIgnoreCase);
    public static string GetDirectory(string gamePath) => Path.GetFullPath(Path.Combine(gamePath, "UserData", "BetterMD", "Replays", "Local"));

    public IReadOnlyList<ReplayInfo> Load(string gamePath, CancellationToken cancellation)
    {
        var directory = GetDirectory(gamePath);
        if (!Directory.Exists(directory)) return Array.Empty<ReplayInfo>();
        var files = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => f.Extension.Equals(".mdrp", StringComparison.OrdinalIgnoreCase))
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        var list = new List<ReplayInfo>(files.Length);
        foreach (var file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            ReplayInfo? info;
            var stamp = file.LastWriteTimeUtc.Ticks;
            lock (_gate) info = _cache.TryGetValue(file.FullName, out var entry) && entry.Size == file.Length && entry.Stamp == stamp ? entry.Info : null;
            if (info == null)
            {
                try { info = new ReplayInfo { FilePath = file.FullName, FileSize = file.Length, Card = ReplayCardReader.Read(file.FullName, cancellation) }; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { info = new ReplayInfo { FilePath = file.FullName, FileSize = file.Length, Error = ex.Message }; }
                lock (_gate) _cache[file.FullName] = (file.Length, stamp, info);
            }
            list.Add(info);
        }
        var paths = files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var stale in _cache.Keys.Where(p => !paths.Contains(p)).ToArray()) _cache.Remove(stale);
        return list.OrderByDescending(r => r.Card?.RecordedAtUtc ?? DateTimeOffset.MinValue).ToArray();
    }

    // Stage and validate the exact copied bytes, then move atomically into BetterMD's local directory.
    public bool Import(string source, string gamePath, CancellationToken cancellation)
    {
        if (!Path.GetExtension(source).Equals(".mdrp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .mdrp 回放文件。");
        var directory = GetDirectory(gamePath);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".import-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
            }
            var card = ReplayCardReader.Read(temporary, cancellation);
            using (var stream = ReplayStorage.OpenRead(temporary, checkpoint: () => cancellation.ThrowIfCancellationRequested()))
                ValidateJson(stream, cancellation);
            var name = Path.GetFileNameWithoutExtension(source);
            var isCustom = card.Chart?.IsCustom == true || card.ChartUid.StartsWith("999", StringComparison.Ordinal)
                || card.ChartUid.StartsWith("UID-999", StringComparison.OrdinalIgnoreCase)
                || card.ChartUid.Length == 32 && card.ChartUid.All(Uri.IsHexDigit);
            if (isCustom && !name.StartsWith("C-", StringComparison.OrdinalIgnoreCase)) name = "C-" + name;
            if (name.Length > 180) name = name[..(char.IsHighSurrogate(name[179]) ? 179 : 180)];
            var destination = Path.Combine(directory, name + ".mdrp");
            for (var i = 2; ; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(destination))
                {
                    if (SameContent(temporary, destination, cancellation)) return false;
                    destination = Path.Combine(directory, $"{name} ({i}).mdrp");
                    continue;
                }
                try { File.Move(temporary, destination); break; }
                catch (IOException) when (File.Exists(destination)) { }
            }
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete(ReplayInfo replay, string gamePath)
    {
        var path = Path.GetFullPath(replay.FilePath);
        if (!string.Equals(Path.GetDirectoryName(path), GetDirectory(gamePath), StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(path).Equals(".mdrp", StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("只能删除本地回放目录中的 .mdrp 文件。");
        File.Delete(path);
        lock (_gate) _cache.Remove(path);
    }

    private static bool SameContent(string a, string b, CancellationToken cancellation)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var left = File.OpenRead(a);
        using var right = File.OpenRead(b);
        var first = new byte[81920];
        var second = new byte[81920];
        int length;
        while ((length = left.Read(first)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            right.ReadExactly(second.AsSpan(0, length));
            if (!first.AsSpan(0, length).SequenceEqual(second.AsSpan(0, length))) return false;
        }
        return true;
    }

    private static void ValidateJson(Stream stream, CancellationToken cancellation)
    {
        var buffer = new byte[16384];
        var remaining = 0;
        var state = new JsonReaderState();
        var sawRoot = false;
        var endedRoot = false;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, remaining, buffer.Length - remaining);
            var final = read == 0;
            var length = remaining + read;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, length), final, state);
            while (reader.Read())
            {
                if (!sawRoot)
                {
                    if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("回放根节点必须是对象。");
                    sawRoot = true;
                }
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) endedRoot = true;
            }
            var consumed = (int)reader.BytesConsumed;
            state = reader.CurrentState;
            remaining = length - consumed;
            if (final)
            {
                if (!endedRoot || remaining != 0) throw new InvalidDataException("回放数据不完整。");
                return;
            }
            if (remaining > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
            if (remaining == buffer.Length)
            {
                if (buffer.Length >= 131072) throw new InvalidDataException("回放字段过大。");
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        }
    }
}
