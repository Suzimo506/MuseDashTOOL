using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MDEN.Protocol.Messages.Mdt;
using MDEN.Protocol.Models;

namespace MdModManager.Models;

public partial class EnsembleLobbyNode : ObservableObject
{
    private static readonly IBrush NodeConnectedBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#66E38E"));
    private static readonly IBrush NodeConnectingBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFD166"));
    private static readonly IBrush NodeDisconnectedBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#FF6B8A"));

    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _statusText = "未连接";
    [ObservableProperty] private bool _isConnected;
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
            if (StatusText.Contains("连接中") || StatusText.Contains("等待")) return NodeConnectingBrush;
            return NodeDisconnectedBrush;
        }
    }

    public bool HasRooms => Rooms.Count > 0;
    public string RoomCountText => $"{Rooms.Count} 房间";

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(StatusBrush));
    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(StatusBrush));

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
    [ObservableProperty] private int _watcherCount;
    [ObservableProperty] private string _currentBattleEntry = "";
    [ObservableProperty] private ObservableCollection<EnsembleLobbyPlayer> _players = new();
    [ObservableProperty] private ObservableCollection<EnsembleLobbyChat> _chats = new();

    public string StatusText
    {
        get
        {
            if (IsPlaying) return "游戏中";
            if (Locked) return "准备中";
            if (JoinLocked) return "已上锁";
            return "等待中";
        }
    }

    public string PrivacyText => IsPrivate ? "需要密码" : "公开";
    public string PlaylistText => $"{PlaylistCount}/{PlaylistSize}";
    public string PlayerCountText => $"{PlayerCount}/{MaxPlayers}";
    public string WatcherText => WatcherCount > 0 ? $"{WatcherCount} 人正在观看" : "暂无观看";
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
    partial void OnPlaylistCountChanged(int value) => OnPropertyChanged(nameof(PlaylistText));
    partial void OnPlaylistSizeChanged(ushort value) => OnPropertyChanged(nameof(PlaylistText));
    partial void OnPlayerCountChanged(int value) => OnPropertyChanged(nameof(PlayerCountText));
    partial void OnMaxPlayersChanged(ushort value) => OnPropertyChanged(nameof(PlayerCountText));
    partial void OnWatcherCountChanged(int value) => OnPropertyChanged(nameof(WatcherText));
}

public partial class EnsembleLobbyPlayer : ObservableObject
{
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
        "made by Ora 2马莉嘉"
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
    public string ScoreText => Battle == null ? "-" : Battle.Score.ToString("N0");
    public string AccuracyText => Battle == null ? "-" : $"{Battle.Accuracy:0.00}%";
    public bool IsAp => Battle != null && Battle.FC && Math.Abs(Battle.Accuracy - 100f) < 0.005f;
    public bool IsFc => Battle?.FC == true;
    public string JudgeText => Battle == null
        ? "P 0 / G 0 / E 0 / L 0 / M 0"
        : $"P {Battle.Perfects} / G {Battle.Greats} / E {Battle.Earlies} / L {Battle.Lates} / M {Battle.Misses}";

    partial void OnGirlIndexChanged(int value) => OnPropertyChanged(nameof(CharacterText));
    partial void OnElfinIndexChanged(int value) => OnPropertyChanged(nameof(CharacterText));

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
            : $"未知角色 {index}";
    }

    private static string ResolveElfinName(int index)
    {
        return index >= 0 && index < ElfinNames.Length
            ? ElfinNames[index]
            : $"未知精灵 {index}";
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
