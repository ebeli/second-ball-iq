using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>
/// Something a detector thinks might deserve airtime. Carries the two texts the decision gates need:
/// a neutral window summary for triage, and kind-specific derived facts for grounding.
/// </summary>
/// <param name="DetectedAtSec">Match clock when the detector fired.</param>
/// <param name="FromSec">Start of the window the pattern was found in.</param>
/// <param name="ToSec">End of that window (usually <paramref name="DetectedAtSec"/>).</param>
/// <param name="Facts">Measured numbers, e.g. "highRecoveries": 6. The Validator checks insight figures against these.</param>
/// <param name="WindowSummary">
/// Neutral prose about both teams in the window, for the triage gate. Written the same way whichever detector fired,
/// so triage judges the match, not the detector.
/// </param>
/// <param name="Evidence">Derived facts in plain language for the grounding gate and the narrator.</param>
/// <param name="Tags">Words the numbers cannot carry: a formation ("3-4-1-2"), a role ("falseNine"), a cause.</param>
public sealed record Candidate(
    string CandidateId,
    InsightKind Kind,
    string TeamId,
    string? PlayerId,
    int DetectedAtSec,
    int FromSec,
    int ToSec,
    IReadOnlyList<string> EvidenceEventIds,
    IReadOnlyDictionary<string, double> Facts,
    string WindowSummary,
    string Evidence,
    IReadOnlyDictionary<string, string>? Tags = null);
