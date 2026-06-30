using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MDEN.Protocol.Messages.Mdt;
using MDEN.Protocol.Models;
using MdModManager.Services;

namespace MdModManager.Models;

public partial class EnsembleLobbyNode : ObservableObject
{
    private static readonly IBrush NodeConnectedBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#66E38E"));
    private static readonly IBrush NodeConnectingBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFD166"));
    private static readonly IBrush NodeDisconnectedBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#FF6B8A"));
    private static readonly IBrush NodeSelectedBackgroundBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#28F25D9C"));
    private static readonly IBrush NodeDefaultBackgroundBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#12000000"));
    private static readonly IBrush NodeSelectedBorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#F25D9C"));
    private static readonly IBrush NodeDefaultBorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#3A2635"));
    private static readonly IBrush NodeSelectedTextBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFFFFFFF"));
    private static readonly IBrush NodeDefaultTextBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#F8D7EA"));

    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _statusText = I18nService.Instance["OnlineLobby_NodeDisconnected"];
    [ObservableProperty] private bool _isFallback;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ObservableCollection<EnsembleLobbyRoom> _rooms = new();

    public EnsembleLobbyNode()
    {
        _rooms.CollectionChanged += OnRoomsCollectionChanged;
    }

    public IBrush StatusBrush
    {
        get
        {
            if (IsConnected) return NodeConnectedBrush;
            if (StatusState is EnsembleLobbyNodeStatus.Connecting or EnsembleLobbyNodeStatus.Waiting) return NodeConnectingBrush;
            return NodeDisconnectedBrush;
        }
    }

    public bool HasRooms => Rooms.Count > 0;
    public string SourceText => IsFallback ? I18nService.Instance["OnlineLobby_FallbackNode"] : "";
    public string RoomCountText => string.Format(I18nService.Instance["OnlineLobby_RoomCount"], Rooms.Count);
    public IBrush TabBackgroundBrush => IsSelected ? NodeSelectedBackgroundBrush : NodeDefaultBackgroundBrush;
    public IBrush TabBorderBrush => IsSelected ? NodeSelectedBorderBrush : NodeDefaultBorderBrush;
    public IBrush TabTextBrush => IsSelected ? NodeSelectedTextBrush : NodeDefaultTextBrush;

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(StatusBrush));
    partial void OnIsFallbackChanged(bool value) => OnPropertyChanged(nameof(SourceText));
    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(StatusBrush));
    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(TabBackgroundBrush));
        OnPropertyChanged(nameof(TabBorderBrush));
        OnPropertyChanged(nameof(TabTextBrush));
    }

    partial void OnRoomsChanging(ObservableCollection<EnsembleLobbyRoom> value)
    {
        if (value != null)
        {
            value.CollectionChanged -= OnRoomsCollectionChanged;
        }
    }

    partial void OnRoomsChanged(ObservableCollection<EnsembleLobbyRoom> value)
    {
        if (value != null)
        {
            value.CollectionChanged += OnRoomsCollectionChanged;
        }

        NotifyRoomsSummaryChanged();
    }

    private void OnRoomsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyRoomsSummaryChanged();
    }

    private void NotifyRoomsSummaryChanged()
    {
        OnPropertyChanged(nameof(HasRooms));
        OnPropertyChanged(nameof(RoomCountText));
    }

    public EnsembleLobbyNodeStatus StatusState { get; set; } = EnsembleLobbyNodeStatus.Disconnected;

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(RoomCountText));
        OnPropertyChanged(nameof(StatusText));
    }
}

public enum EnsembleLobbyNodeStatus
{
    Waiting,
    Connecting,
    Connected,
    Disconnected,
    Failed
}

public partial class EnsembleLobbyRoom : ObservableObject
{
    private static readonly IBrush RoomWaitingBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#66E38E"));
    private static readonly IBrush RoomJoinLockedBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#9DB7FF"));
    private static readonly IBrush RoomReadyBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFD166"));
    private static readonly IBrush RoomPlayingBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#FF6B6B"));

    [ObservableProperty] private EnsembleLobbyNode? _node;
    [ObservableProperty] private int _id;
    [ObservableProperty] private long _revision;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _hostUid = "";
    [ObservableProperty] private string _hostName = "";
    [ObservableProperty] private int _playerCount;
    [ObservableProperty] private ushort _maxPlayers;
    [ObservableProperty] private ushort _playlistSize;
    [ObservableProperty] private int _playlistCount;
    [ObservableProperty] private bool _isPrivate;
    [ObservableProperty] private bool _joinLocked;
    [ObservableProperty] private bool _locked;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private byte _goal;
    [ObservableProperty] private int _watcherCount;
    [ObservableProperty] private string _currentBattleEntry = "";
    [ObservableProperty] private ObservableCollection<EnsembleLobbyPlayer> _players = new();
    [ObservableProperty] private ObservableCollection<EnsembleLobbyChat> _chats = new();
    [ObservableProperty] private ObservableCollection<EnsembleLobbyChat> _viewerChats = new();

    public string StatusText
    {
        get
        {
            if (IsPlaying) return I18nService.Instance["OnlineLobby_RoomPlaying"];
            if (Locked) return I18nService.Instance["OnlineLobby_RoomReady"];
            if (JoinLocked) return I18nService.Instance["OnlineLobby_RoomLocked"];
            return I18nService.Instance["OnlineLobby_RoomWaiting"];
        }
    }

    public string PrivacyText => IsPrivate ? I18nService.Instance["OnlineLobby_Private"] : I18nService.Instance["OnlineLobby_Public"];
    public string HostText => string.Format(I18nService.Instance["OnlineLobby_HostFormat"], HostName);
    public string PlaylistText => $"{PlaylistCount}/{PlaylistSize}";
    public string PlaylistLabelText => string.Format(I18nService.Instance["OnlineLobby_PlaylistFormat"], PlaylistText);
    public string PlayerCountText => $"{PlayerCount}/{MaxPlayers}";
    public string WatcherText => WatcherCount > 0
        ? string.Format(I18nService.Instance["OnlineLobby_WatcherCount"], WatcherCount)
        : I18nService.Instance["OnlineLobby_NoWatchers"];
    public bool CanSendChat => !IsPlaying;
    public IBrush StatusBrush
    {
        get
        {
            if (IsPlaying) return RoomPlayingBrush;
            if (Locked) return RoomReadyBrush;
            if (JoinLocked) return RoomJoinLockedBrush;
            return RoomWaitingBrush;
        }
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanSendChat));
        OnPropertyChanged(nameof(StatusBrush));
    }

    partial void OnLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    partial void OnJoinLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }
    partial void OnIsPrivateChanged(bool value) => OnPropertyChanged(nameof(PrivacyText));
    partial void OnHostNameChanged(string value) => OnPropertyChanged(nameof(HostText));
    partial void OnPlaylistCountChanged(int value)
    {
        OnPropertyChanged(nameof(PlaylistText));
        OnPropertyChanged(nameof(PlaylistLabelText));
    }
    partial void OnPlaylistSizeChanged(ushort value)
    {
        OnPropertyChanged(nameof(PlaylistText));
        OnPropertyChanged(nameof(PlaylistLabelText));
    }
    partial void OnPlayerCountChanged(int value) => OnPropertyChanged(nameof(PlayerCountText));
    partial void OnMaxPlayersChanged(ushort value) => OnPropertyChanged(nameof(PlayerCountText));
    partial void OnWatcherCountChanged(int value) => OnPropertyChanged(nameof(WatcherText));

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PrivacyText));
        OnPropertyChanged(nameof(HostText));
        OnPropertyChanged(nameof(PlaylistLabelText));
        OnPropertyChanged(nameof(WatcherText));
        foreach (var player in Players)
        {
            player.RefreshLocalizedText();
        }
    }
}

public partial class EnsembleLobbyPlayer : ObservableObject
{
    private static readonly IBrush ReadyTextBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#66E38E"));
    private static readonly IBrush NotReadyTextBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#A894A4"));
    private static readonly IBrush ReadyBackgroundBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#1F66E38E"));
    private static readonly IBrush NotReadyBackgroundBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#1CA894A4"));

    private static readonly string[] GirlNames =
    {
        "贝斯手凛",
        "问题少女凛",
        "梦游少女凛",
        "兔女郎凛",
        "飞行员布若",
        "偶像布若",
        "僵尸少女布若",
        "华服小丑布若",
        "提琴少女玛莉嘉",
        "女仆玛莉嘉",
        "魔法少女玛莉嘉",
        "小恶魔玛莉嘉",
        "黑衣少女玛莉嘉",
        "圣诞礼物凛",
        "制服少女布若",
        "领航员柚梅",
        "游戏主播NEKO#ΦωΦ",
        "打工战士凛",
        "红白巫女博丽灵梦",
        "重生的少女El_Clear",
        "修女玛莉嘉",
        "黑白魔法使雾雨魔理沙",
        "罗德岛的领导者阿米娅",
        "拳击手欧拉",
        "道士布若",
        "虚拟歌手初音未来",
        "虚拟歌手镜音铃·连",
        "摩托车手凛",
        "机械舞伶玛莉嘉",
        "萨卡兹的雇佣兵维什戴尔",
        "传奇神装布若",
        "夜勤血裔布若",
        "焚海魔盗凛",
        "潜水员布若",
        "made by Ora 2马莉嘉",
        "幽灵玛莉嘉"
    };

    private static readonly string[] ElfinNames =
    {
        "喵斯",
        "安吉拉",
        "塔纳托斯",
        "Rabot-233",
        "小护士",
        "小女巫",
        "小龙女",
        "莉莉丝",
        "佩奇医生",
        "静音灵",
        "霓虹彩蛋",
        "Beta狗"
    };

    [ObservableProperty] private string _uid = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _chatColor = "";
    [ObservableProperty] private ushort _pingMS;
    [ObservableProperty] private bool _ready;
    [ObservableProperty] private int _girlIndex;
    [ObservableProperty] private int _elfinIndex;
    [ObservableProperty] private BattlePlayerEntry? _battle;

    public string CharacterText => $"{ResolveGirlName(GirlIndex)} / {ResolveElfinName(ElfinIndex)}";
    public string ScoreText => Battle == null ? "-" : (Battle.Alive ? Battle.Score.ToString("N0") : I18nService.Instance["OnlineLobby_Down"]);
    public string AccuracyText => Battle == null ? "-" : $"{Battle.Accuracy:0.00}%";
    public string ReadyText => Ready ? I18nService.Instance["OnlineLobby_Ready"] : I18nService.Instance["OnlineLobby_NotReady"];
    public IBrush ReadyBrush => Ready ? ReadyTextBrush : NotReadyTextBrush;
    public IBrush ReadyBadgeBrush => Ready ? ReadyBackgroundBrush : NotReadyBackgroundBrush;
    public bool IsAp => Battle != null && Battle.FC && Math.Abs(Battle.Accuracy - 100f) < 0.005f;
    public bool IsFc => Battle?.FC == true;
    public string JudgeText => Battle == null
        ? "P 0 / G 0 / E 0 / L 0 / M 0"
        : $"P {Battle.Perfects} / G {Battle.Greats} / E {Battle.Earlies} / L {Battle.Lates} / M {Battle.Misses}";

    partial void OnGirlIndexChanged(int value) => OnPropertyChanged(nameof(CharacterText));
    partial void OnElfinIndexChanged(int value) => OnPropertyChanged(nameof(CharacterText));
    partial void OnReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(ReadyText));
        OnPropertyChanged(nameof(ReadyBrush));
        OnPropertyChanged(nameof(ReadyBadgeBrush));
    }

    partial void OnBattleChanged(BattlePlayerEntry? value)
    {
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(AccuracyText));
        OnPropertyChanged(nameof(IsAp));
        OnPropertyChanged(nameof(IsFc));
        OnPropertyChanged(nameof(JudgeText));
    }

    private static string ResolveGirlName(int index)
    {
        return index >= 0 && index < GirlNames.Length
            ? GirlNames[index]
            : string.Format(I18nService.Instance["OnlineLobby_UnknownGirl"], index);
    }

    private static string ResolveElfinName(int index)
    {
        return index >= 0 && index < ElfinNames.Length
            ? ElfinNames[index]
            : string.Format(I18nService.Instance["OnlineLobby_UnknownElfin"], index);
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(CharacterText));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(ReadyText));
    }
}

public partial class EnsembleLobbyChat : ObservableObject
{
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _senderName = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _color = "";
    [ObservableProperty] private bool _isHostReply;
    [ObservableProperty] private DateTimeOffset _time;

    public string DisplayText => string.IsNullOrWhiteSpace(SenderName)
        ? Message
        : $"{SenderName}：{Message}";

    public IBrush MessageBrush => CreateMessageBrush(Color);

    partial void OnSenderNameChanged(string value) => OnPropertyChanged(nameof(DisplayText));
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(DisplayText));
    partial void OnColorChanged(string value) => OnPropertyChanged(nameof(MessageBrush));

    public static EnsembleLobbyChat FromProtocol(MdtChatMessageEntry entry)
    {
        return new EnsembleLobbyChat
        {
            Source = entry?.Source ?? "",
            SenderName = entry?.SenderName ?? "",
            Message = entry?.Message ?? "",
            Color = entry?.Color ?? "",
            IsHostReply = entry?.IsHostReply ?? false,
            Time = DateTimeOffset.FromUnixTimeMilliseconds(entry?.TimestampUnixMs ?? 0)
        };
    }

    public static EnsembleLobbyChat FromViewerProtocol(MdtViewerChatMessageEntry entry)
    {
        return new EnsembleLobbyChat
        {
            Source = "viewer",
            SenderName = entry?.SenderName ?? "",
            Message = entry?.Message ?? "",
            Color = entry?.Color ?? "",
            IsHostReply = false,
            Time = DateTimeOffset.FromUnixTimeMilliseconds(entry?.TimestampUnixMs ?? 0)
        };
    }

    private static IBrush CreateMessageBrush(string color)
    {
        var value = (color ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return new SolidColorBrush(Avalonia.Media.Color.Parse("#F6EEF3"));
        }

        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        return Avalonia.Media.Color.TryParse(value, out var parsed)
            ? new SolidColorBrush(parsed)
            : new SolidColorBrush(Avalonia.Media.Color.Parse("#F6EEF3"));
    }
}
