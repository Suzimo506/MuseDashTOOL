using System;

namespace MdModManager.Helpers;

public static class GitHubMirrorHelper
{
    private const string GitHubHost = "github.com";
    private const string RawGitHubHost = "raw.githubusercontent.com";

    public static string ApplyMirror(string? url, string? downloadSource)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        if (string.IsNullOrWhiteSpace(downloadSource))
            return url;

        // Suzimo / 高速 DNS 只保留代号，真实域名统一从镜像配置里解析出来。
        var suzimoHost = MirrorDomainRegistry.ResolveMirrorHost(downloadSource);
        if (!string.IsNullOrWhiteSpace(suzimoHost))
        {
            return ApplyCustomMirror(url, suzimoHost);
        }

        return url;
    }

    public static string ApplyCustomMirror(string url, string customHost)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(customHost))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        // 这里允许传完整 URL 或纯主机名，统一裁成 host 再拼接。
        var host = customHost.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        
        // 仅替换 GitHub 相关的域名，防止误伤 R2 直链或其它域名
        if (uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase) || 
            uri.Host.Equals(RawGitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            // 特殊处理 suzimo.site：对于 github.com 的 Release 链接，需要通过代理转发
            // 但如果是 raw.githubusercontent.com 的链接也可以代理
        }
        else
        {
            return url; // 如果不是 GitHub 的链接，直接放行，不做劫持
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

