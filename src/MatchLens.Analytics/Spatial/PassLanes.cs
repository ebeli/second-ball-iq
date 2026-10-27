using MatchLens.Contracts;

namespace MatchLens.Analytics.Spatial;

/// <summary>What stood between a pass and its target when it was played, from the tracking frame at that moment.</summary>
/// <param name="LaneDefenders">Opponents within 2 m of the ball's path.</param>
/// <param name="Interceptable">Opponents who could reach the path before the ball does (a lofted ball can only be cut out near its ends).</param>
/// <param name="ReceiverMarked">An opponent within 2.5 m of where the pass was aimed.</param>
public sealed record PassLane(int LaneDefenders, int Interceptable, bool ReceiverMarked);

/// <summary>One option a passer had: a teammate, how hard the pass to him was, how likely it was to arrive, and its threat.</summary>
/// <param name="X">Target, in the passing team's frame (percent).</param>
/// <param name="Completion">Chance the pass arrives, from its difficulty (fitted on simulated matches, see <see cref="XtGrid"/>).</param>
/// <param name="Xt">Expected threat where it would arrive.</param>
/// <param name="Value">Completion × xT: the threat the pass is expected to deliver.</param>
public sealed record PassOption(string? PlayerId, double X, double Y, int Difficulty, double Completion, double Xt, double Value);

/// <summary>A pass judged against the passer's other options at the moment he played it.</summary>
/// <param name="Better">The best other option, when it would have delivered clearly more threat at a decent chance of arriving.</param>
/// <param name="Gain">How much more threat (xT) the best option was expected to deliver; zero or less when the pass played was best.</param>
public sealed record PassChoice(string EventId, string TeamId, string? PlayerId, int Sec, PassLane Lane, PassOption Chosen, PassOption? Best, PassOption? Better, double Gain);

/// <summary>
/// Passing lanes from tracking: the pass difficulty upgraded with the defenders in the lane, the chance a pass
/// arrives, and the "better pass available" check: the teammate whose pass would have delivered more threat.
/// </summary>
public static class PassLanes
{
    /// <summary>The ball's average speed along a ground pass, m/s, when the feed has none.</summary>
    public const double DefaultBallSpeed = 15;

    /// <summary>A better option must add at least this much xT and arrive at least this often to count.</summary>
    public const double BetterGain = 0.02;
    public const double BetterMinCompletion = 0.55;

    /// <summary>Who is in the lane between two points (tracking metres), against these opponents.</summary>
    public static PassLane Measure(double sx, double sy, double tx, double ty, IEnumerable<(double X, double Y)> opponents, double ballSpeed, bool lofted)
    {
        double dx = tx - sx, dy = ty - sy, len2 = dx * dx + dy * dy, len = Math.Sqrt(len2);
        int lane = 0, inter = 0;
        bool marked = false;
        foreach ((double ox, double oy) in opponents)
        {
            if ((ox - tx) * (ox - tx) + (oy - ty) * (oy - ty) <= 2.5 * 2.5) marked = true;
            if (len2 < 1) continue;
            double u = ((ox - sx) * dx + (oy - sy) * dy) / len2;
            if (u < 0.05 || u > 1) continue;
            double px = sx + u * dx, py = sy + u * dy;
            double d = Math.Sqrt((ox - px) * (ox - px) + (oy - py) * (oy - py));
            if (d <= 2) lane++;
            // Over a lofted ball only its first and last few metres are within reach.
            if (lofted && u > 0.15 && u < 0.85) continue;
            double ballAt = u * len / Math.Max(4, ballSpeed);
            double defenderAt = 0.3 + Math.Max(0, d - 1) / 7;   // a step and a stretch, then a run
            if (defenderAt <= ballAt) inter++;
        }
        return new PassLane(lane, inter, marked);
    }

    /// <summary>
    /// Pass difficulty with the lane, 0 to 100: the event-only rating's ingredients (<see cref="BallMetrics.PassDifficulty"/>)
    /// rescaled to make room, plus 10 for each opponent who could cut the pass out (up to 20) and 10 when the target
    /// is marked.
    /// </summary>
    public static int Difficulty(PitchPoint s, PitchPoint t, bool underPressure, bool lofted, PassLane lane)
    {
        double metres = Pitch.DistanceMetres(s, t);
        double forward = (t.X - s.X) / 100 * Pitch.LengthMetres;
        double score = 4;
        score += Math.Min(metres, 60) / 60 * 30;                       // length: up to 30
        if (underPressure) score += 12;                                // a defender on the passer
        score += Math.Clamp(forward, 0, 30) / 30 * 10;                 // moving the ball forward: up to 10
        if (t.X >= Pitch.BoxStartX && t.Y >= Pitch.BoxMinY && t.Y <= Pitch.BoxMaxY) score += 10;
        else if (t.X >= Pitch.AttackingThirdStart) score += 5;
        if (lofted) score += 4;
        score += Math.Min(2, lane.Interceptable) * 10;                 // opponents who can cut it out
        if (lane.ReceiverMarked) score += 10;                          // the target is marked
        return (int)Math.Round(Math.Clamp(score, 0, 100));
    }

    /// <summary>Chance a pass of this difficulty arrives (a logistic fitted on simulated matches).</summary>
    public static double Completion(int difficulty) => 1 / (1 + Math.Exp(-(XtGrid.CompletionIntercept + XtGrid.CompletionSlope * difficulty)));

    /// <summary>
    /// Judges a pass at the moment it was played (<paramref name="frame"/>): its lane and difficulty, and every other
    /// teammate the passer could have found, by the threat each pass was expected to deliver. A teammate in an offside
    /// position is not an option. Null when the event is not a pass with both ends.
    /// </summary>
    public static PassChoice? Evaluate(MatchEvent e, TrackingFrame frame, MatchInfo info)
    {
        if (e.Type != MatchEventType.Pass || e.Start is not { } s || e.End is not { } t) return null;
        string team = e.TeamId;
        (double sx, double sy) = TrackingFrameOf.Metres(s, team, info);
        var opponents = frame.Players.Where(p => p.TeamId != team).Select(p => (p.X, p.Y)).ToList();
        double speed = e.BallSpeedKmh is { } kmh ? kmh / 3.6 : DefaultBallSpeed;
        bool under = e.UnderPressure == true;

        PassOption Option(string? playerId, PitchPoint target)
        {
            (double tx, double ty) = TrackingFrameOf.Metres(target, team, info);
            double metres = Math.Sqrt((tx - sx) * (tx - sx) + (ty - sy) * (ty - sy));
            bool lofted = e.LongBall == true && playerId == e.RecipientId || metres > 32;
            PassLane lane = Measure(sx, sy, tx, ty, opponents, speed, lofted);
            int d = Difficulty(s, target, under, lofted, lane);
            double p = Completion(d), xt = ExpectedThreat.At(target.X, target.Y);
            return new PassOption(playerId, Math.Round(target.X, 1), Math.Round(target.Y, 1), d, Math.Round(p, 3), Math.Round(xt, 4), Math.Round(p * xt, 4));
        }

        (double ex, double ey) = TrackingFrameOf.Metres(t, team, info);
        PassLane chosenLane = Measure(sx, sy, ex, ey, opponents, speed, e.LongBall == true || Pitch.DistanceMetres(s, t) > 32);
        PassOption chosen = Option(e.RecipientId, t);

        // The offside line for this team: the second-last opponent (keeper included), the ball, or halfway, in its frame.
        var oppX = frame.Players.Where(p => p.TeamId != team).Select(p => TrackingFrameOf.Own(p.X, p.Y, team, info).X).OrderByDescending(x => x).ToList();
        double line = Math.Max(oppX.Count >= 2 ? oppX[1] : 100, Math.Max(s.X, 50));

        PassOption? best = null;
        foreach (PlayerFix mate in frame.Players)
        {
            if (mate.TeamId != team || mate.PlayerId == e.PlayerId) continue;
            if (info.Player(mate.PlayerId)?.Position == PlayerPosition.Goalkeeper) continue;
            PitchPoint at = TrackingFrameOf.Own(mate.X, mate.Y, team, info);
            if (at.X > line + 0.5) continue;
            PassOption o = Option(mate.PlayerId, at);
            if (best is null || o.Value > best.Value) best = o;
        }
        double gain = best is null ? 0 : Math.Round(best.Value - chosen.Value, 4);
        PassOption? better = best is not null && best.PlayerId != e.RecipientId && gain >= BetterGain && best.Completion >= BetterMinCompletion ? best : null;
        return new PassChoice(e.EventId, team, e.PlayerId, e.MatchClockSec, chosenLane, chosen with { }, best, better, gain);
    }
}
