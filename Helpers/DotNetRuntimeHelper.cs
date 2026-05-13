using System;
using System.IO;
using System.Linq;

namespace MdModManager.Helpers;

public static class DotNetRuntimeHelper
{
    public static bool IsDotNet6Installed()
    {
        return HasDotNet6InstallInProgramFiles(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ||
               HasDotNet6InstallInProgramFiles(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
    }

    private static bool HasDotNet6InstallInProgramFiles(string programFilesPath)
    {
        if (string.IsNullOrWhiteSpace(programFilesPath))
            return false;

        var dotnetRoot = Path.Combine(programFilesPath, "dotnet");
        return HasVersion6Directory(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App")) ||
               HasVersion6Directory(Path.Combine(dotnetRoot, "shared", "Microsoft.WindowsDesktop.App")) ||
               HasVersion6Directory(Path.Combine(dotnetRoot, "sdk"));
    }

    private static bool HasVersion6Directory(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
                return false;

            return Directory.EnumerateDirectories(directoryPath)
                .Select(Path.GetFileName)
                .Any(name => !string.IsNullOrWhiteSpace(name) && name.StartsWith("6.", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }
}
