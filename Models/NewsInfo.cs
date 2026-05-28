using System.Text.Json.Serialization;

namespace MdModManager.Models;

// 欢迎页新闻数据
public class NewsInfo
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("title_en")]
    public string TitleEn { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("content_en")]
    public string ContentEn { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonIgnore]
    public string DisplayTitle => MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" && !string.IsNullOrEmpty(TitleEn) ? TitleEn : Title;

    [JsonIgnore]
    public string DisplayContent => MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" && !string.IsNullOrEmpty(ContentEn) ? ContentEn : Content;
}
