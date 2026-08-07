using System.IO.Compression;
using System.Text.Json;

namespace MdModManager.Services;

public sealed class CustomChartMigrationResult
{
    public int RemovedModCount { get; set; }
    public int MigratedChartCount { get; set; }
    public List<string> Errors { get; } = new();
}

public static class CustomChartMigrationService
{
    private const string EuterpeChartsFolderName = "Euterpe_Charts";
    private const string EuterpeTempFolderName = "Euterpe_Temp";
    private static readonly string[] ChartFolderNames = ["Online", "Offline"];

    public static Task<CustomChartMigrationResult> MigrateAsync(string gamePath)
    {
        return Task.Run(() => Migrate(gamePath, cleanLegacyEnvironment: true));
    }

    public static bool HasLegacyChartDirectories(string gamePath)
    {
        var euterpeRoot = Path.Combine(gamePath, EuterpeChartsFolderName);
        return ChartFolderNames.Any(folderName => Directory.Exists(Path.Combine(euterpeRoot, folderName)));
    }

    public static Task<CustomChartMigrationResult> ConvertLegacyChartsAsync(string gamePath)
    {
        return Task.Run(() => Migrate(gamePath, cleanLegacyEnvironment: false));
    }

    private static CustomChartMigrationResult Migrate(string gamePath, bool cleanLegacyEnvironment)
    {
        var result = new CustomChartMigrationResult();
        if (cleanLegacyEnvironment)
        {
            try
            {
                RemoveEuterpeMods(gamePath, result);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"扫描 Euterpe.dll 失败：{ex.Message}");
            }

            RemoveEuterpeTempDirectory(gamePath, result);
        }

        var euterpeRoot = Path.Combine(gamePath, EuterpeChartsFolderName);
        if (!Directory.Exists(euterpeRoot))
            return result;

        var targetDirectory = Path.Combine(gamePath, "Custom_Albums");
        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"创建 {targetDirectory} 失败：{ex.Message}");
            return result;
        }

        foreach (var folderName in ChartFolderNames)
        {
            var sourceDirectory = Path.Combine(euterpeRoot, folderName);
            if (!Directory.Exists(sourceDirectory))
                continue;

            try
            {
                MigrateChartDirectory(sourceDirectory, targetDirectory, result);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"扫描 {sourceDirectory} 失败：{ex.Message}");
            }
        }

        try
        {
            DeleteEmptyDirectories(euterpeRoot);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"清理 {euterpeRoot} 失败：{ex.Message}");
        }

        return result;
    }

    private static void RemoveEuterpeTempDirectory(string gamePath, CustomChartMigrationResult result)
    {
        var tempDirectory = Path.Combine(gamePath, EuterpeTempFolderName);
        if (!Directory.Exists(tempDirectory))
            return;

        try
        {
            DeleteDirectory(tempDirectory);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"删除 {tempDirectory} 失败：{ex.Message}");
        }
    }

    private static void RemoveEuterpeMods(string gamePath, CustomChartMigrationResult result)
    {
        var modsDirectory = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(modsDirectory))
            return;

        foreach (var modPath in Directory.EnumerateFiles(modsDirectory, "*.dll", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(path).Equals("Euterpe.dll", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                DeleteFile(modPath);
                result.RemovedModCount++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"删除 {modPath} 失败：{ex.Message}");
            }
        }
    }

    private static void MigrateChartDirectory(
        string sourceDirectory,
        string targetDirectory,
        CustomChartMigrationResult result)
    {
        var unpackedChartDirectories = FindUnpackedChartDirectories(sourceDirectory);
        var archiveFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !unpackedChartDirectories.Any(directory => IsInsideDirectory(path, directory)))
            .Where(IsChartArchive)
            .ToList();

        foreach (var archivePath in archiveFiles)
            MigrateArchive(archivePath, targetDirectory, result);

        foreach (var chartDirectory in unpackedChartDirectories)
            MigrateUnpackedChart(chartDirectory, targetDirectory, result);

        DeleteEmptyDirectories(sourceDirectory);
    }

    private static List<string> FindUnpackedChartDirectories(string sourceDirectory)
    {
        var candidates = Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories)
            .Prepend(sourceDirectory)
            .Where(HasDirectChartMetadata)
            .OrderByDescending(path => path.Length)
            .ToList();

        var selected = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!selected.Any(existing => IsInsideDirectory(existing, candidate)))
                selected.Add(candidate);
        }

        return selected;
    }

    private static bool HasDirectChartMetadata(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(path => Path.GetFileName(path).Equals("info.json", StringComparison.OrdinalIgnoreCase) ||
                             Path.GetExtension(path).Equals(".epk", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsChartArchive(string filePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            return HasChartMetadata(archive);
        }
        catch
        {
            return false;
        }
    }

    private static void MigrateArchive(
        string sourcePath,
        string targetDirectory,
        CustomChartMigrationResult result)
    {
        var destinationPath = GetUniqueDestinationPath(targetDirectory, Path.GetFileNameWithoutExtension(sourcePath));
        var temporaryPath = destinationPath + ".migrating";

        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: true);
            ChartService.ConvertEpkToInfoJsonInPlace(temporaryPath);
            EnsureConvertedChart(temporaryPath);
            File.Move(temporaryPath, destinationPath);
            DeleteFile(sourcePath);
            result.MigratedChartCount++;
        }
        catch (Exception ex)
        {
            TryDeleteFile(temporaryPath);
            result.Errors.Add($"转换 {sourcePath} 失败：{ex.Message}");
        }
    }

    private static void MigrateUnpackedChart(
        string sourceDirectory,
        string targetDirectory,
        CustomChartMigrationResult result)
    {
        var destinationPath = GetUniqueDestinationPath(targetDirectory, Path.GetFileName(sourceDirectory));
        var temporaryPath = destinationPath + ".migrating";

        try
        {
            ZipFile.CreateFromDirectory(sourceDirectory, temporaryPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            ChartService.ConvertEpkToInfoJsonInPlace(temporaryPath);
            EnsureConvertedChart(temporaryPath);
            File.Move(temporaryPath, destinationPath);
            DeleteDirectory(sourceDirectory);
            result.MigratedChartCount++;
        }
        catch (Exception ex)
        {
            TryDeleteFile(temporaryPath);
            result.Errors.Add($"转换 {sourceDirectory} 失败：{ex.Message}");
        }
    }

    private static void EnsureConvertedChart(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var infoEntry = archive.Entries.FirstOrDefault(entry =>
            entry.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase));
        if (infoEntry == null)
            throw new InvalidDataException("未能生成 info.json");

        using (var infoStream = infoEntry.Open())
        using (var document = JsonDocument.Parse(infoStream))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("info.json 格式无效");
        }

        if (!archive.Entries.Any(entry => IsChartMapFile(entry.Name)))
            throw new InvalidDataException("未找到 BMS 谱面文件");
    }

    private static bool IsChartMapFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".bms", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bme", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasChartMetadata(ZipArchive archive)
    {
        return archive.Entries.Any(entry =>
            entry.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase) ||
            entry.Name.EndsWith(".epk", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetUniqueDestinationPath(string targetDirectory, string sourceName)
    {
        var safeName = string.Join("_", sourceName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "Euterpe Chart";

        var destinationPath = Path.Combine(targetDirectory, safeName + ".mdm");
        var suffix = 1;
        while (File.Exists(destinationPath) || File.Exists(destinationPath + ".migrating"))
        {
            destinationPath = Path.Combine(targetDirectory, $"{safeName} ({suffix++}).mdm");
        }

        return destinationPath;
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(directory, path);
        return relativePath != "." &&
               !Path.IsPathRooted(relativePath) &&
               !relativePath.Equals("..", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void DeleteEmptyDirectories(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
            return;

        foreach (var directory in Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }

        if (!Directory.EnumerateFileSystemEntries(rootDirectory).Any())
            Directory.Delete(rootDirectory);
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        Directory.Delete(path, recursive: true);
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path))
            return;

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            DeleteFile(path);
        }
        catch
        {
        }
    }
}
