using MatchLens.Analytics;
using MatchLens.Analytics.Spatial;
using MatchLens.Contracts;
using MatchLens.Forge;

namespace MatchLens.Server;

/// <summary>
/// Tracking for a live session. The match's synthetic tracking is built in the background (a couple of seconds),
/// then, as the match clock moves, every frame (10 per match second) goes to the running stats and to the
/// auto-eventer, and the browsers get the latest positions. Until it is ready, the session simply runs without it.
/// <para>
/// The same frames drive the spatial analytics (<see cref="SpatialMonitor"/>): pitch control every match second, each
/// pass judged at the moment it was played (lane, difficulty, the better option), and the territory and better-pass
/// detectors, whose candidates join the event detectors' in the session.
/// </para>
/// <para>
/// The auto-eventer only ever sees positions. Its detections are scored against the event feed as the match goes, so
/// the studio can show how well events are found from tracking alone.
/// </para>
/// </summary>
public sealed class TrackingFeed
{
    /// <summary>Detections are scored only up to this many seconds before the newest frame (they confirm late).</summary>
    public const double ScoreLagSec = 4;

    private readonly MatchRecording _recording;
    private readonly Task<TrackingSynth> _build;
    private readonly object _sync = new();
    private readonly Kinematics _kinematics = new();
    private readonly AutoEventer _eventer;
    private readonly List<KinematicAlert> _alerts = [];
    private readonly SpatialMonitor _spatial;
    private List<(double T, MatchEvent Event)>? _passes;
    private int _passIndex;
    private double _t = double.NaN;

    public TrackingFeed(MatchRecording recording)
    {
        _recording = recording;
        _eventer = new AutoEventer(recording.Match);
        _spatial = new SpatialMonitor(recording.Match);
        _build = Task.Run(() => new TrackingSynth(recording));
    }

    public bool Ready => _build.IsCompletedSuccessfully;

    /// <summary>
    /// Moves the tracking up to <paramref name="clock"/>: every frame since the last call goes to the running stats and
    /// the auto-eventer. Returns the latest positions for the browsers and any thresholds reached (null until ready).
    /// </summary>
    public TrackingTick? Advance(double clock, bool quiet = false)
    {
        if (!Ready) return null;
        TrackingSynth synth = _build.Result;
        var alerts = new List<KinematicAlert>();
        var passes = new List<PassView>();
        lock (_sync)
        {
            // Passes in the order they were played on the tracking clock, judged with the frame at that moment.
            _passes ??= _recording.Events
                .Where(e => e.Type == MatchEventType.Pass && synth.EventTimes.ContainsKey(e.EventId))
                .Select(e => (synth.EventTimes[e.EventId], e)).OrderBy(p => p.Item1).ToList();
            double from = double.IsNaN(_t) ? synth.StartT : _t + 1.0 / TrackingSynth.Hz;
            foreach (TrackingFrame f in synth.Frames(from, Math.Min(clock, synth.EndT)))
            {
                _eventer.Push(f);
                alerts.AddRange(_kinematics.Add(f));
                _spatial.Push(f);
                while (_passIndex < _passes.Count && _passes[_passIndex].T <= f.T)
                {
                    if (_spatial.OnPass(_passes[_passIndex].Event, f) is { } choice) passes.Add(PassOf(choice));
                    _passIndex++;
                }
                _t = f.T;
            }
            // Catching up (the jump to the start minute) is not news: only alerts from the last few seconds count.
            if (quiet) alerts.Clear();
            alerts.RemoveAll(a => clock - a.AtSec > 10);
            _alerts.AddRange(alerts);
            if (_alerts.Count > 200) _alerts.RemoveRange(0, _alerts.Count - 200);
        }
        return new TrackingTick(View(synth.Frame(Math.Min(clock, synth.EndT))), alerts, passes);
    }

    /// <summary>The frame at a match time and the one half a second before it (for velocities), or null before tracking.</summary>
    public (TrackingFrame Now, TrackingFrame Before)? FramesAt(double t)
    {
        if (!Ready) return null;
        TrackingSynth synth = _build.Result;
        double at = Math.Clamp(t, synth.StartT + 0.5, synth.EndT);
        return (synth.Frame(at), synth.Frame(at - 0.5));
    }

    /// <summary>The spatial detectors' candidates at this match second (territory, better pass), with ids from the match.</summary>
    public IReadOnlyList<Candidate> Detect(LiveMatch live, int nowSec)
    {
        if (!Ready) return [];
        lock (_sync) return _spatial.Detect(live, nowSec);
    }

    /// <summary>A team's average final-third control (0 to 1) between two match seconds; null without enough tracking.</summary>
    public double? FinalThirdShare(string teamId, int fromSec, int toSec)
    {
        lock (_sync) return _spatial.FinalThirdShare(teamId, fromSec, toSec);
    }

    /// <summary>The latest pitch-control map, for the browsers (home team's control, two decimals).</summary>
    public ControlView? Control()
    {
        lock (_sync)
        {
            if (_spatial.Control is not { } grid) return null;
            return new ControlView(Math.Round(_spatial.ControlT, 1), PitchControl.Cols, PitchControl.Rows,
                grid.Select(v => Math.Round((double)v, 2)).ToArray());
        }
    }

    /// <summary>Every pass judged so far (lane, difficulty, the better option).</summary>
    public IReadOnlyList<PassView> Passes()
    {
        lock (_sync) return _spatial.Choices.Select(PassOf).ToList();
    }

    /// <summary>
    /// The spatial picture for the agents (Ask, MCP): each team's final-third control over the last ten minutes and
    /// the match so far, and the players who most often missed a better pass.
    /// </summary>
    public SpatialSummary Summary(int nowSec)
    {
        MatchInfo info = _recording.Match;
        lock (_sync)
        {
            var teams = new[] { info.Home, info.Away }.Select(t => new TeamSpace(t.TeamId, t.Name,
                Round(_spatial.FinalThirdShare(t.TeamId, Math.Max(0, nowSec - 600), nowSec)), Round(_spatial.FinalThirdShare(t.TeamId, 0, nowSec)),
                _spatial.Choices.Count(c => c.TeamId == t.TeamId && c.Better is not null))).ToList();
            var players = _spatial.Choices.Where(c => c.Better is not null && c.PlayerId is not null).GroupBy(c => c.PlayerId!)
                .Select(g => new PlayerMissed(g.Key, info.Player(g.Key)?.Name ?? g.Key, g.Count(), _spatial.Choices.Count(c => c.PlayerId == g.Key),
                    Math.Round(g.Sum(c => c.Gain), 3), g.OrderByDescending(c => c.Gain).Take(3).Select(c => c.EventId).ToList()))
                .OrderByDescending(p => p.XtLeft).Take(6).ToList();
            return new SpatialSummary(nowSec, teams, players,
                "Pitch control: a time-to-intercept model on tracking (who would reach each spot first). Better pass: at the moment of each pass, another teammate's pass would have delivered at least 0.02 more expected threat (completion chance × xT) and arrived at least 55% of the time.");
        }
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 3) : null;

    private PassView PassOf(PassChoice c)
    {
        bool away = c.TeamId == _recording.Match.Away.TeamId;
        // Board units: percent of the pitch, home attacking towards 100 (the option is in the passing team's frame).
        double Home(double v) => Math.Round(away ? 100 - v : v, 1);
        return new PassView(c.EventId, c.Chosen.Difficulty, c.Lane.Interceptable, c.Lane.LaneDefenders, c.Lane.ReceiverMarked,
            c.Chosen.Completion, c.Chosen.Xt, c.Better?.PlayerId, c.Better is { } b1 ? Home(b1.X) : null, c.Better is { } b2 ? Home(b2.Y) : null,
            c.Better is null ? null : Math.Round(c.Gain, 3), c.Better?.Completion);
    }

    /// <summary>Every player's running so far, fastest first within each team.</summary>
    public IReadOnlyList<RunnerView> Running()
    {
        MatchInfo info = _recording.Match;
        lock (_sync)
        {
            return _kinematics.Runners
                .Select(r => new RunnerView(r.PlayerId, r.TeamId, info.Player(r.PlayerId)?.Name ?? r.PlayerId,
                    Math.Round(r.DistanceMetres / 1000, 2), Math.Round(r.TopSpeedKmh, 1), Math.Round(r.SpeedKmh, 1), r.Sprints,
                    Math.Round(r.HighIntensityMetres / 1000, 2)))
                .OrderBy(r => r.TeamId).ThenByDescending(r => r.DistanceKm)
                .ToList();
        }
    }

    /// <summary>How auto-eventing from tracking alone compares with the event feed so far, or null before any frames.</summary>
    public AutoEventView? Report()
    {
        if (!Ready) return null;
        TrackingSynth synth = _build.Result;
        lock (_sync)
        {
            if (double.IsNaN(_t)) return null;
            double upTo = _t - ScoreLagSec;
            AutoEventReport report = AutoEventScore.Score(_recording.Events, synth.EventTimes, _eventer.Detected, synth.StartT, upTo);
            var recent = _eventer.Detected.Where(d => d.T <= upTo).TakeLast(8).ToList();
            return new AutoEventView(Math.Round(upTo, 1), report, _eventer.Detected.Count, recent);
        }
    }

    private TrackingView View(TrackingFrame f)
    {
        static double Px(double metres, double size) => Math.Round(metres / size * 100, 1);
        return new TrackingView(f.T, Px(f.Ball.X, Pitch.LengthMetres), Px(f.Ball.Y, Pitch.WidthMetres), f.Ball.Z,
            f.Players.Select(p => new TrackedPlayer(p.PlayerId, Px(p.X, Pitch.LengthMetres), Px(p.Y, Pitch.WidthMetres))).ToList());
    }
}

/// <summary>What one tracking step produced: the positions to draw, thresholds reached, and passes just judged.</summary>
public sealed record TrackingTick(TrackingView Frame, IReadOnlyList<KinematicAlert> Alerts, IReadOnlyList<PassView> Passes);

/// <summary>A pitch-control map: the home team's control (0 to 1) per cell, row by row from y 0, home attacking to x = 105.</summary>
public sealed record ControlView(double T, int Cols, int Rows, double[] Home);

/// <summary>
/// A pass judged at the moment it was played: difficulty with the lane (0 to 100), opponents who could cut it out and
/// who were in its path, whether the target was marked, its chance of arriving and the xT where it was aimed; and the
/// better option, when there was one (teammate, where he was in board units, how much more xT, its chance of arriving).
/// </summary>
public sealed record PassView(string EventId, int LaneDifficulty, int Interceptable, int LaneDefenders, bool ReceiverMarked,
    double Completion, double Xt, string? BetterTo, double? BetterX, double? BetterY, double? BetterGain, double? BetterCompletion);

public sealed record TeamSpace(string TeamId, string Name, double? FinalThirdControlLast10, double? FinalThirdControlMatch, int BetterPassesMissed);
public sealed record PlayerMissed(string PlayerId, string Name, int BetterPassesMissed, int PassesJudged, double XtLeft, IReadOnlyList<string> ExampleEventIds);
public sealed record SpatialSummary(int AtSec, IReadOnlyList<TeamSpace> Teams, IReadOnlyList<PlayerMissed> Players, string Method);

/// <summary>Positions for drawing, in the board's units: percent of the pitch, home attacking towards X = 100.</summary>
public sealed record TrackingView(double T, double BallX, double BallY, double BallZ, IReadOnlyList<TrackedPlayer> Players);

public sealed record TrackedPlayer(string Id, double X, double Y);

/// <summary>One player's running: distance (km), top and current speed (km/h), sprints and high-intensity distance (km).</summary>
public sealed record RunnerView(
    string PlayerId, string TeamId, string Name, double DistanceKm, double TopSpeedKmh, double SpeedKmh, int Sprints, double HighIntensityKm);

/// <summary>A threshold a player just reached, for the tag on the pitch: top speed or a distance milestone.</summary>
public sealed record TagView(string PlayerId, string TeamId, double AtSec, string Kind, double SpeedKmh, double DistanceKm, double X, double Y);

/// <summary>Auto-eventing so far: the score against the feed, how many events it found, and the latest of them.</summary>
public sealed record AutoEventView(double UpToSec, AutoEventReport Report, int Detected, IReadOnlyList<DetectedEvent> Recent);
