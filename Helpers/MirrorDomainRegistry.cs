using System;
using MdModManager.Models;

namespace MdModManager.Helpers;

public static class MirrorDomainRegistry
{
    // 代码中固定使用的 Suzimo 下载源代号。
    public const string SuzimoAlias = "suzimo";

    private static readonly object Sync = new();
    private static string _suzimoHost = string.Empty;

    public static string SuzimoHost
    {
        get
        {
            lock (Sync)
            {
                return _suzimoHost;
            }
        }
    }

    public static bool HasSuzimoHost => !string.IsNullOrWhiteSpace(SuzimoHost);

    public static void Update(MirrorDomainsConfig? config)
    {
        if (config == null)
            return;

        var normalizedHost = NormalizeHost(config.Suzimo);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return;

        lock (Sync)
        {
            _suzimoHost = normalizedHost;
        }
    }

    public static bool IsSuzimoDownloadSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Equals(SuzimoAlias, StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Suzimo", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("高速 DNS", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("高速DNS", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Suzimo优化", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 兼容旧配置：如果历史配置里直接写了真实域名，也仍然视作 Suzimo 线路。
        var normalized = NormalizeHost(value);
        var currentHost = SuzimoHost;
        return !string.IsNullOrWhiteSpace(normalized) &&
               ((!string.IsNullOrWhiteSpace(currentHost) && normalized.Equals(currentHost, StringComparison.OrdinalIgnoreCase)) ||
                IsLegacySuzimoHost(normalized));
    }

    public static bool IsSuzimoHost(string? host)
    {
        var normalized = NormalizeHost(host);
        var currentHost = SuzimoHost;

        return !string.IsNullOrWhiteSpace(normalized) &&
               !string.IsNullOrWhiteSpace(currentHost) &&
               normalized.Equals(currentHost, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ResolveMirrorHost(string? downloadSource)
    {
        return IsSuzimoDownloadSource(downloadSource) ? SuzimoHost : null;
    }

    public static string NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
            return absoluteUri.Host;

        var withoutScheme = trimmed
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        var slashIndex = withoutScheme.IndexOf('/');
        if (slashIndex >= 0)
            withoutScheme = withoutScheme[..slashIndex];

        return withoutScheme.Trim().TrimEnd('/');
    }

    private static bool IsLegacySuzimoHost(string normalizedHost)
    {
        // 旧版本里 Suzimo 线路保存的是一个真实域名。
        // 这里只排除已知非 Suzimo 下载源，剩下的主机名都按旧 Suzimo 配置迁移到代号。
        return normalizedHost.Contains('.', StringComparison.Ordinal) &&
               !normalizedHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
               !normalizedHost.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
               !normalizedHost.Equals("kkgithub.com", StringComparison.OrdinalIgnoreCase) &&
               !normalizedHost.Equals("raw.kkgithub.com", StringComparison.OrdinalIgnoreCase) &&
               !normalizedHost.Equals("ghproxy.net", StringComparison.OrdinalIgnoreCase);
    }
}
