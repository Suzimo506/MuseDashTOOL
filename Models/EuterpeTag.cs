using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MdModManager.Models;

// Euterpe 标签实体模型
public sealed class EuterpeTag : ObservableObject
{
    [JsonPropertyName("tag_id")]
    public string TagId { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("sort_order")]
    public int SortOrder { get; init; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }

    [JsonPropertyName("popularity")]
    public int Popularity { get; init; }

    [JsonPropertyName("translations")]
    public Dictionary<string, string> Translations { get; init; } = new();

    private bool _isSelected;
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    [JsonConstructor]
    public EuterpeTag(string tagId, string category, int sortOrder, bool isActive, int popularity, Dictionary<string, string> translations)
    {
        TagId = tagId;
        Category = category;
        SortOrder = sortOrder;
        IsActive = isActive;
        Popularity = popularity;
        Translations = translations;
    }

    // 翻译名称，默认获取中文翻译，若无则回退为英文或 tag_id 本身
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (Translations == null) return TagId;
            foreach (var kvp in Translations)
            {
                if (kvp.Key.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            if (Translations.TryGetValue("en", out var en)) return en;
            return TagId;
        }
    }
}
