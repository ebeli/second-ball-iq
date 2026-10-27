using System.Globalization;
using MatchLens.Analytics;
using MatchLens.Contracts;

namespace MatchLens.Agents;

public enum CallKind
{
    KickOff,
    HalfTime,
    FullTime,
    Goal,
    BigChance,
    SetPiece,
    PredictionResult,
    StatFlash,
    Substitution,
}

/// <summary>A short live call, the way a commentator would shout it. Intensity 1 (aside) to 3 (goal).</summary>
public sealed record Call(
    string CallId, int ClockSec, CallKind Kind, string TeamId, string? PlayerId, int Intensity, string Text, string TextFi);

/// <summary>
/// The live commentator: instant calls on goals, big chances, set pieces and players on a roll, in English and
/// Finnish. Calls are built from the event and the engine's counts, never from a model, so they are immediate and
/// every name and number in them is right. Wording varies so a match does not repeat itself.
/// </summary>
public sealed class Commentator(MatchInfo info)
{
    /// <summary>Shots below this xG are not called unless they go in.</summary>
    public const double BigChanceXg = 0.15;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Dictionary<string, int> _shots = new();
    private readonly Dictionary<string, int> _regains = new();
    private readonly Dictionary<string, int> _goals = new();
    private double _fastestShot;
    private int? _lastPassCallSec;

    /// <summary>At most one long-pass call in this many match seconds, so a direct team does not flood the line.</summary>
    public const int PassCallGapSec = 6 * 60;

    /// <summary>Calls for one event, after the engine has counted it.</summary>
    public IReadOnlyList<Call> OnEvent(MatchEvent e, LiveMatch live)
    {
        var calls = new List<Call>();
        string? player = Name(e.PlayerId);
        string team = info.Team(e.TeamId).Name;
        int h = live.Goals(info.Home.TeamId), a = live.Goals(info.Away.TeamId);
        string score = $"{info.Home.Name} {h}-{a} {info.Away.Name}";

        switch (e.Type)
        {
            case MatchEventType.PeriodStart:
                calls.Add(Make(e, CallKind.KickOff, 1,
                    e.Period == 1 ? "And we're under way!" : "The second half is under way!",
                    e.Period == 1 ? "Ottelu on käynnissä!" : "Toinen puoliaika alkaa!"));
                break;

            case MatchEventType.PeriodEnd:
                calls.Add(e.Period == 1
                    ? Make(e, CallKind.HalfTime, 2, $"Half time: {score}.", $"Puoliaika: {score}.")
                    : Make(e, CallKind.FullTime, 3, $"Full time! It finishes {score}!", $"Ottelu on päättynyt! Lopputulos {score}!"));
                break;

            case MatchEventType.Goal:
            {
                string who = player ?? team;
                int n = Bump(_goals, e.PlayerId);
                string[] en =
                [
                    $"GOAL! {who}! {score}!",
                    $"It's in! {who} scores for {team}! {score}!",
                    $"{who} finds the net! What a moment for {team}! {score}!",
                ];
                string[] fi =
                [
                    $"MAALI! {who}! {score}!",
                    $"Se meni sisään! {who} iskee, {team}! {score}!",
                    $"{who} löytää maalin! Mikä hetki joukkueelle {team}! {score}!",
                ];
                calls.Add(Make(e, CallKind.Goal, 3, Pick(en, e), Pick(fi, e)));
                MatchEvent? shot = live.Events.LastOrDefault(x => x.Type == MatchEventType.Shot && x.TeamId == e.TeamId);
                if (shot is not null && BallMetrics.ShotKmh(shot) is { } goalKmh)
                {
                    if (goalKmh >= BallMetrics.FastShotKmh)
                        calls.Add(Make(e, CallKind.StatFlash, 2,
                            $"That one was clocked at {goalKmh.ToString("0", Inv)} km/h!",
                            $"Laukauksen nopeus oli {goalKmh.ToString("0", Inv)} km/h!", "-speed"));
                    _fastestShot = Math.Max(_fastestShot, goalKmh);
                }
                if (n >= 2 && player is not null)
                    calls.Add(Make(e, CallKind.StatFlash, 2,
                        n == 2 ? $"That's two for {player} today!" : $"{player} has {n} goals today!",
                        $"{player} tekee jo {n}. maalinsa tässä ottelussa!", "-flash"));
                break;
            }

            case MatchEventType.Shot:
            {
                int n = Bump(_shots, e.PlayerId);
                string who = player ?? team;
                bool big = (e.Xg ?? 0) >= BigChanceXg && e.ShotResult != ShotResult.Goal && e.SetPiece is null;
                if (big)
                {
                    (string[] en, string[] fi) = e.ShotResult switch
                    {
                        ShotResult.Saved => (
                            new[] { $"What a save! {who} is denied!", $"Saved! {who} had to score there!", $"Brilliant stop to keep out {who}!" },
                            new[] { $"Mikä torjunta! {who} ei saa palloa sisään!", $"Torjuttu! Tuosta {who} olisi pitänyt tehdä maali!", $"Upea torjunta, {who} jää ilman!" }),
                        ShotResult.Blocked => (
                            new[] { $"Blocked! Bodies on the line to stop {who}!", $"{who} pulls the trigger, and it's charged down!" },
                            new[] { $"Blokattu! Puolustus heittäytyy eteen!", $"{who} laukoo, mutta laukaus blokataan!" }),
                        _ => (
                            new[] { $"Oh, so close from {who}!", $"{who} drags it wide! What a chance!", $"Inches away! {who} can't believe it!" },
                            new[] { $"Läheltä piti, {who}!", $"{who} laukoo ohi! Mikä paikka!", $"Senttejä vailla! {who} ei usko silmiään!" }),
                    };
                    calls.Add(Make(e, CallKind.BigChance, 2, Pick(en, e), Pick(fi, e)));
                }
                if (player is not null && (n == 3 || n == 5))
                    calls.Add(Make(e, CallKind.StatFlash, 1,
                        $"That's {Ordinal(n)} shot of the match for {player}.",
                        $"{player} laukoo jo {n}. kerran tässä ottelussa.", "-flash"));
                // Speed: tagged from BallMetrics.FastShotKmh; "the hardest of the match" only when it beats the last.
                // Speed: tagged from BallMetrics.FastShotKmh; "the hardest of the match" only when it beats the last.
                // A goal's speed is called after the goal itself (the Goal event follows its shot).
                if (BallMetrics.ShotKmh(e) is { } kmh && kmh >= BallMetrics.FastShotKmh && e.ShotResult != ShotResult.Goal)
                {
                    bool record = kmh > _fastestShot;
                    string speed = kmh.ToString("0", Inv);
                    calls.Add(Make(e, CallKind.StatFlash, 2,
                        record ? $"{speed} km/h from {who}! The hardest shot of the match so far!" : $"{who} hits that at {speed} km/h!",
                        record ? $"{speed} km/h, {who}! Ottelun kovin laukaus tähän mennessä!" : $"{who} laukoo {speed} km/h!",
                        "-speed"));
                }
                if (BallMetrics.ShotKmh(e) is { } any && e.ShotResult != ShotResult.Goal) _fastestShot = Math.Max(_fastestShot, any);
                break;
            }

            case MatchEventType.Pass:
            {
                // A long, hard pass that arrives: tagged from BallMetrics.LongPassMetres and HardPass.
                if (player is null || e.SetPiece is not null || !BallMetrics.IsLongCompletedPass(e)) break;
                if (BallMetrics.PassDifficulty(e) is not { } d || d < BallMetrics.HardPass) break;
                if (_lastPassCallSec is { } last && e.MatchClockSec - last < PassCallGapSec) break;
                _lastPassCallSec = e.MatchClockSec;
                string m = BallMetrics.PassMetres(e)!.Value.ToString("0", Inv);
                calls.Add(Make(e, CallKind.StatFlash, 1,
                    $"What a ball from {player}! {m} metres, and right on target.",
                    $"{player} lyö upean pallon: {m} metriä ja suoraan perille!", "-pass"));
                break;
            }

            case MatchEventType.Substitution:
            {
                string? on = Name(e.RecipientId);
                if (on is null || player is null) break;
                calls.Add(Make(e, CallKind.Substitution, 1,
                    $"Change for {team}: {on} comes on for {player}.",
                    $"Vaihto, {team}: {on} sisään, {player} ulos."));
                break;
            }

            default:
                if (Pitch.IsHighRecovery(e) && player is not null)
                {
                    int n = Bump(_regains, e.PlayerId);
                    if (n == 3 || n == 5)
                        calls.Add(Make(e, CallKind.StatFlash, 1,
                            $"{player} has won the ball high up the pitch {n} times already. Relentless.",
                            $"{player} on riistänyt pallon korkealla jo {n} kertaa. Hellittämätöntä!", "-flash"));
                }
                break;
        }
        return calls;
    }

    private double _fastestRun;
    private bool _tenKm;

    /// <summary>
    /// A call when tracking shows something worth saying: the fastest sprint of the match so far, or the first player
    /// to cover 10 km. Other threshold crossings only tag the player on screen.
    /// </summary>
    public Call? OnRunning(KinematicAlert a)
    {
        string? name = Name(a.PlayerId);
        if (name is null) return null;
        string id = $"call-run-{a.PlayerId}-{(int)a.AtSec}";
        if (a.Kind == KinematicAlertKind.TopSpeed && a.SpeedKmh > _fastestRun + 0.05)
        {
            bool first = _fastestRun == 0;
            _fastestRun = a.SpeedKmh;
            string kmh = a.SpeedKmh.ToString("0.0", Inv);
            return new Call(id, (int)a.AtSec, CallKind.StatFlash, a.TeamId, a.PlayerId, 1,
                first ? $"{name} hits {kmh} km/h!" : $"{name} is flying! {kmh} km/h, the fastest sprint of the match so far.",
                first ? $"{name} kiihdyttää {kmh} km/h!" : $"{name} kiitää! {kmh} km/h, ottelun nopein spurtti tähän mennessä.");
        }
        if (a.Kind == KinematicAlertKind.Distance && a.DistanceMetres >= 10_000 && !_tenKm)
        {
            _tenKm = true;
            return new Call(id, (int)a.AtSec, CallKind.StatFlash, a.TeamId, a.PlayerId, 1,
                $"{name} has covered 10 km already, the first in this match to get there.",
                $"{name} on juossut jo 10 km, ensimmäisenä tässä ottelussa.");
        }
        return null;
    }

    /// <summary>The call before a set piece: who is taking it and what the prediction says.</summary>
    public Call PredictionMade(Prediction p)
    {
        PredictionOption best = p.Options.First(o => o.Id == p.Pick);
        string pct = Math.Round(best.Probability * 100).ToString("0", Inv);
        string team = p.TeamId.Length > 0 ? info.Team(p.TeamId).Name : "";
        (string en, string fi) = p.Kind switch
        {
            PredictionKind.Corner => (
                $"Corner to {team}. {p.PlayerName} to take. Our call: {best.Label.ToLowerInvariant()}, {pct}%.",
                $"Kulmapotku, {team}. Potkaisijana {p.PlayerName}. Ennusteemme: {best.LabelFi.ToLowerInvariant()}, {pct} %."),
            PredictionKind.Penalty => (
                $"Penalty! {p.PlayerName} steps up. History says {best.Label.ToLowerInvariant()}, {pct}%.",
                $"Rangaistuspotku! {p.PlayerName} asettuu pallon taakse. Historia sanoo: {best.LabelFi.ToLowerInvariant()}, {pct} %."),
            PredictionKind.FreeKick => (
                $"Free kick in shooting range. {p.PlayerName} over the ball. Our call: {best.Label.ToLowerInvariant()}, {pct}%.",
                $"Vapaapotku laukaisuetäisyydeltä. {p.PlayerName} pallon takana. Ennusteemme: {best.LabelFi.ToLowerInvariant()}, {pct} %."),
            _ => (
                $"Next ten minutes? Our call: {best.Label.ToLowerInvariant()}, {pct}%.",
                $"Seuraavat kymmenen minuuttia? Ennusteemme: {best.LabelFi.ToLowerInvariant()}, {pct} %."),
        };
        return new Call($"call-{p.PredictionId}", p.ClockSec, CallKind.SetPiece, p.TeamId, p.PlayerId, 2, en, fi);
    }

    /// <summary>The call after a prediction settles: right or wrong, said out loud.</summary>
    public Call? PredictionSettled(Prediction p, int clockSec)
    {
        if (p.Outcome is null) return null;
        PredictionOption? actual = p.Options.FirstOrDefault(o => o.Id == p.Outcome);
        string label = actual?.Label ?? p.Outcome, labelFi = actual?.LabelFi ?? p.Outcome;
        (string en, string fi) = (p.Kind, p.Correct == true) switch
        {
            (PredictionKind.NextGoal, true) => ($"Ten minutes up, and we called it: {label.ToLowerInvariant()}.", $"Kymmenen minuuttia täynnä, ja ennuste osui: {labelFi.ToLowerInvariant()}."),
            (PredictionKind.NextGoal, false) => ($"Ten minutes up: {label.ToLowerInvariant()}. Not what we expected!", $"Kymmenen minuuttia täynnä: {labelFi.ToLowerInvariant()}. Ei mennyt ennusteen mukaan!"),
            (_, true) => ($"{label}, just as we called it!", $"{labelFi}, juuri kuten ennustimme!"),
            _ => ($"{label}! Not what the history said.", $"{labelFi}! Historia ennusti toisin."),
        };
        return new Call($"call-{p.PredictionId}-result", clockSec, CallKind.PredictionResult, p.TeamId, p.PlayerId,
            p.Correct == true ? 2 : 1, en, fi);
    }

    private Call Make(MatchEvent e, CallKind kind, int intensity, string en, string fi, string suffix = "") =>
        new($"call-{e.EventId}{suffix}", e.MatchClockSec, kind, e.TeamId, e.PlayerId, intensity, en, fi);

    private string? Name(string? playerId) => info.Player(playerId)?.Name;

    private static int Bump(Dictionary<string, int> counts, string? key)
    {
        if (key is null) return 0;
        counts[key] = counts.GetValueOrDefault(key) + 1;
        return counts[key];
    }

    private static string Pick(string[] options, MatchEvent e) => options[e.Seq % options.Length];

    private static string Ordinal(int n) => n switch { 1 => "the first", 2 => "the second", 3 => "the third", 4 => "the fourth", 5 => "the fifth", _ => $"the {n}th" };
}
