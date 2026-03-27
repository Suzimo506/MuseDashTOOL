using System.Collections.Generic;
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

    // Custom URLs for Album Collection support
    [property: JsonIgnore]
    public string? CustomCoverUrl { get; set; }
    
    [property: JsonIgnore]
    public string? CustomDemoUrl { get; set; }
    
    [property: JsonIgnore]
    public string? CustomDemoMp3Url { get; set; }
    
    [property: JsonIgnore]
    public string? CustomDownloadUrl { get; set; }

    // Derived URLs
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
