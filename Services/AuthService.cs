using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Models;

namespace MdModManager.Services;

// 登录请求参数
public record AppTokenRequest(string Code);

// 登录响应数据
public record AppTokenResponse(string AccessToken, string RefreshToken, EuterpeUserInfo Me);

// 刷新请求参数
public record RefreshRequest(string RefreshToken);

// 刷新响应数据
public record RefreshResponse(string AccessToken, string RefreshToken);

// 登出请求参数
public record LogoutRequest(string RefreshToken);

// 用户状态响应
public record CurrentUserResponse(EuterpeUserInfo User);

// 绑定请求数据
public record MuseDashUidRequest(string Uid);

// 序列化本地文件载荷
public record TokenPayload(string AccessToken, string RefreshToken);

// 序列化上下文
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AppTokenRequest))]
[JsonSerializable(typeof(AppTokenResponse))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(RefreshResponse))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(CurrentUserResponse))]
[JsonSerializable(typeof(TokenPayload))]
[JsonSerializable(typeof(EuterpeUserInfo))]
[JsonSerializable(typeof(MuseDashUidRequest))]
internal partial class EuterpeJsonContext : JsonSerializerContext;


public sealed class AuthService : IAuthService
{
    private const string BaseUrl = "https://euterpe-org.com/api/";
    private const string AuthorizePageUrl = "https://euterpe-org.com/auth/app?redirect_uri=euterpe://auth/callback&app_name=MuseDashTool";
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(14);
    
    private static readonly string TokenFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MdModManager",
        "auth.dat");

    private readonly HttpClient _httpClient;
    private readonly AuthState _authState;
    private readonly Helpers.AsyncExclusiveLock _lock = new();

    public AsyncManualResetEvent Ready { get; } = new(false);

    private sealed class XRequestIdHandler : DelegatingHandler
    {
        public XRequestIdHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Request-Id", Guid.CreateVersion7().ToString());
            return base.SendAsync(request, cancellationToken);
        }
    }

    public AuthService(AuthState authState)
    {
        _authState = authState;
        var handler = new XRequestIdHandler(new HttpClientHandler());
        _httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MuseDashTOOL/1.4.2");
    }

    public Task LoginAsync()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AuthorizePageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        return Task.CompletedTask;
    }

    public async Task LogoutAsync()
    {
        await _lock.StealAsync("logout");
        try
        {
            if (_authState.RefreshToken != null)
            {
                try
                {
                    var request = new LogoutRequest(_authState.RefreshToken);
                    var json = JsonSerializer.Serialize(request, EuterpeJsonContext.Default.LogoutRequest);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await _httpClient.PostAsync("auth/logout", content);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
            await ClearSessionAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CompleteLoginAsync(string code)
    {
        await _lock.AcquireAsync();
        try
        {
            var request = new AppTokenRequest(code);
            var json = JsonSerializer.Serialize(request, EuterpeJsonContext.Default.AppTokenRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("auth/app/token", content);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"请求失败 状态码 {(int)response.StatusCode} 内容 {errorBody}");
            }
            
            var respJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(respJson, EuterpeJsonContext.Default.AppTokenResponse);
            if (result != null)
            {
                await UpdateSessionAsync(result.AccessToken, result.RefreshToken, result.Me);
                Ready.Set();

                // 自动绑定原版游戏UID
                try
                {
                    var museInfo = MuseDashAccountService.ReadAccountInfo();
                    if (museInfo != null && !string.IsNullOrEmpty(museInfo.Uid))
                    {
                        var bindRequest = new MuseDashUidRequest(museInfo.Uid);
                        var bindJson = JsonSerializer.Serialize(bindRequest, EuterpeJsonContext.Default.MuseDashUidRequest);
                        using var bindContent = new StringContent(bindJson, Encoding.UTF8, "application/json");
                        using var req = new HttpRequestMessage(HttpMethod.Put, "me/vanilla-binding")
                        {
                            Content = bindContent
                        };
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
                        var bindResponse = await _httpClient.SendAsync(req);
                        if (!bindResponse.IsSuccessStatusCode)
                        {
                            var bindErr = await bindResponse.Content.ReadAsStringAsync();
                            Console.WriteLine($"[PlayerBind] 自动绑定失败: {bindErr}");
                        }
                    }
                }
                catch (Exception bindEx)
                {
                    Console.WriteLine($"[PlayerBind] 自动绑定异常: {bindEx.Message}");
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> GetAccessTokenAsync()
    {
        await Ready.WaitAsync();
        await _lock.AcquireAsync();
        try
        {
            if (DateTimeOffset.Now < _authState.AccessTokenExpiry)
            {
                return _authState.AccessToken ?? string.Empty;
            }
            return await RefreshInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> RenewAccessTokenAsync()
    {
        await _lock.AcquireAsync();
        try
        {
            return await RefreshInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RestoreSessionAsync()
    {
        var tokens = await LoadTokensAsync();
        if (tokens == null)
        {
            return false;
        }

        _authState.AccessToken = tokens.AccessToken;
        _authState.RefreshToken = tokens.RefreshToken;
        Ready.Set();

        try
        {
            var token = await GetAccessTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Get, "me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                token = await RenewAccessTokenAsync();
                using var retryRequest = new HttpRequestMessage(HttpMethod.Get, "me");
                retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                response = await _httpClient.SendAsync(retryRequest);
            }

            response.EnsureSuccessStatusCode();

            var respJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(respJson, EuterpeJsonContext.Default.CurrentUserResponse);
            if (result != null)
            {
                _authState.CurrentUser = result.User;
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            await ClearSessionAsync();
            return false;
        }

        await ClearSessionAsync();
        return false;
    }

    private async Task<string> RefreshInternalAsync()
    {
        if (_authState.RefreshToken == null)
        {
            throw new InvalidOperationException("Refresh token is missing");
        }

        var request = new RefreshRequest(_authState.RefreshToken);
        var json = JsonSerializer.Serialize(request, EuterpeJsonContext.Default.RefreshRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("auth/refresh", content);
        response.EnsureSuccessStatusCode();

        var respJson = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(respJson, EuterpeJsonContext.Default.RefreshResponse);
        if (result == null)
        {
            throw new InvalidOperationException("Failed to refresh token");
        }

        await UpdateSessionAsync(result.AccessToken, result.RefreshToken, _authState.CurrentUser);
        return result.AccessToken;
    }

    private async Task UpdateSessionAsync(string accessToken, string refreshToken, EuterpeUserInfo? currentUser)
    {
        _authState.AccessToken = accessToken;
        _authState.RefreshToken = refreshToken;
        _authState.AccessTokenExpiry = DateTimeOffset.Now.Add(AccessTokenLifetime);
        _authState.CurrentUser = currentUser;

        await SaveTokensAsync(accessToken, refreshToken);
    }

    private async Task ClearSessionAsync()
    {
        _authState.Clear();
        await ClearTokensAsync();
        Ready.Reset();
    }

    private async Task SaveTokensAsync(string accessToken, string refreshToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(TokenFilePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = new TokenPayload(accessToken, refreshToken);
            var json = JsonSerializer.Serialize(payload, EuterpeJsonContext.Default.TokenPayload);
            var plainBytes = Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

            await File.WriteAllBytesAsync(TokenFilePath, encrypted);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }

    private async Task<TokenPayload?> LoadTokensAsync()
    {
        if (!File.Exists(TokenFilePath))
        {
            return null;
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(TokenFilePath);
            var plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            var payload = JsonSerializer.Deserialize(json, EuterpeJsonContext.Default.TokenPayload);

            if (payload == null || string.IsNullOrEmpty(payload.AccessToken) || string.IsNullOrEmpty(payload.RefreshToken))
            {
                await ClearTokensAsync();
                return null;
            }

            return payload;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            await ClearTokensAsync();
            return null;
        }
    }

    private Task ClearTokensAsync()
    {
        if (File.Exists(TokenFilePath))
        {
            try
            {
                File.Delete(TokenFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
        return Task.CompletedTask;
    }
}
