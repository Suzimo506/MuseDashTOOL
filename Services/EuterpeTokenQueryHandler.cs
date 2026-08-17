using System.Net;
using System.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MdModManager.Services;

public sealed class EuterpeTokenQueryHandler : DelegatingHandler
{
    private static readonly Uri DownloadBaseUri = new("https://dl.euterpe-org.com/files/");
    private readonly IServiceProvider _services;

    public EuterpeTokenQueryHandler(IServiceProvider services) : base(new HttpClientHandler())
    {
        _services = services;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EuterpeRateLimitGate.ThrowIfBlocked();

        if (request.RequestUri == null || !DownloadBaseUri.IsBaseOf(request.RequestUri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var authService = _services.GetRequiredService<IAuthService>();
        var token = await authService.GetAccessTokenAsync().ConfigureAwait(false);
        request.RequestUri = AppendToken(request.RequestUri, token);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        RuntimeLog.Write("EuterpeAuth", $"Download request returned 401: {request.RequestUri.GetLeftPart(UriPartial.Path)}");
        token = await authService.RenewAccessTokenAsync(token).ConfigureAwait(false);
        request.RequestUri = AppendToken(request.RequestUri, token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static Uri AppendToken(Uri uri, string token)
    {
        var builder = new UriBuilder(uri);
        var query = HttpUtility.ParseQueryString(builder.Query);
        query.Set("t", token);
        builder.Query = query.ToString();
        return builder.Uri;
    }
}
