using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;

namespace MdModManager.ViewModels;

public partial class ReplayManagerViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 16;
    private readonly ReplayService _service;
    private readonly IConfigService _config;
    private readonly IGamePathService _gamePath;
    private readonly CancellationTokenSource _lifetime = new();
    private List<ReplayInfo> _all = new();
    private List<ReplayInfo> _filtered = new();
    private string _loadedGamePath = "";
    private bool _disposed;
    public ObservableCollection<ReplayInfo> Replays { get; } = new();
    public event EventHandler? PageChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrevious))]
    [NotifyPropertyChangedFor(nameof(CanNext))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyPropertyChangedFor(nameof(CanPrevious))]
    [NotifyPropertyChangedFor(nameof(CanNext))]
    private int _currentPage = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyPropertyChangedFor(nameof(CanNext))]
    private int _totalPages = 1;
    [ObservableProperty] private bool _isEmpty = true;
    public bool CanPrevious => !IsBusy && CurrentPage > 1;
    public bool CanNext => !IsBusy && CurrentPage < TotalPages;
    public bool CanEdit => !IsBusy;
    public string PageText => $"{CurrentPage} / {TotalPages}";
    private static string L(string key) => I18nService.Instance[key];

    public ReplayManagerViewModel(ReplayService service, IConfigService config, IGamePathService gamePath)
    {
        _service = service;
        _config = config;
        _gamePath = gamePath;
    }

    public async Task InitializeAsync(CancellationToken cancellation = default)
    {
        await RefreshCoreAsync(cancellation);
    }

    private bool TryGetGamePath(out string path)
    {
        path = _config.Config.GamePath;
        if (!string.IsNullOrWhiteSpace(path) && _gamePath.IsValidGamePath(path)) return true;
        Status = L("Replay_NoGame");
        return false;
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync(_lifetime.Token);

    private async Task RefreshCoreAsync(CancellationToken cancellation)
    {
        if (IsBusy || _disposed) return;
        if (!TryGetGamePath(out var path)) return;
        IsBusy = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        try { await LoadCoreAsync(path, linked.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = L("Replay_LoadFailed") + ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    private async Task LoadCoreAsync(string path, CancellationToken cancellation)
    {
        Status = L("Replay_Loading");
        var result = await Task.Run(() => _service.Load(path, cancellation), cancellation);
        if (_disposed) return;
        _all = result.ToList();
        _loadedGamePath = path;
        ApplyFilter();
        var invalid = _all.Count(r => !r.IsValid);
        Status = string.Format(L("Replay_Count"), _all.Count, invalid);
    }

    partial void OnSearchTextChanged(string value)
    {
        CurrentPage = 1;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        _filtered = _all.Where(r => Helpers.SearchHelper.IsMatch(r.SearchText, search, true)).ToList();
        TotalPages = Math.Max(1, (_filtered.Count + PageSize - 1) / PageSize);
        CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);
        ShowPage();
    }

    private void ShowPage()
    {
        Replays.Clear();
        foreach (var replay in _filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize)) Replays.Add(replay);
        IsEmpty = Replays.Count == 0;
        PageChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void Previous() { if (CanPrevious) { CurrentPage--; ShowPage(); } }
    [RelayCommand] private void Next() { if (CanNext) { CurrentPage++; ShowPage(); } }

    public async Task ImportAsync(IEnumerable<string> paths)
    {
        if (IsBusy || _disposed || !TryGetGamePath(out var path)) return;
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        IsBusy = true;
        var imported = 0;
        var skipped = 0;
        var failures = new List<string>();
        try
        {
            Status = L("Replay_Importing");
            await Task.Run(() =>
            {
                foreach (var file in files)
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    try
                    {
                        if (_service.Import(file, path, _lifetime.Token)) imported++; else skipped++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failures.Add(Path.GetFileName(file) + ": " + ex.Message); }
                }
            }, _lifetime.Token);
            await LoadCoreAsync(path, _lifetime.Token);
            if (!_disposed)
            {
                Status = string.Format(L("Replay_ImportResult"), imported, skipped, failures.Count);
                if (failures.Count > 0)
                {
                    RuntimeLog.Write("ReplayImport", string.Join("\n", failures));
                    Status += " · " + failures[0];
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) Status = L("Replay_ImportFailed") + ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    public async Task DeleteAsync(ReplayInfo replay)
    {
        if (IsBusy || _disposed || !TryGetGamePath(out var path)) return;
        if (!string.Equals(path, _loadedGamePath, StringComparison.OrdinalIgnoreCase)) { await RefreshAsync(); return; }
        IsBusy = true;
        try
        {
            await Task.Run(() => _service.Delete(replay, path));
            if (_disposed) return;
            _all.Remove(replay);
            ApplyFilter();
            Status = L("Replay_Deleted");
        }
        catch (Exception ex) { if (!_disposed) Status = L("Replay_DeleteFailed") + ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _all.Clear();
        _filtered.Clear();
        Replays.Clear();
        // Keep the token source valid until in-flight imports/reads observe cancellation.
    }
}
