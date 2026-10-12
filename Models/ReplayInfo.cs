using MdModManager.Services;

namespace MdModManager.Models;

internal sealed record ReplayCardInfo(int FormatVersion, string InputEncoding, DateTimeOffset RecordedAtUtc,
    string ChartUid, int Difficulty, int CharacterIndex, int ElfinIndex, ReplayResult Result,
    string PlayerName, ReplayChartInfo? Chart)
{
    public bool Cleared { get; init; } = true;
}

internal sealed record ReplayChartInfo(string Title, string Composer, string Charter, string Level, string Bpm, float DurationSeconds)
{
    public bool IsCustom { get; init; }
    public string CustomChartMd5 { get; init; } = "";
}

internal readonly record struct ReplayResult(int Score, float AccuracyPercent, int Perfect, int Great,
    int Early, int Late, int Miss, int MaxCombo, long EndTimeMicroseconds);

public sealed class ReplayInfo
{
    public string FilePath { get; init; } = "";
    public long FileSize { get; init; }
    internal ReplayCardInfo? Card { get; init; }
    public string Error { get; init; } = "";
    public bool IsValid => Card != null;
    public string Title => string.IsNullOrWhiteSpace(Card?.Chart?.Title) ? Path.GetFileNameWithoutExtension(FilePath) : Card.Chart.Title;
    public string Player => string.IsNullOrWhiteSpace(Card?.PlayerName) ? L("Replay_UnknownPlayer") : Card.PlayerName;
    public string Recorded => Card?.RecordedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
    public string Accuracy => Card == null ? "—" : $"{Card.Result.AccuracyPercent:F2}%";
    public string Score => Card == null ? "—" : Card.Result.Score.ToString("N0");
    public string ChartDetails => Card == null ? "" : string.Join(" · ", new[]
    {
        $"{L("Replay_Difficulty")} {Card.Difficulty}",
        string.IsNullOrWhiteSpace(Card.Chart?.Level) ? "" : $"{L("Replay_Level")} {Card.Chart.Level}",
        string.IsNullOrWhiteSpace(Card.Chart?.Bpm) ? "" : $"BPM {Card.Chart.Bpm}",
        Card.Chart?.Charter ?? ""
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Character => Card == null ? "" : $"{EnsembleLobbyPlayer.ResolveGirlName(Card.CharacterIndex)} / {EnsembleLobbyPlayer.ResolveElfinName(Card.ElfinIndex)}";
    public string Judgements => Card == null ? "" : $"Perfect {Card.Result.Perfect} · Great {Card.Result.Great} · Miss {Card.Result.Miss} · Max Combo {Card.Result.MaxCombo}";
    public string ResultBadge => Card == null ? L("Replay_Invalid") : !Card.Cleared ? L("Replay_NotCleared") :
        Card.Result.Miss == 0 && Card.Result.Great == 0 && Card.Result.Early == 0 && Card.Result.Late == 0 ? "AP" :
        Card.Result.Miss == 0 ? "FC" : L("Replay_Cleared");
    public string SizeText => $"{FileSize / 1048576d:F2} MB";
    public string SearchText => $"{Title} {Player} {Card?.ChartUid} {Card?.Chart?.Composer} {Card?.Chart?.Charter} {Path.GetFileName(FilePath)}";
    private static string L(string key) => I18nService.Instance[key];
}
