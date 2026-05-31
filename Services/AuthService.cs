using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using MdModManager.Models;

namespace MdModManager.Services;

// 登录请求参数（RFC 8252 loopback + PKCE）
public record AppTokenRequest(string ClientId, string Code, string CodeVerifier, string RedirectUri);

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
    private const string AuthorizePageUrl = "https://euterpe-org.com/auth/app";
    private const string SuccessLandingUrl = "https://euterpe-org.com/auth/app/done";
    private const string ClientId = "musedash-tool";
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(14);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);
    
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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MuseDashTOOL/1.4.6");
    }

    public async Task LoginAsync()
    {
        // RFC 8252 native-app flow：本机起一个 loopback 监听，浏览器授权后
        // 浏览器把授权码回跳到 127.0.0.1，全程不依赖自定义 URL scheme。
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var state = GenerateState();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/callback";

            var authorizeUrl = BuildAuthorizeUrl(redirectUri, codeChallenge, state);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

            using var cts = new CancellationTokenSource(LoginTimeout);
            var code = await ReceiveAuthorizationCodeAsync(listener, state, cts.Token);

            await CompleteLoginAsync(code, codeVerifier, redirectUri);
        }
        finally
        {
            listener.Stop();
        }
    }

    // 拼装授权页 URL：展示名由服务端按 client_id 解析，客户端不自报。
    private static string BuildAuthorizeUrl(string redirectUri, string codeChallenge, string state)
    {
        var query = new StringBuilder();
        query.Append("client_id=").Append(Uri.EscapeDataString(ClientId));
        query.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        query.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
        query.Append("&code_challenge_method=S256");
        query.Append("&state=").Append(Uri.EscapeDataString(state));
        return $"{AuthorizePageUrl}?{query}";
    }

    // 接收单个 loopback 回调：成功 302 到落地页，失败回 400 本地处理。
    private static async Task<string> ReceiveAuthorizationCodeAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();

        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer, ct);
        var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n", 2)[0];

        var queryParams = HttpUtility.ParseQueryString(ExtractQuery(requestLine));
        var code = queryParams["code"];
        var returnedState = queryParams["state"];
        var error = queryParams["error"];

        var success = string.IsNullOrEmpty(error)
            && !string.IsNullOrEmpty(code)
            && FixedTimeEquals(returnedState, expectedState);

        var response = success
            ? $"HTTP/1.1 302 Found\r\nLocation: {SuccessLandingUrl}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            : "HTTP/1.1 400 Bad Request\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\n\r\n登录失败，请返回应用重试。";
        var responseBytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(responseBytes, ct);
        await stream.FlushAsync(ct);

        if (!string.IsNullOrEmpty(error))
        {
            var description = queryParams["error_description"];
            throw new InvalidOperationException(string.IsNullOrEmpty(description) ? error : $"{error}: {description}");
        }
        if (string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException("回调缺少授权码");
        }
        if (!FixedTimeEquals(returnedState, expectedState))
        {
            throw new InvalidOperationException("state 校验失败，疑似伪造回调");
        }

        return code;
    }

    // 从请求行 "GET /callback?<query> HTTP/1.1" 取出 query 串。
    private static string ExtractQuery(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            return string.Empty;
        }
        var path = parts[1];
        var index = path.IndexOf('?');
        return index >= 0 ? path[(index + 1)..] : string.Empty;
    }

    private static string GenerateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string GenerateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    private static string ComputeCodeChallenge(string codeVerifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string? a, string? b)
    {
        if (a == null || b == null)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
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

    private async Task CompleteLoginAsync(string code, string codeVerifier, string redirectUri)
    {
        await _lock.AcquireAsync();
        try
        {
            var request = new AppTokenRequest(ClientId, code, codeVerifier, redirectUri);
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
