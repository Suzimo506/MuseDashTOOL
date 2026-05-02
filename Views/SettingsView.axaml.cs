using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;

namespace MdModManager.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        System.Console.WriteLine("[SettingsView] Start InitializeComponent");
        AvaloniaXamlLoader.Load(this);
        System.Console.WriteLine("[SettingsView] End InitializeComponent");
    }

    private void OnImportImagePointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Right)
        {
            if (this.DataContext is MdModManager.ViewModels.SettingsViewModel vm)
            {
                vm.ClearBackgroundImageCommand.Execute(null);
            }
        }
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Avalonia.Input.DragDrop.SetAllowDrop(this, true);
        AddHandler(Avalonia.Input.DragDrop.DropEvent, OnDrop);
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RemoveHandler(Avalonia.Input.DragDrop.DropEvent, OnDrop);
    }

    private async void OnDrop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        #pragma warning disable CS0618
        var files = e.Data.GetFiles();
        #pragma warning restore CS0618
        if (files != null && this.DataContext is MdModManager.ViewModels.SettingsViewModel vm)
        {
            var filePaths = new System.Collections.Generic.List<string>();
            foreach (var file in files)
            {
                if (file is Avalonia.Platform.Storage.IStorageFile storageFile)
                {
                    filePaths.Add(storageFile.Path.LocalPath);
                }
                else if (file is Avalonia.Platform.Storage.IStorageItem storageItem)
                {
                    filePaths.Add(storageItem.Path.LocalPath);
                }
            }
            if (filePaths.Count > 0)
            {
                await vm.ImportFontsFromFilesAsync(filePaths);
            }
        }
    }
}
