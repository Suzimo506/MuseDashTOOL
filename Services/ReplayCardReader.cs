using System.Text.Json;

using MdModManager.Models;

namespace MdModManager.Services;

// Read only the small card header. Native input/state arrays are neither
// deserialized nor validated until the player chooses to play this file.
internal static class ReplayCardReader
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        "formatVersion", "inputEncoding", "recordedAtUtc", "chartUid", "difficulty",
        "characterIndex", "elfinIndex", "result", "playerName", "chart", "cleared"
    };

    internal static ReplayCardInfo Read(string path, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var stream = ReplayStorage.OpenRead(path, verifyPayload: false);
        return Read(stream, cancellation);
    }

    internal static ReplayCardInfo Read(Stream stream, CancellationToken cancellation = default)
    {
        var buffer = new byte[16384];
        var remaining = 0;
        var state = new JsonReaderState();
        var skipDepth = -1;
        var skipValue = false;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var metadata = new MemoryStream();
        using var writer = new Utf8JsonWriter(metadata);
        writer.WriteStartObject();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, remaining, buffer.Length - remaining);
            var final = count == 0;
            var length = remaining + count;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, length), final, state);
            while (true)
            {
                var checkpoint = reader;
                if (!reader.Read()) break;
                if (skipDepth >= 0)
                {
                    if (reader.CurrentDepth == skipDepth && reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
                        skipDepth = -1;
                    continue;
                }
                if (skipValue)
                {
                    skipValue = false;
                    if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                        skipDepth = reader.CurrentDepth;
                    continue;
                }
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                var name = reader.GetString()!;
                if (!Fields.Contains(name)) { skipValue = true; continue; }
                if (!reader.Read() || !JsonDocument.TryParseValue(ref reader, out var value))
                {
                    reader = checkpoint;
                    break;
                }
                using (value)
                {
                    if (!found.Add(name)) throw new InvalidDataException("Duplicate replay metadata.");
                    writer.WritePropertyName(name);
                    value.RootElement.WriteTo(writer);
                }
                if (found.Count == Fields.Count) return Finish();
            }
            var consumed = (int)reader.BytesConsumed;
            state = reader.CurrentState;
            remaining = length - consumed;
            if (final)
            {
                if (remaining != 0 || found.Count != Fields.Count)
                    throw new InvalidDataException("Incomplete replay metadata.");
                return Finish();
            }
            if (remaining > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
            if (remaining == buffer.Length)
            {
                if (buffer.Length >= 131072) throw new InvalidDataException("Replay metadata is too large.");
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        }

        ReplayCardInfo Finish()
        {
            writer.WriteEndObject();
            writer.Flush();
            var card = JsonSerializer.Deserialize<ReplayCardInfo>(metadata.GetBuffer().AsSpan(0, (int)metadata.Length), Json)
                ?? throw new InvalidDataException("Empty replay metadata.");
            if (card.FormatVersion != 9 || card.InputEncoding != "native-steps-v6")
                throw new InvalidDataException("Unsupported replay input encoding.");
            if (string.IsNullOrWhiteSpace(card.ChartUid) || card.Difficulty is < 1 or > 5 ||
                card.CharacterIndex is < 0 or > 500 || card.ElfinIndex is < -1 or > 500 ||
                card.PlayerName == null || card.PlayerName.Length > 512 ||
                !float.IsFinite(card.Result.AccuracyPercent) || card.Result.AccuracyPercent is < 0 or > 100.01f ||
                card.Result.EndTimeMicroseconds is < 0 or > 86_400_000_000 ||
                card.Chart is { } chart && (chart.Title == null || chart.Composer == null || chart.Charter == null ||
                    chart.Level == null || chart.Bpm == null || chart.CustomChartMd5 == null || chart.CustomChartMd5.Length > 32 || !float.IsFinite(chart.DurationSeconds) ||
                    chart.DurationSeconds is < 0 or > 86400))
                throw new InvalidDataException("Invalid replay metadata.");
            return card;
        }
    }
}
