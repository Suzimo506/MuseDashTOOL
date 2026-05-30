using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Helpers;
using MdModManager.Models;
using MdModManager.Services;

namespace MdModManager.ViewModels;

public partial class ConfigManagerViewModel : ObservableObject
{
    private readonly IConfigFileService? _configFileService;
    private readonly IConfigService? _configService;
    private readonly Services.ILocalModService? _localModService;
    private bool _hasShownTutorial;

    private ObservableCollection<CfgFolderNode> _allCfgNodes = new();
    private System.Threading.CancellationTokenSource? _statusCts;
    private string _baselineStatus = Services.I18nService.Instance["Str_359"];

    [ObservableProperty]
    private ObservableCollection<CfgFolderNode> _cfgNodes = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    private CfgFile? _selectedFile;

    [ObservableProperty]
    private string _statusMessage = Services.I18nService.Instance["Str_359"];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isUserDataMissing = false;

    /// <summary>导航进入时预选的文件路径</summary>
    public string? PreSelectedFilePath { get; set; }

    public bool HasSelectedFile => SelectedFile != null;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SelectFileAsync(CfgFile? file)
    {
        SelectedFile = file;
        if (file != null && _configService?.Config.AutoTranslateDescriptions == true && !file.HasBeenTranslated)
        {
            if (Services.I18nService.Instance.CurrentLanguage != "en-US")
            {
                StatusMessage = Services.I18nService.Instance["Str_360"];
                await TranslateSingleFileAsync(file);
            }
            file.HasBeenTranslated = true;
            StatusMessage = string.Format(Services.I18nService.Instance["Str_361"], file.DisplayName);
        }
    }

    public ConfigManagerViewModel()
    {
        // For designer
    }

    public ConfigManagerViewModel(IConfigFileService configFileService, IConfigService configService, Services.ILocalModService localModService)
    {
        _configFileService = configFileService;
        _configService = configService;
        _localModService = localModService;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 检测并弹出配置文件管理教程
        if (!_hasShownTutorial && _configService != null && !_configService.Config.SuppressConfigManagerTutorial)
        {
            _hasShownTutorial = true;
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null;
            if (mainWindow != null)
            {
                var title = Services.I18nService.Instance["Tutorial_Title"] ?? "教程提示";
                var message = Services.I18nService.Instance["Tutorial_ConfigManager"];
                bool dontRemind = await Views.TutorialDialog.ShowDialogAsync(mainWindow, title, message);
                if (dontRemind)
                {
                    _configService.Config.SuppressConfigManagerTutorial = true;
                    await _configService.SaveAsync();
                }
            }
        }

        await RefreshAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_configFileService == null || _configService == null) return;

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            StatusMessage = Services.I18nService.Instance["Str_362"];
            return;
        }

        var userDataPath = System.IO.Path.Combine(gamePath, "UserData");
        if (!System.IO.Directory.Exists(userDataPath))
        {
            IsUserDataMissing = true;
            StatusMessage = Services.I18nService.Instance["Str_363"];
            CfgNodes.Clear();
            SelectedFile = null;
            return;
        }
        IsUserDataMissing = false;

        IsLoading = true;
        StatusMessage = Services.I18nService.Instance["Str_364"];
        CfgNodes.Clear();
        SelectedFile = null;

        try
        {
            // 获取所有配置文件
            var files = await _configFileService.ScanUserDataAsync(gamePath);
            cancellationToken.ThrowIfCancellationRequested();

            var rootNodes = new ObservableCollection<CfgFolderNode>();

            foreach (var f in files)
            {
                var pathParts = f.RelativePath.Split(
                    new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar }, 
                    System.StringSplitOptions.RemoveEmptyEntries);

                // 如果只有一个部分，说明直接在 UserData 根目录
                if (pathParts.Length == 1)
                {
                    rootNodes.Insert(0, new CfgFolderNode 
                    { 
                        IsFileNode = true, 
                        FileItem = f 
                    });
                    continue;
                }

                // 多层目录：如 MelonStartScreen/Themes/MDMC/LogoImage.cfg
                // 前面的 Length - 1 个都是文件夹
                var currentCollection = rootNodes;
                
                for (int i = 0; i < pathParts.Length - 1; i++)
                {
                    var folderName = pathParts[i];
                    var existingFolder = currentCollection.FirstOrDefault(n => !n.IsFileNode && n.FolderName.Equals(folderName, System.StringComparison.OrdinalIgnoreCase));
                    
                    if (existingFolder == null)
                    {
                        existingFolder = new CfgFolderNode 
                        { 
                            IsFileNode = false, 
                            FolderName = folderName 
                        };
                        currentCollection.Add(existingFolder);
                    }
                    
                    currentCollection = existingFolder.Children;
                }

                // 最后一个元素是具体的配置文件，被添加到最深层文件夹的 Children 中
                var parentFolder = GetOrAddDeepFolder(rootNodes, pathParts.Take(pathParts.Length - 1).ToArray());
                parentFolder.Children.Add(new CfgFolderNode
                {
                    IsFileNode = true,
                    FileItem = f
                });
            }

            // 递归对 Children 进行排序：文件夹在前，文件在后
            foreach (var node in rootNodes)
                SortChildrenRecursive(node);

            CfgNodes = new ObservableCollection<CfgFolderNode>(
                rootNodes.OrderBy(n => n.SortOrder).ThenBy(n => n.FolderName)
            );

            _baselineStatus = CfgNodes.Count == 0
                ? Services.I18nService.Instance["Str_365"]
                : string.Format(Services.I18nService.Instance["Str_366"], files.Count);
            StatusMessage = _baselineStatus;

            // 处理预选文件/文件夹逻辑
            if (!string.IsNullOrEmpty(PreSelectedFilePath))
            {
                var targetFile = files.FirstOrDefault(f => f.FilePath.Equals(PreSelectedFilePath, System.StringComparison.OrdinalIgnoreCase));
                if (targetFile != null)
                {
                    await SelectFileAsync(targetFile);
                    // 展开包含该文件的所有父节点
                    ExpandParentsRecursive(CfgNodes, targetFile.FilePath);
                }
                else
                {
                    // 如果不是具体文件，尝试作为文件夹处理 (仅展开)
                    ExpandDirectoryRecursive(CfgNodes, PreSelectedFilePath);
                }
                PreSelectedFilePath = null; // 处理完即清空
            }
        }
        catch (System.Exception ex)
        {
            StatusMessage = string.Format(Services.I18nService.Instance["Str_367"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool ExpandParentsRecursive(System.Collections.Generic.IEnumerable<CfgFolderNode> nodes, string targetPath)
    {
        bool found = false;
        foreach (var node in nodes)
        {
            if (node.IsFileNode && node.FileItem?.FilePath == targetPath)
            {
                found = true;
            }
            else if (!node.IsFileNode)
            {
                if (ExpandParentsRecursive(node.Children, targetPath))
                {
                    node.IsExpanded = true;
                    found = true;
                }
            }
        }
        return found;
    }

    private bool ExpandDirectoryRecursive(System.Collections.Generic.IEnumerable<CfgFolderNode> nodes, string targetDirPath)
    {
        bool found = false;
        foreach (var node in nodes)
        {
            if (node.IsFileNode) continue;

            // 检查该节点是否就是我们要找的目录
            // 由于 ConfigManagerViewModel 解析时会将 UserData 后面的路径展平
            // 我们需要根据节点里的子文件来推断其在磁盘上的物理对应文件夹
            var firstFile = FindFirstFileNode(node);
            if (firstFile?.FileItem != null)
            {
                var dir = System.IO.Path.GetDirectoryName(firstFile.FileItem.FilePath);
                while (!string.IsNullOrEmpty(dir))
                {
                    if (dir.Equals(targetDirPath, StringComparison.OrdinalIgnoreCase))
                    {
                        node.IsExpanded = true;
                        found = true;
                        break;
                    }
                    dir = System.IO.Path.GetDirectoryName(dir);
                }
            }

            if (!found && ExpandDirectoryRecursive(node.Children, targetDirPath))
            {
                node.IsExpanded = true;
                found = true;
            }
        }
        return found;
    }

    private CfgFolderNode GetOrAddDeepFolder(ObservableCollection<CfgFolderNode> currentCollection, string[] folderPath)
    {
        CfgFolderNode? currentFolder = null;
        foreach (var name in folderPath)
        {
            var folder = currentCollection.FirstOrDefault(n => !n.IsFileNode && n.FolderName.Equals(name, System.StringComparison.OrdinalIgnoreCase));
            if (folder == null)
            {
                folder = new CfgFolderNode { IsFileNode = false, FolderName = name };
                currentCollection.Add(folder);
            }
            currentFolder = folder;
            currentCollection = folder.Children;
        }
        return currentFolder!;
    }

    private void SortChildrenRecursive(CfgFolderNode node)
    {
        if (node.Children.Count > 0)
        {
            var sorted = node.Children.OrderBy(n => n.SortOrder).ThenBy(n => n.FolderName).ToList();
            node.Children.Clear();
            foreach (var child in sorted)
            {
                node.Children.Add(child);
                SortChildrenRecursive(child);
            }
        }
    }

    private async Task TranslateSingleFileAsync(CfgFile file)
    {
        StatusMessage = string.Format(Services.I18nService.Instance["Str_368"], file.DisplayName);
        var textsToTranslate = new System.Collections.Generic.List<string>();
        var entriesWithComments = new System.Collections.Generic.List<CfgEntry>();

        foreach (var entry in file.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.RawComment) && entry.DisplayComment == entry.RawComment)
            {
                textsToTranslate.Add(entry.RawComment);
                entriesWithComments.Add(entry);
            }
        }

        if (textsToTranslate.Count == 0)
        {
            file.HasBeenTranslated = true;
            StatusMessage = Services.I18nService.Instance["Str_359"];
            return;
        }

        var translatedComments = await BingTranslateHelper.TranslateAsync(textsToTranslate);

        if (translatedComments.Count == entriesWithComments.Count)
        {
            for (int i = 0; i < entriesWithComments.Count; i++)
            {
                entriesWithComments[i].DisplayComment = translatedComments[i];
            }
        }
    }

    private async Task ShowStatusMessageAsync(string message, int delayMs = 1500)
    {
        _statusCts?.Cancel();
        _statusCts = new System.Threading.CancellationTokenSource();
        var token = _statusCts.Token;

        StatusMessage = message;

        try
        {
            await Task.Delay(delayMs, token);
            if (!token.IsCancellationRequested)
            {
                StatusMessage = _baselineStatus;
            }
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private async Task SaveEntryAsync(CfgEntry? entry)
    {
        if (entry == null || SelectedFile == null || _configFileService == null) return;

        // 只保存这一个条目所在的文件
        // 先清除其他条目的 IsModified，确保只写这个
        var originalModifiedStates = SelectedFile.Entries.ToDictionary(e => e, e => e.IsModified);
        foreach (var e in SelectedFile.Entries)
            e.IsModified = e == entry && entry.IsModified;

        await _configFileService.SaveCfgFileAsync(SelectedFile);

        // 恢复其他条目的 IsModified 状态（已保存的 entry 已被清零）
        foreach (var (e, wasModified) in originalModifiedStates)
        {
            if (e != entry)
                e.IsModified = wasModified;
        }

        await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_369"], entry.Key));
    }

    [RelayCommand]
    private async Task DeleteSectionAsync(CfgEntry? sectionHeaderEntry)
    {
        if (sectionHeaderEntry == null || SelectedFile == null || _configFileService == null) return;
        
        string sectionNameToDelete = sectionHeaderEntry.SectionName;
        if (string.IsNullOrEmpty(sectionNameToDelete)) return;

        // 获取原文件中所有拥有相同 SectionName 的记录（包括标题本身和所有具体配置项）
        var entriesToDelete = SelectedFile.Entries.Where(e => e.SectionName == sectionNameToDelete).ToList();

        // 将这些记录从本地集合中移除
        foreach (var e in entriesToDelete)
        {
            SelectedFile.Entries.Remove(e);
        }

        // 调用服务将对应的行从物理文件中擦除
        await _configFileService.DeleteSectionAsync(SelectedFile, sectionNameToDelete);

        await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_370"], sectionNameToDelete));
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        if (SelectedFile == null || _configFileService == null) return;

        await _configFileService.SaveCfgFileAsync(SelectedFile);
        await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_371"], SelectedFile.FileName));
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var gamePath = _configService?.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            _ = ShowStatusMessageAsync(Services.I18nService.Instance["Str_372"]);
            return;
        }

        var userDataPath = System.IO.Path.Combine(gamePath, "UserData");
        if (!System.IO.Directory.Exists(userDataPath))
        {
            try
            {
                System.IO.Directory.CreateDirectory(userDataPath);
            }
            catch (System.Exception ex)
            {
                _ = ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_373"], ex.Message));
                return;
            }
        }

        try
        {
            ProcessHelper.OpenFolder(userDataPath);
        }
        catch (System.Exception ex)
        {
            _ = ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_374"], ex.Message));
        }
    }

    public async Task ImportConfigAsync(string filePath)
    {
        var gamePath = _configService?.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            await ShowStatusMessageAsync(Services.I18nService.Instance["Str_362"]);
            return;
        }

        var userDataPath = System.IO.Path.Combine(gamePath, "UserData");
        if (!System.IO.Directory.Exists(userDataPath))
        {
            try
            {
                System.IO.Directory.CreateDirectory(userDataPath);
            }
            catch (System.Exception ex)
            {
                await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_375"], ex.Message));
                return;
            }
        }

        var destPath = System.IO.Path.Combine(userDataPath, System.IO.Path.GetFileName(filePath));
        try
        {
            System.IO.File.Copy(filePath, destPath, overwrite: true);
            await RefreshAsync();
            await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_376"], System.IO.Path.GetFileName(filePath)));
        }
        catch (System.Exception ex)
        {
            await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_377"], ex.Message));
        }
    }

    public async Task DeleteFileNodeAsync(CfgFolderNode node)
    {
        if (node?.FileItem == null) return;

        try
        {
            if (System.IO.File.Exists(node.FileItem.FilePath))
            {
                System.IO.File.Delete(node.FileItem.FilePath);
            }

            // 如果当前正在编辑这个文件，则清空右侧面板
            if (SelectedFile?.FilePath == node.FileItem.FilePath)
            {
                SelectedFile = null;
            }

            // 重新扫描以更新列表
            await RefreshAsync();
            await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_378"], node.FileItem.FileName));
        }
        catch (System.Exception ex)
        {
            await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_379"], ex.Message));
        }
    }

    public async Task DeleteFolderNodeAsync(CfgFolderNode node)
    {
        if (node == null || node.IsFileNode || string.IsNullOrWhiteSpace(node.FolderName)) return;

        var gamePath = _configService?.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) return;
        
        // 尝试构建出这个文件夹的真实路径
        // 因为我们在扫描时将所有的文件夹展平放置，通过递归逻辑也可以处理
        // 更简单、安全的做法是调用现有的 Refresh 逻辑依赖实际文件系统。
        // 此处我们需要精确地定位在 UserData 中对应这个文件夹的路径
        // 我们通过查找该 node 及其任意一级第一个子文件的 RelativePath 来推断目录
        string? folderFullPath = null;

        var firstFileNode = FindFirstFileNode(node);
        if (firstFileNode?.FileItem != null)
        {
            // 例如: FilePath = "D:\Game\UserData\MyFolder\Settings.cfg"
            // 我们知道 FolderName = "MyFolder"
            var dir = System.IO.Path.GetDirectoryName(firstFileNode.FileItem.FilePath);
            // 往上找，直到结尾是 FolderName（防误删）
            while (!string.IsNullOrEmpty(dir))
            {
                if (System.IO.Path.GetFileName(dir).Equals(node.FolderName, System.StringComparison.OrdinalIgnoreCase))
                {
                    folderFullPath = dir;
                    break;
                }
                dir = System.IO.Path.GetDirectoryName(dir);
            }
        }

        if (folderFullPath == null || !System.IO.Directory.Exists(folderFullPath))
        {
            await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_380"], node.FolderName));
            return;
        }

        try
        {
            System.IO.Directory.Delete(folderFullPath, true);
            
            // 如果所选文件在这个文件夹里面，清空选择
            if (SelectedFile != null && SelectedFile.FilePath.StartsWith(folderFullPath, System.StringComparison.OrdinalIgnoreCase))
            {
                SelectedFile = null;
            }

            await RefreshAsync();
            await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_381"], node.FolderName));
        }
        catch (System.Exception ex)
        {
            await ShowStatusMessageAsync(string.Format(Services.I18nService.Instance["Str_379"], ex.Message));
        }
    }

    private CfgFolderNode? FindFirstFileNode(CfgFolderNode current)
    {
        if (current.IsFileNode) return current;

        foreach (var child in current.Children)
        {
            var result = FindFirstFileNode(child);
            if (result != null) return result;
        }

        return null;
    }
}
