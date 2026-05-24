using System.Text.Json.Serialization;

namespace MdModManager.Models;

// 欢迎页新闻数据
public class NewsInfo
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}
