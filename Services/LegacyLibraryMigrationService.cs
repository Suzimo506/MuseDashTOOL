using System.Text.Json;

namespace MdModManager.Services;

// Only retained for upgrading users who still have the removed library directory.
public static class LegacyLibraryMigrationService
{
    public static (int Moved, List<string> Errors) Migrate(string gamePath)
    {
        var source = Path.GetFullPath(Path.Combine(gamePath, "CustomAlbums_Library"));
        var target = Path.GetFullPath(Path.Combine(gamePath, "Custom_Albums"));
        var errors = new List<string>();
        var moved = 0;
        if (!Directory.Exists(source)) return (moved, errors);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            return (0, new List<string> { "旧谱面目录是链接，未自动迁移。" });
        Directory.CreateDirectory(target);
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            try
            {
                var relative = Path.GetRelativePath(source, file);
                var parts = relative.Split(Path.DirectorySeparatorChar);
                var folder = parts.Length == 1 ? target : Path.Combine(target, parts[0]);
                Directory.CreateDirectory(folder);
                if (parts.Length > 1) categories.Add(folder);
                // BetterMD categories load charts directly, so flatten older nested chart folders.
                var destination = parts.Length > 2 && !file.EndsWith(".mdm", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(target, relative) : Path.Combine(folder, Path.GetFileName(file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                destination = AvailablePath(destination);
                File.Move(file, destination); // Never overwrite existing charts or metadata.
                moved++;
            }
            catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
        }
        foreach (var category in categories)
        {
            if (!Directory.EnumerateFiles(category, "*.mdm").Any()) continue;
            var pack = Path.Combine(category, "pack.json");
            if (File.Exists(pack)) continue;
            try
            {
                using var stream = new FileStream(pack, FileMode.CreateNew);
                JsonSerializer.Serialize(stream, new { Title = Path.GetFileName(category), TitleColorHex = "#ffffff", LongTextScroll = false });
            }
            catch (Exception ex) { errors.Add($"{pack}: {ex.Message}"); }
        }
        // Remove only empty directories; any failed or unrecognized entry stays recoverable.
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options).OrderByDescending(p => p.Length).Append(source))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (Exception ex) { errors.Add($"{directory}: {ex.Message}"); }
        }
        return (moved, errors);
    }

    private static string AvailablePath(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(folder, $"{name} ({i}){extension}");
        return path;
    }
}
