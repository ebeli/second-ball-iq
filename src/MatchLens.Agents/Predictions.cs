using System.Globalization;
using System.Text;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;

namespace MatchLens.Agents;

public enum PredictionKind
{
    Corner,
    FreeKick,
    Penalty,
    NextGoal,
}

public sealed record PredictionOption(string Id, string Label, string LabelFi, double Probability);

/// <summary>
/// A call Second Ball IQ makes before something happens, and how it turned out. Made by Decision-1 from what the taker
/// (or the match) has shown so far, settled by the event itself, so the track record is visible to everyone.
/// </summary>
/// <param name="Basis">The evidence in plain words, as the viewer and Decision-1 see it.</param>
/// <param name="Source">"Decision-1", or "history" when Decision-1 was unavailable and frequencies were used.</param>
public sealed record Prediction(
    string PredictionId, PredictionKind Kind, int ClockSec, string TeamId, string? PlayerId, string? PlayerName,
    string Question, string QuestionFi, IReadOnlyList<PredictionOption> Options, string Pick,
    string Basis, string BasisFi, string Source, string? EventId = null, int? SettleAtSec = null,
    string? Outcome = null, bool? Correct = null);

/// <summary>Everything needed to make a prediction, gathered synchronously on the replay thread.</summary>
public sealed record PredictionRequest(
    Prediction Draft, string State, string QuestionText, IReadOnlyDictionary<string, string> OptionDescriptions,
    IReadOnlyDictionary<string, double> Fallback);

/// <summary>
/// Makes and settles predictions. Set pieces (corner delivery, penalty and direct free-kick placement) use the taker's
/// history from earlier matches plus what they have done in this match; "who scores next" uses the run of play.
/// Decision-1 turns the evidence into calibrated probabilities; without it, smoothed frequencies are used and the
/// prediction says so.
/// </summary>
public sealed class Predictor(IDecisionClient? decisions, MatchHistory history)
{
    /// <summary>"Who scores next" looks this far ahead.</summary>
    public const int NextGoalWindowSec = 10 * 60;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------------------------------------------------
    // Set pieces
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Prepares a prediction for a corner, penalty or direct free kick that is about to be taken.</summary>
    public PredictionRequest? PrepareSetPiece(MatchEvent e, IReadOnlyList<MatchEvent> soFar, MatchInfo info, int homeGoals, int awayGoals)
    {
        if (e.SetPiece is not { } kind || e.PlayerId is null) return null;
        if (kind == SetPiece.FreeKick && e.Type != MatchEventType.Shot) return null;   // only direct free kicks
        if (kind == SetPiece.Corner && e.Start is null) return null;

        IReadOnlyList<string> zones = SetPieceZones.For(kind);
        string taker = info.Player(e.PlayerId)?.Name ?? history.Player(e.PlayerId)?.Name ?? "the taker";
        string team = info.Team(e.TeamId).Name;

        var past = history.For(e.PlayerId, kind).ToList();
        var thisMatch = soFar.Where(x => x.PlayerId == e.PlayerId && x.SetPiece == kind && x.Type == e.Type && x.Seq < e.Seq)
            .Select(Zone).OfType<string>().ToList();
        // Too little history for this taker: fall back to every taker's record for this kind of set piece.
        List<SetPieceRecord> league = past.Count >= 3 ? [] : history.SetPieces.Where(s => s.Kind == kind).ToList();

        Dictionary<string, int> Count(IEnumerable<string> z) => zones.ToDictionary(k => k, k => z.Count(x => x == k));
        Dictionary<string, int> pastCounts = Count(past.Select(p => p.Zone));
        Dictionary<string, int> matchCounts = Count(thisMatch);
        Dictionary<string, int> leagueCounts = Count(league.Select(p => p.Zone));

        string what = kind switch
        {
            SetPiece.Corner => "corner",
            SetPiece.Penalty => "penalty",
            _ => "direct free kick",
        };
        string whatFi = kind switch
        {
            SetPiece.Corner => "kulmapotkun",
            SetPiece.Penalty => "rangaistuspotkun",
            _ => "vapaapotkun",
        };
        string side = kind == SetPiece.Corner && e.Start is { } s ? (s.Y < 50 ? " from the left" : " from the right") : "";

        var state = new StringBuilder();
        state.Append(Inv, $"{Cap(what)} for {team} at {NarrativeText.Clock(e.MatchClockSec)}, taken by {taker}{side}. ");
        state.Append(Inv, $"Score: {info.Home.Name} {homeGoals}-{awayGoals} {info.Away.Name}.\n");
        state.Append(Describe($"Earlier this season, {taker}'s {what}s", past.Count, pastCounts, "en")).Append('\n');
        state.Append(Describe($"In this match so far, {taker}'s {what}s", thisMatch.Count, matchCounts, "en")).Append('\n');
        if (league.Count > 0)
            state.Append(Describe($"All {what}s by any player in the earlier matches", league.Count, leagueCounts, "en")).Append('\n');
        if (kind != SetPiece.Corner)
            state.Append("Left and right are from the taker's point of view.\n");

        var basisFi = new StringBuilder();
        basisFi.Append(Describe($"Aiemmin tällä kaudella ({taker})", past.Count, pastCounts, "fi")).Append(' ');
        basisFi.Append(Describe("Tässä ottelussa", thisMatch.Count, matchCounts, "fi"));
        if (league.Count > 0) basisFi.Append(' ').Append(Describe("Kaikki potkut aiemmissa otteluissa", league.Count, leagueCounts, "fi"));

        // Fallback: season frequencies, this match counted double, plus one of each so no zone is impossible.
        var weights = zones.ToDictionary(z => z, z => pastCounts[z] + 2.0 * matchCounts[z] + 0.5 * leagueCounts[z] + 1);
        double total = weights.Values.Sum();
        var fallback = weights.ToDictionary(kv => kv.Key, kv => kv.Value / total);

        var options = zones.ToDictionary(z => z, z => (kind, z) switch
        {
            (SetPiece.Corner, SetPieceZones.NearPost) => "Delivered into the near-post area",
            (SetPiece.Corner, SetPieceZones.Central) => "Delivered into the centre of the six-yard box or penalty spot area",
            (SetPiece.Corner, SetPieceZones.FarPost) => "Delivered to the far post",
            (SetPiece.Corner, SetPieceZones.Short) => "Played short to a teammate",
            (_, SetPieceZones.Left) => "Aimed at the left third of the goal, from the taker's view",
            (_, SetPieceZones.Centre) => "Aimed at the centre of the goal",
            _ => "Aimed at the right third of the goal, from the taker's view",
        });

        var draft = new Prediction(
            $"pred-{e.EventId}", kind switch { SetPiece.Corner => PredictionKind.Corner, SetPiece.Penalty => PredictionKind.Penalty, _ => PredictionKind.FreeKick },
            e.MatchClockSec, e.TeamId, e.PlayerId, taker,
            kind == SetPiece.Corner ? $"Where will {taker} deliver this corner?" : $"Where will {taker} put this {what}?",
            kind == SetPiece.Corner ? $"Minne {taker} kierrättää kulmapotkun?" : $"Mihin {taker} laukoo {whatFi}?",
            [], "", state.ToString().Trim(), basisFi.ToString().Trim(), "history", e.EventId);
        return new PredictionRequest(draft, state.ToString(), draft.Question, options, fallback);
    }

    /// <summary>The zone this set piece actually went to, or null if it cannot be told.</summary>
    public static string? Zone(MatchEvent e) => e.SetPiece switch
    {
        SetPiece.Corner when e.Start is { } s && e.End is { } t => SetPieceZones.Corner(s, t),
        SetPiece.Penalty or SetPiece.FreeKick when e.Type == MatchEventType.Shot && e.End is { } t => SetPieceZones.Shot(t),
        _ => null,
    };

    /// <summary>Settles a set-piece prediction with the event it was about.</summary>
    public static Prediction Settle(Prediction p, MatchEvent e)
    {
        string? zone = Zone(e);
        return zone is null ? p : p with { Outcome = zone, Correct = zone == p.Pick };
    }

    // ------------------------------------------------------------------------------------------------------------
    // Who scores next
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Prepares "who scores in the next 10 minutes", from the last 15 minutes of play.</summary>
    public PredictionRequest PrepareNextGoal(LiveMatch match, int clockSec)
    {
        MatchInfo info = match.Info;
        int from = Math.Max(0, clockSec - 15 * 60);
        TeamWindow home = match.Window(info.Home.TeamId, from, clockSec);
        TeamWindow away = match.Window(info.Away.TeamId, from, clockSec);
        string state =
            match.Text.WindowSummary(from, clockSec) + "\n" +
            string.Create(Inv, $"Question window: the next 10 minutes, from {NarrativeText.Clock(clockSec)} to {NarrativeText.Clock(clockSec + NextGoalWindowSec)}. ") +
            "Most 10-minute spells of a football match have no goal at all.";

        // Fallback: goals as a Poisson process at each side's recent xG rate, with a floor for quiet spells.
        double minutes = Math.Max(5, (clockSec - from) / 60.0);
        double homeRate = Math.Max(0.006, home.Xg / minutes) * 10, awayRate = Math.Max(0.006, away.Xg / minutes) * 10;
        double pNone = Math.Exp(-(homeRate + awayRate));
        double pHome = (1 - pNone) * homeRate / (homeRate + awayRate);
        var fallback = new Dictionary<string, double>
        {
            ["home"] = pHome,
            ["away"] = 1 - pNone - pHome,
            ["none"] = pNone,
        };

        string basis = string.Create(Inv,
            $"Last 15 minutes: {info.Home.Name} {home.Shots.Count} shots ({home.Xg:0.00} xG), {info.Away.Name} {away.Shots.Count} shots ({away.Xg:0.00} xG).");
        string basisFi = string.Create(Inv,
            $"Viimeiset 15 minuuttia: {info.Home.Name} {home.Shots.Count} laukausta (xG {home.Xg:0.00}), {info.Away.Name} {away.Shots.Count} laukausta (xG {away.Xg:0.00}).");
        var draft = new Prediction(
            $"pred-next-{clockSec}", PredictionKind.NextGoal, clockSec, "", null, null,
            "Who scores in the next 10 minutes?", "Kuka tekee maalin seuraavan 10 minuutin aikana?",
            [], "", basis, basisFi, "history", SettleAtSec: clockSec + NextGoalWindowSec);
        var options = new Dictionary<string, string>
        {
            ["home"] = $"{info.Home.Name} score the next goal within these 10 minutes",
            ["away"] = $"{info.Away.Name} score the next goal within these 10 minutes",
            ["none"] = "No goal in these 10 minutes",
        };
        return new PredictionRequest(draft, state, "Which of these happens in the next 10 minutes?", options, fallback);
    }

    /// <summary>Settles "who scores next" with the first goal after the prediction, or none when the window closes.</summary>
    public static Prediction SettleNextGoal(Prediction p, MatchInfo info, MatchEvent? goal)
    {
        string outcome = goal is null ? "none" : goal.TeamId == info.Home.TeamId ? "home" : "away";
        return p with { Outcome = outcome, Correct = outcome == p.Pick };
    }

    // ------------------------------------------------------------------------------------------------------------
    // Asking Decision-1
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Longest Decision-1 may take for one prediction.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(3);

    public async Task<Prediction> PredictAsync(PredictionRequest request, MatchInfo info, CancellationToken ct)
    {
        IReadOnlyDictionary<string, double> probabilities = request.Fallback;
        string source = "history";
        if (decisions is not null)
        {
            try
            {
                // Bounded: a slow or failing model never delays a prediction past this; the history-based odds stand in.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timeout);
                var question = new ChoiceQuestion(request.QuestionText, request.OptionDescriptions);
                DecisionResult result = await decisions.DecideAsync(request.State,
                    new Dictionary<string, DecisionQuestion> { ["prediction"] = question }, timeout.Token);
                if (result.Answers["prediction"] is ChoiceAnswer answer)
                {
                    probabilities = request.OptionDescriptions.Keys.ToDictionary(k => k, k => answer.Probabilities.GetValueOrDefault(k));
                    source = "Decision-1";
                }
            }
            catch (Exception ex) when (ex is DecisionException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Fall back to frequencies; the prediction says so (source "history").
            }
        }

        var options = request.OptionDescriptions.Keys
            .Select(k => new PredictionOption(k, OptionLabel(k, "en", info), OptionLabel(k, "fi", info), Math.Round(probabilities.GetValueOrDefault(k), 3)))
            .ToList();
        string pick = options.OrderByDescending(o => o.Probability).First().Id;
        return request.Draft with { Options = options, Pick = pick, Source = source };
    }

    private static string OptionLabel(string id, string language, MatchInfo info) => id switch
    {
        "home" => info.Home.Name,
        "away" => info.Away.Name,
        "none" => language == "fi" ? "Ei maalia" : "No goal",
        _ => SetPieceZones.Label(id, language),
    };

    private static string Describe(string subject, int total, Dictionary<string, int> counts, string language)
    {
        if (total == 0) return language == "fi" ? $"{subject}: ei yhtään." : $"{subject}: none.";
        string parts = string.Join(", ", counts.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value)
            .Select(kv => $"{SetPieceZones.Label(kv.Key, language).ToLowerInvariant()} {kv.Value}"));
        return string.Create(Inv, $"{subject} ({total}): {parts}.");
    }

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
