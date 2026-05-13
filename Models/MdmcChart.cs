using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MdModManager.Models;

public class MdmcSheet
{
    [JsonPropertyName("difficulty")]
    public string Difficulty { get; set; } = string.Empty;

    [JsonPropertyName("rankedDifficulty")]
    public int RankedDifficulty { get; set; }

    [JsonPropertyName("charter")]
    public string Charter { get; set; } = string.Empty;
}

public partial class MdmcChart : ObservableObject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("titleRomanized")]
    public string? TitleRomanized { get; set; }

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    [JsonPropertyName("charter")]
    public string Charter { get; set; } = string.Empty;

    [JsonPropertyName("bpm")]
    public string Bpm { get; set; } = string.Empty;

    /// <summary>封面图，延迟加载</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private Avalonia.Media.Imaging.Bitmap? _coverImage;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowAnimatedCover))]
    [NotifyPropertyChangedFor(nameof(ShouldShowStaticCover))]
    [property: JsonIgnore]
    private bool _isAnimatedCoverPlaybackEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayCoverSource))]
    [NotifyPropertyChangedFor(nameof(HasDisplayCoverSource))]
    [NotifyPropertyChangedFor(nameof(HasAnimatedDisplayCoverSource))]
    [NotifyPropertyChangedFor(nameof(HasStaticDisplayCoverSource))]
    [NotifyPropertyChangedFor(nameof(AnimatedDisplayCoverSource))]
    [NotifyPropertyChangedFor(nameof(StaticDisplayCoverSource))]
    [NotifyPropertyChangedFor(nameof(ShouldShowAnimatedCover))]
    [NotifyPropertyChangedFor(nameof(ShouldShowStaticCover))]
    [property: JsonIgnore]
    private string? _resolvedCoverSource;

    /// <summary>搜索关键词，用于高亮显示</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _searchText = string.Empty;

    [JsonPropertyName("ranked")]
    public bool Ranked { get; set; }

    [JsonPropertyName("sheets")]
    public List<MdmcSheet> Sheets { get; set; } = new();

    [JsonPropertyName("likesCount")]
    public int LikesCount { get; set; }

    [JsonPropertyName("uploadedAt")]
    public DateTime? UploadedAt { get; set; }

    // 整合包自定义 URL 支持
    [property: JsonIgnore]
    public string? CustomCoverUrl { get; set; }
    
    [property: JsonIgnore]
    public string? CustomDemoUrl { get; set; }
    
    [property: JsonIgnore]
    public string? CustomDemoMp3Url { get; set; }
    
    [property: JsonIgnore]
    public string? CustomDownloadUrl { get; set; }

    [property: JsonIgnore]
    public string? SourceCategoryName { get; set; }

    [property: JsonIgnore]
    public bool IsCommunitySource { get; set; }

    // 派生 URL
    [JsonIgnore]
    public string? DisplayCoverSource => !string.IsNullOrWhiteSpace(ResolvedCoverSource)
        ? ResolvedCoverSource
        : (!string.IsNullOrWhiteSpace(CustomCoverUrl) ? CustomCoverUrl : null);

    [JsonIgnore]
    public bool HasDisplayCoverSource => !string.IsNullOrWhiteSpace(DisplayCoverSource);

    [JsonIgnore]
    public bool HasAnimatedDisplayCoverSource => HasGifLikeSource(DisplayCoverSource);

    [JsonIgnore]
    public bool HasStaticDisplayCoverSource => HasDisplayCoverSource && !HasAnimatedDisplayCoverSource;

    [JsonIgnore]
    public string? AnimatedDisplayCoverSource
    {
        get
        {
            if (!HasAnimatedDisplayCoverSource) return null;
            var source = DisplayCoverSource;
            if (string.IsNullOrWhiteSpace(source)) return null;
            // 只返回本地文件路径，防止 AnimatedImage 库在 UI 线程同步下载远程 GIF
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
                return uri.IsFile ? source : null;
            return source;
        }
    }

    [JsonIgnore]
    public string? StaticDisplayCoverSource => HasStaticDisplayCoverSource ? DisplayCoverSource : null;

    [JsonIgnore]
    public bool ShouldShowAnimatedCover => HasAnimatedDisplayCoverSource && IsAnimatedCoverPlaybackEnabled;

    [JsonIgnore]
    public bool ShouldShowStaticCover => HasStaticDisplayCoverSource || (HasAnimatedDisplayCoverSource && !IsAnimatedCoverPlaybackEnabled);

    public string CoverUrl => !string.IsNullOrWhiteSpace(CustomCoverUrl) ? CustomCoverUrl : $"https://cdn.mdmc.moe/charts/{Id}/cover.png";
    public string DemoUrl  => !string.IsNullOrWhiteSpace(CustomDemoUrl) ? CustomDemoUrl : $"https://cdn.mdmc.moe/charts/{Id}/demo.ogg";
    public string DemoMp3Url => !string.IsNullOrWhiteSpace(CustomDemoMp3Url) ? CustomDemoMp3Url : $"https://cdn.mdmc.moe/charts/{Id}/demo.mp3";
    public string DownloadUrl => !string.IsNullOrWhiteSpace(CustomDownloadUrl) ? CustomDownloadUrl : $"https://api.mdmc.moe/v3/charts/{Id}/download";

    /// <summary>副标题：曲 + 谱 (同向排列)</summary>
    public string SubInfo
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Artist))  parts.Add($"曲：{Artist}");
            if (!string.IsNullOrEmpty(Charter)) parts.Add($"谱：{Charter}");
            return string.Join("  ", parts);
        }
    }

    /// <summary>难度标签列表</summary>
    public List<string> DifficultyLabels
    {
        get
        {
            var labels = new List<string>();
            foreach (var s in Sheets)
                if (!string.IsNullOrEmpty(s.Difficulty)) labels.Add(s.Difficulty);
            return labels;
        }
    }

    public void ReleaseResources()
    {
        CoverImage?.Dispose();
        CoverImage = null;
        ResolvedCoverSource = null;
        IsAnimatedCoverPlaybackEnabled = false;
    }

    private static bool HasGifLikeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
            return string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(Path.GetExtension(source), ".gif", StringComparison.OrdinalIgnoreCase);
    }
}

public class MdmcChartListResponse
{
    [JsonPropertyName("charts")]
    public List<MdmcChart> Charts { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}
