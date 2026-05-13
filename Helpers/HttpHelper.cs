using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using MdModManager.Services;

namespace MdModManager.Helpers;

public static class HttpHelper
{
    // 优选IP
    private static readonly string[] OptimizedIps = { 
        // 原有的经过测验的稳定 IP
        "104.18.39.141", "172.67.220.100", "104.21.22.100", "104.28.14.20", 
        "104.16.132.229", "104.16.212.44", "104.19.16.29", "104.17.182.204",
        // 新增的针对三网优选的种子 IP
        "104.16.160.1", "172.64.32.1", "104.20.157.1", "108.162.236.1",
        "172.64.0.1", "104.28.14.1", "104.22.7.1", "172.67.73.1",
        "104.21.22.1", "104.18.39.1", "172.67.220.1", "104.16.132.1",
        "104.28.15.1", "172.64.36.1", "104.16.161.1", "172.64.1.1",
        "162.159.211.1", "104.24.116.1", "172.64.33.1", "104.25.158.1",
        "172.67.180.1", "104.17.182.1", "104.19.16.1", "104.16.212.1"
    };

    private static string? _fastestIp;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private static string? _staticIp;
    /// <summary>用户自定义的静态 IP，若不为 null 则跳过竞速直接使用</summary>
    public static string? StaticIp 
    {
        get => _staticIp;
        set 
        {
            if (_staticIp == value) return;
            _staticIp = value;
            // 如果清空了静态 IP 且开启了优选，则重新触发测速
            if (string.IsNullOrEmpty(value) && UseOptimizedIps)
            {
                StartBackgroundRacing();
            }
        }
    }
    
    /// <summary>获取当前生效的 IP（静态 IP 优先，其次为竞速出的最快 IP）</summary>
    public static string? GetEffectiveIp()
    {
        if (!string.IsNullOrEmpty(StaticIp)) return StaticIp;
        return _fastestIp;
    }

    // 控制是否启用内置的 Cloudflare IP 优选机制
    private static bool _useOptimizedIps = false;
    public static bool UseOptimizedIps 
    { 
        get => _useOptimizedIps; 
        set 
        {
            if (_useOptimizedIps == value) return;
            _useOptimizedIps = value;
            if (value) StartBackgroundRacing();
            else StopBackgroundRacing();
        }
    }
    // 黑名单 IP 及其拉黑时间 (UTC)
    private static readonly Dictionary<string, DateTime> _blacklistedIps = new();
    private static readonly object _blacklistLock = new();

    private static CancellationTokenSource? _racingCts;
    private static readonly object _racingLock = new();

    private static void StartBackgroundRacing()
    {
        lock (_racingLock)
        {
            if (_racingCts != null) return;
            _racingCts = new CancellationTokenSource();
            var token = _racingCts.Token;

            _ = Task.Run(async () =>
            {
                RuntimeLog.Write("HttpHelper", "后台 IP 竞速已启动。");
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // 每 5 秒竞速一次
                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                        
                        if (!UseOptimizedIps || !string.IsNullOrEmpty(StaticIp)) break;

                        // 执行静默竞速
                        var newIp = await PerformSilentRaceAsync(token);
                        if (newIp != null)
                        {
                            lock (_blacklistLock)
                            {
                                _fastestIp = newIp;
                            }
                            RuntimeLog.Write("HttpHelper", $"后台竞速选出了新 IP: {newIp}");
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        RuntimeLog.Write("HttpHelper", $"后台竞速出错: {ex.Message}");
                    }
                }
                RuntimeLog.Write("HttpHelper", "后台 IP 竞速已停止。");
            }, token);
        }
    }

    private static void StopBackgroundRacing()
    {
        lock (_racingLock)
        {
            _racingCts?.Cancel();
            _racingCts?.Dispose();
            _racingCts = null;
        }
    }

    private static async Task<string?> PerformSilentRaceAsync(CancellationToken ct)
    {
        try
        {
            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            raceCts.CancelAfter(TimeSpan.FromSeconds(3));
            
            var tcs = new TaskCompletionSource<string?>();
            List<string> ipsToTest;
            lock (_blacklistLock)
            {
                // 自动释放：移除拉黑超过 3 分钟的 IP
                var now = DateTime.UtcNow;
                var expired = _blacklistedIps.Where(x => (now - x.Value).TotalMinutes >= 3).Select(x => x.Key).ToList();
                foreach (var key in expired) _blacklistedIps.Remove(key);

                ipsToTest = OptimizedIps.Where(ip => !_blacklistedIps.ContainsKey(ip)).ToList();
                if (ipsToTest.Count == 0)
                {
                    _blacklistedIps.Clear();
                    ipsToTest = OptimizedIps.ToList();
                }
            }

            int remaining = ipsToTest.Count;
            foreach (var ip in ipsToTest)
            {
                _ = Task.Run(async () =>
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Parse(ip), 443, raceCts.Token);
                        tcs.TrySetResult(ip);
                    }
                    catch
                    {
                        if (Interlocked.Decrement(ref remaining) == 0)
                        {
                            tcs.TrySetResult(null);
                        }
                    }
                }, raceCts.Token);
            }

            return await tcs.Task;
        }
        catch { return null; }
    }

    private static async Task<string> GetFastestIpAsync()
    {
        if (!string.IsNullOrEmpty(StaticIp)) return StaticIp;
        if (_fastestIp != null) return _fastestIp;
        await _lock.WaitAsync();
        try
        {
            if (_fastestIp != null) return _fastestIp;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var tcs = new TaskCompletionSource<string>();

            List<string> ipsToTest;
            lock (_blacklistLock)
            {
                // 自动释放：移除拉黑超过 3 分钟的 IP
                var now = DateTime.UtcNow;
                var expired = _blacklistedIps.Where(x => (now - x.Value).TotalMinutes >= 3).Select(x => x.Key).ToList();
                foreach (var key in expired) _blacklistedIps.Remove(key);

                ipsToTest = OptimizedIps.Where(ip => !_blacklistedIps.ContainsKey(ip)).ToList();
                if (ipsToTest.Count == 0)
                {
                    _blacklistedIps.Clear(); // 如果全部被拉黑，说明可能本地网络断开或恢复了，整体重置寻找
                    ipsToTest = OptimizedIps.ToList();
                }
            }

            // 注册所有连接任务，任何一个成功就使 tcs 完成 (竞速机制)
            int remaining = ipsToTest.Count;
            foreach (var ip in ipsToTest)
            {
                _ = Task.Run(async () =>
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try 
                    {
                        await socket.ConnectAsync(IPAddress.Parse(ip), 443, cts.Token);
                        tcs.TrySetResult(ip); // 首个成功立即返回
                    }
                    catch
                    {
                        if (Interlocked.Decrement(ref remaining) == 0)
                        {
                            tcs.TrySetException(new Exception("所有 IP 连接试跑失败"));
                        }
                    }
                }, cts.Token);
            }

            // 等待最先成功的 IP 或者超时取消
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
            _fastestIp = await tcs.Task;
            
            return _fastestIp;
        }
        catch
        {
            // 如果全部失败、被 GFW 瞬间秒断或超时，降级为第一个 IP
            _fastestIp = OptimizedIps[0];
            return _fastestIp;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static bool IsOptimizedAccelerationHost(string? host)
    {
        return MirrorDomainRegistry.IsSuzimoHost(host) ||
               MirrorDomainRegistry.IsConfiguredDownloadHost(host) ||
               (!string.IsNullOrWhiteSpace(host) &&
                (host.Contains("mdmc.moe", StringComparison.OrdinalIgnoreCase) ||
                 host.Contains("musedash.moe", StringComparison.OrdinalIgnoreCase) ||
                 host.Contains("suzimo.site", StringComparison.OrdinalIgnoreCase)));
    }

    public static HttpClient CreateOptimizedClient(TimeSpan timeout, TimeSpan? watchdogTimeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                // 只有在启用优选 DNS 且域名属于 suzimo 或 mdmc 时才强制使用优选 IP 
                if (UseOptimizedIps && IsOptimizedAccelerationHost(host))
                {
                    // ── 用户自定义静态 IP：直连，不重试、不竞速、不拉黑 ──
                    if (!string.IsNullOrEmpty(StaticIp))
                    {
                        var staticSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        staticSocket.NoDelay = true;
                        try
                        {
                            await staticSocket.ConnectAsync(IPAddress.Parse(StaticIp), port, cancellationToken);
                            return new NetworkStream(staticSocket, ownsSocket: true);
                        }
                        catch
                        {
                            staticSocket.Dispose();
                            throw;
                        }
                    }

                    // ── 自动优选 IP：竞速 + 重试 3 次 ──
                    Exception? lastException = null;
                    for (int retry = 0; retry < 3; retry++)
                    {
                        var selectedIp = await GetFastestIpAsync();
                        var optSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        optSocket.NoDelay = true;
                        
                        try
                        {
                            await optSocket.ConnectAsync(IPAddress.Parse(selectedIp), port, cancellationToken);
                            return new NetworkStream(optSocket, ownsSocket: true);
                        }
                        catch (Exception ex)
                        {
                            optSocket.Dispose();
                            _fastestIp = null;
                            lastException = ex;
                        }
                    }
                    throw new Exception("所有经过重新赛跑的优选 IP 均连接失败。", lastException);
                }
                
                // 普通域名的正常连接
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                
                try
                {
                    await socket.ConnectAsync(addresses, port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
        handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30);
        handler.ConnectTimeout = TimeSpan.FromSeconds(5);

        var resilientHandler = new ResilientHandler(handler, watchdogTimeout ?? TimeSpan.FromSeconds(10));
        var client = new HttpClient(resilientHandler) 
        { 
            Timeout = timeout,
            DefaultRequestVersion = new Version(2, 0) // 启用 HTTP/2
        };
        
        // 伪装成真实浏览器，避免被 Cloudflare 或 MDMC 防火墙拦截
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        
        // 补全现代浏览器的特定安全报头 (Sec-Ch-Ua)
        client.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
        client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
        client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");

        // 关键：模拟官方网页来源，绕过服务器的来源校验
        client.DefaultRequestHeaders.Add("Referer", "https://mdmc.moe/");
        client.DefaultRequestHeaders.Add("Origin", "https://mdmc.moe");

        return client;
    }

    public static void InvalidateFastestIp()
    {
        lock (_blacklistLock)
        {
            if (_fastestIp != null)
            {
                if (!_blacklistedIps.ContainsKey(_fastestIp))
                {
                    _blacklistedIps[_fastestIp] = DateTime.UtcNow;
                    RuntimeLog.Write("HttpHelper", $"已拉黑失效 IP: {_fastestIp} (3 分钟冷却)");
                }
                _fastestIp = null;
            }
        }
    }

    private class ResilientHandler : DelegatingHandler
    {
        private readonly TimeSpan _watchdogTimeout;

        public ResilientHandler(HttpMessageHandler innerHandler, TimeSpan watchdogTimeout) : base(innerHandler)
        {
            _watchdogTimeout = watchdogTimeout;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            // 防断控制：如果在指定时间内没拿到响应头，判定为 Cloudflare 节点假死，主动掐断并触发换 IP
            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerCts.CancelAfter(_watchdogTimeout);

            try
            {
                var response = await base.SendAsync(request, headerCts.Token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500)
                {
                    var host = request.RequestUri?.Host;
                    if (UseOptimizedIps && string.IsNullOrEmpty(StaticIp) && IsOptimizedAccelerationHost(host))
                    {
                        HttpHelper.InvalidateFastestIp();
                    }
                }
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is TimeoutException)
            {
                var host = request.RequestUri?.Host;
                if (UseOptimizedIps && IsOptimizedAccelerationHost(host))
                {
                    HttpHelper.InvalidateFastestIp();
                }
                throw;
            }
        }
    }
}
