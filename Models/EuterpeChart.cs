using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MdModManager.Models;

// 难度槽位信息
public sealed record MapSlotInfo(
    [property: JsonPropertyName("slot")] string Slot,
    [property: JsonPropertyName("rating")] string Rating,
    [property: JsonPropertyName("charters")] string[] Charters,
    [property: JsonPropertyName("predicted_rating")] double? PredictedRating);

// Euterpe 谱面实体模型
public partial class EuterpeChart : ObservableObject
{
    [JsonPropertyName("cid")]
    public long Cid { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("bpm")]
    public int Bpm { get; set; }

    [JsonPropertyName("bpm_min")]
    public int? BpmMin { get; set; }

    [JsonPropertyName("bpm_max")]
    public int? BpmMax { get; set; }

    [JsonPropertyName("maps")]
    public List<MapSlotInfo> Maps { get; set; } = new();

    [JsonPropertyName("map_count")]
    public int MapCount { get; set; }

    [JsonPropertyName("cover_thumbnail_url")]
    public string? CoverThumbnailUrl { get; set; }

    [JsonPropertyName("cover_dominant_color")]
    public string? CoverDominantColor { get; set; }

    [JsonPropertyName("owner_uid")]
    public long OwnerUid { get; set; }

    [JsonPropertyName("owner_nickname")]
    public string? OwnerNickname { get; set; }

    [JsonPropertyName("like_count")]
    public int LikeCount { get; set; }

    [JsonPropertyName("download_count")]
    public int DownloadCount { get; set; }

    [JsonPropertyName("has_video")]
    public bool HasVideo { get; set; }

    [JsonPropertyName("has_talk")]
    public bool HasTalk { get; set; }

    // 封面大图缓存
    [ObservableProperty]
    [property: JsonIgnore]
    private Avalonia.Media.Imaging.Bitmap? _coverImage;

    // 是否正在播放试听
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isPlaying;

    // 是否已被点赞
    [ObservableProperty]
    [property: JsonPropertyName("is_liked")]
    private bool _isLiked;

    // 衍生封面直连地址
    [JsonIgnore]
    public string CoverUrl => string.IsNullOrWhiteSpace(CoverThumbnailUrl) 
        ? string.Empty 
        : (CoverThumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) 
            ? CoverThumbnailUrl 
            : $"https://euterpe-org.com/{(CoverThumbnailUrl.StartsWith('/') ? CoverThumbnailUrl.Substring(1) : CoverThumbnailUrl)}");

    // 衍生谱面副标题信息
    [JsonIgnore]
    public string SubInfo => $"曲：{Author} | BPM：{Bpm}";

    // 衍生谱师去重列表
    [JsonIgnore]
    public string CharterInfo
    {
        get
        {
            if (Maps == null || Maps.Count == 0) return string.Empty;
            var charters = new HashSet<string>();
            foreach (var m in Maps)
            {
                if (m.Charters != null)
                {
                    foreach (var c in m.Charters)
                    {
                        if (!string.IsNullOrWhiteSpace(c))
                        {
                            charters.Add(c.Trim());
                        }
                    }
                }
            }
            return string.Join(", ", charters);
        }
    }

    // 衍生曲师文本
    [JsonIgnore]
    public string DisplayAuthor => $"曲：{Author}";

    // 衍生谱师文本
    [JsonIgnore]
    public string DisplayCharter => string.IsNullOrWhiteSpace(CharterInfo) ? string.Empty : $"谱：{CharterInfo}";

    // 衍生难度星级列表
    [JsonIgnore]
    public List<string> DifficultyLabels
    {
        get
        {
            var labels = new List<string>();
            if (Maps != null)
            {
                foreach (var m in Maps)
                {
                    if (!string.IsNullOrEmpty(m.Rating))
                    {
                        labels.Add(m.Rating);
                    }
                }
            }
            return labels;
        }
    }
}

// 搜索响应外壳
public sealed record EuterpeSearchResponse(
    [property: JsonPropertyName("items")] List<EuterpeChart> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("total")] int Total);
