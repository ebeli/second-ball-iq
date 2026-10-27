using System.Text.RegularExpressions;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MatchLens.Agents;

/// <summary>
/// Decision-1 coach's corner. Two coach agents (Microsoft Agent Framework) with opposite instincts each propose a plan
/// for one team from a menu of measurable plans; Decision-1 picks between them as a choice question with a
/// probability for each; both plans become tactical tests that the match settles, so the record shows how often
/// Decision-1 picked the plan that held. Without models the coaches fall back to plain rules and there is no pick.
/// </summary>
public sealed partial class CoachCorner(IChatClient? chat, IDecisionClient? decisions)
{
    public static TimeSpan CoachTimeout { get; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Runs the corner for one team: both coaches propose (the second must choose a different plan), then Decision-1
    /// picks. Returns the decision without test ids; the caller opens the tests.
    /// </summary>
    public async Task<CoachDecision> RunAsync(string decisionId, LiveMatch match, string teamId, int nowSec,
        double? finalThirdControl, double? opponentFinalThirdControl, CancellationToken ct)
    {
        IReadOnlyList<CoachPlanOption> menu = CoachPlans.Menu(match, teamId, nowSec, finalThirdControl);
        string brief = CoachPlans.Brief(match, teamId, nowSec, finalThirdControl, opponentFinalThirdControl);
        bool ahead = match.Goals(teamId) > match.Goals(match.Info.OpponentOf(teamId));

        var proposals = new List<CoachProposal>();
        foreach (var coach in CoachPlans.Coaches)
        {
            CoachPlanKind? taken = proposals.Count > 0 ? proposals[0].Plan : null;
            CoachProposal? p = chat is null ? null : await AskCoachAsync(coach, menu, brief, match.Info.Team(teamId).Name, taken, ct);
            proposals.Add(p ?? CoachPlans.Fallback(coach, menu, ahead, taken));
        }

        (string? picked, IReadOnlyDictionary<string, double>? probabilities, string source) =
            await CoachPlans.ArbitrateAsync(decisions, match.Info.Team(teamId).Name, menu, proposals, brief, ct);
        return new CoachDecision(decisionId, teamId, nowSec, brief, menu, proposals, picked, probabilities, source, new Dictionary<string, string>());
    }

    private async Task<CoachProposal?> AskCoachAsync((string Id, string Name, string NameFi, string Instincts) coach,
        IReadOnlyList<CoachPlanOption> menu, string brief, string team, CoachPlanKind? taken, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CoachTimeout);
            var agent = new ChatClientAgent(chat!, new ChatClientAgentOptions
            {
                Name = coach.Id,
                ChatOptions = new ChatOptions
                {
                    Instructions = coach.Instincts + """

                        You advise one team for the next ten minutes. Pick exactly one plan id from the menu and say why
                        in one sentence of at most 25 words. Use only numbers that appear in the brief; never invent
                        players, events or history. Players are fictional; refer to the team, not to individuals.
                        """,
                    ResponseFormat = ChatResponseFormat.ForJsonSchema<CoachPick>(),
                },
            });
            string menuText = string.Join("\n", menu.Where(m => m.Kind != taken).Select(m => $"- {m.Kind}: {m.Title}. Decided by: {m.Criterion}"));
            string prompt = $"Team you advise: {team}.\nBrief: {brief}\nMenu:\n{menuText}" +
                            (taken is { } t ? $"\nThe other coach already proposed {t}; choose a different plan." : "");
            AgentResponse<CoachPick> response = await agent.RunAsync<CoachPick>(prompt, cancellationToken: timeout.Token);
            CoachPick? pick = response.Result;
            if (pick is null || !Enum.TryParse(pick.Plan, ignoreCase: true, out CoachPlanKind kind) || kind == taken || menu.All(m => m.Kind != kind)) return null;
            string why = pick.Rationale.Trim();
            // A rationale with a number the brief does not carry is replaced, like any unmeasured number.
            var allowed = Numbers().Matches(brief).Select(m => m.Value).ToHashSet();
            if (why.Length == 0 || why.Length > 220 || Numbers().Matches(why).Any(m => !allowed.Contains(m.Value))) why = CoachPlans.FallbackReason(kind);
            return new CoachProposal(coach.Id, coach.Name, kind, why, FromModel: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\d+(?:[.,]\d+)?")]
    private static partial Regex Numbers();
}
