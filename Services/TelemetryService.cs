using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MdModManager.Models;

namespace MdModManager.Services;

// 遥测会话载荷
public record TelemetrySessionPayload(
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("arch")] string Arch,
    [property: JsonPropertyName("app_version")] string AppVersion);

// 账号绑定载荷
public record VanillaBindPayload(
    [property: JsonPropertyName("uid")] string VanillaUid);

// 遥测接口 JSON 序列化上下文
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TelemetrySessionPayload))]
[JsonSerializable(typeof(VanillaBindPayload))]
internal partial class TelemetryJsonContext : JsonSerializerContext;

public sealed class TelemetryService : ITelemetryService
{
    private const string BaseUrl = "https://euterpe-org.com/api/";
    private readonly HttpClient _httpClient;
    private readonly IAuthService _authService;
    private readonly AuthState _authState;

    public TelemetryService(IAuthService authService, AuthState authState)
    {
        _authService = authService;
        _authState = authState;
        _httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MuseDashTOOL/1.4.8");
    }

    // 发送应用会话遥测请求
    public async Task TrackSessionAsync()
    {
        try
        {
            var country = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => "unknown"
            };
            var version = typeof(TelemetryService).Assembly.GetName().Version?.ToString(3) ?? "1.4.8";

            var payload = new TelemetrySessionPayload(
                country,
                "win",
                arch,
                $"MDT/{version}");

            var json = JsonSerializer.Serialize(payload, TelemetryJsonContext.Default.TelemetrySessionPayload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, "telemetry/app/session");
            request.Headers.Add("X-Request-Id", Guid.CreateVersion7().ToString());
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                RuntimeLog.Write("TelemetryService", "发送会话遥测成功");
            }
            else
            {
                RuntimeLog.Write("TelemetryService", "发送会话遥测失败 状态码 " + (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("TelemetryService", "发送会话遥测发生错误 " + ex.Message);
        }
    }

    // 绑定游戏账号
    public async Task BindVanillaAccountAsync()
    {
        // 确保用户已登录
        if (_authState.CurrentUser == null)
        {
            RuntimeLog.Write("TelemetryService", "用户未登录 跳过账号绑定");
            return;
        }

        // 获取游戏账号 UID
        var uid = GetMuseDashUid();
        if (string.IsNullOrEmpty(uid))
        {
            RuntimeLog.Write("TelemetryService", "未找到游戏账号 UID 跳过绑定");
            return;
        }

        try
        {
            // 获取最新 AccessToken
            var token = await _authService.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                RuntimeLog.Write("TelemetryService", "获取访问令牌失败 跳过绑定");
                return;
            }

            var payload = new VanillaBindPayload(uid);
            var json = JsonSerializer.Serialize(payload, TelemetryJsonContext.Default.VanillaBindPayload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Put, "me/vanilla-binding");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Request-Id", Guid.CreateVersion7().ToString());
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                RuntimeLog.Write("TelemetryService", "绑定游戏账号成功 UID " + uid);
            }
            else
            {
                RuntimeLog.Write("TelemetryService", "绑定游戏账号失败 状态码 " + (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("TelemetryService", "绑定游戏账号发生错误 " + ex.Message);
        }
    }

    // 获取游戏 UID
    private string? GetMuseDashUid()
    {
        return MuseDashAccountService.ReadAccountInfo()?.Uid;
    }
}
