using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IChartUploadService
{
    Task<ChartUploadOperationResult> UploadAsync(
        PreparedChartUpload package,
        MuseDashAccountInfo accountInfo,
        string burl,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ChartUploadService : IChartUploadService
{
    private readonly IChartUploadConfigService _configService;

    public ChartUploadService(IChartUploadConfigService configService)
    {
        _configService = configService;
    }

    public async Task<ChartUploadOperationResult> UploadAsync(
        PreparedChartUpload package,
        MuseDashAccountInfo accountInfo,
        string burl,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountInfo.Uid))
        {
            return new ChartUploadOperationResult
            {
                Success = false,
                Message = "未读取到 Muse Dash 账号 UID。"
            };
        }

        var config = await _configService.GetConfigAsync(cancellationToken).ConfigureAwait(false);
        if (!config.Enabled)
        {
            return new ChartUploadOperationResult
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(config.Notice)
                    ? "上传服务当前未启用。"
                    : config.Notice
            };
        }

        var apiCandidates = config.GetApiCandidates();
        if (apiCandidates.Count == 0)
        {
            return new ChartUploadOperationResult
            {
                Success = false,
                Message = "未配置可用的上传接口地址。"
            };
        }

        var timeoutSeconds = Math.Max(15, config.TimeoutSeconds);
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MuseDashTOOL-ChartUpload");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        ChartUploadOperationResult? lastFailure = null;
        foreach (var apiUrl in apiCandidates)
        {
            try
            {
                using var rawContent = BuildUploadContent(package, accountInfo, burl);
                using var content = new MdModManager.Helpers.ProgressableStreamContent(rawContent, (uploaded, total) => 
                {
                    if (total > 0 && progress != null)
                    {
                        var percentage = (double)uploaded / total * 100;
                        progress.Report(percentage);
                    }
                });

                using var response = await httpClient.PostAsync(apiUrl, content, cancellationToken).ConfigureAwait(false);
                var result = await ParseResponseAsync(response, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    RuntimeLog.Write("ChartUploadService", $"谱面上传成功：{package.SourceFileName} -> {apiUrl}");
                    return result;
                }

                lastFailure = result;
                RuntimeLog.Write("ChartUploadService", $"谱面上传失败：{package.SourceFileName} -> {apiUrl}，{result.Message}");
            }
            catch (Exception ex)
            {
                lastFailure = new ChartUploadOperationResult
                {
                    Success = false,
                    Message = ex.Message
                };
                RuntimeLog.Write("ChartUploadService", $"请求上传接口失败：{apiUrl}，{ex.Message}");
            }
        }

        return lastFailure ?? new ChartUploadOperationResult
        {
            Success = false,
            Message = "上传失败，未拿到服务器响应。"
        };
    }

    private static MultipartFormDataContent BuildUploadContent(
        PreparedChartUpload package,
        MuseDashAccountInfo accountInfo,
        string burl)
    {
        var content = new MultipartFormDataContent();

        void AddText(string name, string value)
        {
            var textContent = new StringContent(value ?? string.Empty, Encoding.UTF8);
            textContent.Headers.Remove("Content-Disposition");
            textContent.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"{name}\"");
            content.Add(textContent);
        }

        void AddJson(string name, object value)
        {
            var jsonContent = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
            jsonContent.Headers.Remove("Content-Disposition");
            jsonContent.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"{name}\"");
            content.Add(jsonContent);
        }

        void AddFile(string name, string fileName, byte[] bytes)
        {
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(fileName));
            fileContent.Headers.Remove("Content-Disposition");
            fileContent.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"{name}\"; filename=\"{Uri.EscapeDataString(fileName)}\"");
            content.Add(fileContent);
        }

        AddText("uid", accountInfo.Uid ?? string.Empty);
        AddText("nickname", (accountInfo.Nickname ?? accountInfo.Username ?? string.Empty).Trim());
        AddText("b_url", burl?.Trim() ?? string.Empty);
        AddText("chart_id", package.Id);
        AddText("original_id", package.OriginalId);
        AddText("artist", package.Artist);
        AddText("charter", package.Charter);
        AddText("bpm", package.Bpm);
        AddJson("difficulties_json", package.Difficulties);
        AddText("source_file_name", package.SourceFileName);
        AddText("client_version", GetClientVersion());

        AddFile("mdm", package.MdmFileName, package.MdmContent);
        AddFile("cover", package.CoverFileName, package.CoverContent);
        AddFile("demo", package.DemoFileName, package.DemoContent);

        return content;
    }

    private static async Task<ChartUploadOperationResult> ParseResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        ChartUploadApiResponse? parsed = null;
        try
        {
            parsed = JsonSerializer.Deserialize<ChartUploadApiResponse>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            // Ignore invalid json and fallback to plain text.
        }

        if (response.IsSuccessStatusCode)
        {
            if (parsed != null)
            {
                var success = parsed.Success;
                if (!success &&
                    string.IsNullOrWhiteSpace(parsed.Message) &&
                    string.IsNullOrWhiteSpace(parsed.UploadId))
                {
                    success = true;
                }

                return new ChartUploadOperationResult
                {
                    Success = success,
                    Message = string.IsNullOrWhiteSpace(parsed.Message) ? "上传成功。" : parsed.Message!,
                    UploadId = parsed.UploadId
                };
            }

            return new ChartUploadOperationResult
            {
                Success = true,
                Message = string.IsNullOrWhiteSpace(text) ? "上传成功。" : text
            };
        }

        return new ChartUploadOperationResult
        {
            Success = false,
            Message = !string.IsNullOrWhiteSpace(parsed?.Message)
                ? parsed!.Message!
                : !string.IsNullOrWhiteSpace(text)
                    ? text
                    : $"服务器返回错误：{(int)response.StatusCode} {response.ReasonPhrase}"
        };
    }

    private static string GetClientVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "unknown";
    }

    private static string GetContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".ogg" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".mdm" or ".zip" => "application/octet-stream",
            _ => "application/octet-stream"
        };
    }
}
