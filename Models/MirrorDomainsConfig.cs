using System.Text.Json.Serialization;

namespace MdModManager.Models;

public class MirrorDomainsConfig
{
    [JsonPropertyName("suzimo")]
    public string Suzimo { get; set; } = "";
}
