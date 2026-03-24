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
    // Cloudflare Optimized IPs (Domain Fronting / Direct IP Connect)
    private static readonly string[] OptimizedIps = { 
        "104.18.39.141", "172.67.220.100", "104.21.22.100", 
        "104.28.14.20", "104.16.132.229", "104.16.212.44", 
        "104.19.16.29", "104.17.182.204" 
    };

    private static string? _fastestIp;
    private static readonly SemaphoreSlim _lock = new(1, 1);
    
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
    private static readonly HashSet<string> _blacklistedIps = new();
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
                RuntimeLog.Write("HttpHelper", "Background IP racing started.");
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // 每 10 秒竞速一次
                        await Task.Delay(TimeSpan.FromSeconds(10), token);
                        
                        if (!UseOptimizedIps) break;

                        // 执行静默竞速
                        var newIp = await PerformSilentRaceAsync(token);
                        if (newIp != null)
                        {
                            _fastestIp = newIp;
                            RuntimeLog.Write("HttpHelper", $"Background race selected new IP: {newIp}");
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        RuntimeLog.Write("HttpHelper", $"Background race error: {ex.Message}");
                    }
                }
                RuntimeLog.Write("HttpHelper", "Background IP racing stopped.");
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
                ipsToTest = OptimizedIps.Where(ip => !_blacklistedIps.Contains(ip)).ToList();
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
                ipsToTest = OptimizedIps.Where(ip => !_blacklistedIps.Contains(ip)).ToList();
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
                            tcs.TrySetException(new Exception("All IPs failed"));
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

    public static HttpClient CreateOptimizedClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                // 只有在启用优选 DNS 且域名属于 suzimo 或 mdmc 时才强制使用优选 IP 
                if (UseOptimizedIps && (host.Contains("suzimo.online", StringComparison.OrdinalIgnoreCase) ||
                    host.Contains("mdmc.moe", StringComparison.OrdinalIgnoreCase)))
                {
                    Exception? lastException = null;
                    // 如果优选 IP 挂了，清空缓存重新赛跑，最多重试 3 次
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
                            _fastestIp = null; // 清除死掉的 IP 缓存
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
        handler.ConnectTimeout = TimeSpan.FromSeconds(3);

        var resilientHandler = new ResilientHandler(handler);
        var client = new HttpClient(resilientHandler) { Timeout = timeout };
        
        client.DefaultRequestHeaders.Add("User-Agent", "MuseDashTOOL-Downloader");
        return client;
    }

    public static void InvalidateFastestIp()
    {
        if (_fastestIp != null)
        {
            lock (_blacklistLock)
            {
                _blacklistedIps.Add(_fastestIp);
                RuntimeLog.Write("HttpHelper", $"Blacklisted dead IP: {_fastestIp}");
            }
        }
        _fastestIp = null;
    }

    private class ResilientHandler : DelegatingHandler
    {
        public ResilientHandler(HttpMessageHandler innerHandler) : base(innerHandler)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            // 防断控制：如果在 4 秒内没拿到响应头，判定为 Cloudflare 节点假死，主动掐断并触发换 IP
            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerCts.CancelAfter(TimeSpan.FromSeconds(4));

            try
            {
                var response = await base.SendAsync(request, headerCts.Token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500)
                {
                    var host = request.RequestUri?.Host;
                    if (UseOptimizedIps && host != null && (host.Contains("suzimo.online", StringComparison.OrdinalIgnoreCase) ||
                                         host.Contains("mdmc.moe", StringComparison.OrdinalIgnoreCase)))
                    {
                        HttpHelper.InvalidateFastestIp();
                    }
                }
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is TimeoutException)
            {
                var host = request.RequestUri?.Host;
                if (UseOptimizedIps && host != null && (host.Contains("suzimo.online", StringComparison.OrdinalIgnoreCase) ||
                                     host.Contains("mdmc.moe", StringComparison.OrdinalIgnoreCase)))
                {
                    HttpHelper.InvalidateFastestIp();
                }
                throw;
            }
        }
    }
}
