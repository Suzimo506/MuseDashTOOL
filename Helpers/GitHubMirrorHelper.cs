using System;

namespace MdModManager.Helpers;

public static class GitHubMirrorHelper
{
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

        // Suzimo 选项：固定使用 suzimo.site
        if (downloadSource.Equals("Suzimo", StringComparison.OrdinalIgnoreCase) || 
            downloadSource.Equals("suzimo.site", StringComparison.OrdinalIgnoreCase) ||
            downloadSource.Equals("高速DNS", StringComparison.OrdinalIgnoreCase) ||
            downloadSource.Equals("Suzimo优化", StringComparison.OrdinalIgnoreCase))
        {
            return ApplyCustomMirror(canonicalUrl, "suzimo.site");
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

    public static string ApplyCustomMirror(string url, string customHost)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(customHost))
            return url;

        var canonical = NormalizeCanonicalUrl(url);
        if (!Uri.TryCreate(canonical, UriKind.Absolute, out var uri))
            return canonical;

        var host = customHost.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        
        // 特殊处理 kkgithub 的 raw 域名
        if (host.Equals("kkgithub.com", StringComparison.OrdinalIgnoreCase) && 
            uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            host = "raw.kkgithub.com";
        }
        
        var scheme = uri.Scheme;
        var pathAndQuery = uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimStart('/');
        var fragment = uri.GetComponents(UriComponents.Fragment, UriFormat.UriEscaped);
        
        var result = $"{scheme}://{host}/{pathAndQuery}";
        if (!string.IsNullOrEmpty(fragment))
        {
            result += "#" + fragment;
        }
        return result;
    }

    public static string NormalizeCanonicalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var trimmed = url.Trim();

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
        // 安全替换 Host，避免 UriBuilder 破坏已编码的特殊字符如 %23
        var scheme = uri.Scheme;
        var pathAndQuery = uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimStart('/');
        var fragment = uri.GetComponents(UriComponents.Fragment, UriFormat.UriEscaped);
        
        var result = $"{scheme}://{host}/{pathAndQuery}";
        if (!string.IsNullOrEmpty(fragment))
        {
            result += "#" + fragment;
        }
        return result;
    }
}
