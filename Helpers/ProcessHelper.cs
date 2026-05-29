using System;
using System.Diagnostics;

namespace MdModManager.Helpers;

public static class ProcessHelper
{
    // 调用系统默认程序打开指定文件夹
    public static void OpenFolder(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return;

        if (OperatingSystem.IsWindows())
        {
            Process.Start("explorer.exe", $"\"{folderPath}\"");
        }
        else if (OperatingSystem.IsLinux())
        {
            Process.Start("xdg-open", folderPath);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", folderPath);
        }
    }
}
