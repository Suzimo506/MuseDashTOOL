using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class ChartManagerView : UserControl
{
    private ScrollViewer? _chartScrollViewer;

    public ChartManagerView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);

        _chartScrollViewer = this.FindControl<ScrollViewer>("ChartScrollViewer");

        this.DataContextChanged += (s, e) =>
        {
            if (DataContext is ChartManagerViewModel vm)
            {
                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(ChartManagerViewModel.RequestedScrollY)
                        && vm.RequestedScrollY.HasValue)
                    {
                        var y = vm.RequestedScrollY.Value;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (_chartScrollViewer != null)
                            {
                                _chartScrollViewer.Offset = new Avalonia.Vector(_chartScrollViewer.Offset.X, y);
                            }
                        }, Avalonia.Threading.DispatcherPriority.Background);

                        vm.RequestedScrollY = null;
                    }

                    if (args.PropertyName == nameof(ChartManagerViewModel.IsEditingPageNumber)
                        && vm.IsEditingPageNumber)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            var tb = this.FindControl<TextBox>("PageJumpTextBox_Top")
                                ?? this.FindControl<TextBox>("PageJumpTextBox_Bottom");
                            if (tb != null)
                            {
                                tb.Focus();
                                tb.SelectAll();
                            }
                        }, Avalonia.Threading.DispatcherPriority.Loaded);
                    }
                };
            }
        };
    }
    
    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.FocusManager?.ClearFocus();
    }
    
    private void OnSearchIconPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Avalonia.Visual);
        if (point.Properties.IsRightButtonPressed)
        {
            if (DataContext is ChartManagerViewModel vm)
            {
                vm.ClearSearchCommand.Execute(null);
            }
            e.Handled = true;
        }
    }
    
    private void DragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files != null && files.Any(f => f.Path.LocalPath.EndsWith(".mdm", StringComparison.OrdinalIgnoreCase) || 
                                           f.Path.LocalPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void Drop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files != null && DataContext is ChartManagerViewModel cvm)
        {
            foreach (var file in files)
            {
                var filePath = file.Path.LocalPath;
                if (filePath.EndsWith(".mdm", StringComparison.OrdinalIgnoreCase) || 
                    filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    await cvm.ImportChartAsync(filePath);
                }
            }
        }
    }

    private async void OnDeleteChartClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not ChartInfo chart) return;
        if (DataContext is not ChartManagerViewModel vm) return;

        var configService = Ioc.Default.GetRequiredService<IConfigService>();
        if (!configService.Config.SuppressDeleteConfirmation)
        {
            var dialog = new DeleteConfirmDialog();
            var owner = TopLevel.GetTopLevel(this) as Window;
            await dialog.ShowIndependentDialogAsync(owner);

            if (!dialog.Confirmed) return;

            if (dialog.DontShowAgain)
            {
                configService.Config.SuppressDeleteConfirmation = true;
                _ = configService.SaveAsync();
            }
        }

        vm.DeleteChartCommand.Execute(chart);
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要导入的谱面 (.mdm)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Muse Dash 谱面 (.mdm, .zip)") { Patterns = new[] { "*.mdm", "*.zip" } }
            }
        });

        if (result == null || result.Count == 0) return;

        var file = result[0];
        var filePath = file.Path.LocalPath;

        if (!filePath.EndsWith(".mdm", StringComparison.OrdinalIgnoreCase) && 
            !filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var notificationService = Ioc.Default.GetRequiredService<INotificationService>();
            notificationService.ShowFailure("导入失败", "此文件格式错误！");
            return;
        }

        if (DataContext is ChartManagerViewModel cvm)
        {
            await cvm.ImportChartAsync(filePath);
        }
    }

    private void OnPageNumberClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ChartManagerViewModel vm)
        {
            vm.StartEditPageCommand.Execute(null);
        }
    }

    private void OnPageJumpLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChartManagerViewModel vm)
        {
            vm.JumpPageCommand.Execute(null);
        }
    }
}
