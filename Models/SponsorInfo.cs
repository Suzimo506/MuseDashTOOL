using System.Text.Json.Serialization;

namespace MdModManager.Models;

public class SponsorInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("amount")]
    public string Amount { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";
}
