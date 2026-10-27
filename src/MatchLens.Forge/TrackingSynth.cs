using MatchLens.Contracts;

namespace MatchLens.Forge;

/// <summary>
/// Synthetic tracking data for a generated match: every player on the pitch and the ball, at any moment, consistent
/// with the event stream. It is what an optical tracking provider would have delivered for the same match.
/// <list type="bullet">
/// <item><b>The ball</b> follows the events: carried between them, flying for passes and shots at the speed the event
/// reports (a pass of 30 m at 90 km/h is in the air for 1.2 s), lofted for long balls, into the net, wide or into the
/// keeper's hands as the shot ended. Event moments are the feed's whole seconds, made continuous: when a pass is
/// still in the air at the next event's second, that event happens when the ball arrives (at most a couple of seconds
/// later), so the ball never moves faster than its reported speed.</item>
/// <item><b>Players</b> hold their position in the team's current shape (from the generator's shape spells), shift
/// with the ball and with possession, drift a little on their own, and are exactly where the events put them whenever
/// they touch the ball: the passer at the pass, the receiver where it arrives, the presser beside the carrier.</item>
/// <item><b>Measurement noise</b> of about 12 cm per coordinate, as optical tracking has. <see cref="Frame"/> with
/// <c>observed: false</c> gives the noise-free truth, so measurements made from the noisy frames can be scored.</item>
/// </list>
/// Nothing is stored: a frame is computed from the events when it is asked for, and the same match always gives the
/// same frames.
/// </summary>
public sealed class TrackingSynth
{
    /// <summary>Frames per second a provider would deliver.</summary>
    public const int Hz = 10;

    /// <summary>Standard deviation of the measurement noise on each player coordinate, metres.</summary>
    public const double NoiseMetres = 0.12;

    private const double L = Pitch.LengthMetres, W = Pitch.WidthMetres;
    /// <summary>How long before a pass is played an interceptor may start to read it.</summary>
    private const double AnticipateSec = 0.5;
    private const double KeyReachSec = 6;        // how long a touch pulls a player off their position, each way
    private const double WanderMetres = 3.2;
    /// <summary>A failed pass whose ball is logged as won later than this went out of play (a throw-in).</summary>
    public const double ThrowInGapSec = 4;

    private readonly MatchInfo _info;
    private readonly int _seed;
    private readonly List<BallSegment> _ball = [];
    private readonly Dictionary<string, List<Key>> _keys = new(StringComparer.Ordinal);
    private readonly List<(double T, string TeamId)> _possession = [];
    private readonly List<(double T, int Period)> _periods = [];
    private readonly List<Spell> _spells = [];
    private readonly Dictionary<string, string> _teamOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double F1, double F2, double P1, double P2)> _wander = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _eventTime = new(StringComparer.Ordinal);

    /// <summary>First and last moments with tracking, match seconds.</summary>
    public double StartT { get; }
    public double EndT { get; }

    /// <summary>When each event with a position actually happened in the tracking (at or a little after its second).</summary>
    public IReadOnlyDictionary<string, double> EventTimes => _eventTime;

    public MatchInfo Info => _info;

    public TrackingSynth(MatchRecording recording, int? seed = null)
    {
        _info = recording.Match;
        _seed = seed ?? recording.Match.Seed;
        foreach (TeamInfo team in new[] { _info.Home, _info.Away })
            foreach (PlayerInfo p in team.Players) _teamOf[p.PlayerId] = team.TeamId;

        BuildSpells(recording);
        BuildBall(recording.Events);
        foreach (string id in _teamOf.Keys)
        {
            ulong h = Hash((ulong)_seed, Fnv(id));
            _wander[id] = (0.025 + Unit(h, 1) * 0.05, 0.02 + Unit(h, 2) * 0.05, Unit(h, 3) * Math.Tau, Unit(h, 4) * Math.Tau);
        }
        foreach (List<Key> keys in _keys.Values)
        {
            keys.Sort((a, b) => a.T.CompareTo(b.T));
            for (int i = 0; i < keys.Count; i++)
            {
                Key k = keys[i];
                (double bx, double by) = Base(k.PlayerId, k.T);
                keys[i] = k with { Dx = k.X - bx, Dy = k.Y - by };
            }
        }
        StartT = _ball.Count > 0 ? Math.Floor(_ball[0].T0) : 0;
        EndT = _ball.Count > 0 ? _ball[^1].T1 : 0;
        Precompute();
    }

    /// <summary>Top running speed the synthesis allows, m/s (34.6 km/h, a very fast sprint); each player's own top
    /// speed is between 31 and 34.6 km/h (keepers 27).</summary>
    public const double MaxSpeed = 9.6;

    private double TopSpeed(string playerId)
    {
        bool keeper = _info.Player(playerId)?.Position == PlayerPosition.Goalkeeper;
        double u = Unit(Hash((ulong)_seed, Fnv(playerId)), 9);
        return keeper ? 7.5 : 8.6 + u * (MaxSpeed - 8.6);
    }

    private int _frames;
    private readonly Dictionary<string, float[]> _px = new(StringComparer.Ordinal), _py = new(StringComparer.Ordinal);

    /// <summary>
    /// Every player's path at <see cref="Hz"/>, computed once. Where the target (shape + touches) asks for more than a
    /// sprint, a forward and a backward speed limit are averaged: the player starts a little early and arrives a
    /// little late instead of teleporting, as at a restart after a goal.
    /// </summary>
    private void Precompute()
    {
        _frames = (int)Math.Ceiling((EndT - StartT) * Hz) + 1;
        foreach (string id in _spells.SelectMany(s => s.Slots).Select(s => s.PlayerId).Distinct())
        {
            double step = TopSpeed(id) / Hz;
            var tx = new double[_frames];
            var ty = new double[_frames];
            for (int i = 0; i < _frames; i++) (tx[i], ty[i]) = Target(id, StartT + i / (double)Hz);
            var fx = (double[])tx.Clone();
            var fy = (double[])ty.Clone();
            var bx = (double[])tx.Clone();
            var by = (double[])ty.Clone();
            for (int i = 1; i < _frames; i++) Limit(fx, fy, i, i - 1, step);
            for (int i = _frames - 2; i >= 0; i--) Limit(bx, by, i, i + 1, step);
            var ox = new float[_frames];
            var oy = new float[_frames];
            for (int i = 0; i < _frames; i++)
            {
                ox[i] = (float)((fx[i] + bx[i]) / 2);
                oy[i] = (float)((fy[i] + by[i]) / 2);
            }
            _px[id] = ox;
            _py[id] = oy;
        }
    }

    private static void Limit(double[] x, double[] y, int i, int from, double step)
    {
        double dx = x[i] - x[from], dy = y[i] - y[from], d = Math.Sqrt(dx * dx + dy * dy);
        if (d <= step) return;
        x[i] = x[from] + dx / d * step;
        y[i] = y[from] + dy / d * step;
    }

    private (double X, double Y) Position(string playerId, double t)
    {
        if (!_px.TryGetValue(playerId, out float[]? xs)) return Target(playerId, t);
        float[] ys = _py[playerId];
        double u = Math.Clamp((t - StartT) * Hz, 0, _frames - 1);
        int i = (int)Math.Floor(u);
        int j = Math.Min(i + 1, _frames - 1);
        double w = u - i;
        return (xs[i] + (xs[j] - xs[i]) * w, ys[i] + (ys[j] - ys[i]) * w);
    }

    /// <summary>All frames from <paramref name="from"/> to <paramref name="to"/>, <see cref="Hz"/> per second.</summary>
    public IEnumerable<TrackingFrame> Frames(double from, double to, bool observed = true)
    {
        long first = (long)Math.Ceiling(Math.Max(from, StartT) * Hz), last = (long)Math.Floor(Math.Min(to, EndT) * Hz);
        for (long i = first; i <= last; i++) yield return Frame(i / (double)Hz, observed);
    }

    /// <summary>The frame at match second <paramref name="t"/>; noisy like a real provider unless <paramref name="observed"/> is false.</summary>
    public TrackingFrame Frame(double t, bool observed = true)
    {
        long index = (long)Math.Round(t * Hz);
        (double bx, double by, double bz) = BallAt(t);
        if (observed)
        {
            bx += Gauss((ulong)_seed, 0xBA11, index, 0) * NoiseMetres * 0.6;
            by += Gauss((ulong)_seed, 0xBA11, index, 1) * NoiseMetres * 0.6;
        }
        var players = new List<PlayerFix>(22);
        foreach (Spell spell in _spells)
        {
            if (t < spell.From || t >= spell.To) continue;
            foreach (Slot slot in spell.Slots)
            {
                (double x, double y) = Position(slot.PlayerId, t);
                if (observed)
                {
                    ulong id = Fnv(slot.PlayerId);
                    x += Gauss((ulong)_seed, id, index, 0) * NoiseMetres;
                    y += Gauss((ulong)_seed, id, index, 1) * NoiseMetres;
                }
                players.Add(new PlayerFix(slot.PlayerId, spell.TeamId, Math.Round(x, 2), Math.Round(y, 2)));
            }
        }
        return new TrackingFrame(Math.Round(t, 2), PeriodAt(t), new BallFix(Math.Round(bx, 2), Math.Round(by, 2), Math.Round(bz, 2)), players);
    }

    // ------------------------------------------------------------------------------------------------------------
    // The ball
    // ------------------------------------------------------------------------------------------------------------

    private void BuildBall(IReadOnlyList<MatchEvent> events)
    {
        double tau = double.NegativeInfinity, busy = 0;
        (double X, double Y) ball = (L / 2, W / 2);
        double ballT = double.NaN;
        string? carrier = null;      // who has the ball at their feet between events, if anyone
        (double T, double X, double Y, double Played)? failed = null;   // where and when the last failed pass arrived, and when it was played
        foreach (MatchEvent e in events)
        {
            if (e.Type == MatchEventType.PeriodStart)
            {
                _periods.Add((Math.Max(e.MatchClockSec, tau + busy), e.Period));
                continue;
            }
            if (e.Type is MatchEventType.PeriodEnd or MatchEventType.Substitution || e.Start is not { } start) continue;

            double t = double.IsNegativeInfinity(tau) ? e.MatchClockSec : Math.Max(e.MatchClockSec, tau + busy);
            tau = t;
            _eventTime[e.EventId] = t;
            (double sx, double sy) = Metres(start, e.TeamId);
            if (double.IsNaN(ballT)) ballT = t;
            // An interception happens where and when the failed pass arrives; the interceptor then has the ball.
            if (failed is { } fp && e.Type is MatchEventType.Interception or MatchEventType.Tackle or MatchEventType.PossessionChange
                && Math.Abs(fp.X - sx) < 1.5 && Math.Abs(fp.Y - sy) < 1.5)
            {
                if (t - fp.T <= ThrowInGapSec)
                {
                    // The interceptor reads the pass only just before it is played: he cannot drift into the lane
                    // seconds early, which would let anything measured at the moment of the pass see its outcome.
                    Touch(e.PlayerId, fp.T, fp.X, fp.Y, from: fp.Played - AnticipateSec);
                    carrier = e.PlayerId;
                }
                else
                {
                    // The feed logs the ball won seconds later: it went out for a throw-in. It rolls over the nearest
                    // touchline, stays out, and is thrown back in to where the feed has it.
                    double outY = fp.Y < W / 2 ? -1.0 : W + 1.0;
                    _ball.Add(new BallSegment(fp.T, fp.T + 0.8, fp.X, fp.Y, fp.X, outY, 0));
                    double throwAt = Math.Max(fp.T + 0.8, t - 1.0);
                    _ball.Add(new BallSegment(fp.T + 0.8, throwAt, fp.X, outY, fp.X, outY, 0));
                    ball = (fp.X, outY);
                    ballT = throwAt;
                    carrier = null;
                }
            }
            failed = null;
            if (ballT < t)
            {
                _ball.Add(new BallSegment(ballT, t, ball.X, ball.Y, sx, sy, 0));
                // The player on the ball stays with it, and dribbles it to wherever the next event happens.
                if (carrier is not null)
                    for (double u = 0.4; u < t - ballT; u += 0.4)
                    {
                        double w = u / (t - ballT);
                        Touch(carrier, ballT + u, ball.X + (sx - ball.X) * w, ball.Y + (sy - ball.Y) * w);
                    }
                if (carrier is not null) Touch(carrier, t, sx, sy);
            }
            ball = (sx, sy);
            ballT = t;
            busy = 0.25;

            switch (e.Type)
            {
                case MatchEventType.Pass when e.End is { } end:
                {
                    (double ex, double ey) = Metres(end, e.TeamId);
                    double metres = Math.Max(0.5, Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy)));
                    double kmh = e.BallSpeedKmh ?? Math.Min(95, 25 + 1.6 * metres);
                    double flight = metres / (kmh / 3.6);
                    double peak = e.LongBall == true || metres > 32 ? Math.Min(9, 0.16 * metres) : metres > 20 ? 0.5 : 0;
                    _ball.Add(new BallSegment(t, t + flight, sx, sy, ex, ey, peak));
                    Touch(e.PlayerId, t, sx, sy);
                    if (e.Outcome == EventOutcome.Success && e.RecipientId is { } to) Touch(to, t + flight, ex, ey);
                    carrier = e.Outcome == EventOutcome.Success ? e.RecipientId : null;
                    if (e.Outcome != EventOutcome.Success) failed = (t + flight, ex, ey, t);
                    ball = (ex, ey);
                    ballT = t + flight;
                    busy = flight + 0.3;
                    Possess(t, e.TeamId);
                    break;
                }
                case MatchEventType.Shot:
                {
                    ulong h = Hash((ulong)_seed, (ulong)e.Seq * 7919);
                    double side = Unit(h, 1) < 0.5 ? -1 : 1;
                    // Where the shot ends, in the shooter's frame: the net, the keeper, wide, or a defender's legs.
                    // Goals and misses are aimed at a point on the goal line (inside the posts, or wide of them) and
                    // carried on past it, so the ball crosses the line exactly there whatever the angle.
                    PitchPoint target = e.ShotResult switch
                    {
                        ShotResult.Goal => Past(start, new PitchPoint(100, 50 + side * Unit(h, 2) * 4.6), 0.6),
                        ShotResult.Saved => new PitchPoint(99.0, 50 + side * Unit(h, 2) * 3.5),
                        ShotResult.Blocked => Inside(Toward(start, new PitchPoint(100, 50), 4.5, side * Unit(h, 2) * 3)),
                        _ => Past(start, new PitchPoint(100, 50 + side * (6.5 + Unit(h, 2) * 9)), 2.5),
                    };
                    (double ex, double ey) = Metres(target, e.TeamId);
                    double metres = Math.Max(0.5, Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy)));
                    double kmh = e.BallSpeedKmh ?? 90;
                    double flight = metres / (kmh / 3.6);
                    double peak = e.ShotResult == ShotResult.Blocked ? 0.3 : 0.4 + Unit(h, 3) * (e.ShotResult == ShotResult.OffTarget ? 2.2 : 1.4);
                    _ball.Add(new BallSegment(t, t + flight, sx, sy, ex, ey, peak));
                    Touch(e.PlayerId, t, sx, sy);
                    // The keeper is where a saved shot ends; a defender is where a blocked one does.
                    string opponent = e.TeamId == _info.Home.TeamId ? _info.Away.TeamId : _info.Home.TeamId;
                    carrier = e.ShotResult switch
                    {
                        ShotResult.Saved => SlotNear(opponent, t, ex, ey, keeper: true),
                        ShotResult.Blocked => SlotNear(opponent, t, ex, ey, keeper: false),
                        _ => null,
                    };
                    if (carrier is not null) Touch(carrier, t + flight, ex, ey);
                    ball = (ex, ey);
                    ballT = t + flight;
                    busy = flight + 0.6;
                    Possess(t, e.TeamId);
                    break;
                }
                case MatchEventType.Pressure:
                {
                    // The presser closes the carrier down from the goal side, a metre and a half away.
                    PitchPoint own = new(Math.Max(0, start.X - 1.5), start.Y);
                    (double px, double py) = Metres(own, e.TeamId);
                    Touch(e.PlayerId, t, px, py);
                    busy = 0;
                    break;
                }
                case MatchEventType.Foul:
                {
                    PitchPoint own = new(Math.Max(0, start.X - 0.8), start.Y);
                    (double px, double py) = Metres(own, e.TeamId);
                    Touch(e.PlayerId, t, px, py);
                    carrier = null;      // a free kick follows
                    break;
                }
                case MatchEventType.PossessionChange:
                    Touch(e.PlayerId, t, sx, sy);
                    Possess(t, e.TeamId);
                    carrier = e.PlayerId;
                    busy = 0;
                    break;
                case MatchEventType.Goal:
                    carrier = null;
                    busy = 1.5;
                    break;
                default:
                    Touch(e.PlayerId, t, sx, sy);
                    if (e.Type is MatchEventType.Tackle or MatchEventType.Interception or MatchEventType.KickOff)
                    {
                        Possess(t, e.TeamId);
                        carrier = e.PlayerId;
                    }
                    break;
            }
        }
        if (!double.IsNaN(ballT)) _ball.Add(new BallSegment(ballT, ballT + 1, ball.X, ball.Y, ball.X, ball.Y, 0));
        if (_periods.Count == 0) _periods.Add((0, 1));
    }

    /// <summary>The team's keeper, or its outfield player whose place in the shape is nearest a point (tracking metres).</summary>
    private string? SlotNear(string teamId, double t, double x, double y, bool keeper)
    {
        Spell? spell = _spells.FirstOrDefault(s => s.TeamId == teamId && t >= s.From && t < s.To);
        if (spell is null) return null;
        PitchPoint own = TrackingFrameOf.Own(x, y, teamId, _info);
        return spell.Slots.Where(s => s.Keeper == keeper)
            .OrderBy(s => (s.X - own.X) * (s.X - own.X) + (s.Y - own.Y) * (s.Y - own.Y))
            .FirstOrDefault()?.PlayerId;
    }

    /// <summary>The point <paramref name="beyond"/> units past <paramref name="through"/>, on the line from <paramref name="from"/>.</summary>
    private static PitchPoint Past(PitchPoint from, PitchPoint through, double beyond)
    {
        double dx = through.X - from.X, dy = through.Y - from.Y, n = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
        return new PitchPoint(through.X + dx / n * beyond, through.Y + dy / n * beyond);
    }

    private static PitchPoint Inside(PitchPoint p) => new(Math.Min(p.X, 98.5), Math.Clamp(p.Y, 1, 99));

    private static PitchPoint Toward(PitchPoint from, PitchPoint to, double units, double sideways)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, n = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
        return new PitchPoint(from.X + dx / n * units - dy / n * sideways, from.Y + dy / n * units + dx / n * sideways);
    }

    private void Touch(string? playerId, double t, double x, double y, double from = double.NegativeInfinity)
    {
        if (playerId is null || !_teamOf.ContainsKey(playerId)) return;
        if (!_keys.TryGetValue(playerId, out List<Key>? list)) _keys[playerId] = list = [];
        list.Add(new Key(playerId, t, x, y, 0, 0, from));
    }

    private void Possess(double t, string teamId)
    {
        if (_possession.Count == 0 || _possession[^1].TeamId != teamId) _possession.Add((t, teamId));
    }

    /// <summary>The ball at <paramref name="t"/>: along its segment, with a parabola in height for lofted balls.</summary>
    public (double X, double Y, double Z) BallAt(double t)
    {
        if (_ball.Count == 0) return (L / 2, W / 2, 0);
        int i = SegmentAt(t);
        BallSegment s = _ball[i];
        double u = s.T1 > s.T0 ? Math.Clamp((t - s.T0) / (s.T1 - s.T0), 0, 1) : 1;
        return (s.X0 + (s.X1 - s.X0) * u, s.Y0 + (s.Y1 - s.Y0) * u, 4 * s.Peak * u * (1 - u));
    }

    private int SegmentAt(double t)
    {
        int lo = 0, hi = _ball.Count - 1;
        if (t <= _ball[0].T0) return 0;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_ball[mid].T0 <= t) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private int PeriodAt(double t)
    {
        int period = _periods[0].Period;
        foreach ((double T, int Period) p in _periods) if (p.T <= t) period = p.Period;
        return period;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Players
    // ------------------------------------------------------------------------------------------------------------

    private void BuildSpells(MatchRecording recording)
    {
        if (recording.Shapes.Count > 0)
        {
            foreach (ShapeSpell s in recording.Shapes)
            {
                string teamId = s.Side == Side.Home ? _info.Home.TeamId : _info.Away.TeamId;
                _spells.Add(new Spell(teamId, s.FromSec == 0 ? double.NegativeInfinity : s.FromSec,
                    s.ToSec >= recording.Events[^1].MatchClockSec ? double.PositiveInfinity : s.ToSec,
                    s.Formation.Slots.Select(x => new Slot(x.PlayerId, x.Role == PlayerRole.Goalkeeper, x.X, x.Y)).ToList()));
            }
            return;
        }

        // A recording loaded from disk has no shape spells: each player plays where they touched the ball on average,
        // and a substitute takes over from the player they replaced.
        var sums = new Dictionary<string, (double X, double Y, int N)>(StringComparer.Ordinal);
        foreach (MatchEvent e in recording.Events)
        {
            if (e.PlayerId is null || e.Start is not { } p || e.Type is MatchEventType.Substitution or MatchEventType.KickOff) continue;
            var (x, y, n) = sums.GetValueOrDefault(e.PlayerId);
            sums[e.PlayerId] = (x + p.X, y + p.Y, n + 1);
        }
        foreach (TeamInfo team in new[] { _info.Home, _info.Away })
        {
            var onPitch = team.Players.Where(p => p.Starter).Select(p => p.PlayerId).Take(11).ToList();
            double from = double.NegativeInfinity;
            foreach (MatchEvent sub in recording.Events.Where(e => e.Type == MatchEventType.Substitution && e.TeamId == team.TeamId))
            {
                _spells.Add(new Spell(team.TeamId, from, sub.MatchClockSec, SlotsOf(onPitch, team, sums)));
                int i = onPitch.IndexOf(sub.PlayerId!);
                if (i >= 0 && sub.RecipientId is not null)
                {
                    if (sums.TryGetValue(onPitch[i], out var off) && !sums.ContainsKey(sub.RecipientId)) sums[sub.RecipientId] = off;
                    onPitch[i] = sub.RecipientId;
                }
                from = sub.MatchClockSec;
            }
            _spells.Add(new Spell(team.TeamId, from, double.PositiveInfinity, SlotsOf(onPitch, team, sums)));
        }
    }

    private static List<Slot> SlotsOf(List<string> ids, TeamInfo team, Dictionary<string, (double X, double Y, int N)> sums) =>
        ids.Select(id =>
        {
            bool keeper = team.Players.First(p => p.PlayerId == id).Position == PlayerPosition.Goalkeeper;
            (double x, double y, int n) = sums.GetValueOrDefault(id);
            return n > 0 ? new Slot(id, keeper, x / n, y / n) : new Slot(id, keeper, keeper ? 5 : 45, 50);
        }).ToList();

    /// <summary>Where the shape and the touches want the player at <paramref name="t"/>, before the speed limit.</summary>
    private (double X, double Y) Target(string playerId, double t)
    {
        (double bx, double by) = Base(playerId, t);
        if (!_keys.TryGetValue(playerId, out List<Key>? keys) || keys.Count == 0) return Clamp(bx, by);

        // Between the touches before and after t, the offset from the player's position fades in and out.
        int lo = 0, hi = keys.Count - 1, after = keys.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid].T > t) { after = mid; hi = mid - 1; } else lo = mid + 1;
        }
        Key? k1 = after > 0 ? keys[after - 1] : null, k2 = after < keys.Count ? keys[after] : null;
        double dx = 0, dy = 0;
        if (k1 is { } a && k2 is { } b)
        {
            double w = Smooth((t - a.T) / Math.Max(1e-6, b.T - a.T));
            double fa = Fade(t - a.T), fb = t >= b.From ? Fade(b.T - t) : 0;
            dx = (1 - w) * a.Dx * fa + w * b.Dx * fb;
            dy = (1 - w) * a.Dy * fa + w * b.Dy * fb;
        }
        else if (k1 is { } only1) { double f = Fade(t - only1.T); dx = only1.Dx * f; dy = only1.Dy * f; }
        else if (k2 is { } only2) { double f = t >= only2.From ? Fade(only2.T - t) : 0; dx = only2.Dx * f; dy = only2.Dy * f; }
        return Clamp(bx + dx, by + dy);
    }

    /// <summary>
    /// A player's position without their touches: their place in the shape, moved by the ball and possession. When the
    /// shape changes the player moves to the new place over <see cref="ShapeBlendSec"/>; a substitute runs on from the
    /// touchline.
    /// </summary>
    private (double X, double Y) Base(string playerId, double t)
    {
        int at = -1;
        Slot? slot = null;
        for (int i = 0; i < _spells.Count; i++)
        {
            Spell s = _spells[i];
            if (t < s.From || t >= s.To) continue;
            Slot? found = s.Slots.FirstOrDefault(x => x.PlayerId == playerId);
            if (found is not null) { at = i; slot = found; break; }
        }
        if (at < 0 || slot is null) return Bench(playerId);

        Spell spell = _spells[at];
        (double x, double y) = Placed(spell, slot, t);
        double since = t - spell.From;
        if (since < ShapeBlendSec)
        {
            Spell? before = _spells.FirstOrDefault(s => s.TeamId == spell.TeamId && s.To == spell.From);
            Slot? was = before?.Slots.FirstOrDefault(s => s.PlayerId == playerId);
            (double px, double py) = was is not null ? Placed(before!, was, t) : Bench(playerId);
            double w = Smooth(since / ShapeBlendSec);
            x = px + (x - px) * w;
            y = py + (y - py) * w;
        }
        (double f1, double f2, double p1, double p2) = _wander[playerId];
        double amp = slot.Keeper ? 0.5 : WanderMetres;
        return (x + amp * Math.Sin(Math.Tau * f1 * t + p1), y + amp * Math.Sin(Math.Tau * f2 * t + p2));
    }

    private const double ShapeBlendSec = 10;

    /// <summary>Off the pitch (before coming on, after going off): on the touchline by the halfway line.</summary>
    private (double X, double Y) Bench(string playerId) =>
        (L / 2 + (_teamOf.GetValueOrDefault(playerId) == _info.Home.TeamId ? -6 : 6), -1.5);

    private (double X, double Y) Placed(Spell spell, Slot slot, double t)
    {
        // The ball as the team sees it, smoothed over the last six seconds: players react, they do not twitch, and a
        // restart (the ball placed for a goal kick) does not send the whole team sprinting.
        double sx = 0, sy = 0;
        const int samples = 12;
        for (int i = 0; i < samples; i++)
        {
            (double bx, double by, _) = BallAt(t - 0.5 - i * 0.5);
            sx += bx;
            sy += by;
        }
        PitchPoint ball = TrackingFrameOf.Own(sx / samples, sy / samples, spell.TeamId, _info);
        double possession = PossessionFactor(spell.TeamId, t);
        double ox, oy;
        if (slot.Keeper)
        {
            ox = Math.Clamp(slot.X + 0.10 * (ball.X - 50), 2, 16);
            oy = Math.Clamp(50 + 0.30 * (ball.Y - 50), 40, 60);
        }
        else
        {
            ox = Math.Clamp(slot.X + 0.42 * (ball.X - 50) + 5 * possession, 3, 97);
            oy = Math.Clamp(slot.Y + 0.28 * (ball.Y - 50), 3, 97);
        }
        return Metres(new PitchPoint(ox, oy), spell.TeamId);
    }

    /// <summary>+1 when the team has had the ball for a few seconds, -1 when the other team has, in between while it changes.</summary>
    private double PossessionFactor(string teamId, double t)
    {
        if (_possession.Count == 0) return 0;
        int lo = 0, hi = _possession.Count - 1, at = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_possession[mid].T <= t) { at = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (at < 0) return 0;
        double now = _possession[at].TeamId == teamId ? 1 : -1;
        double before = at > 0 ? (_possession[at - 1].TeamId == teamId ? 1 : -1) : 0;
        double w = Smooth((t - _possession[at].T) / 3);
        return before + (now - before) * w;
    }

    private static double Fade(double seconds) => seconds >= KeyReachSec ? 0 : Smooth(1 - seconds / KeyReachSec);

    private static double Smooth(double u)
    {
        u = Math.Clamp(u, 0, 1);
        return u * u * (3 - 2 * u);
    }

    private static (double X, double Y) Clamp(double x, double y) => (Math.Clamp(x, -1, L + 1), Math.Clamp(y, -1.5, W + 1.5));

    private (double X, double Y) Metres(PitchPoint p, string teamId) => TrackingFrameOf.Metres(p, teamId, _info);

    // ------------------------------------------------------------------------------------------------------------
    // Deterministic noise: a frame depends only on the match, the seed and the moment.
    // ------------------------------------------------------------------------------------------------------------

    private static ulong Fnv(string s)
    {
        ulong h = 1469598103934665603;
        foreach (char c in s) { h ^= c; h *= 1099511628211; }
        return h;
    }

    private static ulong Hash(ulong a, ulong b)
    {
        ulong z = a * 0x9E3779B97F4A7C15 + b + 0x632BE59BD9B4E019;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    private static double Unit(ulong h, ulong salt) => (Hash(h, salt) >> 11) * (1.0 / (1UL << 53));

    private static double Gauss(ulong seed, ulong id, long frame, ulong axis)
    {
        ulong h = Hash(Hash(seed, id), (ulong)frame * 2 + axis);
        double u1 = Math.Max(1e-12, Unit(h, 11)), u2 = Unit(h, 12);
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(Math.Tau * u2);
    }

    private sealed record BallSegment(double T0, double T1, double X0, double Y0, double X1, double Y1, double Peak);

    /// <param name="From">The touch pulls the player towards it only from this time (an interceptor reacting to the pass).</param>
    private readonly record struct Key(string PlayerId, double T, double X, double Y, double Dx, double Dy, double From = double.NegativeInfinity);

    private sealed record Spell(string TeamId, double From, double To, IReadOnlyList<Slot> Slots);

    private sealed record Slot(string PlayerId, bool Keeper, double X, double Y);
}
