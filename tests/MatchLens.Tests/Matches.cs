using System.Collections.Concurrent;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Forge;

namespace MatchLens.Tests;

/// <summary>Generated matches played through the engine, cached per scenario and seed (generation is deterministic).</summary>
internal static class Matches
{
    private static readonly ConcurrentDictionary<(string, int), Played> Cache = new();

    public sealed record Played(MatchRecording Recording, LiveMatch Live, IReadOnlyList<(Candidate Candidate, int AtSec)> Candidates);

    public static Played Play(string scenarioId, int seed = 1) => Cache.GetOrAdd((scenarioId, seed), key =>
    {
        Scenario scenario = Scenarios.Find(key.Item1) ?? throw new ArgumentException($"unknown scenario {key.Item1}");
        MatchRecording recording = MatchSimulator.Generate(scenario, key.Item2);
        var live = new LiveMatch(recording.Match);
        var candidates = new List<(Candidate, int)>();
        foreach (MatchEvent e in recording.Events)
            foreach (Candidate c in live.Ingest(e))
                candidates.Add((c, live.ClockSec));
        return new Played(recording, live, candidates);
    });

    private static readonly ConcurrentDictionary<(string, int), TrackingSynth> Tracking = new();

    /// <summary>The match's synthetic tracking (built once per scenario and seed; it takes a couple of seconds).</summary>
    public static TrackingSynth TrackingOf(string scenarioId, int seed = 1) =>
        Tracking.GetOrAdd((scenarioId, seed), key => new TrackingSynth(Play(key.Item1, key.Item2).Recording));

    /// <summary>A fresh engine fed the match up to (and including) a match second.</summary>
    public static LiveMatch Until(MatchRecording recording, int sec)
    {
        var live = new LiveMatch(recording.Match);
        foreach (MatchEvent e in recording.Events.TakeWhile(e => e.MatchClockSec <= sec)) live.Ingest(e);
        return live;
    }
}
