using System;
using System.Collections.Generic;
using System.Linq;
using MdModManager.Models;

namespace MdModManager.Helpers;

public static class DesignerChartUpdateComparer
{
    public static bool HasChartListChanged(IReadOnlyList<DesignerChart> localCharts, IReadOnlyList<DesignerChart> remoteCharts)
    {
        if (localCharts.Count != remoteCharts.Count)
            return true;

        var localFingerprints = localCharts
            .Select(BuildChartUpdateFingerprint)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var remoteFingerprints = remoteCharts
            .Select(BuildChartUpdateFingerprint)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return !localFingerprints.SequenceEqual(remoteFingerprints, StringComparer.Ordinal);
    }

    public static bool HasChartListChanged(IReadOnlyList<MdmcChart> localCharts, IReadOnlyList<MdmcChart> remoteCharts)
    {
        if (localCharts.Count != remoteCharts.Count)
            return true;

        var localFingerprints = localCharts
            .Select(BuildMdmcChartUpdateFingerprint)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var remoteFingerprints = remoteCharts
            .Select(BuildMdmcChartUpdateFingerprint)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return !localFingerprints.SequenceEqual(remoteFingerprints, StringComparer.Ordinal);
    }

    private static string BuildChartUpdateFingerprint(DesignerChart chart)
    {
        var primaryIdentity = NormalizeDownloadIdentity(chart.DownloadUrl);
        if (string.IsNullOrEmpty(primaryIdentity) && !LooksGeneratedId(chart.Id))
            primaryIdentity = Normalize(chart.Id);

        if (string.IsNullOrEmpty(primaryIdentity))
        {
            primaryIdentity = string.Join("|",
                Normalize(chart.Title),
                Normalize(chart.Author),
                Normalize(chart.Artist),
                Normalize(chart.Bpm));
        }

        var difficulties = chart.Difficulties == null
            ? string.Empty
            : string.Join(",",
                chart.Difficulties
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(Normalize)
                    .OrderBy(x => x, StringComparer.Ordinal));

        return string.Join("||",
            primaryIdentity,
            Normalize(chart.Title),
            Normalize(chart.Author),
            Normalize(chart.Artist),
            Normalize(chart.Bpm),
            difficulties);
    }

    private static string BuildMdmcChartUpdateFingerprint(MdmcChart chart)
    {
        var primaryIdentity = NormalizeDownloadIdentity(chart.CustomDownloadUrl);
        if (string.IsNullOrEmpty(primaryIdentity) && !LooksGeneratedId(chart.Id))
            primaryIdentity = Normalize(chart.Id);

        if (string.IsNullOrEmpty(primaryIdentity))
        {
            primaryIdentity = string.Join("|",
                Normalize(chart.Title),
                Normalize(chart.Charter),
                Normalize(chart.Artist),
                Normalize(chart.Bpm));
        }

        var difficulties = string.Join(",",
            chart.Sheets
                .Select(x => x.Difficulty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(Normalize)
                .OrderBy(x => x, StringComparer.Ordinal));

        return string.Join("||",
            primaryIdentity,
            Normalize(chart.Title),
            Normalize(chart.Charter),
            Normalize(chart.Artist),
            Normalize(chart.Bpm),
            difficulties);
    }

    private static string NormalizeDownloadIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            candidate = uri.AbsolutePath;
        }
        else
        {
            var queryIndex = candidate.IndexOfAny(['?', '#']);
            if (queryIndex >= 0)
                candidate = candidate[..queryIndex];
        }

        candidate = Uri.UnescapeDataString(candidate)
            .Replace('\\', '/')
            .Trim()
            .Trim('/');

        var mdmMarker = "/mdm/";
        var mdmIndex = candidate.IndexOf(mdmMarker, StringComparison.OrdinalIgnoreCase);
        if (mdmIndex >= 0)
            candidate = candidate[(mdmIndex + mdmMarker.Length)..];

        return Normalize(candidate);
    }

    private static bool LooksGeneratedId(string? value)
        => Guid.TryParse(value, out _);

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
