using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using MDEN.Protocol;
using MDEN.Protocol.Envelopes;
using MDEN.Protocol.Messages.Mdt;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IEnsembleLobbyService : IDisposable
{
    event Action<string, MdtLobbySnapshot>? SnapshotReceived;
    event Action<string, int, MdtChatMessageEntry>? ChatReceived;
    event Action<string, int, MdtViewerChatMessageEntry>? ViewerChatReceived;
    event Action<string, string, bool>? NodeStatusChanged;
    Task<IReadOnlyList<EnsembleLobbyNodeConfig>> GetNodesAsync(CancellationToken ct);
    Task ConnectAsync(EnsembleLobbyNodeConfig node, string displayName, CancellationToken ct);
    Task<MdtChatResponse> SendPhraseAsync(string nodeId, int lobbyId, string senderName, int phraseIndex, CancellationToken ct);
    Task<MdtViewerChatResponse> SendViewerChatAsync(string nodeId, int lobbyId, string senderName, string message, CancellationToken ct);
    Task WatchLobbyAsync(string nodeId, int? lobbyId, CancellationToken ct);
    Task DisconnectAllAsync();
}

public sealed class EnsembleLobbyService : IEnsembleLobbyService
{
    private const string ServerListUrl = "https://api.xmjjs.top/mpmd/serverlist.php";
    private readonly IConfigService _configService;
    private readonly ConcurrentDictionary<string, NodeConnection> _connections = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(6) };

    public event Action<string, MdtLobbySnapshot>? SnapshotReceived;
    public event Action<string, int, MdtChatMessageEntry>? ChatReceived;
    public event Action<string, int, MdtViewerChatMessageEntry>? ViewerChatReceived;
    public event Action<string, string, bool>? NodeStatusChanged;

    public EnsembleLobbyService(IConfigService configService)
    {
        _configService = configService;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MuseDashTOOL-MDT/1.0");
    }

    public async Task<IReadOnlyList<EnsembleLobbyNodeConfig>> GetNodesAsync(CancellationToken ct)
    {
        var nodes = await TryFetchOfficialNodesAsync(ct).ConfigureAwait(false);
        if (nodes.Count > 0)
        {
            return nodes;
        }

        return _configService.Config.EnsembleLobbyNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Address))
            .ToArray();
    }

    public async Task ConnectAsync(EnsembleLobbyNodeConfig node, string displayName, CancellationToken ct)
    {
        if (node == null || string.IsNullOrWhiteSpace(node.Address)) return;

        var key = GetNodeKey(node);
        if (_connections.TryGetValue(key, out var existing) && existing.IsConnected)
        {
            return;
        }

        await RemoveConnectionAsync(key).ConfigureAwait(false);
        NodeStatusChanged?.Invoke(key, "连接中", false);

        var endpoint = ParseEndpoint(node.Address);
        var connection = new NodeConnection(key, node, DispatchPush, OnConnectionClosed);
        _connections[key] = connection;

        try
        {
            await connection.ConnectAsync(endpoint.Host, endpoint.Port, ct).ConfigureAwait(false);
            connection.StartReceiveLoop();
            await connection.SendRequestAsync<MdtObserveRequest, MdtObserveResponse>(
                OpCodes.MdtObserveReq,
                new MdtObserveRequest { ClientName = NormalizeDisplayName(displayName) },
                ct).ConfigureAwait(false);

            NodeStatusChanged?.Invoke(key, "已连接", true);

            var snapshot = await connection.SendRequestAsync<MdtGetLobbySnapshotRequest, MdtGetLobbySnapshotResponse>(
                OpCodes.MdtGetLobbySnapshotReq,
                new MdtGetLobbySnapshotRequest(),
                ct).ConfigureAwait(false);

            if (snapshot?.Snapshot != null)
            {
                SnapshotReceived?.Invoke(key, snapshot.Snapshot);
            }

        }
        catch (Exception ex)
        {
            NodeStatusChanged?.Invoke(key, $"连接失败：{ex.Message}", false);
            await RemoveConnectionAsync(key).ConfigureAwait(false);
        }
    }

    public async Task<MdtChatResponse> SendPhraseAsync(string nodeId, int lobbyId, string senderName, int phraseIndex, CancellationToken ct)
    {
        if (!_connections.TryGetValue(nodeId, out var connection) || !connection.IsConnected)
        {
            throw new InvalidOperationException("节点未连接");
        }

        return await connection.SendRequestAsync<MdtChatRequest, MdtChatResponse>(
            OpCodes.MdtChatReq,
            new MdtChatRequest
            {
                LobbyId = lobbyId,
                SenderName = senderName,
                PhraseIndex = phraseIndex
            },
            ct).ConfigureAwait(false) ?? throw new InvalidOperationException("服务端响应为空");
    }

    public async Task<MdtViewerChatResponse> SendViewerChatAsync(string nodeId, int lobbyId, string senderName, string message, CancellationToken ct)
    {
        if (!_connections.TryGetValue(nodeId, out var connection) || !connection.IsConnected)
        {
            throw new InvalidOperationException("节点未连接");
        }

        return await connection.SendRequestAsync<MdtViewerChatRequest, MdtViewerChatResponse>(
            OpCodes.MdtViewerChatReq,
            new MdtViewerChatRequest
            {
                LobbyId = lobbyId,
                SenderName = senderName,
                Message = message
            },
            ct).ConfigureAwait(false) ?? throw new InvalidOperationException("服务端响应为空");
    }

    public async Task WatchLobbyAsync(string nodeId, int? lobbyId, CancellationToken ct)
    {
        if (!_connections.TryGetValue(nodeId, out var connection) || !connection.IsConnected)
        {
            return;
        }

        await connection.SendRequestAsync<MdtWatchLobbyRequest, MdtWatchLobbyResponse>(
            OpCodes.MdtWatchLobbyReq,
            new MdtWatchLobbyRequest { LobbyId = lobbyId },
            ct).ConfigureAwait(false);
    }

    public async Task DisconnectAllAsync()
    {
        var keys = _connections.Keys.ToArray();
        foreach (var key in keys)
        {
            await RemoveConnectionAsync(key).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _ = DisconnectAllAsync();
        _httpClient.Dispose();
    }

    private async Task<IReadOnlyList<EnsembleLobbyNodeConfig>> TryFetchOfficialNodesAsync(CancellationToken ct)
    {
        try
        {
            var json = await _httpClient.GetStringAsync(ServerListUrl, ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize(json, EnsembleServerListJsonContext.Default.EnsembleServerListResponse);
            return response?.Servers?
                .Where(server => !string.IsNullOrWhiteSpace(server.Address))
                .Select(server => new EnsembleLobbyNodeConfig
                {
                    Id = string.IsNullOrWhiteSpace(server.Id) ? server.Address : server.Id,
                    Name = string.IsNullOrWhiteSpace(server.Name) ? server.Address : server.Name,
                    Address = server.Address
                })
                .ToArray() ?? Array.Empty<EnsembleLobbyNodeConfig>();
        }
        catch
        {
            return Array.Empty<EnsembleLobbyNodeConfig>();
        }
    }

    private async Task RemoveConnectionAsync(string key)
    {
        if (!_connections.TryRemove(key, out var connection)) return;

        await connection.DisposeAsync().ConfigureAwait(false);
        NodeStatusChanged?.Invoke(key, "未连接", false);
    }

    private void DispatchPush(string nodeId, ushort opCode, JsonElement payload)
    {
        try
        {
            if (opCode == OpCodes.MdtLobbySnapshotPush)
            {
                var push = payload.Deserialize(EnsembleProtocolJsonContext.Default.MdtLobbySnapshotPush);
                if (push?.Snapshot != null)
                {
                    SnapshotReceived?.Invoke(nodeId, push.Snapshot);
                }
            }
            else if (opCode == OpCodes.MdtChatPush)
            {
                var push = payload.Deserialize(EnsembleProtocolJsonContext.Default.MdtChatPush);
                if (push?.Message != null)
                {
                    ChatReceived?.Invoke(nodeId, push.LobbyId, push.Message);
                }
            }
            else if (opCode == OpCodes.MdtViewerChatPush)
            {
                var push = payload.Deserialize(EnsembleProtocolJsonContext.Default.MdtViewerChatPush);
                if (push?.Message != null)
                {
                    ViewerChatReceived?.Invoke(nodeId, push.LobbyId, push.Message);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("EnsembleLobbyService", $"解析推送失败 {opCode}: {ex.Message}");
        }
    }

    private void OnConnectionClosed(string nodeId, string reason)
    {
        NodeStatusChanged?.Invoke(nodeId, reason, false);
        _connections.TryRemove(nodeId, out _);
    }

    private static string GetNodeKey(EnsembleLobbyNodeConfig node)
    {
        if (!string.IsNullOrWhiteSpace(node.Id)) return node.Id.Trim();
        return node.Address.Trim();
    }

    private static (string Host, int Port) ParseEndpoint(string address)
    {
        var parts = address.Trim().Split(':', 2);
        var host = parts[0].Trim();
        var port = 10423;
        if (parts.Length > 1 && int.TryParse(parts[1], out var parsedPort))
        {
            port = parsedPort;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("节点地址无效");
        }

        return (host, port);
    }

    private static string NormalizeDisplayName(string value)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) text = "MuseDashTOOL";
        return text.Length > 16 ? text[..16] : text;
    }

    private sealed class NodeConnection : IAsyncDisposable
    {
        private readonly string _nodeId;
        private readonly EnsembleLobbyNodeConfig _node;
        private readonly Action<string, ushort, JsonElement> _pushHandler;
        private readonly Action<string, string> _closedHandler;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<uint, PendingRequest> _pendingRequests = new();
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _receiveCts;
        private uint _nextReqId;
        private bool _disposed;

        public NodeConnection(
            string nodeId,
            EnsembleLobbyNodeConfig node,
            Action<string, ushort, JsonElement> pushHandler,
            Action<string, string> closedHandler)
        {
            _nodeId = nodeId;
            _node = node;
            _pushHandler = pushHandler;
            _closedHandler = closedHandler;
        }

        public bool IsConnected => _client?.Connected == true && _stream != null && !_disposed;

        public async Task ConnectAsync(string host, int port, CancellationToken ct)
        {
            _client = new TcpClient();
            await _client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            _stream = _client.GetStream();
        }

        public void StartReceiveLoop()
        {
            _receiveCts?.Cancel();
            _receiveCts = new CancellationTokenSource();
            _ = ReceiveLoopAsync(_receiveCts.Token);
        }

        public async Task<TResp?> SendRequestAsync<TReq, TResp>(ushort opCode, TReq request, CancellationToken ct)
        {
            if (_stream == null) throw new InvalidOperationException("节点未连接");

            var reqId = unchecked(++_nextReqId);
            if (reqId == 0) reqId = unchecked(++_nextReqId);

            var pending = new PendingRequest(typeof(TResp));
            _pendingRequests[reqId] = pending;

            try
            {
                await SendAsync(new ClientEnvelope
                {
                    Op = opCode,
                    ReqId = reqId,
                    Payload = request!
                }, ct).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                using var registration = timeoutCts.Token.Register(() =>
                {
                    if (_pendingRequests.TryRemove(reqId, out var removed))
                    {
                        removed.TrySetException(new TimeoutException("请求超时"));
                    }
                });

                var result = await pending.Task.ConfigureAwait(false);
                return result == null ? default : (TResp)result;
            }
            finally
            {
                _pendingRequests.TryRemove(reqId, out _);
            }
        }

        private async Task SendAsync(ClientEnvelope envelope, CancellationToken ct)
        {
            if (_stream == null) throw new InvalidOperationException("节点未连接");

            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options);
            var header = BitConverter.GetBytes(payload.Length);

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(header, ct).ConfigureAwait(false);
                await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _stream != null)
                {
                    var envelope = await ReadEnvelopeAsync(_stream, ct).ConfigureAwait(false);
                    if (envelope == null) break;

                    if (envelope.ReqId.HasValue)
                    {
                        HandleResponse(envelope);
                    }
                    else if (envelope.Payload is JsonElement payload)
                    {
                        _pushHandler(_nodeId, envelope.Op, payload);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("EnsembleLobbyService", $"{_node.Name} 接收失败 {ex.Message}");
            }
            finally
            {
                await DisposeAsync().ConfigureAwait(false);
                _closedHandler(_nodeId, "已断开");
            }
        }

        private void HandleResponse(ServerEnvelope envelope)
        {
            if (!envelope.ReqId.HasValue) return;
            if (!_pendingRequests.TryRemove(envelope.ReqId.Value, out var pending)) return;

            if (!envelope.Success)
            {
                pending.TrySetException(new InvalidOperationException(ReadReason(envelope.Payload)));
                return;
            }

            try
            {
                if (pending.ResponseType == typeof(object) || envelope.Payload == null)
                {
                    pending.TrySetResult(null);
                    return;
                }

                if (envelope.Payload is JsonElement element)
                {
                    pending.TrySetResult(element.Deserialize(pending.ResponseType, ProtocolJson.Options));
                    return;
                }

                pending.TrySetResult(envelope.Payload);
            }
            catch (Exception ex)
            {
                pending.TrySetException(ex);
            }
        }

        private static async Task<ServerEnvelope?> ReadEnvelopeAsync(NetworkStream stream, CancellationToken ct)
        {
            var header = new byte[4];
            if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false)) return null;

            var length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > 4 * 1024 * 1024)
            {
                throw new InvalidOperationException("协议包长度无效");
            }

            var payload = new byte[length];
            if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false)) return null;

            return JsonSerializer.Deserialize<ServerEnvelope>(payload, ProtocolJson.Options);
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
            }

            return true;
        }

        private static string ReadReason(object? payload)
        {
            if (payload is JsonElement element && element.TryGetProperty("Reason", out var reason))
            {
                return reason.GetString() ?? "请求失败";
            }

            return "请求失败";
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { _receiveCts?.Cancel(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _sendLock.Dispose();
            _receiveCts?.Dispose();

            foreach (var pending in _pendingRequests.Values)
            {
                pending.TrySetException(new InvalidOperationException("连接已关闭"));
            }

            _pendingRequests.Clear();
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private sealed class PendingRequest
        {
            private readonly TaskCompletionSource<object?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public PendingRequest(Type responseType)
            {
                ResponseType = responseType;
            }

            public Type ResponseType { get; }
            public Task<object?> Task => _tcs.Task;
            public void TrySetResult(object? result) => _tcs.TrySetResult(result);
            public void TrySetException(Exception ex) => _tcs.TrySetException(ex);
        }
    }
}

public sealed class EnsembleServerEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("address")]
    public string Address { get; set; } = "";
}

public sealed class EnsembleServerListResponse
{
    [JsonPropertyName("servers")]
    public List<EnsembleServerEntry> Servers { get; set; } = new();
}

[JsonSerializable(typeof(EnsembleServerListResponse))]
internal partial class EnsembleServerListJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(ClientEnvelope))]
[JsonSerializable(typeof(ServerEnvelope))]
[JsonSerializable(typeof(MdtLobbySnapshotPush))]
[JsonSerializable(typeof(MdtChatPush))]
[JsonSerializable(typeof(MdtViewerChatPush))]
[JsonSerializable(typeof(MdtWatchLobbyRequest))]
[JsonSerializable(typeof(MdtWatchLobbyResponse))]
[JsonSerializable(typeof(MdtViewerChatRequest))]
[JsonSerializable(typeof(MdtViewerChatResponse))]
internal partial class EnsembleProtocolJsonContext : JsonSerializerContext { }
