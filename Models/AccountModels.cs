using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdModManager.Models;

public class MuseDashAccountInfo
{
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    /// <summary>Real in-game nickname fetched from musedash.moe (null if offline).</summary>
    public string? OnlineNickname { get; set; }
}

public class PlayerProfileData
{
    public string? Nickname { get; set; }
    public decimal RelativeLevel { get; set; }
    public int RecordsCount { get; set; }
    public int PerfectsCount { get; set; }
    public decimal AverageAccuracy { get; set; }
    public List<PlayerSongRecord> RecentPlays { get; set; } = new();
}

public class PlayerSongRecord : INotifyPropertyChanged
{
    public int DisplayIndex { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public bool HasCoverUrl => !string.IsNullOrEmpty(CoverUrl);
    public string Level { get; set; } = "";
    public string Accuracy { get; set; } = "";
    public string Score { get; set; } = "";
    public string Gear { get; set; } = "";
    public string Rank { get; set; } = "";

    // 用于排序的原始数值
    public int RawRank { get; set; } = int.MaxValue;
    public decimal RawAccuracy { get; set; }
    public int RawDifficulty { get; set; }

    // 搜索匹配高亮
    private bool _isSearchMatch;
    public bool IsSearchMatch
    {
        get => _isSearchMatch;
        set
        {
            if (_isSearchMatch == value) return;
            _isSearchMatch = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSearchMatch)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal class MdMoePlayerResponse
{
    [JsonPropertyName("user")]
    public MdMoeUser? User { get; set; }

    [JsonPropertyName("plays")]
    public List<MdMoePlay>? Plays { get; set; }

    [JsonPropertyName("rl")]
    public JsonElement? RelativeLevel { get; set; }
}

internal class MdMoeUser
{
    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }
}

internal class MdMoePlay
{
    [JsonPropertyName("name")]
    public string? SongName { get; set; }

    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("score")]
    public int? Score { get; set; }

    [JsonPropertyName("acc")]
    public decimal? Accuracy { get; set; }

    [JsonPropertyName("character_uid")]
    public string? CharacterUid { get; set; }

    [JsonPropertyName("elfin_uid")]
    public string? ElfinUid { get; set; }
    
    [JsonPropertyName("level")]
    public string? Level { get; set; }
    
    [JsonPropertyName("difficulty")]
    public int? Difficulty { get; set; }
    
    [JsonPropertyName("i")]
    public int? Rank { get; set; }
}

internal class MdMoeAlbum
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("music")]
    public Dictionary<string, MdMoeMusic>? Music { get; set; }
}

internal class MdMoeMusic
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    /// <summary>Difficulty levels for [Easy, Hard, Master, Hidden, Extra]</summary>
    [JsonPropertyName("difficulty")]
    public string[]? Difficulty { get; set; }
}
