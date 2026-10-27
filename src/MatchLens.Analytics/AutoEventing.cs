using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>
/// Auto-eventing: football events found from tracking positions alone, with no event feed. This is the step a
/// computer-vision pipeline does after it has turned video into player and ball positions; here the positions come
/// from Match Forge's synthetic tracking, so every detection can be scored against the events the match really had.
/// <list type="bullet">
/// <item><b>Control</b>: the nearest player within <see cref="ControlRadius"/> of a slow, low ball, for two frames.</item>
/// <item><b>Passes</b>: the ball leaves one player's control, travels, and the next player controls it. Same team: a
/// completed pass; the other team: a failed pass and an interception. Length from release to control; ball speed
/// from the first half-second of flight; under pressure when an opponent was within <see cref="PressureRadius"/> at
/// release.</item>
/// <item><b>Shots</b>: a fast ball from within shooting range, heading for the goal mouth. A goal when it crosses
/// the line between the posts and under the bar; saved when the keeper controls it; blocked when an outfield
/// opponent stops it close by; off target when it goes out.</item>
/// <item><b>Ball wins</b>: an opponent takes control without the ball having flown (a tackle).</item>
/// </list>
/// It works on a stream: frames go in one by one and detections come out about half a second later.
/// </summary>
public sealed class AutoEventer(MatchInfo info, double hz = 10)
{
    public const double ControlRadius = 1.3;
    /// <summary>A ball faster than this (m/s, about 16 km/h) is travelling, not under anyone's control.</summary>
    public const double ControlMaxSpeed = 4.5;
    public const double PressureRadius = 3.0;
    /// <summary>The player on the ball keeps it while within this distance, unless someone else controls it.</summary>
    public const double OwnerKeepRadius = 2.0;
    public const double ShotMinSpeedKmh = 69;
    /// <summary>A shot stopped by an outfield player this close to where it was struck counts as blocked.</summary>
    public const double BlockMetres = 8;
    public const double ShotMaxDistanceMetres = 42;
    /// <summary>A shot's line passes at least this close to the centre of the goal.</summary>
    public const double GoalWindowMetres = 12;

    private const double GoalHalfWidth = 7.32 / 2, BarHeight = 2.44;
    private const double L = Pitch.LengthMetres, W = Pitch.WidthMetres;
    private const int Lookahead = 2;

    private readonly List<TrackingFrame> _buffer = [];
    private readonly List<DetectedEvent> _out = [];
    private readonly Dictionary<string, string> _keepers = new[] { info.Home, info.Away }
        .SelectMany(t => t.Players.Where(p => p.Position == PlayerPosition.Goalkeeper).Select(p => (p.PlayerId, t.TeamId)))
        .ToDictionary(x => x.PlayerId, x => x.TeamId);

    private string? _owner, _ownerTeam;
    private (double T, double X, double Y, bool Pressured) _lastControl;
    private string? _candidate;
    private int _candidateFrames;
    private bool _inFlight;
    private bool _dead;               // after a goal or the ball going out, until someone controls it again
    private readonly List<(double T, double X, double Y)> _flight = [];
    private (double X, double Y) _flightFar;

    /// <summary>Everything detected so far.</summary>
    public IReadOnlyList<DetectedEvent> Detected => _out;

    /// <summary>Adds a frame; returns what it confirmed (detections lag the newest frame by a few frames).</summary>
    public IReadOnlyList<DetectedEvent> Push(TrackingFrame frame)
    {
        int before = _out.Count;
        _buffer.Add(frame);
        if (_buffer.Count >= 2 * Lookahead + 1)
        {
            Process(_buffer[^(Lookahead + 1)], _buffer[^(2 * Lookahead + 1)], _buffer[^1]);
            if (_buffer.Count > 64) _buffer.RemoveRange(0, _buffer.Count - 64);
        }
        return _out.Count > before ? _out.Skip(before).ToList() : [];
    }

    /// <summary>Runs a whole match of frames and returns every detection.</summary>
    public static IReadOnlyList<DetectedEvent> Run(MatchInfo info, IEnumerable<TrackingFrame> frames, double hz = 10)
    {
        var e = new AutoEventer(info, hz);
        foreach (TrackingFrame f in frames) e.Push(f);
        return e.Detected;
    }

    private void Process(TrackingFrame f, TrackingFrame prev, TrackingFrame next)
    {
        BallFix b = f.Ball;
        double dt = next.T - prev.T;
        double speed = dt > 0 ? Dist(next.Ball.X, next.Ball.Y, prev.Ball.X, prev.Ball.Y) / dt : 0;

        // Goal or out of play ends any flight.
        if (!_dead && _owner is not null)
        {
            bool overLine = b.X < 0 || b.X > L, overTouch = b.Y < 0 || b.Y > W;
            if (overLine || overTouch)
            {
                // Only a ball that was struck can go in: a player walking it over the line ends nothing.
                bool inGoal = _inFlight && overLine && Attacks(_ownerTeam!, b.X) && Math.Abs(b.Y - W / 2) <= GoalHalfWidth && b.Z <= BarHeight;
                EndFlightOut(f.T, b, inGoal ? "goal" : overLine && Attacks(_ownerTeam!, b.X) ? "offTarget" : null);
                _dead = true;
                return;
            }
        }

        // Who is on the ball: the nearest player to a slow, low ball.
        (PlayerFix? near, double d) = Nearest(f, b.X, b.Y);
        // Control means the ball has been stopped at a player's feet: a ball rolling past someone is not theirs.
        bool control = near is not null && d <= ControlRadius && b.Z < 1.6 && speed < ControlMaxSpeed;
        if (_inFlight && _flight.Count < 6) _flight.Add((f.T, b.X, b.Y));
        if (_inFlight) _flightFar = (b.X, b.Y);

        // The player on the ball keeps it while they are close to it, even if someone else is a touch closer.
        if (!_inFlight && _owner is not null && OwnerDistance(f, b) is { } od && od <= OwnerKeepRadius && b.Z < 1.6)
        {
            if (near is { } other && other.PlayerId != _owner && d <= ControlRadius && d < od - 0.3) { /* contested: wait */ }
            else
            {
                _candidate = null;
                _candidateFrames = 0;
                // The ball is still at the owner's feet: this is the latest moment and place they had it.
                if (OwnerFix(f) is { } me) _lastControl = (f.T, b.X, b.Y, Pressured(f, me));
                return;
            }
        }

        // A first-time touch: the ball changes direction sharply at another player without stopping (a one-touch
        // pass or shot, a deflection, a save). That player had it, for one frame.
        if (!control && _inFlight && near is { } toucher && toucher.PlayerId != _owner && d <= ControlRadius && b.Z < 1.6
            && Redirected(prev, f, next))
        {
            EndFlight(f.T, b, toucher);
            _owner = toucher.PlayerId;
            _ownerTeam = toucher.TeamId;
            _inFlight = false;
            _candidate = null;
            _candidateFrames = 0;
            _lastControl = (f.T, b.X, b.Y, Pressured(f, toucher));
            return;
        }

        if (!control)
        {
            _candidate = null;
            _candidateFrames = 0;
            if (_owner is not null && !_inFlight && Dist(b.X, b.Y, _lastControl.X, _lastControl.Y) > 1.5 && speed > 5)
            {
                _inFlight = true;
                _flight.Clear();
                _flight.Add((f.T, b.X, b.Y));
                _flightFar = (b.X, b.Y);
            }
            return;
        }

        PlayerFix p = near!.Value;
        if (p.PlayerId == _owner)
        {
            _candidate = null;
            _candidateFrames = 0;
            if (_inFlight && Dist(b.X, b.Y, _lastControl.X, _lastControl.Y) < 3) _inFlight = false;   // never really left
            else if (_inFlight) _inFlight = false;
            _lastControl = (f.T, b.X, b.Y, Pressured(f, p));
            return;
        }

        if (_candidate != p.PlayerId) { _candidate = p.PlayerId; _candidateFrames = 0; }
        // A teammate receiving takes two frames; an opponent taking the ball off someone needs three.
        int needed = _inFlight ? (p.TeamId == _ownerTeam ? 1 : 2)
            : p.TeamId == _ownerTeam ? 2
            : OwnerDistance(f, b) is { } owned && owned <= OwnerKeepRadius ? 5 : 3;   // a challenge has to be won, not brushed past
        if (++_candidateFrames < needed) return;

        // A new player has the ball.
        if (_owner is not null && !_dead)
        {
            double moved = Dist(b.X, b.Y, _lastControl.X, _lastControl.Y);
            if (_inFlight) EndFlight(f.T, b, p);
            else if (p.TeamId != _ownerTeam)
            {
                // A short ball that went to an opponent is a failed pass; otherwise the ball was taken off the player.
                if (moved > 3) AddPass(f.T, b, p, completed: false);
                _out.Add(new DetectedEvent(DetectedKind.BallWin, f.T, p.PlayerId, p.TeamId, _owner, null, null, null, null, null, b.X, b.Y, null, null));
            }
            else if (moved > 1) AddPass(f.T, b, p, completed: true);
        }
        _owner = p.PlayerId;
        _ownerTeam = p.TeamId;
        _dead = false;
        _inFlight = false;
        _candidate = null;
        _lastControl = (f.T, b.X, b.Y, Pressured(f, p));
    }

    private void EndFlight(double t, BallFix b, PlayerFix to)
    {
        // A teammate controlling it makes it a pass. A shot is stopped by the keeper, or blocked close to the shooter.
        if (to.TeamId != _ownerTeam && IsShot(b.X, b.Y))
        {
            bool keeper = _keepers.TryGetValue(to.PlayerId, out string? kt) && kt != _ownerTeam;
            if (keeper || Dist(b.X, b.Y, _lastControl.X, _lastControl.Y) <= BlockMetres)
            {
                AddShot(t, keeper ? "saved" : "blocked", b);
                return;
            }
        }
        AddPass(t, b, to, completed: to.TeamId == _ownerTeam);
        if (to.TeamId != _ownerTeam)
            _out.Add(new DetectedEvent(DetectedKind.BallWin, t, to.PlayerId, to.TeamId, _owner, null, null, null, null, null, b.X, b.Y, null, null));
    }

    private void EndFlightOut(double t, BallFix b, string? shotResult)
    {
        if (_inFlight && (shotResult == "goal" || (shotResult is not null && IsShot(b.X, b.Y)))) AddShot(t, shotResult!, b);
        else if (_inFlight) AddPass(t, b, null, completed: false);
        _inFlight = false;
        _owner = null;
        _ownerTeam = null;
    }

    private void AddPass(double t, BallFix b, PlayerFix? to, bool completed)
    {
        double metres = Dist(b.X, b.Y, _lastControl.X, _lastControl.Y);
        _out.Add(new DetectedEvent(DetectedKind.Pass, _lastControl.T, _owner!, _ownerTeam!, to?.PlayerId, completed,
            Math.Round(metres, 1), FlightKmh(), _lastControl.Pressured, null, _lastControl.X, _lastControl.Y, b.X, b.Y));
    }

    private void AddShot(double t, string result, BallFix b) =>
        _out.Add(new DetectedEvent(DetectedKind.Shot, _lastControl.T, _owner!, _ownerTeam!, null, null,
            Math.Round(Dist(b.X, b.Y, _lastControl.X, _lastControl.Y), 1), FlightKmh(), _lastControl.Pressured, result,
            _lastControl.X, _lastControl.Y, b.X, b.Y));

    /// <summary>
    /// The ball's speed as it left the player: distance over time across the first few frames of flight (up to
    /// 0.4 s), which averages out the position noise of single frames.
    /// </summary>
    private double? FlightKmh()
    {
        if (_flight.Count == 0) return null;
        // The strike: from the last moment at the player's feet to the first point in flight. It can only read low
        // (the ball left somewhere in between), so it counts when it is the larger.
        double dt0 = _flight[0].T - _lastControl.T;
        double strike = dt0 > 0 ? Dist(_flight[0].X, _flight[0].Y, _lastControl.X, _lastControl.Y) / dt0 : 0;
        if (_flight.Count == 1) return dt0 <= 0 ? null : Math.Round(strike * 3.6, 1);
        // Then the flight itself, while the ball is still travelling: stop at the first step much shorter than the first.
        int k = 1;
        double first = Dist(_flight[1].X, _flight[1].Y, _flight[0].X, _flight[0].Y);
        while (k < Math.Min(4, _flight.Count - 1) &&
               Dist(_flight[k + 1].X, _flight[k + 1].Y, _flight[k].X, _flight[k].Y) > 0.5 * first) k++;
        double dt = _flight[k].T - _flight[0].T;
        double flying = dt > 0 ? Dist(_flight[k].X, _flight[k].Y, _flight[0].X, _flight[0].Y) / dt : 0;
        return Math.Round(Math.Max(flying, strike) * 3.6, 1);
    }

    /// <summary>True when the ball arrives and leaves at speed in directions more than 35 degrees apart, or leaves much faster.</summary>
    private static bool Redirected(TrackingFrame prev, TrackingFrame f, TrackingFrame next)
    {
        double ix = f.Ball.X - prev.Ball.X, iy = f.Ball.Y - prev.Ball.Y, ox = next.Ball.X - f.Ball.X, oy = next.Ball.Y - f.Ball.Y;
        double vin = Math.Sqrt(ix * ix + iy * iy) / Math.Max(1e-6, f.T - prev.T);
        double vout = Math.Sqrt(ox * ox + oy * oy) / Math.Max(1e-6, next.T - f.T);
        if (vin < 3 || vout < 3) return false;
        if (vout > 1.6 * vin) return true;    // struck first time: the ball leaves much faster than it came
        double cos = (ix * ox + iy * oy) / (Math.Sqrt(ix * ix + iy * iy) * Math.Sqrt(ox * ox + oy * oy));
        return cos < Math.Cos(35 * Math.PI / 180);
    }

    private PlayerFix? OwnerFix(TrackingFrame f)
    {
        foreach (PlayerFix p in f.Players)
            if (p.PlayerId == _owner) return p;
        return null;
    }

    /// <summary>How far the current owner is from the ball, if they are on the pitch.</summary>
    private double? OwnerDistance(TrackingFrame f, BallFix b)
    {
        foreach (PlayerFix p in f.Players)
            if (p.PlayerId == _owner) return Dist(p.X, p.Y, b.X, b.Y);
        return null;
    }

    /// <summary>A fast ball from shooting range that was heading for the goal mouth.</summary>
    private bool IsShot(double endX, double endY)
    {
        if (_ownerTeam is null) return false;
        double goalX = _ownerTeam == info.Home.TeamId ? L : 0;
        double sx = _lastControl.X, sy = _lastControl.Y;
        if (Dist(sx, sy, goalX, W / 2) > ShotMaxDistanceMetres) return false;
        if ((FlightKmh() ?? 0) < ShotMinSpeedKmh) return false;
        (double fx, double fy) = _flightFar;
        double dx = fx - sx, dy = fy - sy;
        double moved = Math.Sqrt(dx * dx + dy * dy);
        if (moved < 1 || Math.Sign(dx) != Math.Sign(goalX - sx) || Math.Abs(dx) < 0.05 * moved) return false;
        // Heading for goal: the ball's line passes within GoalWindowMetres of the centre of the goal, ahead of it.
        double ux = dx / moved, uy = dy / moved;
        double along = (goalX - sx) * ux + (W / 2 - sy) * uy;
        if (along <= 0) return false;
        double px = sx + ux * along - goalX, py = sy + uy * along - W / 2;
        return Math.Sqrt(px * px + py * py) <= GoalWindowMetres;
    }

    private bool Attacks(string teamId, double x) => teamId == info.Home.TeamId ? x > L : x < 0;

    private static bool Pressured(TrackingFrame f, PlayerFix p) =>
        f.Players.Any(o => o.TeamId != p.TeamId && Dist(o.X, o.Y, p.X, p.Y) <= PressureRadius);

    private static (PlayerFix? Player, double Distance) Nearest(TrackingFrame f, double x, double y)
    {
        PlayerFix? best = null;
        double bd = double.MaxValue;
        foreach (PlayerFix p in f.Players)
        {
            double d = Dist(p.X, p.Y, x, y);
            if (d < bd) { bd = d; best = p; }
        }
        return (best, bd);
    }

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
}

public enum DetectedKind
{
    Pass,
    Shot,
    BallWin,
}

/// <summary>
/// One auto-evented event. <see cref="T"/> is when the ball left the player (passes, shots) or when control changed
/// (ball wins). Positions are tracking metres. For a ball win, <see cref="ToPlayerId"/> is the player who lost it.
/// </summary>
public sealed record DetectedEvent(
    DetectedKind Kind, double T, string PlayerId, string TeamId, string? ToPlayerId, bool? Completed,
    double? Metres, double? SpeedKmh, bool? UnderPressure, string? ShotResult, double X, double Y, double? EndX, double? EndY);

/// <summary>How well auto-eventing matched the real events: found (recall), real (precision) and the measurements' errors.</summary>
public sealed record AutoEventReport(
    KindScore Passes, KindScore Shots, KindScore BallWins,
    double PassOutcomeAgreement, double ReceiverAgreement, double PassLengthErrorMetres, double PassSpeedErrorKmh,
    double PressureAgreement, double ShotSpeedErrorKmh, double ShotResultAgreement, int GoalsTrue, int GoalsFound);

public sealed record KindScore(int True, int Detected, int Matched)
{
    public double Recall => True == 0 ? 1 : (double)Matched / True;
    public double Precision => Detected == 0 ? 1 : (double)Matched / Detected;
}

/// <summary>Scores detections against the event feed they were never shown.</summary>
public static class AutoEventScore
{
    /// <summary>How far apart in time a detection and a real event may be and still be the same, seconds.</summary>
    public const double ToleranceSec = 1.0;
    /// <summary>For ball wins: the feed stamps an interception on its whole second, after the ball arrived.</summary>
    public const double WinToleranceSec = 2.0;
    /// <summary>An interception logged more than this after the failed pass was a throw-in, not a ball won in play.</summary>
    public const double OutOfPlayGapSec = 5.0;

    /// <param name="eventTimes">When each event happened in the tracking (its continuous time; see TrackingSynth).</param>
    public static AutoEventReport Score(IReadOnlyList<MatchEvent> events, IReadOnlyDictionary<string, double> eventTimes,
        IReadOnlyList<DetectedEvent> detected, double fromSec = double.NegativeInfinity, double toSec = double.PositiveInfinity)
    {
        double T(MatchEvent e) => eventTimes.TryGetValue(e.EventId, out double t) ? t : e.MatchClockSec;
        bool In(double t) => t >= fromSec && t <= toSec;
        var truePasses = events.Where(e => e.Type == MatchEventType.Pass && e.End is not null && In(T(e))).ToList();
        var trueShots = events.Where(e => e.Type == MatchEventType.Shot && In(T(e))).ToList();
        // Ball wins in play: tackles, and interceptions logged soon after the failed pass. One logged long after it
        // is the feed's way of recording a throw-in, which tracking rightly sees as the ball going out.
        var trueWins = new List<MatchEvent>();
        double lastFailed = double.NegativeInfinity;
        foreach (MatchEvent e in events)
        {
            if (e.Type == MatchEventType.Pass && e.Outcome == EventOutcome.Fail) lastFailed = T(e);
            if (e.Outcome != EventOutcome.Success || !In(T(e))) continue;
            if (e.Type == MatchEventType.Tackle) trueWins.Add(e);
            if (e.Type == MatchEventType.Interception && T(e) - lastFailed <= OutOfPlayGapSec) trueWins.Add(e);
        }
        var dPasses = detected.Where(d => d.Kind == DetectedKind.Pass && In(d.T)).ToList();
        var dShots = detected.Where(d => d.Kind == DetectedKind.Shot && In(d.T)).ToList();
        var dWins = detected.Where(d => d.Kind == DetectedKind.BallWin && In(d.T)).ToList();

        var passPairs = Match(truePasses, dPasses, T);
        var shotPairs = Match(trueShots, dShots, T);
        var winPairs = Match(trueWins, dWins, T, WinToleranceSec);

        static double Share<TP>(IEnumerable<TP> pairs, Func<TP, bool> ok)
        {
            var list = pairs.ToList();
            return list.Count == 0 ? 1 : (double)list.Count(ok) / list.Count;
        }
        static double Mae(IEnumerable<double> errors)
        {
            var list = errors.ToList();
            return list.Count == 0 ? 0 : Math.Round(list.Average(), 2);
        }

        return new AutoEventReport(
            new KindScore(truePasses.Count, dPasses.Count, passPairs.Count),
            new KindScore(trueShots.Count, dShots.Count, shotPairs.Count),
            new KindScore(trueWins.Count, dWins.Count, winPairs.Count),
            Math.Round(Share(passPairs, p => (p.True.Outcome == EventOutcome.Success) == (p.Found.Completed == true)), 3),
            Math.Round(Share(passPairs.Where(p => p.True.Outcome == EventOutcome.Success), p => p.Found.ToPlayerId == p.True.RecipientId), 3),
            Mae(passPairs.Where(p => p.True.Outcome == EventOutcome.Success && p.Found.Metres is not null)
                .Select(p => Math.Abs(p.Found.Metres!.Value - (BallMetrics.PassMetres(p.True) ?? 0)))),
            Mae(passPairs.Where(p => p.True.BallSpeedKmh is not null && p.Found.SpeedKmh is not null)
                .Select(p => Math.Abs(p.Found.SpeedKmh!.Value - p.True.BallSpeedKmh!.Value))),
            Math.Round(Share(passPairs, p => (p.True.UnderPressure == true) == (p.Found.UnderPressure == true)), 3),
            Mae(shotPairs.Where(p => p.True.BallSpeedKmh is not null && p.Found.SpeedKmh is not null)
                .Select(p => Math.Abs(p.Found.SpeedKmh!.Value - p.True.BallSpeedKmh!.Value))),
            Math.Round(Share(shotPairs, p => string.Equals(p.Found.ShotResult, p.True.ShotResult?.ToString(), StringComparison.OrdinalIgnoreCase)), 3),
            trueShots.Count(e => e.ShotResult == ShotResult.Goal),
            dShots.Count(d => d.ShotResult == "goal"));
    }

    /// <summary>Pairs each real event with the closest unused detection by the same player within the tolerance.</summary>
    private static List<(MatchEvent True, DetectedEvent Found)> Match(
        List<MatchEvent> truth, List<DetectedEvent> found, Func<MatchEvent, double> time, double tolerance = ToleranceSec)
    {
        var used = new HashSet<DetectedEvent>();
        var pairs = new List<(MatchEvent, DetectedEvent)>();
        foreach (MatchEvent e in truth.OrderBy(time))
        {
            double t = time(e);
            DetectedEvent? best = null;
            double bestGap = double.MaxValue;
            foreach (DetectedEvent d in found)
            {
                if (used.Contains(d) || d.PlayerId != e.PlayerId) continue;
                double gap = Math.Abs(d.T - t);
                if (gap <= tolerance && gap < bestGap) { best = d; bestGap = gap; }
            }
            if (best is null) continue;
            used.Add(best);
            pairs.Add((e, best));
        }
        return pairs;
    }
}
