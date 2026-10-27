using MatchLens.Agents;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;
using Xunit;

namespace MatchLens.Tests;

public class ValidatorTests
{
    [Fact]
    public void Template_drafts_pass_for_every_moment_and_persona()
    {
        Matches.Played m = Matches.Play("demo-match");
        foreach ((Candidate c, _) in m.Candidates)
            foreach (Persona p in Persona.DemoSet)
            {
                ValidationResult r = Validator.Check(c, Templates.Analysis(c), Templates.Draft(c, p, m.Recording.Match), m.Live);
                Assert.True(r.Passed, $"{c.Kind} {p.Key}: {string.Join("; ", r.Problems)}");
            }
    }

    [Fact]
    public void An_unknown_cited_event_fails_the_evidence_check()
    {
        Matches.Played m = Matches.Play("demo-match");
        Candidate c = m.Candidates.First(x => x.Candidate.Kind == InsightKind.PressingShift).Candidate;
        AnalysisResult analysis = Templates.Analysis(c);
        analysis.CitedEventIds = [.. analysis.CitedEventIds, "evt-9999999"];
        ValidationResult r = Validator.Check(c, analysis, Templates.Draft(c, Persona.DemoSet[0], m.Recording.Match), m.Live);
        Assert.Equal(CheckStatus.Fail, r.Evidence);
        Assert.Contains(r.Problems, p => p.Contains("evt-9999999"));
    }

    [Fact]
    public void A_measured_share_passes_as_a_percentage_and_with_a_Finnish_decimal_comma()
    {
        Matches.Played m = Matches.Play("demo-match");
        Candidate c = m.Candidates.First(x => x.Candidate.Kind == InsightKind.PossessionIllusion).Candidate;
        double share = c.Facts["possessionShare"];
        string pct = Math.Round(share * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        string comma = Math.Round(share, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
        var draft = new NarrativeDraft { Headline = $"{pct}% of the ball", Summary = $"Osuus {comma}." };
        Assert.True(Validator.Check(c, Templates.Analysis(c), draft, m.Live).Passed);
    }

    [Fact]
    public void A_measured_formation_is_a_name_but_an_invented_one_fails()
    {
        Matches.Played m = Matches.Play("demo-match");
        Candidate c = m.Candidates.First(x => x.Candidate.Tags?.GetValueOrDefault("formationAfter") == "3-4-1-2").Candidate;
        AnalysisResult a = Templates.Analysis(c);
        Assert.True(Validator.Check(c, a, new NarrativeDraft { Headline = "Northvale go to a 3-4-1-2", Summary = "A back three now." }, m.Live).Passed);
        Assert.False(Validator.Check(c, a, new NarrativeDraft { Headline = "Northvale go to a 5-3-2", Summary = "A back five now." }, m.Live).Passed);
    }
}

public class FaultDrillTests
{
    [Theory]
    [InlineData("demo-match")]
    [InlineData("game-state")]
    [InlineData("possession-illusion")]
    public void A_planted_number_is_always_caught_by_the_Validator(string scenarioId)
    {
        Matches.Played m = Matches.Play(scenarioId);
        Persona analyst = Persona.DemoSet.First(p => p.Key == "Analyst-en");
        foreach ((Candidate c, _) in m.Candidates)
        {
            (NarrativeDraft planted, string what) = FaultDrill.Plant(new PlantedError(PlantedError.Number), Templates.Draft(c, analyst, m.Recording.Match), c, m.Live);
            ValidationResult r = Validator.Check(c, Templates.Analysis(c), planted, m.Live);
            Assert.False(r.Passed, $"{c.Kind}: planted \"{what}\" passed");
            Assert.Equal(CheckStatus.Fail, r.Numbers);
        }
    }

    [Fact]
    public void A_planted_claim_keeps_the_headline_and_adds_one_sentence()
    {
        Matches.Played m = Matches.Play("demo-match");
        Candidate c = m.Candidates[0].Candidate;
        NarrativeDraft draft = Templates.Draft(c, Persona.DemoSet[0], m.Recording.Match);
        (NarrativeDraft planted, string what) = FaultDrill.Plant(new PlantedError(PlantedError.Claim), draft, c, m.Live);
        Assert.Equal(draft.Headline, planted.Headline);
        Assert.EndsWith(what, planted.Summary);
        Assert.True(Validator.Check(c, Templates.Analysis(c), planted, m.Live).Passed);   // no number: grounding's job
    }

    [Fact]
    public void Only_number_and_claim_are_known_kinds()
    {
        Assert.True(PlantedError.IsKnown("number"));
        Assert.True(PlantedError.IsKnown("claim"));
        Assert.False(PlantedError.IsKnown("drop-table"));
        Assert.False(PlantedError.IsKnown(null));
    }
}
