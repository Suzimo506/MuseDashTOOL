using System;
using System.IO;

namespace MdModManager.Services;

public static class RuntimeLog
{
    private static readonly object Sync = new();
    private static string? _preferredLogDirectory;

    public static string DefaultLogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuseDashTOOL");

    public static string LogDirectory => GetWritableLogDirectory();

    public static string LogPath => Path.Combine(LogDirectory, "runtime-debug.log");

    public static void Configure(string? gamePath)
    {
        lock (Sync)
        {
            // 优先把运行日志放到游戏根目录，方便用户直接在游戏目录里找到。
            // 如果游戏路径为空、无效，或者后续写入失败，会自动回退到 LocalAppData。
            _preferredLogDirectory = string.IsNullOrWhiteSpace(gamePath) ? null : gamePath;
        }
    }

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
                var logPath = GetWritableLogPath();
                File.AppendAllText(logPath, line + Environment.NewLine);
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
                var logPath = GetWritableLogPath();
                File.WriteAllText(logPath, $"=== MuseDashTOOL runtime log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static string GetWritableLogDirectory()
    {
        if (TryEnsureDirectory(_preferredLogDirectory, out var preferred))
        {
            return preferred;
        }

        if (TryEnsureDirectory(DefaultLogDirectory, out var fallback))
        {
            return fallback;
        }

        // 最后兜底回当前目录，尽量保证 LogPath 始终可返回一个值。
        return AppContext.BaseDirectory;
    }

    private static string GetWritableLogPath()
    {
        var directory = GetWritableLogDirectory();
        return Path.Combine(directory, "runtime-debug.log");
    }

    private static bool TryEnsureDirectory(string? directory, out string resolvedDirectory)
    {
        resolvedDirectory = string.Empty;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            resolvedDirectory = directory;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
