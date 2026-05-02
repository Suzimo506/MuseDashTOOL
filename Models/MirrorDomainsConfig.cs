using System.Text.Json.Serialization;

namespace MdModManager.Models;

public class MirrorDomainsConfig
{
    [JsonPropertyName("suzimo")]
    public string Suzimo { get; set; } = "";

    [JsonPropertyName("album_download_domain")]
    public string AlbumDownloadDomain { get; set; } = "";

    [JsonPropertyName("album_info_domain")]
    public string AlbumInfoDomain { get; set; } = "";
}
