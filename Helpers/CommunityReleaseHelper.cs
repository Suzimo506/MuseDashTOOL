using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace MdModManager.Helpers;

public static class CommunityReleaseHelper
{
    public static List<string> ParseReleaseTags(JsonElement element)
    {
        var tags = new List<string>();

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AddSplitTags(tags, element.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        AddSplitTags(tags, item.GetString());
                }
                break;
        }

        return tags;
    }

    public static List<string> MergeReleaseTags(IEnumerable<string>? defaults, params JsonElement[] elements)
    {
        var tags = new List<string>();

        if (defaults != null)
        {
            foreach (var tag in defaults)
                AddTag(tags, tag);
        }

        foreach (var element in elements)
        {
            foreach (var tag in ParseReleaseTags(element))
                AddTag(tags, tag);
        }

        return tags;
    }

    public static string ResolveReleaseDownloadUrl(
        string downloadUrl,
        string githubRepoUrl,
        IReadOnlyList<string> candidateTags)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl) ||
            downloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return downloadUrl;
        }

        var assetName = Path.GetFileName(downloadUrl);
        foreach (var tag in candidateTags)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;
            return $"{githubRepoUrl}/releases/download/{tag}/{assetName}";
        }

        return downloadUrl;
    }

    private static void AddSplitTags(List<string> tags, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        foreach (var part in raw.Split([',', '，', ';', '；', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddTag(tags, part);
    }

    private static void AddTag(List<string> tags, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        var normalized = tag.Trim();
        if (!tags.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            tags.Add(normalized);
    }
}
