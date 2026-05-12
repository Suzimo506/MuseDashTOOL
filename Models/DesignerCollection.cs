using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MdModManager.Models;

public partial class DesignerCategory : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    private string _description = string.Empty;
    [JsonPropertyName("description")]
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    [JsonPropertyName("charts")]
    public List<DesignerChart> Charts { get; set; } = new();
}

public class DesignerChart
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("bpm")]
    public string Bpm { get; set; } = string.Empty;

    [JsonPropertyName("coverUrl")]
    public string CoverUrl { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("demoUrl")]
    public string DemoUrl { get; set; } = string.Empty;

    [JsonPropertyName("demoMp3Url")]
    public string DemoMp3Url { get; set; } = string.Empty;

    [JsonPropertyName("difficulties")]
    public List<string>? Difficulties { get; set; }

    [JsonIgnore]
    public bool IsFolder { get; set; }

    [JsonIgnore]
    public string FolderPath { get; set; } = string.Empty;

    [JsonIgnore]
    public int ChildFolderCount { get; set; }
}
