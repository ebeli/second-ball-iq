using System.Text.Json;
using MatchLens.Contracts;

namespace MatchLens.Forge;

/// <summary>
/// Reads and writes a generated match as three files in one folder:
/// match.json (metadata), events.jsonl (one event per line, in order) and ground-truth.json.
/// </summary>
public static class RecordingStore
{
    public const string MatchFile = "match.json";
    public const string EventsFile = "events.jsonl";
    public const string GroundTruthFile = "ground-truth.json";

    public static async Task WriteAsync(MatchRecording recording, string folder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, MatchFile),
            JsonSerializer.Serialize(recording.Match, ContractJson.Indented), cancellationToken);
        await File.WriteAllLinesAsync(Path.Combine(folder, EventsFile),
            recording.Events.Select(e => JsonSerializer.Serialize(e, ContractJson.Compact)), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, GroundTruthFile),
            JsonSerializer.Serialize(recording.GroundTruth, ContractJson.Indented), cancellationToken);
    }

    public static async Task<MatchRecording> ReadAsync(string folder, CancellationToken cancellationToken = default)
    {
        MatchInfo match = JsonSerializer.Deserialize<MatchInfo>(
            await File.ReadAllTextAsync(Path.Combine(folder, MatchFile), cancellationToken), ContractJson.Compact)
            ?? throw new InvalidDataException($"{MatchFile} is empty.");

        var events = new List<MatchEvent>();
        foreach (string line in await File.ReadAllLinesAsync(Path.Combine(folder, EventsFile), cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            events.Add(JsonSerializer.Deserialize<MatchEvent>(line, ContractJson.Compact)
                       ?? throw new InvalidDataException($"Empty event line in {EventsFile}."));
        }

        GroundTruth truth = JsonSerializer.Deserialize<GroundTruth>(
            await File.ReadAllTextAsync(Path.Combine(folder, GroundTruthFile), cancellationToken), ContractJson.Compact)
            ?? throw new InvalidDataException($"{GroundTruthFile} is empty.");

        return new MatchRecording(match, events, truth);
    }
}
