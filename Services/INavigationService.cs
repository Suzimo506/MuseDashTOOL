using System;
using MdModManager.Models;

namespace MdModManager.Services;

public interface INavigationService
{
    event Action<string> OnRequestConfigNavigation;
    event Action<string> OnRequestModDownloadNavigation;
    event Action<MdenGlobalSearchRequest> OnRequestGlobalChartSearchNavigation;
    void RequestNavigateToConfig(string filePath);
    void RequestNavigateToModDownload(string modName);
    void RequestNavigateToGlobalChartSearch(MdenGlobalSearchRequest request);
    MdenGlobalSearchRequest? ConsumePendingGlobalChartSearch();
}

public class NavigationService : INavigationService
{
    public event Action<string>? OnRequestConfigNavigation;
    public event Action<string>? OnRequestModDownloadNavigation;
    public event Action<MdenGlobalSearchRequest>? OnRequestGlobalChartSearchNavigation;
    private MdenGlobalSearchRequest? _pendingGlobalChartSearch;

    public void RequestNavigateToConfig(string filePath)
    {
        OnRequestConfigNavigation?.Invoke(filePath);
    }

    public void RequestNavigateToModDownload(string modName)
    {
        OnRequestModDownloadNavigation?.Invoke(modName);
    }

    public void RequestNavigateToGlobalChartSearch(MdenGlobalSearchRequest request)
    {
        if (OnRequestGlobalChartSearchNavigation == null)
        {
            _pendingGlobalChartSearch = request;
            return;
        }

        OnRequestGlobalChartSearchNavigation.Invoke(request);
    }

    public MdenGlobalSearchRequest? ConsumePendingGlobalChartSearch()
    {
        var request = _pendingGlobalChartSearch;
        _pendingGlobalChartSearch = null;
        return request;
    }
}
