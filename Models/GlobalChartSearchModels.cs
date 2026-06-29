using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MdModManager.Models;

public enum GlobalChartSource
{
    Mdmc,
    Euterpe,
    QQGroup
}

public enum GlobalChartSourceStatus
{
    Idle,
    Searching,
    Ready,
    Warning,
    Error
}

public sealed partial class GlobalChartSearchSourceState : ObservableObject
{
    public GlobalChartSource Source { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private GlobalChartSourceStatus _status = GlobalChartSourceStatus.Idle;

    [ObservableProperty]
    private string _message = "等待搜索";

    [ObservableProperty]
    private int _resultCount;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasNotice => Status is GlobalChartSourceStatus.Warning or GlobalChartSourceStatus.Error;

    public GlobalChartSearchSourceState(GlobalChartSource source, string displayName)
    {
        Source = source;
        DisplayName = displayName;
    }

    partial void OnStatusChanged(GlobalChartSourceStatus value)
    {
        OnPropertyChanged(nameof(HasNotice));
    }
}

public sealed partial class GlobalChartSearchResult : ObservableObject
{
    public required GlobalChartSource Source { get; init; }
    public required string SourceName { get; init; }
    public string SourceDetail { get; init; } = string.Empty;
    public MdmcChart? MdmcChart { get; init; }
    public EuterpeChart? EuterpeChart { get; init; }

    public string Title => MdmcChart?.Title ?? EuterpeChart?.Name ?? string.Empty;
    public string Artist => MdmcChart?.Artist ?? EuterpeChart?.Author ?? string.Empty;
    public string Charter => MdmcChart?.Charter ?? EuterpeChart?.CharterInfo ?? string.Empty;
    public string TitleRomanized => MdmcChart?.TitleRomanized ?? string.Empty;
    public string Bpm => MdmcChart?.Bpm ?? (EuterpeChart?.Bpm > 0 ? EuterpeChart.Bpm.ToString() : string.Empty);
    public int LikesCount => MdmcChart?.LikesCount ?? EuterpeChart?.LikeCount ?? 0;
    public int DownloadCount => EuterpeChart?.DownloadCount ?? 0;
    public bool Ranked => MdmcChart?.Ranked ?? false;
    public bool HasLikesCount => LikesCount > 0;
    public bool ShowLikesBadge => Source != GlobalChartSource.QQGroup && HasLikesCount;
    public bool IsMdmcLikeResult => MdmcChart != null;
    public bool IsEuterpeResult => EuterpeChart != null;
    public bool IsPlaying => MdmcChart?.IsPlaying ?? EuterpeChart?.IsPlaying ?? false;
    public string RepositoryName => Source == GlobalChartSource.QQGroup ? SourceDetail : string.Empty;
    public bool HasRepositoryBadge => !string.IsNullOrWhiteSpace(RepositoryName);
    public bool HasMdenCandidateLabel => !string.IsNullOrWhiteSpace(MdenCandidateLabel);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMdenCandidateLabel))]
    private string _mdenCandidateLabel = string.Empty;

    public string? DisplayCoverSource => MdmcChart?.DisplayCoverSource ?? EuterpeChart?.CoverUrl;
    public bool HasDisplayCoverSource => !string.IsNullOrWhiteSpace(DisplayCoverSource);
    public bool HasStaticDisplayCoverSource => MdmcChart?.HasStaticDisplayCoverSource ?? HasDisplayCoverSource;
    public bool HasAnimatedDisplayCoverSource => MdmcChart?.HasAnimatedDisplayCoverSource ?? false;
    public string? StaticDisplayCoverSource => MdmcChart?.StaticDisplayCoverSource ?? MdmcChart?.DisplayCoverSource ?? DisplayCoverSource;
    public string? AnimatedDisplayCoverSource => MdmcChart?.AnimatedDisplayCoverSource;
    public bool ShouldShowAnimatedCover => MdmcChart?.ShouldShowAnimatedCover ?? false;
    public bool ShouldShowStaticCover => MdmcChart?.ShouldShowStaticCover ?? HasStaticDisplayCoverSource;

    public string DisplayArtist
    {
        get
        {
            var clean = FirstLine(Artist);
            return string.IsNullOrWhiteSpace(clean) ? string.Empty : $"曲：{clean}";
        }
    }

    public string DisplayCharter
    {
        get
        {
            var clean = FirstLine(Charter);
            return string.IsNullOrWhiteSpace(clean) ? string.Empty : $"谱：{clean}";
        }
    }

    public IReadOnlyList<string> DifficultyLabels
    {
        get
        {
            if (MdmcChart != null)
                return MdmcChart.DifficultyLabels;

            if (EuterpeChart != null)
                return EuterpeChart.DifficultyLabels;

            return Array.Empty<string>();
        }
    }

    public void RefreshPlaybackState()
    {
        OnPropertyChanged(nameof(IsPlaying));
    }

    public void RefreshCoverState()
    {
        OnPropertyChanged(nameof(DisplayCoverSource));
        OnPropertyChanged(nameof(HasDisplayCoverSource));
        OnPropertyChanged(nameof(HasStaticDisplayCoverSource));
        OnPropertyChanged(nameof(HasAnimatedDisplayCoverSource));
        OnPropertyChanged(nameof(StaticDisplayCoverSource));
        OnPropertyChanged(nameof(AnimatedDisplayCoverSource));
        OnPropertyChanged(nameof(ShouldShowAnimatedCover));
        OnPropertyChanged(nameof(ShouldShowStaticCover));
    }

    private static string FirstLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var idx = value.IndexOf('\n');
        return (idx >= 0 ? value[..idx] : value).Trim();
    }
}

public sealed record GlobalChartSearchServiceResult(
    GlobalChartSource Source,
    string SourceName,
    IReadOnlyList<GlobalChartSearchResult> Results,
    GlobalChartSourceStatus Status,
    string Message);

public sealed record MdenGlobalSearchRequest(
    string Query,
    string? ChartKey,
    int Difficulty,
    string? Artist,
    string? Charter);

public sealed record EuterpeBuildZipResponse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("filename")] string Filename);
