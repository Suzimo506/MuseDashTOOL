using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MDEN.Protocol.Rules;
using MdModManager.Helpers;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.Views;
using NAudio.Vorbis;
using NAudio.Wave;
using System.Text.RegularExpressions;

namespace MdModManager.ViewModels;

public sealed partial class GlobalChartSearchViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 12;
    private static readonly HttpClient PreviewHttp = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(12));
    private static readonly SemaphoreSlim CoverSemaphore = new(7);

    private readonly IGlobalChartSearchService _searchService;
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    private readonly IDownloadManagerService _downloadManagerService;
    private readonly IChartIndexService _chartIndexService;
    private readonly IAuthService _authService;
    private readonly AuthState _authState;

    private readonly List<GlobalChartSearchResult> _allResults = new();
    private readonly List<GlobalChartSearchResult> _filteredResults = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _stopCts;
    private WaveOutEvent? _waveOut;
    private GlobalChartSearchResult? _playingResult;
    private MdenGlobalSearchRequest? _mdenSearchRequest;
    private bool _mdenAutoDownloadStarted;
    private string _mdenCandidateStatus = string.Empty;

    public ObservableCollection<GlobalChartSearchResult> Results { get; } = new();
    public ObservableCollection<GlobalChartSearchSourceState> SourceStates { get; } = new()
    {
        new(GlobalChartSource.Euterpe, "Euterpe"),
        new(GlobalChartSource.QQGroup, "QQ群谱面"),
        new(GlobalChartSource.Mdmc, "MDMC")
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _searchDraftText = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "输入关键词，同时搜索 Euterpe、QQ群谱面与 MDMC";

    [ObservableProperty]
    private string _previewStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNext))]
    [NotifyPropertyChangedFor(nameof(CanLoadPrev))]
    private int _totalPages = 1;

    [ObservableProperty]
    private string _jumpPageText = "1";

    [ObservableProperty]
    private bool _isEditingPageNumber;

    [ObservableProperty]
    private double? _requestedScrollY;

    [ObservableProperty]
    private bool _isDuplicateDialogOpen;

    [ObservableProperty]
    private GlobalChartSearchResult? _duplicateDialogTarget;

    [ObservableProperty]
    private List<ChartInfo> _duplicateDialogItems = new();

    [ObservableProperty]
    private GlobalChartSource? _selectedSource;

    public bool CanLoadNext => CurrentPage < TotalPages && !IsLoading;
    public bool CanLoadPrev => CurrentPage > 1 && !IsLoading;
    public bool EnableMarquee => _configService.Config.EnableChartNameMarquee;

    public GlobalChartSearchViewModel(
        IGlobalChartSearchService searchService,
        IConfigService configService,
        INotificationService notificationService,
        IDownloadManagerService downloadManagerService,
        IChartIndexService chartIndexService,
        IAuthService authService,
        AuthState authState)
    {
        _searchService = searchService;
        _configService = configService;
        _notificationService = notificationService;
        _downloadManagerService = downloadManagerService;
        _chartIndexService = chartIndexService;
        _authService = authService;
        _authState = authState;
    }

    partial void OnCurrentPageChanged(int value)
    {
        JumpPageText = value.ToString();
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        UpdateStatusMessage();
        return Task.CompletedTask;
    }

    public async Task OpenMdenSearchAsync(MdenGlobalSearchRequest request)
    {
        _mdenSearchRequest = request;
        _mdenAutoDownloadStarted = false;
        _mdenCandidateStatus = string.Empty;
        SearchDraftText = request.Query;
        SelectedSource = null;
        await SearchCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchDraftText.Trim();
        if (_mdenSearchRequest != null &&
            !string.Equals(query, _mdenSearchRequest.Query, StringComparison.OrdinalIgnoreCase))
        {
            _mdenSearchRequest = null;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            ClearResults();
            SearchText = string.Empty;
            StatusMessage = "输入关键词，同时搜索 Euterpe、QQ群谱面与 MDMC";
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        StopPlayback();
        SearchText = query;
        HasSearched = true;
        IsLoading = true;
        IsEmpty = false;
        CurrentPage = 1;
        TotalPages = 1;
        Results.Clear();
        _allResults.Clear();
        _filteredResults.Clear();
        SetAllSourcesSearching();
        UpdateStatusMessage();

        try
        {
            var sourceResults = await _searchService.SearchAsync(query, ct);
            ct.ThrowIfCancellationRequested();

            foreach (var sourceResult in sourceResults)
                UpdateSourceState(sourceResult);

            _allResults.Clear();
            _allResults.AddRange(sourceResults
                .SelectMany(r => r.Results)
                .OrderBy(r => SourceOrder(r.Source))
                .ThenBy(r => r.SourceDetail, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(r => r.LikesCount)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase));

            var mdenDecision = BuildMdenCandidateDecision();
            ApplyMdenCandidateDecision(mdenDecision);
            ApplySourceFilter(resetPage: true);
            ApplyCurrentPage();
            StartMdenAutoDownloadIfEligible(mdenDecision);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("GlobalSearchVM", $"Search failed: {ex}");
            StatusMessage = "全局搜索失败：" + ex.Message;
            _notificationService.ShowFailure("全局搜索失败", ex.Message);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
                IsEmpty = _filteredResults.Count == 0;
                UpdateStatusMessage();
            }
        }
    }

    [RelayCommand]
    private void ToggleSourceFilter(GlobalChartSearchSourceState state)
    {
        if (state == null || IsLoading)
            return;

        SelectedSource = SelectedSource == state.Source ? null : state.Source;
        ApplySourceFilter(resetPage: true);
        ApplyCurrentPage();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _searchCts?.Cancel();
        _mdenSearchRequest = null;
        _mdenAutoDownloadStarted = false;
        _mdenCandidateStatus = string.Empty;
        SearchDraftText = string.Empty;
        SearchText = string.Empty;
        ClearResults();
        StatusMessage = "输入关键词，同时搜索 Euterpe、QQ群谱面与 MDMC";
    }

    [RelayCommand]
    private void StartEditPage()
    {
        JumpPageText = CurrentPage.ToString();
        IsEditingPageNumber = true;
    }

    [RelayCommand]
    private void CancelEditPage()
    {
        JumpPageText = CurrentPage.ToString();
        IsEditingPageNumber = false;
    }

    [RelayCommand]
    private void LoadFirstPage()
    {
        if (!CanLoadPrev)
            return;

        CurrentPage = 1;
        ApplyCurrentPage();
    }

    [RelayCommand]
    private void LoadPrevPage()
    {
        if (!CanLoadPrev)
            return;

        CurrentPage--;
        ApplyCurrentPage();
    }

    [RelayCommand]
    private void LoadNextPage()
    {
        if (!CanLoadNext)
            return;

        CurrentPage++;
        ApplyCurrentPage();
    }

    [RelayCommand]
    private void LoadLastPage()
    {
        if (!CanLoadNext)
            return;

        CurrentPage = TotalPages;
        ApplyCurrentPage();
    }

    [RelayCommand]
    private void JumpPage()
    {
        if (!IsEditingPageNumber)
            return;

        IsEditingPageNumber = false;
        if (!int.TryParse(JumpPageText, out var page))
            return;

        CurrentPage = Math.Clamp(page, 1, TotalPages);
        ApplyCurrentPage();
    }

    [RelayCommand]
    private async Task TogglePreviewAsync(GlobalChartSearchResult result)
    {
        if (_playingResult == result)
        {
            StopPlayback();
            return;
        }

        StopPlayback();
        await PlayDemoAsync(result);
    }

    [RelayCommand]
    private async Task DownloadChartAsync(GlobalChartSearchResult result)
    {
        if (!DotNetRuntimeHelper.IsDotNet6Installed())
        {
            await ShowMessageBoxAsync(I18nService.Instance["Str_404"] ?? "请先安装.net6环境！");
            return;
        }

        var title = result.Title;
        if (_configService.Config.EnableDownloadDuplicateCheck)
        {
            var duplicates = _chartIndexService.FindDuplicatesByTitle(title);
            if (duplicates.Count > 0)
            {
                DuplicateDialogTarget = result;
                DuplicateDialogItems = duplicates;
                IsDuplicateDialogOpen = true;
                return;
            }
        }

        await ExecuteDownloadAsync(result);
    }

    [RelayCommand]
    private async Task ConfirmSingleDownloadActionAsync(string action)
    {
        IsDuplicateDialogOpen = false;
        var result = DuplicateDialogTarget;
        if (result == null)
            return;

        if (action == "overwrite")
        {
            foreach (var local in DuplicateDialogItems)
            {
                try
                {
                    if (File.Exists(local.FilePath))
                        File.Delete(local.FilePath);
                    _chartIndexService.RemoveFromIndex(local.FilePath);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Write("GlobalSearchVM", $"Failed to delete duplicate '{local.FilePath}': {ex.Message}");
                }
            }
            await ExecuteDownloadAsync(result);
        }
        else if (action == "both")
        {
            await ExecuteDownloadAsync(result);
        }

        DuplicateDialogTarget = null;
        DuplicateDialogItems.Clear();
    }

    private async Task ExecuteDownloadAsync(GlobalChartSearchResult result)
    {
        var chart = await BuildDownloadChartAsync(result);
        if (chart == null)
            return;

        _downloadManagerService.EnqueueDownload(chart);
        _notificationService.ShowSuccess($"已添加到下载列表: 《{chart.Title}》");
    }

    private async Task<MdmcChart?> BuildDownloadChartAsync(GlobalChartSearchResult result)
    {
        if (result.MdmcChart != null)
        {
            var chart = result.MdmcChart;
            var url = chart.DownloadUrl;
            if (!string.IsNullOrEmpty(url) && (url.Contains("~%23FFFFFF~") || url.Contains("~#FFFFFF~") || chart.Title?.Contains("调色盘") == true))
            {
                var manualUrl = url.Replace("/blob/", "/").Replace("github.com", "raw.githubusercontent.com").Replace("~#FFFFFF~", "~%23FFFFFF~");
                chart.CustomDownloadUrl = GitHubMirrorHelper.ApplyMirror(manualUrl, _configService.Config.DownloadSource);
            }

            return chart;
        }

        if (result.EuterpeChart == null)
            return null;

        if (_authState.CurrentUser == null)
        {
            _notificationService.ShowFailure("Euterpe 未登录", "请先登录 Euterpe 后再下载谱面。");
            return null;
        }

        try
        {
            var token = await _authService.GetAccessTokenAsync();
            var buildZip = await _searchService.BuildEuterpeZipAsync(result.EuterpeChart.Cid);
            var zipDownloadUrl = buildZip.Path;
            if (zipDownloadUrl.Contains("euterpe-org.com", StringComparison.OrdinalIgnoreCase) && !zipDownloadUrl.Contains("t="))
            {
                var connector = zipDownloadUrl.Contains('?') ? "&" : "?";
                zipDownloadUrl += $"{connector}t={Uri.EscapeDataString(token)}";
            }

            var chart = new MdmcChart
            {
                Id = result.EuterpeChart.Cid.ToString(),
                Title = result.EuterpeChart.Name,
                Artist = result.EuterpeChart.Author,
                Bpm = result.EuterpeChart.Bpm.ToString(),
                Charter = result.EuterpeChart.CharterInfo,
                CustomCoverUrl = result.EuterpeChart.CoverUrl,
                CustomDownloadUrl = zipDownloadUrl,
                SourceCategoryName = "Euterpe",
                IsCommunitySource = true,
                Sheets = result.EuterpeChart.Maps.Select(m => new MdmcSheet
                {
                    Difficulty = m.Rating,
                    RankedDifficulty = int.TryParse(m.Rating, out var rd) ? rd : 0,
                    Charter = string.Join(", ", m.Charters)
                }).ToList()
            };
            return chart;
        }
        catch (Exception ex)
        {
            _notificationService.ShowFailure("Euterpe 下载失败", ex.Message);
            return null;
        }
    }

    private MdenCandidateDecision? BuildMdenCandidateDecision()
    {
        var request = _mdenSearchRequest;
        if (request == null ||
            string.IsNullOrWhiteSpace(request.ChartKey) ||
            !ChartSelectionRules.IsCustomChartKey(request.ChartKey))
        {
            ClearMdenCandidateLabels();
            _mdenCandidateStatus = string.Empty;
            return null;
        }

        var matches = _allResults
            .Where(result => IsStrongMdenCandidate(result, request))
            .ToList();

        var groups = matches
            .GroupBy(result => BuildMdenCandidateGroupKey(result, request), StringComparer.OrdinalIgnoreCase)
            .Select(group => new MdenCandidateGroup(group.Key, group.ToList()))
            .ToList();

        if (groups.Count > 0)
        {
            return new MdenCandidateDecision(matches, groups, MdenCandidateMatchMode.Strong);
        }

        var titleMatches = _allResults
            .Where(result => IsTitleMdenCandidate(result, request))
            .Where(CanAutoDownloadMdenCandidate)
            .ToList();

        var titleGroups = titleMatches
            .GroupBy(BuildMdenTitleCandidateGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MdenCandidateGroup(group.Key, group.ToList()))
            .ToList();

        return new MdenCandidateDecision(titleMatches, titleGroups, MdenCandidateMatchMode.TitleOnly);
    }

    private void ApplyMdenCandidateDecision(MdenCandidateDecision? decision)
    {
        ClearMdenCandidateLabels();

        if (_mdenSearchRequest == null || decision == null)
        {
            _mdenCandidateStatus = string.Empty;
            return;
        }

        if (decision.Groups.Count == 0)
        {
            _mdenCandidateStatus = Tr("Mden_NoAutoCandidate");
            return;
        }

        var label = decision.Mode == MdenCandidateMatchMode.Strong
            ? (decision.Groups.Count == 1 ? Tr("Mden_StrongCandidateLabel") : Tr("Mden_CandidateLabel"))
            : Tr("Mden_UniqueCandidateLabel");
        foreach (var result in decision.StrongMatches)
        {
            result.MdenCandidateLabel = label;
        }

        if (decision.Groups.Count == 1)
        {
            _mdenCandidateStatus = decision.Mode == MdenCandidateMatchMode.Strong
                ? Tr("Mden_UniqueStrongCandidate")
                : Tr("Mden_UniqueDownloadableCandidate");
            return;
        }

        _mdenCandidateStatus = decision.Mode == MdenCandidateMatchMode.Strong
            ? Tr("Mden_MultipleStrongCandidates", decision.Groups.Count)
            : Tr("Mden_MultipleDownloadableCandidates", decision.Groups.Count);
    }

    private void StartMdenAutoDownloadIfEligible(MdenCandidateDecision? decision)
    {
        var request = _mdenSearchRequest;
        if (request == null ||
            decision == null ||
            _mdenAutoDownloadStarted ||
            decision.Groups.Count != 1 ||
            string.IsNullOrWhiteSpace(request.ChartKey))
        {
            return;
        }

        var target = SelectPreferredMdenCandidate(decision.Groups[0]);
        if (target == null)
        {
            return;
        }

        _mdenAutoDownloadStarted = true;
        _ = AutoDownloadMdenCandidateAsync(target, request);
    }

    private async Task AutoDownloadMdenCandidateAsync(GlobalChartSearchResult target, MdenGlobalSearchRequest request)
    {
        try
        {
            _notificationService.ShowInfo(Tr("Mden_AutoDownloadStarting", target.Title));
            MdenStatusBridge.NotifyMissingChartDownloadStarted(target.Title, request.ChartKey, request.Difficulty);
            var chart = await BuildDownloadChartAsync(target);
            if (chart == null)
            {
                _mdenCandidateStatus = Tr("Mden_AutoDownloadPrepareFailed");
                MdenStatusBridge.NotifyMissingChartDownloadFailed(target.Title, Tr("Mden_AutoDownloadPrepareFailedReason"), request.ChartKey, request.Difficulty);
                UpdateStatusMessage();
                return;
            }

            var result = await _downloadManagerService.EnqueueDownloadAndWaitAsync(
                chart,
                (path, ct) => ValidateDownloadedMdenChartAsync(path, request.ChartKey!, ct));

            if (result.Success)
            {
                _mdenCandidateStatus = Tr("Mden_AutoDownloadVerified");
                _notificationService.ShowSuccess(Tr("Mden_AutoDownloadVerifiedNotification"));
                MdenStatusBridge.NotifyMissingChartDownloadCompleted(target.Title, request.ChartKey, request.Difficulty);
            }
            else
            {
                _mdenCandidateStatus = Tr("Mden_AutoDownloadVerifyFailed");
                _notificationService.ShowFailure(Tr("Mden_AutoDownloadFailedTitle"), result.ErrorMessage ?? Tr("Mden_AutoDownloadManualCorrectChart"));
                MdenStatusBridge.NotifyMissingChartDownloadFailed(target.Title, result.ErrorMessage ?? Tr("Mden_AutoDownloadManualCorrectChartForMden"), request.ChartKey, request.Difficulty);
            }

            UpdateStatusMessage();
        }
        catch (Exception ex)
        {
            _mdenCandidateStatus = Tr("Mden_AutoDownloadFailed");
            RuntimeLog.Write("GlobalSearchVM", $"MDEN auto download failed: {ex}");
            _notificationService.ShowFailure(Tr("Mden_AutoDownloadFailedTitle"), ex.Message);
            MdenStatusBridge.NotifyMissingChartDownloadFailed(target.Title, ex.Message, request.ChartKey, request.Difficulty);
            UpdateStatusMessage();
        }
    }

    private static GlobalChartSearchResult? SelectPreferredMdenCandidate(MdenCandidateGroup group)
    {
        return group.Results
            .OrderBy(result => SourceOrder(result.Source))
            .ThenBy(result => result.SourceDetail, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsStrongMdenCandidate(GlobalChartSearchResult result, MdenGlobalSearchRequest request)
    {
        if (!IsTitleMdenCandidate(result, request))
        {
            return false;
        }

        var requestArtist = NormalizeMdenText(request.Artist);
        if (string.IsNullOrWhiteSpace(requestArtist) ||
            NormalizeMdenText(result.Artist) != requestArtist)
        {
            return false;
        }

        if (!IsMdenCharterMatch(result, request.Charter))
        {
            return false;
        }

        return true;
    }

    private static bool IsTitleMdenCandidate(GlobalChartSearchResult result, MdenGlobalSearchRequest request)
    {
        var title = NormalizeMdenText(result.Title);
        var romanized = NormalizeMdenText(result.TitleRomanized);
        var query = NormalizeMdenText(request.Query);
        if (string.IsNullOrWhiteSpace(query) || (title != query && romanized != query))
        {
            return false;
        }
        return true;
    }

    private bool CanAutoDownloadMdenCandidate(GlobalChartSearchResult result)
    {
        if (result.MdmcChart != null)
        {
            return true;
        }

        return result.EuterpeChart != null && _authState.CurrentUser != null;
    }

    private static string BuildMdenCandidateGroupKey(GlobalChartSearchResult result, MdenGlobalSearchRequest request)
    {
        return string.Join("|",
            NormalizeMdenText(result.Title),
            NormalizeMdenText(result.Artist),
            NormalizeMdenText(request.Charter));
    }

    private static string BuildMdenTitleCandidateGroupKey(GlobalChartSearchResult result)
    {
        return string.Join("|",
            NormalizeMdenText(result.Title),
            NormalizeMdenText(result.Artist),
            NormalizeMdenText(result.Charter));
    }

    private static bool IsMdenCharterMatch(GlobalChartSearchResult result, string? requestCharter)
    {
        if (string.IsNullOrWhiteSpace(requestCharter))
            return false;

        var requestNames = SplitNormalizedNames(requestCharter).ToHashSet(StringComparer.Ordinal);
        if (requestNames.Count == 0)
            return false;

        var candidateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in GetCandidateCharterTexts(result))
        {
            if (NamesMatch(candidate, requestCharter))
                return true;

            var names = SplitNormalizedNames(candidate).ToHashSet(StringComparer.Ordinal);
            if (names.Count > 0 && names.SetEquals(requestNames))
                return true;

            foreach (var name in names)
                candidateNames.Add(name);
        }

        return candidateNames.Count > 0 && candidateNames.SetEquals(requestNames);
    }

    private static bool NamesMatch(string? left, string? right)
    {
        var normalizedLeft = NormalizeMdenText(left);
        var normalizedRight = NormalizeMdenText(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft) &&
               !string.IsNullOrWhiteSpace(normalizedRight) &&
               normalizedLeft == normalizedRight;
    }

    private static IEnumerable<string> SplitNormalizedNames(string? value)
    {
        foreach (var name in SplitNames(value))
        {
            var normalized = NormalizeMdenText(name);
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }
    }

    private static IEnumerable<string> GetCandidateCharterTexts(GlobalChartSearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Charter))
        {
            yield return result.Charter;
        }

        if (result.MdmcChart?.Sheets != null)
        {
            foreach (var sheet in result.MdmcChart.Sheets)
            {
                if (!string.IsNullOrWhiteSpace(sheet.Charter))
                    yield return sheet.Charter;
            }
        }

        if (result.EuterpeChart?.Maps != null)
        {
            foreach (var map in result.EuterpeChart.Maps)
            {
                if (map.Charters == null) continue;
                foreach (var charter in map.Charters)
                {
                    if (!string.IsNullOrWhiteSpace(charter))
                        yield return charter;
                }
            }
        }
    }

    private static IEnumerable<string> SplitNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (var part in value.Split(new[] { ',', '，', '/', '、', ';', '；', '&', '＆', '+', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = part.Trim();
            if (!string.IsNullOrWhiteSpace(clean))
                yield return clean;
        }
    }

    private static string NormalizeMdenText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = RemoveDiacritics(Regex.Replace(value, "<.*?>", string.Empty));
        value = Regex.Replace(value, @"\s+\d+(\.\d+)?\s*(★|\*)\s*$", string.Empty);
        value = value.Trim().ToLowerInvariant()
            .Replace('（', '(')
            .Replace('）', ')')
            .Replace('！', '!')
            .Replace('？', '?')
            .Replace('：', ':')
            .Replace('，', ',')
            .Replace('。', '.')
            .Replace('　', ' ')
            .Replace("☆", string.Empty)
            .Replace("★", string.Empty);

        return Regex.Replace(value, @"[\s\-_·・~～'""`.,:;!?()\[\]【】]+", string.Empty);
    }

    private static string RemoveDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static async Task<string?> ValidateDownloadedMdenChartAsync(string filePath, string chartKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chartKey))
            return Tr("Mden_ValidateMissingMd5");

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Tr("Mden_ValidateFileMissing");

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(entry.Name);
            if (!string.Equals(ext, ".bms", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".bme", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".bml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var entryStream = entry.Open();
            using var md5 = MD5.Create();
            var hash = await md5.ComputeHashAsync(entryStream, ct);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            if (string.Equals(actual, chartKey, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return Tr("Mden_ValidateChartMismatch");
    }

    private void ClearMdenCandidateLabels()
    {
        foreach (var result in _allResults)
        {
            result.MdenCandidateLabel = string.Empty;
        }
    }

    private async Task PlayDemoAsync(GlobalChartSearchResult result)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
            string url;
            if (result.MdmcChart != null)
            {
                url = !string.IsNullOrWhiteSpace(result.MdmcChart.CustomDemoUrl)
                    ? result.MdmcChart.CustomDemoUrl
                    : (!string.IsNullOrWhiteSpace(result.MdmcChart.CustomDemoMp3Url)
                        ? result.MdmcChart.CustomDemoMp3Url
                        : (!string.IsNullOrWhiteSpace(result.MdmcChart.DemoUrl)
                            ? result.MdmcChart.DemoUrl
                            : result.MdmcChart.DemoMp3Url));
                if (!string.IsNullOrEmpty(url) && (url.Contains("~%23FFFFFF~") || url.Contains("~#FFFFFF~") || result.Title.Contains("调色盘")))
                {
                    var manualUrl = url.Replace("/blob/", "/").Replace("github.com", "raw.githubusercontent.com").Replace("~#FFFFFF~", "~%23FFFFFF~");
                    url = GitHubMirrorHelper.ApplyMirror(manualUrl, _configService.Config.DownloadSource);
                }
            }
            else if (result.EuterpeChart != null)
            {
                var token = await _authService.GetAccessTokenAsync();
                url = $"https://dl.euterpe-org.com/files/charts/{result.EuterpeChart.Cid}/demo.ogg";
                if (!string.IsNullOrEmpty(token))
                    url += $"?t={Uri.EscapeDataString(token)}";
            }
            else
            {
                return;
            }

            PreviewStatusText = $"正在缓冲 {result.Title} 试听文件";
            UpdateStatusMessage();

            using var response = await PreviewHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (ct.IsCancellationRequested)
                return;

            var ext = Path.GetExtension(url.Split('?')[0]);
            await StartAudioStreamAsync(bytes, ext, result, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PreviewStatusText = string.Empty;
            UpdateStatusMessage();
            _notificationService.ShowFailure("试听加载失败", ex.Message);
        }
    }

    private async Task StartAudioStreamAsync(byte[] bytes, string ext, GlobalChartSearchResult result, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return;

        var ms = new MemoryStream(bytes);
        IWaveProvider waveProvider = CreateWaveProvider(ms, ext);

        _waveOut = new WaveOutEvent();
        _waveOut.Init(waveProvider);
        _waveOut.Volume = (float)_configService.Config.ChartPreviewVolume;

        var cts = _stopCts = new CancellationTokenSource();
        _waveOut.PlaybackStopped += (_, _) =>
        {
            if (!cts.IsCancellationRequested)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_playingResult == result)
                    {
                        _playingResult = null;
                        PreviewStatusText = string.Empty;
                        UpdateStatusMessage();
                    }
                    SetPlaying(result, false);
                });
            }
            if (waveProvider is IDisposable disposable)
                disposable.Dispose();
            ms.Dispose();
        };

        _waveOut.Play();
        _playingResult = result;
        SetPlaying(result, true);
        PreviewStatusText = $"正在播放 {result.Title} 试听";
        UpdateStatusMessage();
        await Task.CompletedTask;
    }

    private static IWaveProvider CreateWaveProvider(Stream stream, string ext)
    {
        if (string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase))
            return new Mp3FileReader(stream);

        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
            return new WaveFileReader(stream);

        return new VorbisWaveReader(stream);
    }

    private static void SetPlaying(GlobalChartSearchResult result, bool isPlaying)
    {
        if (result.MdmcChart != null)
            result.MdmcChart.IsPlaying = isPlaying;
        if (result.EuterpeChart != null)
            result.EuterpeChart.IsPlaying = isPlaying;
        result.RefreshPlaybackState();
    }

    private void ApplyCurrentPage()
    {
        Results.Clear();

        var pageItems = _filteredResults
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        foreach (var item in pageItems)
        {
            if (item.MdmcChart != null)
            {
                item.MdmcChart.SearchText = SearchText;
                item.MdmcChart.IsAnimatedCoverPlaybackEnabled = false;
            }

            Results.Add(item);
        }

        IsEmpty = Results.Count == 0;
        RequestedScrollY = 0;
        UpdateStatusMessage();

        _ = LoadCurrentPageCoversAsync(pageItems);
    }

    private async Task LoadCurrentPageCoversAsync(IEnumerable<GlobalChartSearchResult> pageItems)
    {
        var tasks = pageItems
            .Where(item => item.MdmcChart != null && !item.MdmcChart.HasDisplayCoverSource)
            .Select(async item =>
            {
                await CoverSemaphore.WaitAsync();
                try
                {
                    if (item.MdmcChart != null)
                    {
                        await ChartCoverSourceResolver.EnsureResolvedAsync(item.MdmcChart);
                        item.RefreshCoverState();
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Write("GlobalSearchVM", $"Cover load failed: {ex.Message}");
                }
                finally
                {
                    CoverSemaphore.Release();
                }
            });

        await Task.WhenAll(tasks);
    }

    private void ClearResults()
    {
        _searchCts?.Cancel();
        StopPlayback();
        _allResults.Clear();
        _filteredResults.Clear();
        Results.Clear();
        HasSearched = false;
        IsLoading = false;
        IsEmpty = true;
        SelectedSource = null;
        CurrentPage = 1;
        TotalPages = 1;
        foreach (var state in SourceStates)
        {
            state.Status = GlobalChartSourceStatus.Idle;
            state.Message = "等待搜索";
            state.ResultCount = 0;
            state.IsSelected = false;
        }
    }

    private void SetAllSourcesSearching()
    {
        SelectedSource = null;
        foreach (var state in SourceStates)
        {
            state.Status = GlobalChartSourceStatus.Searching;
            state.Message = "搜索中...";
            state.ResultCount = 0;
            state.IsSelected = false;
        }
    }

    private void UpdateSourceState(GlobalChartSearchServiceResult result)
    {
        var state = SourceStates.FirstOrDefault(x => x.Source == result.Source);
        if (state == null)
            return;

        state.Status = result.Status;
        state.ResultCount = result.Results.Count;
        state.Message = result.Message;
    }

    private void ApplySourceFilter(bool resetPage)
    {
        _filteredResults.Clear();
        _filteredResults.AddRange(SelectedSource.HasValue
            ? _allResults.Where(r => r.Source == SelectedSource.Value)
            : _allResults);

        foreach (var state in SourceStates)
            state.IsSelected = SelectedSource == state.Source;

        TotalPages = Math.Max(1, (int)Math.Ceiling((double)_filteredResults.Count / PageSize));
        if (resetPage)
        {
            CurrentPage = 1;
        }
        else
        {
            CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);
        }
    }

    private void UpdateStatusMessage()
    {
        if (!string.IsNullOrWhiteSpace(PreviewStatusText))
        {
            StatusMessage = PreviewStatusText;
            return;
        }

        if (IsLoading)
        {
            StatusMessage = $"正在全局搜索 “{SearchText}”...";
            return;
        }

        if (!HasSearched)
        {
            StatusMessage = "输入关键词，同时搜索 Euterpe、QQ群谱面与 MDMC";
            return;
        }

        if (_allResults.Count == 0)
        {
            StatusMessage = WithSourceNotice("没有找到匹配的谱面");
            return;
        }

        if (_filteredResults.Count == 0)
        {
            var sourceName = SourceStates.FirstOrDefault(s => s.Source == SelectedSource)?.DisplayName ?? "当前来源";
            StatusMessage = WithSourceNotice($"{sourceName} 没有匹配的谱面");
            return;
        }

        var prefix = SelectedSource.HasValue
            ? $"{SourceStates.FirstOrDefault(s => s.Source == SelectedSource)?.DisplayName} | "
            : string.Empty;
        StatusMessage = WithSourceNotice($"{prefix}第 {CurrentPage} / {TotalPages} 页，共 {_filteredResults.Count} 张谱面");
    }

    private string WithSourceNotice(string message)
    {
        var notice = string.Join("；", SourceStates
            .Where(s => s.HasNotice && !string.IsNullOrWhiteSpace(s.Message))
            .Select(s => s.Message));

        var result = string.IsNullOrWhiteSpace(notice) ? message : $"{message}；{notice}";
        if (_mdenSearchRequest != null)
        {
            result = string.IsNullOrWhiteSpace(_mdenCandidateStatus)
                ? Tr("Mden_SearchPrefix") + result
                : $"{Tr("Mden_SearchPrefix")}{_mdenCandidateStatus}{Tr("Mden_StatusSeparator")}{result}";
        }

        return result;
    }

    private static string Tr(string key, params object[] args)
    {
        var template = I18nService.Instance[key];
        if (args == null || args.Length == 0)
            return template;

        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public void StopPlayback()
    {
        _loadCts?.Cancel();
        _loadCts = null;

        _stopCts?.Cancel();
        _stopCts = null;

        if (_playingResult != null)
        {
            SetPlaying(_playingResult, false);
            _playingResult = null;
        }

        PreviewStatusText = string.Empty;
        UpdateStatusMessage();

        var waveOut = _waveOut;
        _waveOut = null;
        if (waveOut != null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Write("GlobalSearchVM", $"Error disposing WaveOut: {ex.Message}");
                }
            });
        }
    }

    private static int SourceOrder(GlobalChartSource source) => source switch
    {
        GlobalChartSource.Euterpe => 0,
        GlobalChartSource.QQGroup => 1,
        GlobalChartSource.Mdmc => 2,
        _ => 9
    };

    private sealed record MdenCandidateDecision(
        IReadOnlyList<GlobalChartSearchResult> StrongMatches,
        IReadOnlyList<MdenCandidateGroup> Groups,
        MdenCandidateMatchMode Mode);

    private sealed record MdenCandidateGroup(
        string Key,
        IReadOnlyList<GlobalChartSearchResult> Results);

    private enum MdenCandidateMatchMode
    {
        Strong,
        TitleOnly
    }

    private static async Task ShowMessageBoxAsync(string message)
    {
        var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow as MainWindow
            : null;

        if (mainWindow != null)
            await mainWindow.ShowMessageBoxAsync(message);
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        StopPlayback();

        foreach (var result in _allResults)
        {
            if (result.MdmcChart != null)
            {
                ChartCoverSourceResolver.ReleaseChartCache(result.MdmcChart);
                result.MdmcChart.ReleaseResources();
            }
            result.EuterpeChart?.CoverImage?.Dispose();
        }

        _allResults.Clear();
        _filteredResults.Clear();
        Results.Clear();
    }
}
