using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MDEN.Protocol.Messages.Mdt;
using MdModManager.Models;
using MdModManager.Services;

namespace MdModManager.ViewModels;

public partial class OnlineLobbyViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] FixedPhrases =
    {
        "龙币们放我进去好吗",
        "我也想进房间玩~",
        "敢不敢让我进来，直接点里水里火里蛇..."
    };

    private readonly IEnsembleLobbyService _lobbyService;
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _cooldownTimer;
    private string? _watchingNodeId;
    private int? _watchingLobbyId;
    private DateTimeOffset _nextAllowedSendTime = DateTimeOffset.MinValue;

    [ObservableProperty] private ObservableCollection<EnsembleLobbyNode> _nodes = new();
    [ObservableProperty] private EnsembleLobbyRoom? _selectedRoom;
    [ObservableProperty] private string _displayName = "喵斯兔玩家";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "未连接";
    [ObservableProperty] private string _cooldownText = "";

    public IReadOnlyList<string> Phrases => FixedPhrases;
    public bool HasSelectedRoom => SelectedRoom != null;
    public bool CanSendPhrase => SelectedRoom?.CanSendChat == true && DateTimeOffset.Now >= _nextAllowedSendTime;

    public OnlineLobbyViewModel(
        IEnsembleLobbyService lobbyService,
        IConfigService configService,
        INotificationService notificationService)
    {
        _lobbyService = lobbyService;
        _configService = configService;
        _notificationService = notificationService;
        DisplayName = NormalizeDisplayName(_configService.Config.OnlineLobbyDisplayName);

        _lobbyService.SnapshotReceived += OnSnapshotReceived;
        _lobbyService.ChatReceived += OnChatReceived;
        _lobbyService.NodeStatusChanged += OnNodeStatusChanged;
    }

    public async Task InitializeAsync(CancellationToken externalToken)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        IsLoading = true;
        StatusText = "正在连接节点";

        try
        {
            var nodes = await _lobbyService.GetNodesAsync(_cts.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SelectedRoom = null;
                _watchingNodeId = null;
                _watchingLobbyId = null;
                Nodes.Clear();
                foreach (var node in nodes)
                {
                    Nodes.Add(new EnsembleLobbyNode
                    {
                        Id = string.IsNullOrWhiteSpace(node.Id) ? node.Address : node.Id,
                        Name = string.IsNullOrWhiteSpace(node.Name) ? node.Address : node.Name,
                        Address = node.Address,
                        StatusText = "等待连接"
                    });
                }

                StatusText = Nodes.Count == 0 ? "没有可用节点" : $"发现 {Nodes.Count} 个节点";
            });

            foreach (var node in nodes)
            {
                _ = _lobbyService.ConnectAsync(node, DisplayName, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText = "联机大厅加载失败";
                _notificationService.ShowFailure("联机大厅加载失败", ex.Message);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _cts?.Cancel();
        SelectedRoom = null;
        _watchingNodeId = null;
        _watchingLobbyId = null;
        await _lobbyService.DisconnectAllAsync();
        await InitializeAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void SelectRoom(EnsembleLobbyRoom room)
    {
        SelectedRoom = room;
    }

    [RelayCommand]
    private async Task SaveDisplayNameAsync()
    {
        DisplayName = NormalizeDisplayName(DisplayName);
        _configService.Config.OnlineLobbyDisplayName = DisplayName;
        await _configService.SaveAsync();
        _notificationService.ShowSuccess("联机大厅名字已保存");
    }

    [RelayCommand]
    private async Task SendPhraseAsync(string phrase)
    {
        if (SelectedRoom?.Node == null || string.IsNullOrWhiteSpace(phrase)) return;

        if (!SelectedRoom.CanSendChat)
        {
            _notificationService.ShowFailure("发送失败", "房间游戏中无法发送");
            return;
        }

        var phraseIndex = Array.IndexOf(FixedPhrases, phrase);
        if (phraseIndex < 0) return;

        if (DateTimeOffset.Now < _nextAllowedSendTime)
        {
            UpdateCooldownText();
            _notificationService.ShowFailure("发送失败", CooldownText);
            return;
        }

        try
        {
            var response = await _lobbyService.SendPhraseAsync(
                SelectedRoom.Node.Id,
                SelectedRoom.Id,
                NormalizeDisplayName(DisplayName),
                phraseIndex,
                _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _nextAllowedSendTime = DateTimeOffset.FromUnixTimeMilliseconds(response.NextAllowedUnixMs);
                UpdateCooldownText();
                StartCooldownTimer();
                _notificationService.ShowSuccess("已发送到游戏内");
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                _notificationService.ShowFailure("发送失败", ex.Message));
        }
    }

    partial void OnSelectedRoomChanged(EnsembleLobbyRoom? value)
    {
        OnPropertyChanged(nameof(HasSelectedRoom));
        OnPropertyChanged(nameof(CanSendPhrase));
        _ = ReportWatchingRoomAsync(value);
    }

    private async Task ReportWatchingRoomAsync(EnsembleLobbyRoom? room)
    {
        var previousNodeId = _watchingNodeId;
        var previousLobbyId = _watchingLobbyId;
        var nextNodeId = room?.Node?.Id;
        var nextLobbyId = room?.Id;

        if (previousNodeId == nextNodeId && previousLobbyId == nextLobbyId) return;

        _watchingNodeId = nextNodeId;
        _watchingLobbyId = nextLobbyId;

        try
        {
            var token = _cts?.Token ?? CancellationToken.None;

            if (!string.IsNullOrWhiteSpace(previousNodeId) && previousNodeId != nextNodeId)
            {
                await _lobbyService.WatchLobbyAsync(previousNodeId, null, token).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(nextNodeId) && nextLobbyId.HasValue)
            {
                await _lobbyService.WatchLobbyAsync(nextNodeId, nextLobbyId.Value, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("OnlineLobbyViewModel", $"上报联机大厅观看房间失败: {ex.Message}");
        }
    }

    private void OnSnapshotReceived(string nodeId, MdtLobbySnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => ApplySnapshot(nodeId, snapshot));
    }

    private void OnChatReceived(string nodeId, int lobbyId, MdtChatMessageEntry message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var room = FindRoom(nodeId, lobbyId);
            if (room == null) return;

            AppendChat(room, message);
        });
    }

    private void OnNodeStatusChanged(string nodeId, string status, bool isConnected)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var node = FindNode(nodeId);
            if (node == null) return;

            node.StatusText = status;
            node.IsConnected = isConnected;
            StatusText = $"{node.Name}：{status}";
        });
    }

    private void ApplySnapshot(string nodeId, MdtLobbySnapshot snapshot)
    {
        var node = FindNode(nodeId);
        if (node == null) return;

        node.IsConnected = true;
        node.StatusText = "已连接";

        var incoming = snapshot.Lobbies ?? Array.Empty<MdtLobbyEntry>();
        var incomingIds = incoming.Select(lobby => lobby.Id).ToHashSet();

        for (var i = node.Rooms.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(node.Rooms[i].Id))
            {
                if (SelectedRoom == node.Rooms[i]) SelectedRoom = null;
                node.Rooms.RemoveAt(i);
            }
        }

        foreach (var lobby in incoming)
        {
            var room = node.Rooms.FirstOrDefault(item => item.Id == lobby.Id);
            if (room == null)
            {
                room = new EnsembleLobbyRoom { Node = node, Id = lobby.Id };
                node.Rooms.Add(room);
            }

            ApplyRoom(room, lobby);
        }

        StatusText = $"已同步 {Nodes.Sum(item => item.Rooms.Count)} 个房间";
        OnPropertyChanged(nameof(CanSendPhrase));
    }

    private static void ApplyRoom(EnsembleLobbyRoom room, MdtLobbyEntry lobby)
    {
        room.Revision = lobby.Revision;
        room.Name = lobby.Name ?? "";
        room.HostUid = lobby.HostUid ?? "";
        room.HostName = lobby.HostName ?? "";
        room.MaxPlayers = lobby.MaxPlayers;
        room.PlayerCount = lobby.Players?.Length ?? 0;
        room.PlaylistSize = lobby.PlaylistSize;
        room.PlaylistCount = lobby.PlaylistCount;
        room.IsPrivate = lobby.IsPrivate;
        room.JoinLocked = lobby.JoinLocked;
        room.Locked = lobby.Locked;
        room.IsPlaying = lobby.IsPlaying;
        room.WatcherCount = lobby.WatcherCount;
        room.CurrentBattleEntry = FormatEntry(lobby.CurrentBattleEntry);
        MergePlayers(room, lobby.Players ?? Array.Empty<MdtLobbyPlayerEntry>());
        MergeChats(room, lobby.Chats ?? Array.Empty<MdtChatMessageEntry>());
    }

    private static void MergePlayers(EnsembleLobbyRoom room, MdtLobbyPlayerEntry[] players)
    {
        var incomingUids = players.Select(player => player.Uid).ToHashSet();
        for (var i = room.Players.Count - 1; i >= 0; i--)
        {
            if (!incomingUids.Contains(room.Players[i].Uid))
            {
                room.Players.RemoveAt(i);
            }
        }

        foreach (var player in players)
        {
            var target = room.Players.FirstOrDefault(item => item.Uid == player.Uid);
            if (target == null)
            {
                target = new EnsembleLobbyPlayer { Uid = player.Uid ?? "" };
                room.Players.Add(target);
            }

            target.Name = player.Name ?? player.Uid ?? "";
            target.ChatColor = player.ChatColor ?? "";
            target.PingMS = player.PingMS;
            target.Ready = player.Ready;
            target.GirlIndex = player.GirlIndex;
            target.ElfinIndex = player.ElfinIndex;
            target.Battle = player.Battle;
        }
    }

    private static void MergeChats(EnsembleLobbyRoom room, IEnumerable<MdtChatMessageEntry> chats)
    {
        var next = chats
            .Where(chat => chat != null)
            .OrderBy(chat => chat.TimestampUnixMs)
            .Select(EnsembleLobbyChat.FromProtocol)
            .ToArray();

        room.Chats.Clear();
        foreach (var chat in next)
        {
            room.Chats.Add(chat);
        }
    }

    private static void AppendChat(EnsembleLobbyRoom room, MdtChatMessageEntry message)
    {
        if (message == null) return;

        var chat = EnsembleLobbyChat.FromProtocol(message);
        var exists = room.Chats.Any(item =>
            item.Time == chat.Time &&
            item.Source == chat.Source &&
            item.SenderName == chat.SenderName &&
            item.Message == chat.Message);

        if (exists) return;

        room.Chats.Add(chat);
        while (room.Chats.Count > 30)
        {
            room.Chats.RemoveAt(0);
        }
    }

    private EnsembleLobbyNode? FindNode(string nodeId)
    {
        return Nodes.FirstOrDefault(node => node.Id == nodeId || node.Address == nodeId);
    }

    private EnsembleLobbyRoom? FindRoom(string nodeId, int lobbyId)
    {
        return FindNode(nodeId)?.Rooms.FirstOrDefault(room => room.Id == lobbyId);
    }

    private void UpdateCooldownText()
    {
        var remain = _nextAllowedSendTime - DateTimeOffset.Now;
        CooldownText = remain.TotalMilliseconds <= 0
            ? ""
            : $"还需要等待 {Math.Ceiling(remain.TotalSeconds)} 秒";
        OnPropertyChanged(nameof(CanSendPhrase));
    }

    private void StartCooldownTimer()
    {
        _cooldownTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cooldownTimer.Tick -= OnCooldownTimerTick;
        _cooldownTimer.Tick += OnCooldownTimerTick;
        _cooldownTimer.Start();
    }

    private void OnCooldownTimerTick(object? sender, EventArgs e)
    {
        UpdateCooldownText();
        if (DateTimeOffset.Now >= _nextAllowedSendTime)
        {
            _cooldownTimer?.Stop();
        }
    }

    private static string NormalizeDisplayName(string value)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) text = "喵斯兔玩家";
        text = text.Replace("\r", "").Replace("\n", "");
        return text.Length > 5 ? text[..5] : text;
    }

    private static string FormatEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return "未选择歌曲";

        var parts = entry.Split('#');
        if (parts.Length < 4) return entry;

        return $"{parts[3]} #{parts[1]}";
    }

    public void Dispose()
    {
        _lobbyService.SnapshotReceived -= OnSnapshotReceived;
        _lobbyService.ChatReceived -= OnChatReceived;
        _lobbyService.NodeStatusChanged -= OnNodeStatusChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        if (_cooldownTimer != null)
        {
            _cooldownTimer.Stop();
            _cooldownTimer.Tick -= OnCooldownTimerTick;
        }

        _ = _lobbyService.DisconnectAllAsync();
    }
}
