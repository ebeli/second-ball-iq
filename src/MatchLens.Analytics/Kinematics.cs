using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>
/// Players' running from tracking frames, as they arrive: distance covered, current and top speed, sprints and
/// high-intensity running, and the alerts that tag a player on screen when a threshold is reached.
/// <para>
/// Positions carry measurement noise, so speeds and distances are measured on half-second averages: speed from the
/// last five frames against the five before, distance from one averaged point to the next. That keeps the noise
/// from adding distance (raw 10 Hz noise of 12 cm would add kilometres over a match).
/// </para>
/// </summary>
public sealed class Kinematics
{
    /// <summary>A sprint: at least this fast for at least <see cref="SprintMinSec"/> (the common 7 m/s definition).</summary>
    public const double SprintKmh = 25.2;
    public const double SprintMinSec = 1.0;

    /// <summary>High-intensity running, the usual 5.5 m/s line.</summary>
    public const double HighIntensityKmh = 19.8;

    /// <summary>A player reaching this speed is tagged on screen (at most once every two minutes each).</summary>
    public const double AlertKmh = 33.5;
    public const double AlertGapSec = 120;

    /// <summary>Distances that tag a player when they pass them.</summary>
    public static readonly double[] DistanceMilestonesKm = [5, 10];

    private const int Window = 5;
    private readonly Dictionary<string, Runner> _runners = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Runner> Runners => _runners.Values;

    public Runner? Of(string playerId) => _runners.GetValueOrDefault(playerId);

    /// <summary>Adds one frame (frames must come in order, one per tick of the provider's clock); returns any alerts.</summary>
    public IReadOnlyList<KinematicAlert> Add(TrackingFrame frame, double hz = 10)
    {
        List<KinematicAlert>? alerts = null;
        foreach (PlayerFix p in frame.Players)
        {
            if (!_runners.TryGetValue(p.PlayerId, out Runner? r)) _runners[p.PlayerId] = r = new Runner(p.PlayerId, p.TeamId);
            if (r.Step(frame.T, p.X, p.Y, hz) is { } alert) (alerts ??= []).Add(alert);
        }
        return alerts ?? (IReadOnlyList<KinematicAlert>)[];
    }

    /// <summary>One player's running so far.</summary>
    public sealed class Runner(string playerId, string teamId)
    {
        public string PlayerId { get; } = playerId;
        public string TeamId { get; } = teamId;
        public double DistanceMetres { get; private set; }
        public double HighIntensityMetres { get; private set; }
        public double SpeedKmh { get; private set; }
        public double TopSpeedKmh { get; private set; }
        public double TopSpeedAtSec { get; private set; }
        public int Sprints { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }

        private readonly Queue<(double X, double Y)> _recent = new();
        private (double X, double Y)? _lastMean;
        private int _sinceMean;
        private double _fastSince = double.NaN;
        private bool _inSprint;
        private double _lastAlert = double.NegativeInfinity;
        private int _milestone;

        internal KinematicAlert? Step(double t, double x, double y, double hz)
        {
            X = x;
            Y = y;
            _recent.Enqueue((x, y));
            if (_recent.Count > 2 * Window) _recent.Dequeue();
            KinematicAlert? alert = null;

            if (_recent.Count == 2 * Window)
            {
                (double X, double Y)[] all = _recent.ToArray();
                (double ax, double ay) = Mean(all, 0, Window);
                (double bx, double by) = Mean(all, Window, Window);
                double kmh = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) / (Window / hz) * 3.6;
                SpeedKmh = kmh;
                if (kmh > TopSpeedKmh) { TopSpeedKmh = kmh; TopSpeedAtSec = t; }

                if (kmh >= SprintKmh)
                {
                    if (double.IsNaN(_fastSince)) _fastSince = t;
                    if (!_inSprint && t - _fastSince >= SprintMinSec) { _inSprint = true; Sprints++; }
                }
                else { _fastSince = double.NaN; _inSprint = false; }

                if (kmh >= AlertKmh && t - _lastAlert >= AlertGapSec)
                {
                    _lastAlert = t;
                    alert = new KinematicAlert(PlayerId, TeamId, t, KinematicAlertKind.TopSpeed, Math.Round(kmh, 1), Math.Round(DistanceMetres), bx, by);
                }

                if (++_sinceMean >= Window)
                {
                    _sinceMean = 0;
                    if (_lastMean is { } last)
                    {
                        double step = Math.Sqrt((bx - last.X) * (bx - last.X) + (by - last.Y) * (by - last.Y));
                        DistanceMetres += step;
                        if (kmh >= HighIntensityKmh) HighIntensityMetres += step;
                    }
                    _lastMean = (bx, by);
                    if (_milestone < DistanceMilestonesKm.Length && DistanceMetres >= DistanceMilestonesKm[_milestone] * 1000)
                    {
                        alert ??= new KinematicAlert(PlayerId, TeamId, t, KinematicAlertKind.Distance, Math.Round(kmh, 1),
                            DistanceMilestonesKm[_milestone] * 1000, bx, by);
                        _milestone++;
                    }
                }
            }
            return alert;
        }

        private static (double X, double Y) Mean((double X, double Y)[] a, int from, int n)
        {
            double sx = 0, sy = 0;
            for (int i = from; i < from + n; i++) { sx += a[i].X; sy += a[i].Y; }
            return (sx / n, sy / n);
        }
    }
}

public enum KinematicAlertKind
{
    /// <summary>The player reached <see cref="Kinematics.AlertKmh"/>.</summary>
    TopSpeed,
    /// <summary>The player passed a distance milestone (5 km, 10 km).</summary>
    Distance,
}

/// <summary>A threshold reached: who, when, the speed (km/h), the distance so far (metres) and where (tracking metres).</summary>
public sealed record KinematicAlert(
    string PlayerId, string TeamId, double AtSec, KinematicAlertKind Kind, double SpeedKmh, double DistanceMetres, double X, double Y);
