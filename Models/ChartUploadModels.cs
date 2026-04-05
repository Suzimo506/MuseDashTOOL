using System.Text.Json.Serialization;

namespace MdModManager.Models;

public sealed class ChartUploadConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("api_url")]
    public string ApiUrl { get; set; } = string.Empty;

    [JsonPropertyName("fallback_api_urls")]
    public List<string> FallbackApiUrls { get; set; } = new();

    [JsonPropertyName("notice")]
    public string Notice { get; set; } = string.Empty;

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    [JsonPropertyName("default_release_tag")]
    public string DefaultReleaseTag { get; set; } = "NO.1";

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 120;

    public static ChartUploadConfig CreateDefault() => new()
    {
        Enabled = false,
        ApiUrl = string.Empty,
        Notice = "上传功能已接入独立后端配置，等待填写可用的上传接口地址。",
        Categories = GetDefaultCategories(),
        DefaultReleaseTag = "NO.1",
        TimeoutSeconds = 120
    };

    public static List<string> GetDefaultCategories() =>
        new() { "通过审议", "令人生草", "待定或存在小问题" };

    public List<string> GetApiCandidates()
    {
        var result = new List<string>();

        if (!string.IsNullOrWhiteSpace(ApiUrl))
            result.Add(ApiUrl.Trim());

        foreach (var url in FallbackApiUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var normalized = url.Trim();
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                result.Add(normalized);
        }

        return result;
    }
}

public sealed class PreparedChartUpload
{
    public required string SourceFilePath { get; init; }
    public required string SourceFileName { get; init; }
    public required string Id { get; init; }
    public required string OriginalId { get; init; }
    public required string Artist { get; init; }
    public required string Charter { get; init; }
    public required string Bpm { get; init; }
    public required List<string> Difficulties { get; init; }
    public required string MdmFileName { get; init; }
    public required byte[] MdmContent { get; init; }
    public required string CoverFileName { get; init; }
    public required byte[] CoverContent { get; init; }
    public required string DemoFileName { get; init; }
    public required byte[] DemoContent { get; init; }

    public string DifficultyText => Difficulties.Count > 0
        ? string.Join(" / ", Difficulties)
        : "未识别";
}

public sealed class ChartUploadApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("upload_id")]
    public string? UploadId { get; set; }
}

public sealed class ChartUploadOperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? UploadId { get; init; }
}
