using Avalonia.Controls;
using Avalonia.Interactivity;
using MdModManager.Services;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MdModManager.Views;

public partial class AlbumCollectionIntroDialog : Window
{
    private readonly IConfigService? _configService;

    public AlbumCollectionIntroDialog()
    {
        InitializeComponent();
    }

    public AlbumCollectionIntroDialog(IConfigService configService) : this()
    {
        _configService = configService;
    }

    public static async Task ShowDialogAsync(Window owner, IConfigService configService)
    {
        if (configService.Config.SuppressAlbumCollectionIntro)
            return;

        var dialog = new AlbumCollectionIntroDialog(configService);
        dialog.ShowInTaskbar = true;
        var tcs = new TaskCompletionSource<bool>();
        dialog.Closed += (_, _) => tcs.TrySetResult(true);

        var contentControl = owner?.Content as Control;
        var originalHitTest = contentControl?.IsHitTestVisible ?? true;
        if (contentControl != null)
            contentControl.IsHitTestVisible = false;

        dialog.Show();
        await tcs.Task;

        if (contentControl != null)
            contentControl.IsHitTestVisible = originalHitTest;

        if (owner != null && owner.WindowState != WindowState.Minimized)
            owner.Activate();
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (DontShowAgainCheckBox.IsChecked == true && _configService != null)
        {
            _configService.Config.SuppressAlbumCollectionIntro = true;
            _ = _configService.SaveAsync();
        }

        Close();
    }

    private void OnOpenFeishuClick(object? sender, RoutedEventArgs e) =>
        OpenUrl("https://my.feishu.cn/wiki/FMmsw2JswiGTdRk9YwecoMAhnrf");

    private void OnOpenGreenHubClick(object? sender, RoutedEventArgs e) =>
        OpenUrl("https://space.bilibili.com/130793282?spm_id_from=333.337.0.0");

    private void OnOpenMayflycmdClick(object? sender, RoutedEventArgs e) =>
        OpenUrl("https://space.bilibili.com/522632665?spm_id_from=333.337.0.0");

    private void OnOpenSuZimoClick(object? sender, RoutedEventArgs e) =>
        OpenUrl("https://space.bilibili.com/289883561?spm_id_from=333.1387.0.0");

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
