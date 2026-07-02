using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Helpers;
using MdModManager.Models;

namespace MdModManager.Services;

public sealed record ModUpdateCandidate(LocalMod Mod)
{
    public string Name => Mod.Name;
    public string LocalVersion => Mod.Version;
    public string RemoteVersion => Mod.RemoteInfo?.Version ?? string.Empty;
    public string DismissKey => CreateDismissKey(Name, RemoteVersion);

    public static string CreateDismissKey(string name, string remoteVersion) =>
        $"{name}::{remoteVersion}";
}

public interface IModUpdateService
{
    Task<IReadOnlyList<ModUpdateCandidate>> GetLaunchUpdateCandidatesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    bool ShouldShowLaunchUpdatePrompt(IReadOnlyList<ModUpdateCandidate> candidates);

    Task DismissLaunchUpdatePromptAsync(
        IReadOnlyList<ModUpdateCandidate> candidates,
        CancellationToken cancellationToken = default);

    Task UpdateModsAsync(
        IReadOnlyList<ModUpdateCandidate> candidates,
        CancellationToken cancellationToken = default);
}

public sealed class ModUpdateService : IModUpdateService
{
    private readonly IConfigService _configService;
    private readonly IModCatalogService _catalogService;
    private readonly ILocalModService _localModService;
    private readonly ModStagingService _stagingService;

    public ModUpdateService(
        IConfigService configService,
        IModCatalogService catalogService,
        ILocalModService localModService,
        ModStagingService stagingService)
    {
        _configService = configService;
        _catalogService = catalogService;
        _localModService = localModService;
        _stagingService = stagingService;
    }

    public async Task<IReadOnlyList<ModUpdateCandidate>> GetLaunchUpdateCandidatesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath))
            return [];

        var remoteMods = await _catalogService.GetModsAsync(forceRefresh, cancellationToken);
        var stagingPath = _stagingService.GetStagingPath(gamePath);
        var localMods = _localModService.GetLocalMods(remoteMods, stagingPath);

        return localMods
            .Where(static mod =>
                mod.HasUpdate &&
                !mod.IsStaged &&
                !string.IsNullOrWhiteSpace(mod.FilePath) &&
                mod.RemoteInfo != null)
            .OrderBy(static mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static mod => new ModUpdateCandidate(mod))
            .ToList();
    }

    public bool ShouldShowLaunchUpdatePrompt(IReadOnlyList<ModUpdateCandidate> candidates)
    {
        if (candidates.Count == 0)
            return false;

        var dismissed = _configService.Config.DismissedUpdateKeys ?? [];
        return candidates.Any(candidate => !dismissed.Contains(candidate.DismissKey));
    }

    public async Task DismissLaunchUpdatePromptAsync(
        IReadOnlyList<ModUpdateCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        _configService.Config.DismissedUpdateKeys ??= [];
        foreach (var candidate in candidates)
        {
            if (!_configService.Config.DismissedUpdateKeys.Contains(candidate.DismissKey))
            {
                _configService.Config.DismissedUpdateKeys.Add(candidate.DismissKey);
            }
        }

        await _configService.SaveAsync();
    }

    public async Task UpdateModsAsync(
        IReadOnlyList<ModUpdateCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
            return;

        if (!DotNetRuntimeHelper.IsDotNet6Installed())
            throw new InvalidOperationException("请先安装 .NET 6 环境后再自动更新模组。");

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath))
            throw new InvalidOperationException("游戏路径未设置。");

        var remoteMods = await _catalogService.GetModsAsync(forceRefresh: true, cancellationToken);
        var stagingPath = _stagingService.GetStagingPath(gamePath);
        var localMods = _localModService.GetLocalMods(remoteMods, stagingPath)
            .Where(static mod => !mod.IsStaged)
            .ToList();

        using var client = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(30));
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localMod = localMods.FirstOrDefault(mod =>
                !string.IsNullOrWhiteSpace(mod.FilePath) &&
                mod.FilePath.Equals(candidate.Mod.FilePath, StringComparison.OrdinalIgnoreCase));
            if (localMod == null || localMod.RemoteInfo == null || !localMod.HasUpdate)
                continue;

            var remoteInfo = ResolveLatestRemoteInfo(localMod.RemoteInfo, remoteMods);
            var fileName = ResolveDownloadFileName(remoteInfo, "Mods");
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            var visitedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fileName };
            var visitedLibs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await InstallModDependenciesAsync(remoteInfo, gamePath, remoteMods, localMods, client, visitedMods, visitedLibs, cancellationToken);
            await InstallLibDependenciesAsync(remoteInfo, gamePath, client, visitedLibs, cancellationToken);
            await DownloadModFileAsync(remoteInfo, fileName, gamePath, localMod.FilePath, localMod.IsDisabled, client, cancellationToken);
        }
    }

    private static ModInfo ResolveLatestRemoteInfo(ModInfo remoteInfo, IReadOnlyList<ModInfo> remoteMods)
    {
        var fileName = ResolveDownloadFileName(remoteInfo, "Mods");
        return remoteMods.FirstOrDefault(remote =>
            remote.Name.Equals(remoteInfo.Name, StringComparison.OrdinalIgnoreCase) ||
            ResolveDownloadFileName(remote, "Mods").Equals(fileName, StringComparison.OrdinalIgnoreCase))
            ?? remoteInfo;
    }

    private static async Task InstallModDependenciesAsync(
        ModInfo remoteInfo,
        string gamePath,
        IReadOnlyList<ModInfo> remoteMods,
        IReadOnlyList<LocalMod> localMods,
        HttpClient client,
        HashSet<string> visitedMods,
        HashSet<string> visitedLibs,
        CancellationToken cancellationToken)
    {
        foreach (var dependencyName in remoteInfo.DependentMods.Where(static d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dependencyInfo = ResolveModDependency(dependencyName, remoteMods);
            var dependencyFileName = ResolveDownloadFileName(dependencyInfo, "Mods");
            if (string.IsNullOrWhiteSpace(dependencyFileName))
                continue;

            if (!visitedMods.Add(dependencyFileName))
                continue;

            await InstallModDependenciesAsync(dependencyInfo, gamePath, remoteMods, localMods, client, visitedMods, visitedLibs, cancellationToken);
            await InstallLibDependenciesAsync(dependencyInfo, gamePath, client, visitedLibs, cancellationToken);

            if (IsModDependencySatisfied(localMods, dependencyInfo, dependencyFileName))
                continue;

            await DownloadDependencyFileAsync(dependencyInfo, dependencyFileName, gamePath, client, cancellationToken);
        }
    }

    private static async Task InstallLibDependenciesAsync(
        ModInfo remoteInfo,
        string gamePath,
        HttpClient client,
        HashSet<string> visitedLibs,
        CancellationToken cancellationToken)
    {
        foreach (var dependency in remoteInfo.DependentLibs.Where(static d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = ResolveDependencyFileName(dependency, "UserLibs");
            if (string.IsNullOrWhiteSpace(fileName) || !visitedLibs.Add(fileName))
                continue;

            var downloadUrl = ResolveDownloadReference(dependency, "Mods", remoteInfo.Source);
            var targetPath = System.IO.Path.Combine(gamePath, "UserLibs", fileName);
            await DownloadFileAsync(client, downloadUrl, targetPath, cancellationToken);
        }
    }

    private static async Task DownloadDependencyFileAsync(
        ModInfo remoteInfo,
        string fileName,
        string gamePath,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var downloadUrl = BuildDownloadUrl(remoteInfo, fileName, "Mods");
        var targetPath = System.IO.Path.Combine(gamePath, "Mods", fileName);
        await DownloadFileAsync(client, downloadUrl, targetPath, cancellationToken);
    }

    private static async Task DownloadModFileAsync(
        ModInfo remoteInfo,
        string fileName,
        string gamePath,
        string oldFilePath,
        bool keepDisabled,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var downloadUrl = BuildDownloadUrl(remoteInfo, fileName, "Mods");
        var targetFileName = keepDisabled ? fileName + ".disabled" : fileName;
        var targetPath = System.IO.Path.Combine(gamePath, "Mods", targetFileName);
        var bytes = await client.GetByteArrayAsync(downloadUrl, cancellationToken);

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath)!);
        var tempPath = targetPath + ".musedashtool.tmp";
        await System.IO.File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);

        if (!string.Equals(oldFilePath, targetPath, StringComparison.OrdinalIgnoreCase) &&
            System.IO.File.Exists(oldFilePath))
        {
            System.IO.File.Delete(oldFilePath);
        }

        System.IO.File.Move(tempPath, targetPath, overwrite: true);
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        string downloadUrl,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var bytes = await client.GetByteArrayAsync(downloadUrl, cancellationToken);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath)!);
        var tempPath = targetPath + ".musedashtool.tmp";
        await System.IO.File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
        System.IO.File.Move(tempPath, targetPath, overwrite: true);
    }

    private static ModInfo ResolveModDependency(string dependency, IReadOnlyList<ModInfo> remoteMods)
    {
        var dependencyFileName = EnsureDllFileName(ResolveDependencyFileName(dependency, "Mods"));
        var dependencyName = System.IO.Path.GetFileNameWithoutExtension(dependencyFileName);
        return remoteMods.FirstOrDefault(remote =>
            remote.Name.Equals(dependency, StringComparison.OrdinalIgnoreCase) ||
            remote.Name.Equals(dependencyName, StringComparison.OrdinalIgnoreCase) ||
            System.IO.Path.GetFileName(remote.FileName).Equals(dependencyFileName, StringComparison.OrdinalIgnoreCase) ||
            System.IO.Path.GetFileNameWithoutExtension(remote.FileName).Equals(dependencyName, StringComparison.OrdinalIgnoreCase))
            ?? new ModInfo
            {
                Name = dependencyName,
                FileName = dependencyFileName,
                GameVersion = "*",
                Source = "Euterpe"
            };
    }

    private static bool IsModDependencySatisfied(
        IReadOnlyList<LocalMod> localMods,
        ModInfo dependencyInfo,
        string fileName)
    {
        var localMod = localMods.FirstOrDefault(mod =>
            !mod.IsDisabled &&
            System.IO.Path.GetFileName(mod.FilePath).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (localMod == null)
            return false;

        if (string.IsNullOrWhiteSpace(dependencyInfo.Version))
            return true;

        var localVersion = localMod.Version.Replace(" (暂存)", "", StringComparison.OrdinalIgnoreCase).Trim();
        return localVersion.Equals(dependencyInfo.Version, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDownloadFileName(ModInfo remoteInfo, string defaultFolder)
    {
        var value = !string.IsNullOrWhiteSpace(remoteInfo.FileName)
            ? remoteInfo.FileName
            : remoteInfo.DownloadLink;

        if (string.IsNullOrWhiteSpace(value) && defaultFolder == "Mods" && !string.IsNullOrWhiteSpace(remoteInfo.Name))
        {
            value = remoteInfo.Name + ".dll";
        }

        var fileName = ResolveDependencyFileName(value, defaultFolder);
        return defaultFolder == "Mods" ? EnsureDllFileName(fileName) : fileName;
    }

    private static string ResolveDependencyFileName(string? dependency, string defaultFolder)
    {
        if (string.IsNullOrWhiteSpace(dependency))
            return string.Empty;

        var value = dependency.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.LocalPath;
        }

        value = value.Replace('\\', '/');
        var fileName = System.IO.Path.GetFileName(value);
        return string.IsNullOrWhiteSpace(fileName) && defaultFolder == "Mods"
            ? EnsureDllFileName(value)
            : fileName;
    }

    private static string EnsureDllFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        return string.IsNullOrWhiteSpace(System.IO.Path.GetExtension(fileName))
            ? fileName + ".dll"
            : fileName;
    }

    private static string BuildDownloadUrl(ModInfo remoteInfo, string fileName, string defaultFolder)
    {
        if (!string.IsNullOrWhiteSpace(remoteInfo.DownloadLink))
        {
            return ResolveDownloadReference(remoteInfo.DownloadLink, defaultFolder, remoteInfo.Source);
        }

        return remoteInfo.Source == "Euterpe"
            ? BuildMirrorDownloadUrl($"{defaultFolder}/{fileName}")
            : BuildGiteeDownloadUrl($"{defaultFolder}/{fileName}");
    }

    private static string ResolveDownloadReference(string reference, string defaultFolder, string source)
    {
        var trimmed = reference.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            return trimmed;
        }

        var relativePath = trimmed.Replace('\\', '/').TrimStart('/');
        if (!relativePath.Contains('/'))
        {
            relativePath = $"{defaultFolder}/{relativePath}";
        }

        return source == "Euterpe"
            ? BuildMirrorDownloadUrl(relativePath)
            : BuildGiteeDownloadUrl(relativePath);
    }

    private static string BuildMirrorDownloadUrl(string relativePath)
    {
        var domain = MirrorDomainRegistry.GetDownloadDomainOrDefault().TrimEnd('/');
        var protocol = domain.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? string.Empty : "https://";
        return $"{protocol}{domain}/{EscapeRelativePath(relativePath)}";
    }

    private static string BuildGiteeDownloadUrl(string relativePath)
    {
        return $"https://gitee.com/lxymahatma/ModLinks/raw/dev/{EscapeRelativePath(relativePath)}";
    }

    private static string EscapeRelativePath(string relativePath)
    {
        return string.Join("/",
            relativePath.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }
}
