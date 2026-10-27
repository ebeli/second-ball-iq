using MatchLens.Agents;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;
using Xunit;

namespace MatchLens.Tests;

/// <summary>Fixes from the first full live run (Oct 26).</summary>
public class LiveRunFixTests
{
    [Fact]
    public void A_share_that_rounds_up_in_binary_is_still_a_measured_fact()
    {
        // 0.285 × 100 is 28.499999999999996: the template writes "29%", which the Validator used to reject.
        var played = Matches.Play("demo-match");
        LiveMatch live = Matches.Until(played.Recording, 89 * 60);
        MatchInfo info = played.Recording.Match;
        var c = new Candidate("cand-x", InsightKind.Territory, info.Away.TeamId, null, 5359, 5059, 5359, [],
            new Dictionary<string, double> { ["finalThirdControl"] = 0.373, ["finalThirdControlBefore"] = 0.285, ["windowMinutes"] = 5, ["shots"] = 2, ["finalThirdPasses"] = 0 },
            "", "", new Dictionary<string, string> { ["source"] = "tracking" });
        foreach (Persona p in Persona.DemoSet)
        {
            NarrativeDraft d = Templates.Draft(c, p, info);
            ValidationResult v = Validator.Check(c, Templates.Analysis(c), d, live);
            Assert.True(v.Problems.Count == 0, $"{p.Key}: {string.Join("; ", v.Problems)} in \"{d.Summary}\"");
        }
    }

    [Fact]
    public void Reads_measured_over_minutes_are_grounded_without_the_event_list_check()
    {
        Assert.Equal("outcomesMatch,fairReading", string.Join(",", GroundingGate.PatternQuestions.Keys));
        Assert.True(GroundingGate.Questions.ContainsKey("eventsListed"));
    }

    [Fact]
    public void A_he_or_she_fails_the_Validator()
    {
        var played = Matches.Play("demo-match");
        (Candidate c, _) = played.Candidates.First(x => x.Candidate.Kind == InsightKind.PlayerRole);
        LiveMatch live = Matches.Until(played.Recording, c.DetectedAtSec);
        NarrativeDraft good = Templates.Draft(c, Persona.DemoSet[0], played.Recording.Match);
        Assert.True(Validator.Check(c, Templates.Analysis(c), good, live).Passed);
        var bad = new NarrativeDraft { Headline = good.Headline, Summary = good.Summary + " He is everywhere.", FigureKeys = good.FigureKeys };
        ValidationResult v = Validator.Check(c, Templates.Analysis(c), bad, live);
        Assert.False(v.Passed);
        Assert.Contains(v.Problems, p => p.Contains("\"he\""));
    }
}
