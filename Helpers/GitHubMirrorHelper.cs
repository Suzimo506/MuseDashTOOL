using System;

namespace MdModManager.Helpers;

public static class GitHubMirrorHelper
{
    private const string GhProxyPrefix = "https://ghproxy.net/";
    private const string KkGitHubHost = "kkgithub.com";
    private const string KkRawHost = "raw.kkgithub.com";
    private const string GitHubHost = "github.com";
    private const string RawGitHubHost = "raw.githubusercontent.com";

    public static string ApplyMirror(string? url, string? downloadSource)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var canonicalUrl = NormalizeCanonicalUrl(url);
        if (!Uri.TryCreate(canonicalUrl, UriKind.Absolute, out var uri))
            return canonicalUrl;

        if (string.IsNullOrWhiteSpace(downloadSource) ||
            downloadSource.Contains(GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            return canonicalUrl;
        }

        if (downloadSource.Contains("ghproxy.net", StringComparison.OrdinalIgnoreCase))
        {
            return canonicalUrl.StartsWith(GhProxyPrefix, StringComparison.OrdinalIgnoreCase)
                ? canonicalUrl
                : GhProxyPrefix + canonicalUrl;
        }

        if (downloadSource.Contains(KkGitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            if (uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase))
                return ReplaceHost(uri, KkGitHubHost);

            if (uri.Host.Equals(RawGitHubHost, StringComparison.OrdinalIgnoreCase))
                return ReplaceHost(uri, KkRawHost);
        }

        return canonicalUrl;
    }

    public static string NormalizeCanonicalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var trimmed = url.Trim();

        if (trimmed.StartsWith(GhProxyPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[GhProxyPrefix.Length..];

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed;

        if (uri.Host.Equals(KkGitHubHost, StringComparison.OrdinalIgnoreCase))
            return ReplaceHost(uri, GitHubHost);

        if (uri.Host.Equals(KkRawHost, StringComparison.OrdinalIgnoreCase))
            return ReplaceHost(uri, RawGitHubHost);

        return trimmed;
    }

    private static string ReplaceHost(Uri uri, string host)
    {
        var builder = new UriBuilder(uri)
        {
            Host = host
        };
        return builder.Uri.AbsoluteUri;
    }
}
