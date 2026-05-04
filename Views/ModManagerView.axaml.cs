using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class ModManagerView : UserControl
{
    public ModManagerView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);
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
            if (DataContext is ModManagerViewModel vm)
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
        if (files != null && files.Any(f => f.Path.LocalPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.Path.LocalPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
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
        if (files != null && DataContext is ModManagerViewModel vm)
        {
            foreach (var file in files)
            {
                var filePath = file.Path.LocalPath;
                if (filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    await vm.ImportModFileAsync(filePath);
                }
            }
        }
    }

    /// <summary>
    /// "导入"按钮点击事件：处理文件选择和格式校验
    /// </summary>
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要导入的 Mod (.dll)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("MelonLoader Mod") { Patterns = new[] { "*.dll" } }
            }
        });

        if (result == null || result.Count == 0) return;

        var file = result[0];
        var filePath = file.Path.LocalPath;

        if (!filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var notificationService = Ioc.Default.GetRequiredService<INotificationService>();
            notificationService.ShowFailure("导入失败", "此文件格式错误！");
            return;
        }

        if (DataContext is ModManagerViewModel vm)
        {
            await vm.ImportModFileAsync(filePath);
        }
    }

    private async void OnDeleteModClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not LocalMod mod) return;
        if (DataContext is not ModManagerViewModel vm) return;

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

        vm.DeleteModCommand.Execute(mod);
    }

    /// <summary>
    /// 下载按钮点击：.NET 6 特殊处理，其余跳转 Euterpe
    /// </summary>
    private void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LocalMod mod && mod.RemoteInfo?.FileName == "dotnet6-runtime-placeholder")
        {
            var url = mod.RemoteInfo.DownloadLink;
            if (string.IsNullOrEmpty(url)) url = mod.RemoteInfo.HomePage;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://euterpe-org.com") { UseShellExecute = true });
        }
        catch { }
    }
}
