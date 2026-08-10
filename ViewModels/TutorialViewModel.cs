using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using System;

namespace MdModManager.ViewModels;

public partial class TutorialViewModel : ObservableObject
{

    [RelayCommand]
    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TutorialViewModel] OpenUrl error: {ex.Message}");
        }
    }

}
