using Avalonia.Controls;

namespace MdModManager.Views;

public partial class EuterpeWebView : UserControl
{
    public EuterpeWebView()
    {
        InitializeComponent();
    }

    private void OnGoBackClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WebViewHost.GoBack();
    }
}
