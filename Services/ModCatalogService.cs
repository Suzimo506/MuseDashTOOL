using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IModCatalogService
{
    Task<List<ModInfo>> GetModsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

public class ModCatalogService : IModCatalogService
{
    private readonly IConfigService _configService;
    private readonly HttpClient _httpClient;
    private List<ModInfo>? _cachedMods;

    public ModCatalogService(IConfigService configService)
    {
        _configService = configService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MdModManager/1.0");
    }

    /// <summary>
    /// 获取 Mod 列表。当前使用硬编码的 Euterpe 静态数据。
    /// 预留给未来固定的获取地址。
    /// </summary>
    public async Task<List<ModInfo>> GetModsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cachedMods != null) return _cachedMods;

        try
        {
            string jsonData = StaticJsonData;
            try
            {
                using var client = MdModManager.Helpers.HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(10));
                var response = await client.GetStringAsync("https://workdl.suzimo.site/mods.json", cancellationToken);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    jsonData = response;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModCatalogService] 远程获取 JSON 失败: {ex.Message}，使用本地缓存");
            }

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = JsonSerializer.Deserialize<List<EuterpeModEntry>>(jsonData, opts);
            if (list == null) return new List<ModInfo>();

            _cachedMods = list.Select(e => new ModInfo
            {
                Name = e.Name ?? "",
                Version = e.CurrentVersion ?? "",
                Author = e.Author ?? "",
                FileName = (e.Name ?? "") + ".dll",
                Description = e.Description ?? "",
                GameVersion = e.GameVersion ?? "*",
                Source = "Euterpe"
            }).ToList();

            return _cachedMods;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModCatalogService] 解析异常: {ex.Message}");
            return new List<ModInfo>();
        }
    }

    // Euterpe JSON 条目的反序列化辅助类
    private class EuterpeModEntry
    {
        [JsonPropertyName("mid")] public int Mid { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("game_version")] public string? GameVersion { get; set; }
        [JsonPropertyName("current_version")] public string? CurrentVersion { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    }

    private const string StaticJsonData = @"[
{""mid"":20,""name"":""CustomAlbums"",""author"":""Two Fellas"",""game_version"":""*"",""current_version"":""4.1.9"",""description"":""自制谱加载核心框架。安装此模组后才能在游戏中游玩来自 Custom_Albums 文件夹的 .mdm 自制谱文件。""},
{""mid"":17,""name"":""Cinema"",""author"":""AshtonMemer"",""game_version"":""*"",""current_version"":""1.2.1"",""description"":""在游戏中播放自制谱的自定义背景视频。需要 CustomAlbums 前置。""},
{""mid"":13,""name"":""CharacterScoreboard"",""author"":""Creepler13 & lxy"",""game_version"":""*"",""current_version"":""2.0.1"",""description"":""在选歌界面的排行榜上，将玩家昵称替换为该分数记录所使用的角色 + 精灵名称。""},
{""mid"":1,""name"":""AccuracyDisplay"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""在游戏内的屏幕左上角实时显示准度百分比。""},
{""mid"":64,""name"":""VictoryScreenSwitcher"",""author"":""Ultra_Rabbit"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""允许在设置中将结算界面的样式切换为 DJMAX 风格或明日方舟风格。""},
{""mid"":11,""name"":""BPMDisplay"",""author"":""lxy"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""在预备界面的谱师文本上方显示当前曲目的 BPM 信息。""},
{""mid"":63,""name"":""UnlockAll"",""author"":""thegamemaster1234"",""game_version"":""*"",""current_version"":""2.1.0"",""description"":""在安装期间解锁所有歌曲内容和 Master 难度。""},
{""mid"":45,""name"":""PopupLib"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""前置库模组，为其它模组提供游戏内置窗口样式的弹窗功能。""},
{""mid"":41,""name"":""MuseDashMirror"",""author"":""lxy"",""game_version"":""*"",""current_version"":""3.1.3"",""description"":""前置库模组，为其它模组提供便捷的 PlayerData 访问等工具。""},
{""mid"":59,""name"":""True rank for 999+"",""author"":""bnfour"",""game_version"":""*"",""current_version"":""2.0.1"",""description"":""将排行榜中显示为 999+ 的排名替换为实际数字。""},
{""mid"":52,""name"":""Scoreboard characters and elfins"",""author"":""bnfour"",""game_version"":""*"",""current_version"":""3.6.0"",""description"":""以图标形式在排行榜每条记录旁显示所用角色和精灵。""},
{""mid"":33,""name"":""Info+"",""author"":""KARPED1EM"",""game_version"":""*"",""current_version"":""3.0.3"",""description"":""高度客制化的游戏模组，用于显示额外的游戏信息。""},
{""mid"":31,""name"":""Headquarters"",""author"":""AshtonMemer"",""game_version"":""*"",""current_version"":""4.1.0"",""description"":""允许将自制谱的游玩成绩提交到 MDMC 网站的排行榜。""},
{""mid"":9,""name"":""BestCombinationSuggest"",""author"":""brooke_zb"",""game_version"":""*"",""current_version"":""1.0.0"",""description"":""在暂停界面显示当前关卡的最佳角色+精灵得分组合。""},
{""mid"":8,""name"":""BALLCOCK"",""author"":""AshtonMemer"",""game_version"":""*"",""current_version"":""1.0.0"",""description"":""简化 Fever 激活时的背景特效，隐藏 Fever 背景精灵。""},
{""mid"":4,""name"":""AltTabMute"",""author"":""Dom Gintoki"",""game_version"":""*"",""current_version"":""3.0.0"",""description"":""当游戏窗口失焦时自动将游戏静音。""},
{""mid"":57,""name"":""Song info"",""author"":""bnfour"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""在预备界面显示当前选中曲目的 BPM 和时长信息。""},
{""mid"":56,""name"":""SongDesc"",""author"":""lxy"",""game_version"":""*"",""current_version"":""2.0.2"",""description"":""在游戏中的屏幕右上角显示当前曲目名称和作曲者信息。""},
{""mid"":44,""name"":""NoLevelUpAnimations"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""加速或跳过关卡结算后的升级动画、解锁动画。""},
{""mid"":37,""name"":""LocalizeLib"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""前置库模组，为其它模组提供多语言文本工具。""},
{""mid"":35,""name"":""KeybindManager"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""3.0.0"",""description"":""前置库模组，为其它模组提供可配置快捷键功能。""},
{""mid"":34,""name"":""IntroSkip"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""4.0.0"",""description"":""大幅加速游戏启动序列。""},
{""mid"":32,""name"":""HiddenQol"",""author"":""RobotLucca & lxy & Asgragrt"",""game_version"":""*"",""current_version"":""2.2.2"",""description"":""直接显示所有隐藏谱面，无需满足原本的解锁条件。""},
{""mid"":23,""name"":""CustomHitSound"",""author"":""lxy"",""game_version"":""*"",""current_version"":""2.1.0"",""description"":""允许使用自定义打击音效包替换游戏内的默认音效。""},
{""mid"":22,""name"":""CustomBGBrightness"",""author"":""lxy"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""允许将游戏设置中的背景亮度滑块降低到 0%。""},
{""mid"":16,""name"":""ChineseMode"",""author"":""RobotLucca"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""强制游戏使用中国区内容审查路径。""},
{""mid"":14,""name"":""ChartDeleter"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""批量删除或禁用自制谱。""},
{""mid"":3,""name"":""Album scroll"",""author"":""bnfour"",""game_version"":""*"",""current_version"":""2.0.3"",""description"":""按住 Shift 快速跳转曲包。""},
{""mid"":2,""name"":""AccuracyIndicator"",""author"":""Azn9 & lxy"",""game_version"":""*"",""current_version"":""2.0.2"",""description"":""添加准度可视化指示条。""},
{""mid"":61,""name"":""UI tweaks"",""author"":""bnfour"",""game_version"":""*"",""current_version"":""1.3.2"",""description"":""一系列小型 UI 改进的集合。""},
{""mid"":54,""name"":""SelectiveEffects"",""author"":""Asgragrt"",""game_version"":""*"",""current_version"":""1.2.1"",""description"":""允许精细控制游戏内各类视觉特效的开关。""},
{""mid"":49,""name"":""RomajiSongName"",""author"":""Asgragrt"",""game_version"":""*"",""current_version"":""1.0.2"",""description"":""将日语/中文歌曲标题替换为罗马字/拼音版本。""},
{""mid"":48,""name"":""QuickSwitchCombination"",""author"":""lxy"",""game_version"":""*"",""current_version"":""3.0.1"",""description"":""将角色+精灵组合绑定到快捷键一键切换。""},
{""mid"":47,""name"":""QuickRestart"",""author"":""AshtonMemer"",""game_version"":""*"",""current_version"":""2.2.0"",""description"":""快速重新开始或退出关卡。""},
{""mid"":46,""name"":""PreventLowAcc"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""禁止提交成绩模式，适合练习。""},
{""mid"":30,""name"":""FeverEffectDisable"",""author"":""lxy"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""完全禁用 Fever 激活时的背景视觉特效。""},
{""mid"":28,""name"":""FavGirl Cat Fix"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""修复 FavGirl 模组与猫咪皮肤选择冲突的 Bug。""},
{""mid"":27,""name"":""FavGirl"",""author"":""RobotLucca & AshtonMemer"",""game_version"":""*"",""current_version"":""2.4.8"",""description"":""允许收藏（点亮红心）角色和精灵的外观。""},
{""mid"":26,""name"":""FadeIn"",""author"":""Asgragrt"",""game_version"":""*"",""current_version"":""1.0.1"",""description"":""为 Note 添加渐入效果。""},
{""mid"":25,""name"":""CustomResolution"",""author"":""lxy & PBalint817"",""game_version"":""*"",""current_version"":""2.1.0"",""description"":""添加自定义分辨率选项。""},
{""mid"":24,""name"":""CustomLoadingScreens"",""author"":""Mr. Talk"",""game_version"":""*"",""current_version"":""1.1.1"",""description"":""允许使用自定义图片和提示语替换加载界面。""},
{""mid"":21,""name"":""CustomAnchorSupport"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""让自制谱支持游戏的主播模式。""},
{""mid"":18,""name"":""CinemaToggler"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""1.0.0"",""description"":""快速开启或关闭 Cinema 背景视频。""},
{""mid"":15,""name"":""ChartReview"",""author"":""lxy"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""提供谱面预览模式，专注于谱面练习。""},
{""mid"":7,""name"":""AlwaysPigeons"",""author"":""lxy"",""game_version"":""*"",""current_version"":""1.0.1"",""description"":""将 Boss 射出的投射物替换为鸽子。""},
{""mid"":66,""name"":""Fever switch"",""author"":""bnfour"",""game_version"":""*"",""current_version"":""1.0.2"",""description"":""快速切换自动/手动 Fever 模式。""},
{""mid"":65,""name"":""Rank preview"",""author"":""bnfour"",""game_version"":""*"",""current_version"":""1.0.2"",""description"":""在结算界面显示一个预估排名。""},
{""mid"":62,""name"":""UltraInstinctSleepwalking"",""author"":""RobotLucca"",""game_version"":""*"",""current_version"":""3.0.0"",""description"":""优化梦游少女的自动连打触发。""},
{""mid"":58,""name"":""StricterJudge"",""author"":""lxy & Asgragrt"",""game_version"":""*"",""current_version"":""2.1.2"",""description"":""允许自定义 Perfect 和 Great 的判定窗口。""},
{""mid"":55,""name"":""ShopLift"",""author"":""Mr. Talk"",""game_version"":""*"",""current_version"":""1.0.0"",""description"":""隐藏选歌界面的 DLC 购买按钮。""},
{""mid"":50,""name"":""SaveMySpace"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""1.0.0"",""description"":""退出游戏时自动清理临时文件。""},
{""mid"":43,""name"":""NoAutoPause"",""author"":""AshtonMemer"",""game_version"":""*"",""current_version"":""2.1.0"",""description"":""阻止游戏在窗口失焦时自动暂停。""},
{""mid"":39,""name"":""MDRPC"",""author"":""Brasileiro"",""game_version"":""*"",""current_version"":""0.1.0"",""description"":""增强版 Discord Rich Presence。""},
{""mid"":38,""name"":""LockCursor"",""author"":""Mr. Talk"",""game_version"":""*"",""current_version"":""1.0.1"",""description"":""关卡进行时锁定鼠标光标。""},
{""mid"":36,""name"":""LeagueIsBad"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""1.0.0"",""description"":""整活：启动时检测 LOL 自动关机。""},
{""mid"":19,""name"":""CurrentCombination"",""author"":""Asgragrt"",""game_version"":""*"",""current_version"":""1.2.3"",""description"":""显示当前选定的角色和精灵名称。""},
{""mid"":12,""name"":""CharacterRandomizer"",""author"":""lxy"",""game_version"":""*"",""current_version"":""1.1.4"",""description"":""进入关卡加载阶段时自动随机选择角色精灵。""},
{""mid"":10,""name"":""BetterNativeHook"",""author"":""PBalint817"",""game_version"":""*"",""current_version"":""1.0.0"",""description"":""前置库模组，提供改进的原生方法 Hook 框架。""},
{""mid"":6,""name"":""AlwaysBadApple"",""author"":""lxy"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""在东方场景下强制启用 Bad Apple 特殊模式。""},
{""mid"":5,""name"":""AlwaysAprilFool"",""author"":""lxy"",""game_version"":""*"",""current_version"":""2.0.0"",""description"":""强制游戏始终处于愚人节模式。""},
{""mid"":60,""name"":""UIDisable"",""author"":""lxy"",""game_version"":""4.1.0"",""current_version"":""2.0.0"",""description"":""允许选择性隐藏游戏内的 UI 元素。""},
{""mid"":53,""name"":""ScrollSpeed"",""author"":""RobotLucca"",""game_version"":""2.10.0"",""current_version"":""1.0.0"",""description"":""支持变速滚动。需要 CustomAlbums 前置。""},
{""mid"":51,""name"":""SceneEggs"",""author"":""RobotLucca"",""game_version"":""2.11.0"",""current_version"":""1.1.0"",""description"":""允许自制谱使用官方谱面中的特殊场景彩蛋元素。""},
{""mid"":40,""name"":""ModManager"",""author"":""Creepler13"",""game_version"":""3.7.0"",""current_version"":""1.0.1"",""description"":""游戏内模组管理面板。""},
{""mid"":29,""name"":""FC AP indicator"",""author"":""lxy"",""game_version"":""3.12.0"",""current_version"":""1.7.0"",""description"":""实时显示当前是否处于 AP 或 FC 进度。""}
]";
}
