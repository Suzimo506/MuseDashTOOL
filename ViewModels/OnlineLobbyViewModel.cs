using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MDEN.Protocol;
using MDEN.Protocol.Enums;
using MDEN.Protocol.Messages.Mdt;
using MdModManager.Models;
using MdModManager.Services;

namespace MdModManager.ViewModels;

public partial class OnlineLobbyViewModel : ViewModelBase, IDisposable
{
    private const int MaxViewerChatLength = 80;
    private const int MaxMdtChatCount = 30;
    private const int MaxViewerChatCount = 50;
    private const string ChatHistoryClearWarning = "聊天记录即将清除...";
    private static readonly TimeSpan ChatHistoryLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ChatHistoryWarningLeadTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChatHistoryCleanupTick = TimeSpan.FromSeconds(1);

    private static readonly string[] FixedPhrases =
    {
        "龙币们放我进去好吗",
        "我也想进房间玩~",
        "可以把密码告诉我吗我在喵斯兔等你",
        "敢不敢让我进去把你们全超了？"
    };

    private readonly IEnsembleLobbyService _lobbyService;
    private readonly INotificationService _notificationService;
    private readonly INavigationService _navigationService;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _cooldownTimer;
    private DispatcherTimer? _viewerChatCooldownTimer;
    private DispatcherTimer? _chatHistoryCleanupTimer;
    private readonly Dictionary<string, ChatHistoryState> _chatHistoryStates = new();
    private string? _watchingNodeId;
    private int? _watchingLobbyId;
    private string _museDashUid = string.Empty;
    private DateTimeOffset _nextAllowedSendTime = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAllowedViewerChatTime = DateTimeOffset.MinValue;

    [ObservableProperty] private ObservableCollection<EnsembleLobbyNode> _nodes = new();
    [ObservableProperty] private EnsembleLobbyNode? _selectedNode;
    [ObservableProperty] private EnsembleLobbyRoom? _selectedRoom;
    [ObservableProperty] private string _displayName = "喵斯兔玩家";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "未连接";
    [ObservableProperty] private string _cooldownText = "";
    [ObservableProperty] private string _viewerChatText = "";
    [ObservableProperty] private string _viewerChatCooldownText = "";

    public IReadOnlyList<string> Phrases => FixedPhrases;
    public bool HasSelectedNode => SelectedNode != null;
    public bool SelectedNodeHasRooms => SelectedNode?.HasRooms == true;
    public bool HasSelectedRoom => SelectedRoom != null;
    public bool IsUsingFallbackNodes => Nodes.Any(node => node.IsFallback);
    public bool HasMdtIdentity => !string.IsNullOrWhiteSpace(_museDashUid);
    public bool CanSendPhrase => HasMdtIdentity && SelectedRoom?.CanSendChat == true && DateTimeOffset.Now >= _nextAllowedSendTime;
    public bool CanSendViewerChat =>
        HasMdtIdentity &&
        SelectedRoom?.Node != null &&
        !string.IsNullOrWhiteSpace(NormalizeViewerChatText(ViewerChatText)) &&
        NormalizeViewerChatText(ViewerChatText).Length <= MaxViewerChatLength &&
        DateTimeOffset.Now >= _nextAllowedViewerChatTime;
    public string ViewerChatLengthText => $"{NormalizeViewerChatText(ViewerChatText).Length}/{MaxViewerChatLength}";
    public bool HasViewerChatCooldownText => !string.IsNullOrWhiteSpace(ViewerChatCooldownText);

    public OnlineLobbyViewModel(
        IEnsembleLobbyService lobbyService,
        INotificationService notificationService,
        INavigationService navigationService)
    {
        _lobbyService = lobbyService;
        _notificationService = notificationService;
        _navigationService = navigationService;
        RefreshMdtIdentity();

        _lobbyService.SnapshotReceived += OnSnapshotReceived;
        _lobbyService.ChatReceived += OnChatReceived;
        _lobbyService.ViewerChatReceived += OnViewerChatReceived;
        _lobbyService.NodeStatusChanged += OnNodeStatusChanged;
        StartChatHistoryCleanupTimer();
    }

    public async Task InitializeAsync(CancellationToken externalToken)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        IsLoading = true;
        StatusText = "正在连接节点";
        RefreshMdtIdentity();

        try
        {
            var nodes = await _lobbyService.GetNodesAsync(_cts.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SelectedRoom = null;
                SelectedNode = null;
                _watchingNodeId = null;
                _watchingLobbyId = null;
                _chatHistoryStates.Clear();
                Nodes.Clear();
                foreach (var node in nodes)
                {
                    Nodes.Add(new EnsembleLobbyNode
                    {
                        Id = string.IsNullOrWhiteSpace(node.Id) ? node.Address : node.Id,
                        Name = string.IsNullOrWhiteSpace(node.Name) ? node.Address : node.Name,
                        Address = node.Address,
                        IsFallback = node.IsFallback,
                        StatusText = "等待连接"
                    });
                }

                SelectedNode = Nodes.FirstOrDefault();
                UpdateLobbyStatusText();
            });

            foreach (var node in nodes)
            {
                _ = _lobbyService.ConnectAsync(node, _museDashUid, _cts.Token);
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
        SelectedNode = null;
        _watchingNodeId = null;
        _watchingLobbyId = null;
        await _lobbyService.DisconnectAllAsync();
        await InitializeAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void SelectNode(EnsembleLobbyNode node)
    {
        if (node == null) return;
        SelectedNode = node;
        if (SelectedRoom?.Node != node)
        {
            SelectedRoom = null;
        }
    }

    [RelayCommand]
    private void SelectRoom(EnsembleLobbyRoom room)
    {
        if (room?.Node != null && SelectedNode != room.Node)
        {
            SelectedNode = room.Node;
        }

        SelectedRoom = room;
    }

    [RelayCommand]
    private void OpenOnlineModDownload()
    {
        _navigationService.RequestNavigateToModDownload("Ensemble");
        _notificationService.ShowInfo("已打开联机模组下载项");
    }

    [RelayCommand]
    private async Task SendPhraseAsync(string phrase)
    {
        var room = SelectedRoom;
        var node = room?.Node;
        if (room == null || node == null || string.IsNullOrWhiteSpace(phrase)) return;

        if (!room.CanSendChat)
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
            await _lobbyService.WatchLobbyAsync(
                node.Id,
                room.Id,
                _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

            var response = await _lobbyService.SendPhraseAsync(
                node.Id,
                room.Id,
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

    [RelayCommand]
    private async Task SendViewerChatAsync()
    {
        var room = SelectedRoom;
        var node = room?.Node;
        if (room == null || node == null) return;

        var message = NormalizeViewerChatText(ViewerChatText);
        if (string.IsNullOrWhiteSpace(message))
        {
            _notificationService.ShowFailure("发送失败", "消息不能为空");
            return;
        }

        if (message.Length > MaxViewerChatLength)
        {
            _notificationService.ShowFailure("发送失败", $"消息最多 {MaxViewerChatLength} 个字");
            return;
        }

        if (DateTimeOffset.Now < _nextAllowedViewerChatTime)
        {
            UpdateViewerChatCooldownText();
            _notificationService.ShowFailure("发送失败", ViewerChatCooldownText);
            return;
        }

        try
        {
            await _lobbyService.WatchLobbyAsync(
                node.Id,
                room.Id,
                _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

            var response = await _lobbyService.SendViewerChatAsync(
                node.Id,
                room.Id,
                message,
                _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _nextAllowedViewerChatTime = DateTimeOffset.FromUnixTimeMilliseconds(response.NextAllowedUnixMs);
                ViewerChatText = "";
                UpdateViewerChatCooldownText();
                StartViewerChatCooldownTimer();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                _notificationService.ShowFailure("发送失败", ex.Message));
        }
    }

    partial void OnSelectedNodeChanged(EnsembleLobbyNode? value)
    {
        foreach (var node in Nodes)
        {
            node.IsSelected = node == value;
        }

        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(SelectedNodeHasRooms));
    }

    partial void OnSelectedRoomChanged(EnsembleLobbyRoom? value)
    {
        OnPropertyChanged(nameof(HasSelectedRoom));
        OnPropertyChanged(nameof(CanSendPhrase));
        OnPropertyChanged(nameof(CanSendViewerChat));
        _ = ReportWatchingRoomAsync(value);
    }

    partial void OnViewerChatTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanSendViewerChat));
        OnPropertyChanged(nameof(ViewerChatLengthText));
    }

    partial void OnViewerChatCooldownTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasViewerChatCooldownText));
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

    private void OnViewerChatReceived(string nodeId, int lobbyId, MdtViewerChatMessageEntry message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var room = FindRoom(nodeId, lobbyId);
            if (room == null) return;

            AppendViewerChat(room, message);
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
            UpdateLobbyStatusText();
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
                var removedRoom = node.Rooms[i];
                if (SelectedRoom == removedRoom) SelectedRoom = null;
                RemoveChatHistoryState(removedRoom);
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

        if (SelectedNode == null || !Nodes.Contains(SelectedNode))
        {
            SelectedNode = Nodes.FirstOrDefault();
        }

        UpdateLobbyStatusText();
        OnPropertyChanged(nameof(SelectedNodeHasRooms));
        OnPropertyChanged(nameof(CanSendPhrase));
        OnPropertyChanged(nameof(CanSendViewerChat));
    }

    private void UpdateLobbyStatusText()
    {
        var nodeCount = Nodes.Count;
        OnPropertyChanged(nameof(IsUsingFallbackNodes));
        if (nodeCount == 0)
        {
            StatusText = "没有可用节点";
            return;
        }

        var fallbackPrefix = IsUsingFallbackNodes ? "API 获取失败，正在显示内置兜底节点。" : "";
        var roomCount = Nodes.Sum(item => item.Rooms.Count);
        if (roomCount > 0)
        {
            StatusText = $"{fallbackPrefix}已同步 {roomCount} 个房间";
            return;
        }

        var connectedCount = Nodes.Count(item => item.IsConnected);
        if (connectedCount > 0)
        {
            StatusText = $"{fallbackPrefix}已连接 {connectedCount}/{nodeCount} 个节点，暂无房间";
            return;
        }

        var connectingCount = Nodes.Count(IsConnectingNode);
        if (connectingCount > 0)
        {
            StatusText = $"{fallbackPrefix}正在连接节点 {connectingCount}/{nodeCount}";
            return;
        }

        var failedCount = Nodes.Count(IsFailedNode);
        StatusText = failedCount > 0
            ? $"{fallbackPrefix}暂无可用节点，{failedCount}/{nodeCount} 个连接失败"
            : $"{fallbackPrefix}发现 {nodeCount} 个节点";
    }

    private static bool IsConnectingNode(EnsembleLobbyNode node)
    {
        var status = node.StatusText ?? "";
        return status.Contains("连接中") || status.Contains("等待");
    }

    private static bool IsFailedNode(EnsembleLobbyNode node)
    {
        return (node.StatusText ?? "").Contains("连接失败");
    }

    private void ApplyRoom(EnsembleLobbyRoom room, MdtLobbyEntry lobby)
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
        room.Goal = lobby.Goal;
        room.WatcherCount = lobby.WatcherCount;
        room.CurrentBattleEntry = FormatEntry(lobby.CurrentBattleEntry);
        MergePlayers(room, lobby.Players ?? Array.Empty<MdtLobbyPlayerEntry>());
        MergeChats(room, lobby.Chats ?? Array.Empty<MdtChatMessageEntry>());
        MergeViewerChats(room, lobby.ViewerChats ?? Array.Empty<MdtViewerChatMessageEntry>());
    }

    private static void MergePlayers(EnsembleLobbyRoom room, MdtLobbyPlayerEntry[] players)
    {
        var incoming = players
            .Where(player => player != null && !string.IsNullOrWhiteSpace(player.Uid))
            .ToArray();
        var incomingUids = incoming.Select(player => player.Uid).ToHashSet();
        for (var i = room.Players.Count - 1; i >= 0; i--)
        {
            if (!incomingUids.Contains(room.Players[i].Uid))
            {
                room.Players.RemoveAt(i);
            }
        }

        foreach (var player in incoming)
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

        ReorderPlayers(room, GetOrderedPlayerUids(room, incoming));
    }

    private static string[] GetOrderedPlayerUids(EnsembleLobbyRoom room, MdtLobbyPlayerEntry[] players)
    {
        IEnumerable<MdtLobbyPlayerEntry> ordered = players;
        if (room.IsPlaying)
        {
            ordered = (LobbyGoal)room.Goal == LobbyGoal.Score
                ? players
                    .OrderBy(player => player.Battle == null)
                    .ThenByDescending(player => player.Battle?.Alive ?? true)
                    .ThenByDescending(player => player.Battle?.Score ?? 0u)
                    .ThenByDescending(player => player.Battle?.Accuracy ?? 0f)
                    .ThenBy(player => player.Name ?? player.Uid ?? "")
                : players
                    .OrderBy(player => player.Battle == null)
                    .ThenByDescending(player => player.Battle?.Alive ?? true)
                    .ThenByDescending(player => player.Battle?.Accuracy ?? 0f)
                    .ThenByDescending(player => player.Battle?.Score ?? 0u)
                    .ThenBy(player => player.Name ?? player.Uid ?? "");
        }

        return ordered
            .Select(player => player.Uid ?? "")
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .ToArray();
    }

    private static void ReorderPlayers(EnsembleLobbyRoom room, string[] orderedUids)
    {
        for (var targetIndex = 0; targetIndex < orderedUids.Length; targetIndex++)
        {
            var currentIndex = -1;
            for (var i = targetIndex; i < room.Players.Count; i++)
            {
                if (room.Players[i].Uid == orderedUids[targetIndex])
                {
                    currentIndex = i;
                    break;
                }
            }

            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                room.Players.Move(currentIndex, targetIndex);
            }
        }
    }

    private void MergeChats(EnsembleLobbyRoom room, IEnumerable<MdtChatMessageEntry> chats)
    {
        var state = GetChatHistoryState(room, ChatHistoryKind.Mdt);
        var next = chats
            .Where(chat => chat != null)
            .Where(chat => DateTimeOffset.FromUnixTimeMilliseconds(chat.TimestampUnixMs) > state.ClearedAt)
            .OrderBy(chat => chat.TimestampUnixMs)
            .Select(EnsembleLobbyChat.FromProtocol)
            .ToArray();

        var shouldResetTimer = room.Chats.Count == 0 && next.Length > 0;
        room.Chats.Clear();
        if (shouldResetTimer)
        {
            ResetChatHistoryState(room, ChatHistoryKind.Mdt);
        }

        foreach (var chat in next)
        {
            room.Chats.Add(chat);
        }
    }

    private void MergeViewerChats(EnsembleLobbyRoom room, IEnumerable<MdtViewerChatMessageEntry> chats)
    {
        var state = GetChatHistoryState(room, ChatHistoryKind.Viewer);
        var next = chats
            .Where(chat => chat != null)
            .Where(chat => DateTimeOffset.FromUnixTimeMilliseconds(chat.TimestampUnixMs) > state.ClearedAt)
            .OrderBy(chat => chat.TimestampUnixMs)
            .Select(EnsembleLobbyChat.FromViewerProtocol)
            .ToArray();

        var shouldResetTimer = room.ViewerChats.Count == 0 && next.Length > 0;
        room.ViewerChats.Clear();
        if (shouldResetTimer)
        {
            ResetChatHistoryState(room, ChatHistoryKind.Viewer);
        }

        foreach (var chat in next)
        {
            room.ViewerChats.Add(chat);
        }
    }

    private void AppendChat(EnsembleLobbyRoom room, MdtChatMessageEntry message)
    {
        if (message == null) return;

        var state = GetChatHistoryState(room, ChatHistoryKind.Mdt);
        if (DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs) <= state.ClearedAt) return;

        var chat = EnsembleLobbyChat.FromProtocol(message);
        var exists = room.Chats.Any(item =>
            item.Time == chat.Time &&
            item.Source == chat.Source &&
            item.SenderName == chat.SenderName &&
            item.Message == chat.Message);

        if (exists) return;

        ResetChatHistoryStateIfNeeded(room, ChatHistoryKind.Mdt, room.Chats);
        room.Chats.Add(chat);
        while (room.Chats.Count > MaxMdtChatCount)
        {
            room.Chats.RemoveAt(0);
        }
    }

    private void AppendViewerChat(EnsembleLobbyRoom room, MdtViewerChatMessageEntry message)
    {
        if (message == null) return;

        var state = GetChatHistoryState(room, ChatHistoryKind.Viewer);
        if (DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs) <= state.ClearedAt) return;

        var chat = EnsembleLobbyChat.FromViewerProtocol(message);
        var exists = room.ViewerChats.Any(item =>
            item.Time == chat.Time &&
            item.SenderName == chat.SenderName &&
            item.Message == chat.Message);

        if (exists) return;

        ResetChatHistoryStateIfNeeded(room, ChatHistoryKind.Viewer, room.ViewerChats);
        room.ViewerChats.Add(chat);
        while (room.ViewerChats.Count > MaxViewerChatCount)
        {
            room.ViewerChats.RemoveAt(0);
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

    private void StartChatHistoryCleanupTimer()
    {
        _chatHistoryCleanupTimer ??= new DispatcherTimer { Interval = ChatHistoryCleanupTick };
        _chatHistoryCleanupTimer.Tick -= OnChatHistoryCleanupTimerTick;
        _chatHistoryCleanupTimer.Tick += OnChatHistoryCleanupTimerTick;
        _chatHistoryCleanupTimer.Start();
    }

    private void OnChatHistoryCleanupTimerTick(object? sender, EventArgs e)
    {
        foreach (var node in Nodes)
        {
            foreach (var room in node.Rooms)
            {
                UpdateChatHistory(room, ChatHistoryKind.Mdt, room.Chats);
                UpdateChatHistory(room, ChatHistoryKind.Viewer, room.ViewerChats);
            }
        }
    }

    private void UpdateChatHistory(
        EnsembleLobbyRoom room,
        ChatHistoryKind kind,
        ObservableCollection<EnsembleLobbyChat> chats)
    {
        if (room == null || chats.Count == 0) return;

        var state = GetChatHistoryState(room, kind);
        var now = DateTimeOffset.Now;
        if (now >= state.ClearAt)
        {
            chats.Clear();
            state.ClearedAt = now;
            state.ClearAt = now + ChatHistoryLifetime;
            state.WarningShown = false;
            return;
        }

        if (!state.WarningShown && now >= state.ClearAt - ChatHistoryWarningLeadTime)
        {
            state.WarningShown = true;
            chats.Add(CreateSystemChat(ChatHistoryClearWarning));
        }
    }

    private void ResetChatHistoryStateIfNeeded(
        EnsembleLobbyRoom room,
        ChatHistoryKind kind,
        ObservableCollection<EnsembleLobbyChat> chats)
    {
        if (chats.Count > 0) return;

        ResetChatHistoryState(room, kind);
    }

    private void ResetChatHistoryState(EnsembleLobbyRoom room, ChatHistoryKind kind)
    {
        var state = GetChatHistoryState(room, kind);
        state.ClearAt = DateTimeOffset.Now + ChatHistoryLifetime;
        state.WarningShown = false;
    }

    private ChatHistoryState GetChatHistoryState(EnsembleLobbyRoom room, ChatHistoryKind kind)
    {
        var key = GetChatHistoryKey(room, kind);
        if (!_chatHistoryStates.TryGetValue(key, out var state))
        {
            state = new ChatHistoryState
            {
                ClearAt = DateTimeOffset.Now + ChatHistoryLifetime,
                ClearedAt = DateTimeOffset.MinValue
            };
            _chatHistoryStates[key] = state;
        }

        return state;
    }

    private static string GetChatHistoryKey(EnsembleLobbyRoom room, ChatHistoryKind kind)
    {
        return $"{room.Node?.Id ?? room.Node?.Address ?? "unknown"}:{room.Id}:{kind}";
    }

    private void RemoveChatHistoryState(EnsembleLobbyRoom room)
    {
        _chatHistoryStates.Remove(GetChatHistoryKey(room, ChatHistoryKind.Mdt));
        _chatHistoryStates.Remove(GetChatHistoryKey(room, ChatHistoryKind.Viewer));
    }

    private static EnsembleLobbyChat CreateSystemChat(string message)
    {
        return new EnsembleLobbyChat
        {
            Source = "system",
            SenderName = "",
            Message = message,
            Color = "FFD166",
            Time = DateTimeOffset.Now
        };
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

    private void UpdateViewerChatCooldownText()
    {
        var remain = _nextAllowedViewerChatTime - DateTimeOffset.Now;
        ViewerChatCooldownText = remain.TotalMilliseconds <= 0
            ? ""
            : $"还需要等待 {Math.Ceiling(remain.TotalSeconds)} 秒";
        OnPropertyChanged(nameof(CanSendViewerChat));
    }

    private void StartViewerChatCooldownTimer()
    {
        _viewerChatCooldownTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _viewerChatCooldownTimer.Tick -= OnViewerChatCooldownTimerTick;
        _viewerChatCooldownTimer.Tick += OnViewerChatCooldownTimerTick;
        _viewerChatCooldownTimer.Start();
    }

    private void OnViewerChatCooldownTimerTick(object? sender, EventArgs e)
    {
        UpdateViewerChatCooldownText();
        if (DateTimeOffset.Now >= _nextAllowedViewerChatTime)
        {
            _viewerChatCooldownTimer?.Stop();
        }
    }

    private void RefreshMdtIdentity()
    {
        _museDashUid = ResolveMuseDashUid();
        DisplayName = string.IsNullOrWhiteSpace(_museDashUid)
            ? "未登录"
            : MdtIdentity.GenerateNameFromUid(_museDashUid);
        OnPropertyChanged(nameof(HasMdtIdentity));
        OnPropertyChanged(nameof(CanSendPhrase));
        OnPropertyChanged(nameof(CanSendViewerChat));
    }

    private static string ResolveMuseDashUid()
    {
        var uid = MuseDashAccountService.CachedAccountInfo?.Uid;
        if (string.IsNullOrWhiteSpace(uid))
        {
            uid = MuseDashAccountService.ReadAccountInfo()?.Uid;
        }

        var normalizedUid = MdtIdentity.NormalizeUid(uid);
        return normalizedUid.Length > MdtIdentity.MaxUidLength ? string.Empty : normalizedUid;
    }

    private static string NormalizeViewerChatText(string value)
    {
        return (value ?? "")
            .Trim()
            .Replace("\r", "")
            .Replace("\n", "");
    }

    private static string FormatEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return "未选择歌曲";

        var parts = entry.Split('#', 4);
        if (parts.Length < 4) return entry;

        return $"{DecodeEntryPart(parts[3])} #{parts[1]}";
    }

    private static string DecodeEntryPart(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        const string prefix = "__mden_uri__";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return value;

        try
        {
            return Uri.UnescapeDataString(value[prefix.Length..]);
        }
        catch
        {
            return value;
        }
    }

    public void Dispose()
    {
        _lobbyService.SnapshotReceived -= OnSnapshotReceived;
        _lobbyService.ChatReceived -= OnChatReceived;
        _lobbyService.ViewerChatReceived -= OnViewerChatReceived;
        _lobbyService.NodeStatusChanged -= OnNodeStatusChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        if (_cooldownTimer != null)
        {
            _cooldownTimer.Stop();
            _cooldownTimer.Tick -= OnCooldownTimerTick;
        }

        if (_viewerChatCooldownTimer != null)
        {
            _viewerChatCooldownTimer.Stop();
            _viewerChatCooldownTimer.Tick -= OnViewerChatCooldownTimerTick;
        }

        if (_chatHistoryCleanupTimer != null)
        {
            _chatHistoryCleanupTimer.Stop();
            _chatHistoryCleanupTimer.Tick -= OnChatHistoryCleanupTimerTick;
        }

        _ = _lobbyService.DisconnectAllAsync();
    }

    private enum ChatHistoryKind
    {
        Mdt,
        Viewer
    }

    private sealed class ChatHistoryState
    {
        public DateTimeOffset ClearAt { get; set; }
        public DateTimeOffset ClearedAt { get; set; }
        public bool WarningShown { get; set; }
    }
}
