using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AsmResolver.DotNet;

namespace MdModManager.Services;

public sealed record BetterMdConflict(string FilePath, string DisplayName);

public sealed record BetterMdDisableResult(int DisabledCount, IReadOnlyList<string> FailedMods);

/// <summary>检测 BetterMD 已声明的不兼容模组，并将它们以 .disabled 后缀停用。</summary>
public sealed class BetterMdConflictService
{
    // 基于 BetterMD 的不兼容声明，并补充已确认的实际冲突模组。
    private static readonly HashSet<string> IncompatibleAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AltTabMute", "Amalgamuse", "BackgroundRecorder", "BetterView", "Cinema",
        "CustomAlbums", "CustomBackgrounds", "Ensemble", "Euterpe",
        "FavGirl", "Headquarters", "HiddenQol", "HiddenQol_fixed", "Info+",
        "MuseDashEnsemble", "ModCombination", "OwnGirl", "RankTarget",
        "ScoreboardCharacters", "SelectiveEffects", "SongInfo", "TrueAbove1kRank", "UITweaks", "UnlockAll",
        "VictoryScreenSwitcher"
    };

    // 模组市场名称与程序集名称不完全一致时使用的别名。
    private static readonly HashSet<string> IncompatibleCatalogAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "CharacterScoreboard", "Scoreboard characters and elfins", "Song info", "True rank for 999+", "UI tweaks"
    };

    public static bool IsBetterMdIncompatible(params string?[] identities) =>
        identities.Any(identity => !string.IsNullOrWhiteSpace(identity)
            && (IncompatibleAssemblyNames.Contains(identity)
                || IncompatibleCatalogAliases.Contains(identity)));

    public Task<IReadOnlyList<BetterMdConflict>> FindConflictsAsync(string gamePath) =>
        Task.Run(() => FindConflicts(gamePath));

    public async Task<BetterMdDisableResult> DisableAsync(IEnumerable<BetterMdConflict> conflicts)
    {
        var disabledCount = 0;
        var failedMods = new List<string>();

        foreach (var conflict in conflicts)
        {
            try
            {
                if (!File.Exists(conflict.FilePath))
                    continue;

                await Task.Run(() => File.Move(conflict.FilePath, conflict.FilePath + ".disabled"));
                disabledCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BetterMD] 禁用冲突模组失败 {conflict.FilePath}: {ex.Message}");
                failedMods.Add(conflict.DisplayName);
            }
        }

        return new BetterMdDisableResult(disabledCount, failedMods);
    }

    private static IReadOnlyList<BetterMdConflict> FindConflicts(string gamePath)
    {
        var modsPath = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(modsPath))
            return Array.Empty<BetterMdConflict>();

        var mods = Directory.EnumerateFiles(modsPath, "*.dll")
            .Select(ReadModIdentity)
            .Where(identity => identity != null)
            .Cast<ModIdentity>()
            .ToList();

        if (!mods.Any(IsBetterMd))
            return Array.Empty<BetterMdConflict>();

        return mods
            .Where(identity => !IsBetterMd(identity))
            .Where(identity => IsBetterMdIncompatible(
                identity.AssemblyName,
                identity.ModName,
                Path.GetFileNameWithoutExtension(identity.FilePath)))
            .Select(identity => new BetterMdConflict(identity.FilePath, identity.ModName))
            .ToList();
    }

    private static bool IsBetterMd(ModIdentity identity) =>
        identity.AssemblyName.Equals("BetterMD", StringComparison.OrdinalIgnoreCase)
        || identity.ModName.Equals("BetterMD", StringComparison.OrdinalIgnoreCase);

    private static ModIdentity? ReadModIdentity(string filePath)
    {
        try
        {
            var module = ModuleDefinition.FromFile(filePath);
            var assemblyName = module.Assembly?.Name?.ToString() ?? Path.GetFileNameWithoutExtension(filePath);
            var melonInfo = module.Assembly?.CustomAttributes
                .FirstOrDefault(attribute => attribute.Constructor?.DeclaringType?.Name == "MelonInfoAttribute");
            var modName = melonInfo?.Signature?.FixedArguments.Count >= 4
                ? melonInfo.Signature.FixedArguments[1].Element?.ToString()
                : null;

            return new ModIdentity(filePath, assemblyName, string.IsNullOrWhiteSpace(modName) ? assemblyName : modName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BetterMD] 无法读取模组 {filePath}: {ex.Message}");
            return null;
        }
    }

    private sealed record ModIdentity(string FilePath, string AssemblyName, string ModName);
}
