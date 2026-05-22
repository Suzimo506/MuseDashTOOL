using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MdModManager.Models;

namespace MdModManager.Services;

public sealed class AuthHeaderHandler : DelegatingHandler
{
    private readonly IServiceProvider _services;

    public AuthHeaderHandler(IServiceProvider services) : base(new HttpClientHandler())
    {
        _services = services;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 自动注入 X-Request-Id 请求头
        if (!request.Headers.Contains("X-Request-Id"))
        {
            request.Headers.Add("X-Request-Id", Guid.CreateVersion7().ToString());
        }

        var authState = _services.GetRequiredService<AuthState>();
        if (string.IsNullOrEmpty(authState.RefreshToken))
        {
            // 未登录状态，直接穿透
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var authService = _services.GetRequiredService<IAuthService>();
        var token = await authService.GetAccessTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is not HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();

        // 401 提示凭证可能过期，尝试静默刷新并重试一次
        token = await authService.RenewAccessTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
