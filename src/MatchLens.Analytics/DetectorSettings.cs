namespace MatchLens.Analytics;

/// <summary>
/// Detector thresholds. Tuned in the Python prototype on 50 seeds per scenario (Oct 15):
/// pressing shift 47/50 recall with 0.44 false alarms per balanced match; possession illusion 50/50 recall
/// with 0.04 false alarms per balanced match. These are deliberately independent of the ground-truth rules.
/// </summary>
public sealed record DetectorSettings
{
    public static DetectorSettings Default { get; } = new();

    /// <summary>How often window detectors run, in match seconds. Event detectors run on every event.</summary>
    public int EvaluateEverySec { get; init; } = 60;

    // Pressing shift: a sustained rise in high recoveries that is already producing shots.
    public int PressWindowSec { get; init; } = 8 * 60;
    public int PressMinRecoveries { get; init; } = 5;
    public int PressMinRiseOverPrevious { get; init; } = 3;
    public int PressMinShotsAfterRecovery { get; init; } = 1;
    public int PressCooldownSec { get; init; } = 10 * 60;

    // Possession illusion: lots of the ball, nothing to show for it.
    public int PossessionWindowSec { get; init; } = 10 * 60;
    public double PossessionMinShare { get; init; } = 0.72;
    public int PossessionMinCompletedPasses { get; init; } = 45;
    public int PossessionMaxShots { get; init; } = 1;
    public double PossessionMaxXg { get; init; } = 0.15;
    public int PossessionCooldownSec { get; init; } = 15 * 60;

    // Turnover to chance: a shot soon after winning the ball high, with no opponent possession in between.
    public int ChanceMaxSecondsAfterRecovery { get; init; } = 15;

    /// <summary>Speculative shots are not a story: below this xG only goals are reported.</summary>
    public double ChanceMinXg { get; init; } = 0.08;

    // Shape (tuned in the Python prototype on 30 seeds, Oct 17). Checked every few minutes over a sliding window.
    public int ShapeCheckEverySec { get; init; } = 3 * 60;
    public int ShapeWindowSec { get; init; } = 15 * 60;

    /// <summary>A new formation must fit this much better than the established one, in two checks running.</summary>
    public double FormationSwitchMargin { get; init; } = 1.5;
    public int FormationConfirmChecks { get; init; } = 2;

    /// <summary>Style shifts compare the last window with everything since kick-off or the last shift of that kind.</summary>
    public int StyleMinBaselineSec { get; init; } = 10 * 60;
    public int StyleFirstCheckSec { get; init; } = 25 * 60;
    public double DeeperMinHighPressZ { get; init; } = 2.2;
    public double DeeperMinPressZ { get; init; } = 2.8;
    public double DirectMinZ { get; init; } = 3.0;
    public double DirectMinLongBallShare { get; init; } = 0.25;
    public int ShiftCooldownSec { get; init; } = 20 * 60;

    /// <summary>Roles: read over this window, confirmed in two checks, at most one new role per team this often.</summary>
    public int RoleWindowSec { get; init; } = 25 * 60;
    public int RoleFirstCheckSec { get; init; } = 17 * 60;
    public int RoleMinTouches { get; init; } = 20;
    public int RoleTeamCooldownSec { get; init; } = 3 * 60;

    // Player milestones: completed passes in this match, only for the match's leading passer.
    public IReadOnlyList<int> PassMilestones { get; init; } = [50, 75, 100];
}
