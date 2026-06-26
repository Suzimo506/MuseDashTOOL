using System;

namespace MdModManager.Services;

public interface INavigationService
{
    event Action<string> OnRequestConfigNavigation;
    event Action<string> OnRequestModDownloadNavigation;
    void RequestNavigateToConfig(string filePath);
    void RequestNavigateToModDownload(string modName);
}

public class NavigationService : INavigationService
{
    public event Action<string>? OnRequestConfigNavigation;
    public event Action<string>? OnRequestModDownloadNavigation;

    public void RequestNavigateToConfig(string filePath)
    {
        OnRequestConfigNavigation?.Invoke(filePath);
    }

    public void RequestNavigateToModDownload(string modName)
    {
        OnRequestModDownloadNavigation?.Invoke(modName);
    }
}
