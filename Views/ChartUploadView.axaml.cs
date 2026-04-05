using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class ChartUploadView : UserControl
{
    public ChartUploadView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files != null && files.Any(IsSupportedFile))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files == null || DataContext is not ChartUploadViewModel vm)
            return;

        await vm.AddFilesAsync(files
            .Where(IsSupportedFile)
            .Take(1)
            .Select(file => file.Path.LocalPath));
    }

    private async void OnPickFilesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ChartUploadViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要上传的谱面文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Muse Dash 谱面 (.mdm, .zip)")
                {
                    Patterns = new[] { "*.mdm", "*.zip" }
                }
            }
        });

        if (files.Count == 0)
            return;

        await vm.AddFilesAsync(files.Select(file => file.Path.LocalPath));
    }

    private static bool IsSupportedFile(IStorageItem item)
    {
        var path = item.Path.LocalPath;
        return path.EndsWith(".mdm", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }
}
