using System;
using System.IO;

namespace MdModManager.Services;

public static class RuntimeLog
{
    private static readonly object Sync = new();

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuseDashTOOL");

    public static string LogPath => Path.Combine(LogDirectory, "runtime-debug.log");

    public static void Write(string source, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {message}";

        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // Ignore console failures for WinExe.
        }

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    public static void Reset()
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.WriteAllText(LogPath, $"=== MuseDashTOOL runtime log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            }
        }
        catch
        {
            // Best effort only.
        }
    }
}
