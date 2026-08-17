using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MdModManager.ViewModels;

public partial class EuterpeWebViewModel : ObservableObject
{
    private const string HomeUrl = "https://euterpe-org.com/zh-CN/charts";

    [ObservableProperty]
    private Uri _browserUrl = new(HomeUrl);

    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void GoHome()
    {
        BrowserUrl = new Uri(HomeUrl);
    }
}
