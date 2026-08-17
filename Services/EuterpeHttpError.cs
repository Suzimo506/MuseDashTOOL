using System.Net;
using System.Text.Json;

namespace MdModManager.Services;

public sealed class EuterpeHttpException : HttpRequestException
{
    public EuterpeHttpException(HttpStatusCode statusCode, string message) : base(message, null, statusCode)
    {
    }
}

public static class EuterpeHttpError
{
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct = default)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var detail = ExtractMessage(body);
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Euterpe 登录已失效，请退出账号后重新登录",
            HttpStatusCode.Forbidden => "Euterpe 拒绝了当前账号的访问请求",
            HttpStatusCode.TooManyRequests => EuterpeRateLimitGate.Register(response),
            _ when !string.IsNullOrWhiteSpace(detail) => $"{operation}失败：{detail}",
            _ => $"{operation}失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
        };

        throw new EuterpeHttpException(response.StatusCode, message);
    }

    public static string ToUserMessage(Exception exception, string operation)
    {
        return exception switch
        {
            EuterpeHttpException => exception.Message,
            NullReferenceException => $"{operation}失败：Euterpe 返回的数据缺少必要字段",
            JsonException => $"{operation}失败：Euterpe 返回了无法识别的数据",
            HttpRequestException => $"{operation}失败：无法连接 Euterpe，请检查网络后重试",
            _ => $"{operation}失败：{exception.Message}"
        };
    }

    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var nestedMessage))
                    return nestedMessage.GetString() ?? string.Empty;
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("message", out var message))
                return message.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
        }

        return body.Length <= 300 ? body : body[..300];
    }
}

public static class EuterpeRateLimitGate
{
    private static long _blockedUntilUtcTicks;

    public static void ThrowIfBlocked()
    {
        var blockedUntilTicks = Interlocked.Read(ref _blockedUntilUtcTicks);
        if (blockedUntilTicks <= DateTime.UtcNow.Ticks)
            return;

        var seconds = Math.Max(1, (int)Math.Ceiling((new DateTime(blockedUntilTicks, DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds));
        throw new EuterpeHttpException(HttpStatusCode.TooManyRequests, $"Euterpe 正在限流，请在 {seconds} 秒后再试");
    }

    public static string Register(HttpResponseMessage response)
    {
        var now = DateTimeOffset.UtcNow;
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } retryAt ? (TimeSpan?)(retryAt - now) : null)
            ?? TimeSpan.FromSeconds(60);
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.FromSeconds(60);

        var blockedUntil = now.Add(delay).UtcDateTime;
        long observed;
        do
        {
            observed = Interlocked.Read(ref _blockedUntilUtcTicks);
            if (observed >= blockedUntil.Ticks)
                break;
        }
        while (Interlocked.CompareExchange(ref _blockedUntilUtcTicks, blockedUntil.Ticks, observed) != observed);

        var seconds = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds));
        RuntimeLog.Write("EuterpeRateLimit", $"Received 429. Retry-After={retryAfter?.ToString() ?? "missing"}; blocking API requests for {seconds}s.");
        return $"Euterpe 请求过于频繁，请在 {seconds} 秒后再试";
    }
}
