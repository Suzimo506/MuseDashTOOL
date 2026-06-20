using System.IO;
using MdModManager.Models;

namespace MdModManager.Helpers;

public static class ChartDownloadPathHelper
{
    public static string GetDefaultDownloadDirectory(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.GamePath))
            return string.Empty;

        var folderName = config.DownloadChartsToLibraryByDefault
            ? "CustomAlbums_Library"
            : "Custom_Albums";

        var directory = Path.Combine(config.GamePath, folderName);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
