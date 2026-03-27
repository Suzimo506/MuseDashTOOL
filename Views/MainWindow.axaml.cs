using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.DependencyInjection;
using Avalonia.Input;
using MdModManager.Services;
using MdModManager.ViewModels;
using System;
using System.Threading.Tasks;

namespace MdModManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (s, e) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.InitializeAsync();
            }
        };

        this.PointerPressed += (s, e) =>
        {
            var control = e.Source as Avalonia.Controls.Control;
            while (control != null)
            {
                if (control is Avalonia.Controls.ComboBox || 
                    control is Avalonia.Controls.Button || 
                    control is Avalonia.Controls.TextBox || 
                    control is Avalonia.Controls.Slider ||
                    control is Avalonia.Controls.Primitives.Thumb ||
                    control is Avalonia.Controls.Primitives.ScrollBar ||
                    control is Avalonia.Controls.ListBoxItem)
                {
                    return; // Ignore drag if clicking on an interactive control
                }
                control = control.Parent as Avalonia.Controls.Control;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // 如果没有中途 return，说明点击的是非交互区域
                // 清除当前焦点，解决输入框一直处于输入状态的问题
                this.FocusManager?.ClearFocus();
                this.BeginMoveDrag(e);
            }
        };
    }

    private async void OnSelectGamePathClick(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 Muse Dash 游戏目录",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            var path = result[0].Path.LocalPath;
            var pathService = Ioc.Default.GetRequiredService<IGamePathService>();
            var configService = Ioc.Default.GetRequiredService<IConfigService>();
            
            if (pathService.IsValidGamePath(path))
            {
                configService.Config.GamePath = path;
                await configService.SaveAsync();

                if (DataContext is MainWindowViewModel vm)
                {
                    await vm.InitializeAsync();
                }
            }
        }
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                _ = vm.NavigateToTutorialAsync();
                e.Handled = true;
            }
        }
    }

    public async Task ShowMessageBoxAsync(string message)
    {
        await MessageBox.ShowDialogAsync(this, message);
    }
}