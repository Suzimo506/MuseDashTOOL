using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;
using MdModManager.Models;

namespace MdModManager.Services;



public static class MuseDashAccountService
{
    private const string RegPath = @"Software\PeroPeroGames\MuseDash";
    private const string ApiBase = "https://api.musedash.moe";
    private static IConfigService? _configService;

    // Fast client for player API (Increased to 60s to handle extremely slow server/network)
    private static readonly HttpClient _http = Helpers.HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(60));
    // Slow client for background caches like albums/characters (~3MB responses)
    private static readonly HttpClient _httpCache = Helpers.HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(15));

    // Dynamic character and elfin cache loaded from api.musedash.moe/ce
    private static System.Collections.Generic.List<string>? _characterNames = null;
    private static System.Collections.Generic.List<string>? _elfinNames = null;

    public static void Configure(IConfigService configService)
    {
        _configService = configService;
    }

    private static async Task EnsureCharacterCacheAsync()
    {
        if (_characterNames != null && _elfinNames != null) return;
        try
        {
            var json = await _httpCache.GetStringAsync($"{ApiBase}/ce");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // c.ChineseS = array of character names (index == character_uid)
            if (root.TryGetProperty("c", out var c))
            {
                _characterNames = new();
                if (c.TryGetProperty("ChineseS", out var chars))
                    foreach (var el in chars.EnumerateArray())
                        _characterNames.Add(el.GetString() ?? "?");

                _elfinNames = new();
                if (c.TryGetProperty("elfin", out var elfinArr) && elfinArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in elfinArr.EnumerateArray())
                        _elfinNames.Add(el.GetString() ?? "?");
                }
            }

            // Fallback: try the 'e' key for elfins (some versions use that)
            if ((_elfinNames == null || _elfinNames.Count == 0) && root.TryGetProperty("e", out var e))
            {
                _elfinNames = new();
                if (e.TryGetProperty("ChineseS", out var elArr))
                    foreach (var el in elArr.EnumerateArray())
                        _elfinNames.Add(el.GetString() ?? "?");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MuseDashAccountService] EnsureCharacterCacheAsync failed: {ex.Message}");
        }
        // Ensure not null so we don't retry on failure
        _characterNames ??= new();
        _elfinNames ??= new();
    }

    private static string GetCharacterName(string? uid)
    {
        if (uid == null || _characterNames == null) return "未知角色";
        if (int.TryParse(uid, out int idx) && idx >= 0 && idx < _characterNames.Count)
            return _characterNames[idx];
        return $"角色#{uid}";
    }

    private static string GetElfinName(string? uid)
    {
        if (uid == null || _elfinNames == null) return "无精灵";
        if (int.TryParse(uid, out int idx) && idx >= 0 && idx < _elfinNames.Count)
            return _elfinNames[idx];
        return $"精灵#{uid}";
    }

    private static string GetDifficultyName(int difficulty)
    {
        return difficulty switch
        {
            0 => "Easy",
            1 => "Hard",
            2 => "Master",
            3 => "Hidden",
            4 => "Extra",
            _ => "?"
        };
    }

    private static string FormatLevel(string? level)
    {
        var value = level?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return "Lv.?";
        return value.StartsWith("Lv.", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"Lv.{value}";
    }

    private static System.Collections.Generic.Dictionary<string, (string Name, string Author, string CoverUrl, string[] Levels)>? _songInfoCache = null;

    private static async Task EnsureSongCacheAsync()
    {
        if (_songInfoCache != null) return;
        _songInfoCache = new();
        try
        {
            var json = await _httpCache.GetStringAsync($"{ApiBase}/albums");
            var albums = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringMdMoeAlbum);
            if (albums != null)
            {
                foreach (var album in albums.Values)
                {
                    if (album.Music == null) continue;
                    foreach (var kvp in album.Music)
                    {
                        var uid = kvp.Key;
                        var song = kvp.Value;
                        if (!string.IsNullOrEmpty(song.Name))
                        {
                            var coverUrl = !string.IsNullOrEmpty(song.Cover)
                                ? $"https://musedash.moe/covers/{song.Cover}.webp"
                                : "";
                            _songInfoCache[uid] = (song.Name, song.Author ?? "", coverUrl, song.Difficulty ?? System.Array.Empty<string>());
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MuseDashAccountService] Failed to fetch albums cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the logged-in Muse Dash account info from the Windows registry.
    /// Returns null if nothing is found or on non-Windows platforms.
    /// </summary>
    public static MuseDashAccountInfo? ReadAccountInfo()
    {
        return ReadRegistryAccountInfo() ?? ReadManualAccountInfo();
    }

    public static MuseDashAccountInfo? ReadRegistryAccountInfo()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath, writable: false);
            if (key == null) return null;

            var raw = key.GetValue("374bfde32ff3436890ff977bc94f8015_#account_id_h274776658", null,
                Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);

            if (raw is byte[] bytes && bytes.Length > 0)
            {
                var uid = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
                if (!string.IsNullOrEmpty(uid))
                {
                    return new MuseDashAccountInfo { Uid = uid };
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MuseDashAccountService] Failed to read registry: {ex.Message}");
        }

        return null;
    }

    public static MuseDashAccountInfo? ReadManualAccountInfo()
    {
        var uid = NormalizeManualUid(_configService?.Config.ManualMuseDashUid);
        return string.IsNullOrWhiteSpace(uid)
            ? null
            : new MuseDashAccountInfo { Uid = uid, IsManual = true };
    }

    public static string GetManualMuseDashUid() => NormalizeManualUid(_configService?.Config.ManualMuseDashUid);

    public static bool IsValidManualUid(string? uid)
    {
        var normalized = NormalizeManualUid(uid);
        if (normalized.Length is < 6 or > 64)
            return false;

        foreach (var c in normalized)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        }

        return true;
    }

    public static async Task SaveManualMuseDashUidAsync(string uid)
    {
        if (_configService == null)
            throw new InvalidOperationException("Config service is not initialized");

        var normalized = NormalizeManualUid(uid);
        if (!IsValidManualUid(normalized))
            throw new ArgumentException("Invalid Muse Dash UID", nameof(uid));

        _configService.Config.ManualMuseDashUid = normalized;
        await _configService.SaveAsync();
        InvalidateCache();
    }

    private static string NormalizeManualUid(string? uid)
    {
        return (uid ?? "").Trim();
    }

    // 检查用户是否在游戏内登录
    public static bool HasLoginUid()
    {
        try
        {
            var info = ReadAccountInfo();
            if (info != null && !string.IsNullOrWhiteSpace(info.Uid))
            {
                return true;
            }
        }
        catch
        {
            // 忽略异常
        }

        return false;
    }

    // ────────────────────────────────────────────────────────────
    //  Startup prefetch — call StartPrefetch() once from App.cs
    // ────────────────────────────────────────────────────────────

    /// <summary>The in-memory cached result of the background prefetch (null = not done yet or failed).</summary>
    public static PlayerProfileData? CachedProfile { get; private set; }
    public static MuseDashAccountInfo? CachedAccountInfo { get; private set; }

    private static Task? _prefetchTask;
    private static readonly object _prefetchLock = new();

    /// <summary>
    /// Kick off background fetching immediately after app starts.
    /// Safe to call multiple times — extra calls are no-ops.
    /// </summary>
    public static void StartPrefetch()
    {
        lock (_prefetchLock)
        {
            if (_prefetchTask != null) return;
            _prefetchTask = Task.Run(RunPrefetchAsync);
        }
    }

    /// <summary>
    /// Await this to get the prefetch result (null if not started or failed).
    /// Returns immediately if already complete.
    /// </summary>
    public static Task WaitForPrefetchAsync() => _prefetchTask ?? Task.CompletedTask;

    private static async Task RunPrefetchAsync()
    {
        try
        {
            var info = ReadAccountInfo();
            if (info == null) return;
            CachedAccountInfo = info;

            var uid = info.Uid ?? "";
            if (string.IsNullOrWhiteSpace(uid)) return;

            // 后台静默重试逻辑：如果失败，每隔 5 秒重试一次，最多尝试 3 次
            int retryCount = 0;
            while (retryCount < 3)
            {
                try
                {
                    var profile = await FetchPlayerProfileAsync(uid);
                    if (profile != null)
                    {
                        CachedProfile = profile;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MuseDashAccountService] Background prefetch attempt {retryCount + 1} failed: {ex.Message}");
                }

                retryCount++;
                if (retryCount < 3) await Task.Delay(5000);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MuseDashAccountService] Prefetch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears cached data and resets the prefetch task so the next StartPrefetch() re-fetches.
    /// Call this when the user manually requests a refresh.
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_prefetchLock)
        {
            CachedProfile = null;
            CachedAccountInfo = null;
            _prefetchTask = null;
        }
    }

    /// <summary>
    /// Fetches the real in-game nickname and full profile from musedash.moe using the player's UID.
    /// Returns null on failure (network error, not found, etc).
    /// </summary>
    public static async Task<PlayerProfileData?> FetchPlayerProfileAsync(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;
        try
        {
            var url = $"{ApiBase}/player/{Uri.EscapeDataString(uid)}";
            var json = await _http.GetStringAsync(url);
            
            // 增加防御性检查：如果返回的是 HTML 错误页面（常见于 522 错误），GetStringAsync 虽可能不报错，但解析会崩
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{"))
            {
                LastError = "服务器返回了无效数据 (可能是 API 正在维护或被 Cloudflare 拦截)";
                return null;
            }

            var resp = JsonSerializer.Deserialize(json, AppJsonContext.Default.MdMoePlayerResponse);
            if (resp == null) return null;

            decimal rlValue = 0m;
            if (resp.RelativeLevel.HasValue)
            {
                var el = resp.RelativeLevel.Value;
                if (el.ValueKind == JsonValueKind.Number)
                    rlValue = el.GetDecimal();
                else if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(), out var parsed))
                    rlValue = parsed;
            }

            var data = new PlayerProfileData
            {
                Nickname = resp.User?.Nickname,
                RelativeLevel = rlValue,
                RecordsCount = resp.Plays?.Count ?? 0,
            };

            await Task.WhenAll(EnsureSongCacheAsync(), EnsureCharacterCacheAsync());

            decimal totalAcc = 0;
            int exactCount = 0;
            
            if (resp.Plays != null)
            {
                var displayIndex = 1;
                foreach (var p in resp.Plays)
                {
                    var acc = p.Accuracy ?? 0m;
                    if (acc > 0)
                    {
                        totalAcc += acc;
                        exactCount++;
                    }
                    if (acc >= 100m)
                    {
                        data.PerfectsCount++;
                    }

                    string charName = GetCharacterName(p.CharacterUid);
                    string elfinName = GetElfinName(p.ElfinUid);

                    var difficultyIndex = p.Difficulty ?? -1;
                    string difficultyStr = GetDifficultyName(difficultyIndex);
                    string lvl = FormatLevel(p.Level);

                    string songName = "";
                    string songAuthor = "";
                    string songCover = "";

                    if (p.Uid != null && _songInfoCache != null && _songInfoCache.TryGetValue(p.Uid, out var songInfo))
                    {
                        songName = songInfo.Name;
                        songAuthor = songInfo.Author;
                        songCover = songInfo.CoverUrl;

                        if (string.IsNullOrWhiteSpace(p.Level) &&
                            difficultyIndex >= 0 &&
                            songInfo.Levels.Length > difficultyIndex)
                        {
                            lvl = FormatLevel(songInfo.Levels[difficultyIndex]);
                        }
                    }
                    else if (!string.IsNullOrEmpty(p.SongName))
                    {
                        songName = p.SongName;
                    }
                    else
                    {
                        songName = p.Uid ?? "Unknown";
                    }

                    // 解析难度等级数值用于排序
                    int rawDiff = 0;
                    if (lvl.StartsWith("Lv.") && int.TryParse(lvl[3..], out var parsedLvl))
                        rawDiff = Math.Max(difficultyIndex, 0) * 100 + parsedLvl;

                    data.RecentPlays.Add(new PlayerSongRecord
                    {
                        DisplayIndex = displayIndex++,
                        Title = songName,
                        Author = songAuthor,
                        CoverUrl = songCover,
                        Level = $"{difficultyStr} {lvl}",
                        Accuracy = $"{acc:0.00}%",
                        Score = p.Score?.ToString() ?? "0",
                        Rank = p.Rank.HasValue ? $"#{p.Rank}" : "-",
                        Gear = $"{charName} / {elfinName}",
                        RawRank = p.Rank ?? int.MaxValue,
                        RawAccuracy = acc,
                        RawDifficulty = rawDiff
                    });
                }
            }

            if (exactCount > 0)
                data.AverageAccuracy = Math.Round(totalAcc / exactCount, 2);

            return data;
        }
        catch (Exception ex)
        {
            LastError = ex is TaskCanceledException
                ? "请求超时（网络较慢或 musedash.moe 不可访问）"
                : $"连接失败：{ex.Message}";
            Console.WriteLine($"[MuseDashAccountService] FetchPlayerProfileAsync failed: {ex}");
            return null;
        }
    }

    /// <summary>Stores the last error from FetchPlayerProfileAsync for display in the UI.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Returns a masked phone number, e.g. "191****4823"</summary>
    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrEmpty(phone) || phone.Length < 7)
            return phone ?? "-";
        return phone[..3] + "****" + phone[^4..];
    }

    /// <summary>Returns first 8 + "..." + last 4 chars of UID for display.</summary>
    public static string ShortenUid(string? uid)
    {
        if (string.IsNullOrEmpty(uid) || uid.Length <= 12) return uid ?? "-";
        return uid[..8] + "..." + uid[^4..];
    }
}
