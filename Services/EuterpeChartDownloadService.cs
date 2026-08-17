using System.IO.Compression;
using System.Text.Json.Nodes;
using MdModManager.Helpers;

namespace MdModManager.Services;

public sealed record EuterpeChartDownloadProgress(int CompletedFiles, int TotalFiles, long DownloadedBytes, long TotalBytes);

public interface IEuterpeChartDownloadService
{
    Task DownloadToMdmAsync(
        long cid,
        string outputPath,
        IProgress<EuterpeChartDownloadProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class EuterpeChartDownloadService : IEuterpeChartDownloadService, IDisposable
{
    public const string DownloadScheme = "euterpe-chart";
    private const string DownloadBaseUrl = "https://dl.euterpe-org.com/files/charts/";
    private const string ManifestFileName = "manifest.epk";
    private readonly HttpClient _httpClient;

    public EuterpeChartDownloadService(EuterpeTokenQueryHandler tokenQueryHandler)
    {
        _httpClient = new HttpClient(tokenQueryHandler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MuseDashTOOL/1.5.5");
    }

    public static string CreateTaskUrl(long cid) => $"{DownloadScheme}://charts/{cid}";

    public static bool TryGetCid(string? taskUrl, out long cid)
    {
        cid = 0;
        return Uri.TryCreate(taskUrl, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(DownloadScheme, StringComparison.OrdinalIgnoreCase) &&
               uri.Host.Equals("charts", StringComparison.OrdinalIgnoreCase) &&
               long.TryParse(uri.AbsolutePath.Trim('/'), out cid);
    }

    public async Task DownloadToMdmAsync(
        long cid,
        string outputPath,
        IProgress<EuterpeChartDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var workFolder = Path.Combine(Path.GetTempPath(), "MuseDashTOOL", "Euterpe", $"{cid}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workFolder);

        try
        {
            var manifestPath = Path.Combine(workFolder, ManifestFileName);
            await DownloadFileAsync(cid, ManifestFileName, manifestPath, ct).ConfigureAwait(false);

            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false);
            var manifest = MsgPackDecoder.Decode(manifestBytes) as JsonObject
                ?? throw new InvalidDataException("manifest.epk 格式无效");
            var files = manifest["files"] as JsonObject
                ?? throw new InvalidDataException("manifest.epk 缺少 files 清单");

            var entries = files
                .Where(entry => !entry.Key.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .Select(entry => new ManifestFile(entry.Key, ReadFileSize(entry.Value)))
                .ToArray();
            var totalBytes = entries.Sum(entry => Math.Max(0, entry.Size)) + new FileInfo(manifestPath).Length;
            var downloadedBytes = new FileInfo(manifestPath).Length;
            progress?.Report(new EuterpeChartDownloadProgress(0, entries.Length, downloadedBytes, totalBytes));

            for (var index = 0; index < entries.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[index];
                var filePath = ResolveSafeFilePath(workFolder, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await DownloadFileAsync(cid, entry.Name, filePath, ct).ConfigureAwait(false);
                downloadedBytes += new FileInfo(filePath).Length;
                progress?.Report(new EuterpeChartDownloadProgress(index + 1, entries.Length, downloadedBytes, totalBytes));
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);
            ZipFile.CreateFromDirectory(workFolder, outputPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            ChartService.ConvertEpkToInfoJsonInPlace(outputPath);
            ValidateConvertedPackage(outputPath);
        }
        finally
        {
            TryDeleteDirectory(workFolder);
        }
    }

    private async Task DownloadFileAsync(long cid, string fileName, string destinationPath, CancellationToken ct)
    {
        var encodedName = string.Join('/', fileName.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        var url = $"{DownloadBaseUrl}{cid}/{encodedName}";
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EuterpeHttpError.EnsureSuccessAsync(response, $"下载 {fileName}", ct).ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    private static string ResolveSafeFilePath(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName))
            throw new InvalidDataException("manifest.epk 包含无效文件名");

        var rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, fileName.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("manifest.epk 包含越界文件路径");
        return fullPath;
    }

    private static long ReadFileSize(JsonNode? node)
    {
        if (node is JsonObject entry && entry["size"] is JsonValue size && size.TryGetValue<long>(out var result))
            return result;
        return 0;
    }

    private static void ValidateConvertedPackage(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (!archive.Entries.Any(entry => entry.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Euterpe 谱面未能生成 info.json");
        if (!archive.Entries.Any(entry => Path.GetExtension(entry.Name).Equals(".bms", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Euterpe 谱面没有可用的 BMS 文件");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record ManifestFile(string Name, long Size);
}
