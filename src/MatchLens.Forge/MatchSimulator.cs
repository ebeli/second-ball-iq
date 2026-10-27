using MatchLens.Contracts;

namespace MatchLens.Forge;

/// <summary>A generated match: metadata, the event stream in order, the measured ground truth, and the shapes played.</summary>
public sealed record MatchRecording(MatchInfo Match, IReadOnlyList<MatchEvent> Events, GroundTruth GroundTruth)
{
    /// <summary>Which shape each team played when, and why it changed. Empty for recordings loaded from disk.</summary>
    public IReadOnlyList<ShapeSpell> Shapes { get; init; } = [];
}

/// <summary>
/// A stretch of the match in which a team kept one shape and style. <paramref name="Reason"/> says why it began
/// (null at kick-off): a coach's change or a reaction to the score.
/// </summary>
public sealed record ShapeSpell(Side Side, Formation Formation, TeamStyle Style, int FromSec, int ToSec, string? Reason);

/// <summary>
/// Possession-chain simulator. Each step the defending team may press (and sometimes win the ball); the team in
/// possession then shoots or passes, and a failed pass is intercepted. Scripted moments interrupt play at fixed
/// times so the scenario's key chains always happen. Deterministic for a given scenario and seed.
/// </summary>
/// <remarks>
/// Internally the ball's progress <c>p</c> is measured from the possessing team's own goal (0) to the opponent's (100);
/// every emitted event uses the acting team's frame, as the contract requires. Organic shots never become goals,
/// so the scoreline is exactly what the script says.
/// </remarks>
public sealed class MatchSimulator
{
    private const int HalfSec = 45 * 60;

    private readonly Scenario _scenario;
    private readonly int _seed;
    private readonly Random _rng;

    /// <summary>
    /// Set pieces draw from their own random stream, so adding them leaves the rest of the match (and every
    /// scenario's ground truth) exactly as it was.
    /// </summary>
    private readonly Random _setPieces;
    private readonly MatchInfo _match;
    private readonly List<MatchEvent> _events = [];
    private readonly Queue<ScriptedMoment> _pending;
    private readonly List<ScriptedMomentRecord> _momentRecords = [];
    private readonly Dictionary<Side, int> _goals = new() { [Side.Home] = 0, [Side.Away] = 0 };

    private int _clock;
    private int _period;

    // Shape: each side's current shape from the coach, changes still to come, style adjustments made so far, and how
    // long the current score has stood (for the reactions).
    private readonly Dictionary<Side, Formation> _shape = new();
    private readonly Dictionary<Side, Formation> _current = new();
    private readonly Dictionary<Side, Dictionary<string, string>> _subs = new() { [Side.Home] = new(), [Side.Away] = new() };
    private readonly Dictionary<Side, List<Func<TeamStyle, TeamStyle>>> _adjust = new() { [Side.Home] = [], [Side.Away] = [] };
    private readonly Queue<CoachChange> _changes;
    private readonly Dictionary<Side, GameStateReaction?> _reaction = new() { [Side.Home] = null, [Side.Away] = null };
    private readonly List<ShapeSpell> _spells = [];
    private readonly Dictionary<Side, (Formation Formation, TeamStyle Style, int FromSec, string? Reason)> _spell = new();
    private int _scoreChangedAt;

    /// <summary>Lateral pull of a pass towards the receiver's position, so play follows the team's shape.</summary>
    private const double ShapePull = 0.5;

    /// <summary>How far a player's touches spread from their position, in pitch units (length, width).</summary>
    private const double SpreadX = 13, SpreadY = 15;

    public MatchSimulator(Scenario scenario, int seed)
    {
        _scenario = scenario;
        _seed = seed;
        _rng = new Random(seed);
        _setPieces = new Random(seed * 31 + 7);
        _pending = new Queue<ScriptedMoment>(scenario.Moments.OrderBy(m => m.AtSec));
        _changes = new Queue<CoachChange>(scenario.CoachChanges.OrderBy(c => c.AtSec));
        _shape[Side.Home] = scenario.HomeLineup;
        _shape[Side.Away] = scenario.AwayLineup;
        _current[Side.Home] = _shape[Side.Home];
        _current[Side.Away] = _shape[Side.Away];
        _match = new MatchInfo
        {
            MatchId = $"{scenario.Id}-{seed}",
            ScenarioId = scenario.Id,
            Seed = seed,
            Competition = "Second Ball IQ Synthetic League",
            Home = Rosters.HarborCity,
            Away = Rosters.Northvale,
        };
    }

    public static MatchRecording Generate(Scenario scenario, int seed) => new MatchSimulator(scenario, seed).Run();

    public MatchRecording Run()
    {
        foreach (Side side in new[] { Side.Home, Side.Away })
            _spell[side] = (_shape[side], StyleOf(side), 0, null);
        Half(1, 0, HalfSec, Side.Home);
        Half(2, HalfSec, 2 * HalfSec, Side.Away);
        foreach (Side side in new[] { Side.Home, Side.Away })
            _spells.Add(new ShapeSpell(side, _spell[side].Formation, _spell[side].Style, _spell[side].FromSec, _clock, _spell[side].Reason));
        List<ShapeSpell> spells = _spells.OrderBy(x => x.FromSec).ToList();
        GroundTruth truth = GroundTruthBuilder.Build(_scenario, _seed, _match, _events, _momentRecords,
            _goals[Side.Home], _goals[Side.Away], spells);
        return new MatchRecording(_match, _events, truth) { Shapes = spells };
    }

    // ------------------------------------------------------------------------------------------------
    // Open play
    // ------------------------------------------------------------------------------------------------

    private void Half(int period, int startSec, int endSec, Side kickOff)
    {
        _period = period;
        // The clock never runs backwards: the second half starts at 45:00, or after first-half stoppage time.
        _clock = Math.Max(startSec, _clock);
        Emit(MatchEventType.PeriodStart, kickOff, null, 50, 50);
        Play(kickOff, endSec);
        Emit(MatchEventType.PeriodEnd, kickOff, null, 50, 50);
    }

    private void Play(Side team, int endSec)
    {
        double p = 50, y = 50;
        string carrier = PickPlayer(team, p, PlayerPosition.Forward);
        Emit(MatchEventType.KickOff, team, carrier, p, y);

        while (_clock < endSec)
        {
            UpdateShapes();
            if (_pending.Count > 0 && _pending.Peek().AtSec <= _clock)
            {
                ScriptedMoment moment = _pending.Dequeue();
                (team, p, y) = Inject(moment, team);
                carrier = moment.Kind is MomentKind.Penalty or MomentKind.FreeKick ? Keeper(team) : PickPlayer(team, p, y: y);
                continue;
            }

            (TeamStyle att, TeamStyle def) = Styles(team);
            Side opp = Other(team);

            // The defending team presses, more often when the attacker is deep in its own half.
            double depth = Math.Max(0, (50 - p) / 50);
            // A low block (BlockHeight below 1) hardly presses while the ball is in the attacker's own half.
            double block = p < 50 ? def.BlockHeight : 1.0;
            bool pressured = _rng.NextDouble() < def.Press * (0.45 + 1.0 * depth) * block;
            if (pressured)
            {
                string presser = PickPlayer(opp, 100 - p, y: 100 - y);
                Emit(MatchEventType.Pressure, opp, presser, 100 - p, 100 - y);
                if (_rng.NextDouble() < def.Tackle * (0.6 + depth))
                {
                    Tick(1, 2);
                    Emit(MatchEventType.Tackle, opp, presser, 100 - p, 100 - y, outcome: EventOutcome.Success);
                    Emit(MatchEventType.PossessionChange, opp, presser, 100 - p, 100 - y);
                    (team, p, y, carrier) = (opp, 100 - p, 100 - y, presser);
                    Tick(1, 3);
                    continue;
                }
            }

            // Shot: only well inside the final third.
            if (p >= 70 && _rng.NextDouble() < att.ShotPropensity * (p - 60) / 30)
            {
                double xg = Xg(p, y) * (pressured ? 0.75 : 1.0);
                double roll = _rng.NextDouble();
                ShotResult result = roll < 0.25 ? ShotResult.Blocked : roll < 0.60 ? ShotResult.OffTarget : ShotResult.Saved;
                Tick(1, 3);
                Emit(MatchEventType.Shot, team, carrier, p, y, underPressure: pressured, xg: xg, shotResult: result,
                    speed: Uniform(70, 115));
                int shotClock = _clock;
                Tick(8, 25);
                // A saved or blocked shot sometimes goes behind for a corner, taken in the stoppage that follows.
                if (result is ShotResult.Saved or ShotResult.Blocked && _setPieces.NextDouble() < 0.45)
                    Corner(team, shotClock, _clock);
                (team, p, y) = (opp, 6, 50);   // goal kick or keeper's ball
                carrier = PickPlayer(team, p, PlayerPosition.Goalkeeper);
                Emit(MatchEventType.PossessionChange, team, carrier, p, y);
                continue;
            }

            // Pass.
            bool longBall = _rng.NextDouble() < att.Directness;
            double delta = longBall ? Gaussian(28, 8) : Gaussian(att.Progression, 9);
            if (p > 60 && delta > 0) delta *= att.FinalThirdEntry;
            double p2 = Math.Clamp(p + delta, 1, 97);
            double y2 = Math.Clamp(y + Gaussian(0, 18), 2, 98);
            // The receiver is whoever's position is nearest where the ball is going; the ball then drifts towards
            // them, so a team's play follows its shape (wingers wide, an inverted winger inside).
            string receiver = PickPlayer(team, p2, y: y2, exclude: carrier);
            y2 = Math.Clamp((1 - ShapePull) * y2 + ShapePull * HomeY(team, receiver), 2, 98);
            double metres = Pitch.DistanceMetres(new PitchPoint(p, y), new PitchPoint(p2, y2));
            double accuracy = att.PassAccuracy
                + (p < 33 && !longBall ? 0.10 : 0)
                - (pressured ? 0.10 : 0)
                - (longBall ? 0.22 : 0)
                - (p2 > 75 ? 0.10 : 0)
                - 0.002 * Math.Max(0, Math.Abs(p2 - p) - 15);
            Tick(2, 5);
            double speed = Math.Min(95, 25 + 1.6 * metres + Uniform(-5, 5));

            if (_rng.NextDouble() < accuracy)
            {
                Emit(MatchEventType.Pass, team, carrier, p, y, p2, y2, EventOutcome.Success, receiver, pressured, speed: speed, longBall: longBall);
                (p, y, carrier) = (p2, y2, receiver);
            }
            else
            {
                Emit(MatchEventType.Pass, team, carrier, p, y, p2, y2, EventOutcome.Fail, receiver, pressured, speed: speed, longBall: longBall);
                if (_rng.NextDouble() < 0.2) Tick(6, 20);   // out of play, throw-in
                Tick(0, 2);
                string interceptor = PickPlayer(opp, 100 - p2, y: 100 - y2);
                Emit(MatchEventType.Interception, opp, interceptor, 100 - p2, 100 - y2, outcome: EventOutcome.Success);
                Emit(MatchEventType.PossessionChange, opp, interceptor, 100 - p2, 100 - y2);
                (team, p, y, carrier) = (opp, 100 - p2, 100 - y2, interceptor);
            }

            if (_rng.NextDouble() < 0.035) Tick(10, 40);   // foul, injury or set-piece set-up
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Scripted moments
    // ------------------------------------------------------------------------------------------------

    private (Side Team, double P, double Y) Inject(ScriptedMoment moment, Side team)
    {
        return moment.Kind switch
        {
            MomentKind.HighTurnover => InjectHighTurnover(moment, team),
            MomentKind.BuildUpShot => InjectBuildUpShot(moment),
            MomentKind.Penalty => InjectSetPieceShot(moment, SetPiece.Penalty),
            MomentKind.FreeKick => InjectSetPieceShot(moment, SetPiece.FreeKick),
            _ => throw new InvalidOperationException($"Unknown moment kind {moment.Kind}."),
        };
    }

    /// <summary>Victim builds from the back, winner presses and wins it in the victim's defensive third.</summary>
    private (Side, double, double) InjectHighTurnover(ScriptedMoment moment, Side team)
    {
        Side winner = moment.Side, victim = Other(moment.Side);
        var ids = new List<string>();

        if (team == winner)
        {
            // The winner has the ball: a hopeful long ball is collected deep by the victim.
            Tick(2, 4);
            string passer = PickPlayer(winner, 55);
            Emit(MatchEventType.Pass, winner, passer, 55, 50, 88, 50, EventOutcome.Fail, PickPlayer(winner, 88, exclude: passer));
            string keeper = PickPlayer(victim, 12, PlayerPosition.Goalkeeper);
            Emit(MatchEventType.Interception, victim, keeper, 12, 50, outcome: EventOutcome.Success);
            Emit(MatchEventType.PossessionChange, victim, keeper, 12, 50);
        }

        double y = Uniform(20, 80);
        string builder = PickPlayer(victim, 12, PlayerPosition.Defender, y: y);
        string target = PickPlayer(victim, 20, PlayerPosition.Defender, exclude: builder, y: y);
        Tick(2, 4);
        Emit(MatchEventType.Pass, victim, builder, 12, y, 20, y, EventOutcome.Success, target);

        string presser = moment.Follow is null && moment.PlayerId is not null
            ? moment.PlayerId
            : PickPlayer(winner, 80, PlayerPosition.Midfielder, y: 100 - y);
        Tick(1, 2);
        ids.Add(Emit(MatchEventType.Pressure, winner, presser, 80, 100 - y).EventId);
        ids.Add(Emit(MatchEventType.Interception, winner, presser, 79, 100 - y, outcome: EventOutcome.Success).EventId);
        ids.Add(Emit(MatchEventType.PossessionChange, winner, presser, 79, 100 - y).EventId);

        if (moment.Follow is null)
        {
            _momentRecords.Add(new("highTurnover", TeamId(winner), _clock, ids));
            return (winner, 79, 100 - y);
        }

        string shooter = moment.PlayerId ?? PickPlayer(winner, 88, PlayerPosition.Forward, exclude: presser);
        Tick(2, 4);
        ids.Add(Emit(MatchEventType.Pass, winner, presser, 79, 100 - y, 88, 52, EventOutcome.Success, shooter,
            speed: Uniform(40, 60)).EventId);
        return FinishWithShot(moment, winner, shooter, ids, "highTurnover");
    }

    /// <summary>A short, clean move into the box ending in a shot.</summary>
    private (Side, double, double) InjectBuildUpShot(ScriptedMoment moment)
    {
        Side team = moment.Side;
        var ids = new List<string>();
        string shooter = moment.PlayerId ?? PickPlayer(team, 88, PlayerPosition.Forward);
        (double P, double Y)[] path = [(45, 50), (58, 35), (72, 30), (86, 45)];
        string carrier = PickPlayer(team, 45, PlayerPosition.Midfielder, exclude: shooter);
        for (int i = 0; i < path.Length - 1; i++)
        {
            bool last = i == path.Length - 2;
            string receiver = last ? shooter : PickPlayer(team, path[i + 1].P, exclude: carrier);
            if (receiver == shooter && !last) receiver = PickPlayer(team, path[i + 1].P, PlayerPosition.Midfielder, exclude: shooter);
            Tick(2, 4);
            ids.Add(Emit(MatchEventType.Pass, team, carrier, path[i].P, path[i].Y, path[i + 1].P, path[i + 1].Y,
                EventOutcome.Success, receiver, speed: Uniform(35, 65)).EventId);
            carrier = receiver;
        }
        return FinishWithShot(moment, team, shooter, ids, "buildUpShot");
    }

    private (Side, double, double) FinishWithShot(ScriptedMoment moment, Side team, string shooter, List<string> ids, string kind)
    {
        ShotResult result = moment.Follow ?? ShotResult.Saved;
        Tick(1, 3);
        ids.Add(Emit(MatchEventType.Shot, team, shooter, 88, 52, xg: Xg(88, 52), shotResult: result,
            speed: Uniform(80, 115)).EventId);

        if (result == ShotResult.Goal)
        {
            _goals[team]++;
            _scoreChangedAt = _clock;
            ids.Add(Emit(MatchEventType.Goal, team, shooter, 100, 50).EventId);
            _momentRecords.Add(new(kind, TeamId(team), _clock, ids));
            Tick(40, 70);   // celebration and restart
            Side restart = Other(team);
            Emit(MatchEventType.KickOff, restart, PickPlayer(restart, 50, PlayerPosition.Forward), 50, 50);
            return (restart, 50, 50);
        }

        _momentRecords.Add(new(kind, TeamId(team), _clock, ids));
        Tick(8, 25);
        Side keeperSide = Other(team);
        Emit(MatchEventType.PossessionChange, keeperSide, PickPlayer(keeperSide, 6, PlayerPosition.Goalkeeper), 6, 50);
        return (keeperSide, 6, 50);
    }

    // ------------------------------------------------------------------------------------------------
    // Set pieces (own random stream)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A corner inside an existing stoppage: taken between the shot and the restart, so no other event moves. The
    /// club's corner taker delivers to a zone drawn from their habits; the keeper or a defender then deals with it.
    /// </summary>
    private void Corner(Side team, int fromClock, int toClock)
    {
        TakerProfile taker = Taker(team, SetPiece.Corner);
        var from = new PitchPoint(99.5, _setPieces.NextDouble() < 0.5 ? 0.5 : 99.5);
        string zone = SetPieceTakers.Draw(taker, _setPieces);
        PitchPoint to = SetPieceTakers.Target(SetPiece.Corner, zone, from, _setPieces);
        bool found = zone == SetPieceZones.Short || _setPieces.NextDouble() < 0.5;
        string receiver = Pick(team, zone == SetPieceZones.Short ? PlayerPosition.Midfielder : PlayerPosition.Defender, taker.PlayerId);

        int resume = _clock;
        _clock = fromClock + (int)((toClock - fromClock) * 0.6);
        Emit(MatchEventType.Pass, team, taker.PlayerId, from.X, from.Y, to.X, to.Y,
            found ? EventOutcome.Success : EventOutcome.Fail, receiver, speed: 40 + 30 * _setPieces.NextDouble(),
            setPiece: SetPiece.Corner);
        _clock = Math.Max(_clock, resume);
    }

    /// <summary>A foul, then a penalty or a direct free kick at goal by the club's taker.</summary>
    private (Side, double, double) InjectSetPieceShot(ScriptedMoment moment, SetPiece kind)
    {
        Side team = moment.Side, defending = Other(moment.Side);
        TakerProfile taker = Taker(team, kind);
        string shooter = moment.PlayerId ?? taker.PlayerId;
        var ids = new List<string>();

        bool penalty = kind == SetPiece.Penalty;
        double y = penalty ? 50 : 35 + 30 * _setPieces.NextDouble();
        double foulX = penalty ? 9 + 4 * _setPieces.NextDouble() : 24 + 5 * _setPieces.NextDouble();   // defending frame
        _clock += 2 + _setPieces.Next(3);
        ids.Add(Emit(MatchEventType.Foul, defending, Pick(defending, PlayerPosition.Defender, null), foulX, 100 - y).EventId);

        _clock += penalty ? 45 + _setPieces.Next(30) : 25 + _setPieces.Next(20);   // the pause before the kick
        string zone = SetPieceTakers.Draw(SetPieceTakers.All.FirstOrDefault(t => t.PlayerId == shooter && t.Kind == kind) ?? taker, _setPieces);
        PitchPoint end = SetPieceTakers.Target(kind, zone, default, _setPieces);
        double x = penalty ? 88.6 : 100 - foulX;
        ShotResult result = moment.Follow ?? (penalty ? ShotResult.Goal : ShotResult.Saved);
        ids.Add(Emit(MatchEventType.Shot, team, shooter, x, penalty ? 50 : y, end.X, end.Y,
            xg: penalty ? 0.76 : 0.06, shotResult: result, speed: 90 + 25 * _setPieces.NextDouble(), setPiece: kind).EventId);

        string record = penalty ? "penalty" : "freeKick";
        if (result == ShotResult.Goal)
        {
            _goals[team]++;
            _scoreChangedAt = _clock;
            ids.Add(Emit(MatchEventType.Goal, team, shooter, 100, 50).EventId);
            _momentRecords.Add(new(record, TeamId(team), _clock, ids));
            _clock += 40 + _setPieces.Next(30);
            Emit(MatchEventType.KickOff, defending, Pick(defending, PlayerPosition.Forward, null), 50, 50);
            return (defending, 50, 50);
        }

        _momentRecords.Add(new(record, TeamId(team), _clock, ids));
        _clock += 6 + _setPieces.Next(12);
        Emit(MatchEventType.PossessionChange, defending, Keeper(defending), 6, 50);
        return (defending, 6, 50);
    }

    private string Keeper(Side side) => Shape(side).Slots[0].PlayerId;

    /// <summary>A player on the pitch for a set piece, drawn from the set-piece stream so open play is untouched.</summary>
    private string Pick(Side side, PlayerPosition position, string? exclude)
    {
        var pool = OnPitch(side).Where(p => p.Position == position && p.PlayerId != exclude).ToList();
        if (pool.Count == 0) pool = OnPitch(side).Where(p => p.Position != PlayerPosition.Goalkeeper && p.PlayerId != exclude).ToList();
        return pool[_setPieces.Next(pool.Count)].PlayerId;
    }

    /// <summary>The club's taker, or a midfielder on the pitch if the taker has been substituted.</summary>
    private TakerProfile Taker(Side side, SetPiece kind)
    {
        TakerProfile taker = SetPieceTakers.For(side == Side.Home ? _match.Home : _match.Away, kind);
        return Shape(side).Slot(taker.PlayerId) is not null ? taker : taker with { PlayerId = Pick(side, PlayerPosition.Midfielder, null) };
    }

    private IEnumerable<PlayerInfo> OnPitch(Side side)
    {
        TeamInfo team = side == Side.Home ? _match.Home : _match.Away;
        return Shape(side).Slots.Select(s => team.Players.First(p => p.PlayerId == s.PlayerId));
    }

    // ------------------------------------------------------------------------------------------------
    // Shape: the coaches' changes and the reactions to the score
    // ------------------------------------------------------------------------------------------------

    /// <summary>The shape a side is playing now: its reaction to the score if one is active, else the coach's shape.</summary>
    private Formation Shape(Side side) => _current[side];

    /// <summary>
    /// Recomputes the side's shape: the reaction's shape if one is active, else the coach's, with anyone substituted
    /// replaced by whoever came on for them.
    /// </summary>
    private void Refresh(Side side)
    {
        Formation f = _reaction[side]?.Formation ?? _shape[side];
        Dictionary<string, string> subs = _subs[side];
        if (subs.Count > 0 && f.Slots.Any(x => subs.ContainsKey(x.PlayerId)))
            f = f with { Slots = f.Slots.Select(x => subs.TryGetValue(x.PlayerId, out string? on) ? x with { PlayerId = on } : x).ToList() };
        if (_current[side].Name != f.Name || !_current[side].Slots.SequenceEqual(f.Slots)) _current[side] = f;
    }

    private ScoreState StateOf(Side side) =>
        _goals[side] > _goals[Other(side)] ? ScoreState.Leading : _goals[side] < _goals[Other(side)] ? ScoreState.Trailing : ScoreState.Level;

    /// <summary>
    /// Applies coaching changes that are due (with their substitutions) and switches score reactions on or off.
    /// Draws nothing from the random streams, so it changes who plays where and how, never the sequence of chances.
    /// </summary>
    private void UpdateShapes()
    {
        while (_changes.Count > 0 && _changes.Peek().AtSec <= _clock)
        {
            CoachChange change = _changes.Dequeue();
            if (change.Substitution is { } sub)
            {
                Emit(MatchEventType.Substitution, change.Side, sub.Off, 50, 0, recipientId: sub.On);
                _subs[change.Side][sub.Off] = sub.On;
            }
            if (change.Formation is not null) _shape[change.Side] = change.Formation;
            if (change.Adjust is not null) _adjust[change.Side].Add(change.Adjust);
            Refresh(change.Side);
            Record(change.Side, change.Reason);
        }

        foreach (Side side in new[] { Side.Home, Side.Away })
        {
            ScoreState state = StateOf(side);
            GameStateReaction? active = _scenario.ScoreReactions.FirstOrDefault(r =>
                r.Side == side && r.When == state && _clock - _scoreChangedAt >= r.DelaySec);
            if (active == _reaction[side]) continue;
            GameStateReaction? previous = _reaction[side];
            _reaction[side] = active;
            Refresh(side);
            Record(side, active?.Reason ?? $"no longer {previous!.Reason}");
        }
    }

    /// <summary>Closes the side's current spell if its shape or style changed, and opens the next one.</summary>
    private void Record(Side side, string reason)
    {
        Formation formation = Shape(side);
        TeamStyle style = StyleOf(side);
        var current = _spell[side];
        if (current.Formation == formation && current.Style == style) return;
        _spells.Add(new ShapeSpell(side, current.Formation, current.Style, current.FromSec, _clock, current.Reason));
        _spell[side] = (formation, style, _clock, reason);
    }

    /// <summary>The side's style without phases: what the coach and the score have asked for.</summary>
    private TeamStyle StyleOf(Side side) => Adjusted(side, TeamStyle.Balanced);

    private TeamStyle Adjusted(Side side, TeamStyle style)
    {
        foreach (Func<TeamStyle, TeamStyle> adjust in _adjust[side]) style = adjust(style);
        return _reaction[side]?.Adjust is { } react ? react(style) : style;
    }

    private double HomeY(Side side, string playerId) => Shape(side).Slot(playerId)?.Y ?? 50;

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private (TeamStyle Attacker, TeamStyle Defender) Styles(Side attacker)
    {
        TeamStyle home = TeamStyle.Balanced, away = TeamStyle.Balanced;
        foreach (Phase phase in _scenario.Phases)
        {
            if (phase.FromSec <= _clock && _clock < phase.ToSec)
            {
                home = phase.Home ?? home;
                away = phase.Away ?? away;
            }
        }
        home = Adjusted(Side.Home, home);
        away = Adjusted(Side.Away, away);
        return attacker == Side.Home ? (home, away) : (away, home);
    }

    private MatchEvent Emit(
        MatchEventType type, Side side, string? playerId, double x, double y,
        double? endX = null, double? endY = null, EventOutcome? outcome = null, string? recipientId = null,
        bool? underPressure = null, double? xg = null, ShotResult? shotResult = null, double? speed = null,
        SetPiece? setPiece = null, bool longBall = false)
    {
        var e = new MatchEvent
        {
            EventId = $"evt-{_events.Count + 1:D6}",
            MatchId = _match.MatchId,
            Seq = _events.Count + 1,
            Period = _period,
            MatchClockSec = _clock,
            Type = type,
            TeamId = TeamId(side),
            PlayerId = playerId,
            Start = new PitchPoint(Math.Round(x, 1), Math.Round(y, 1)),
            End = endX is null || endY is null ? null : new PitchPoint(Math.Round(endX.Value, 1), Math.Round(endY.Value, 1)),
            Outcome = outcome,
            RecipientId = recipientId,
            UnderPressure = underPressure,
            Xg = xg is null ? null : Math.Round(xg.Value, 3),
            ShotResult = shotResult,
            BallSpeedKmh = speed is null ? null : Math.Round(speed.Value, 1),
            SetPiece = setPiece,
            LongBall = longBall ? true : null,
        };
        _events.Add(e);
        return e;
    }

    private string TeamId(Side side) => side == Side.Home ? _match.Home.TeamId : _match.Away.TeamId;

    private static Side Other(Side side) => side == Side.Home ? Side.Away : Side.Home;

    private void Tick(int min, int max) => _clock += _rng.Next(min, max + 1);

    private double Uniform(double min, double max) => min + _rng.NextDouble() * (max - min);

    /// <summary>Box-Muller; System.Random has no Gaussian.</summary>
    private double Gaussian(double mean, double sd)
    {
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = _rng.NextDouble();
        return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Simple distance-based expected goals, 0.02..0.60. Goal centre is (100, 50).</summary>
    public static double Xg(double p, double y)
    {
        double dx = (100 - p) / 100 * Pitch.LengthMetres;
        double dy = (50 - y) / 100 * Pitch.WidthMetres;
        double metres = Math.Sqrt(dx * dx + dy * dy);
        return Math.Clamp(0.65 * Math.Exp(-metres / 9.0), 0.02, 0.60);
    }

    /// <summary>
    /// Picks the player most likely to be on the ball at (<paramref name="p"/>, <paramref name="y"/>) in the side's
    /// frame: whoever's position in the current shape is nearest, with a smooth fall-off, so a false nine drops into
    /// midfield and a wing-back covers the flank. Keepers never turn up in open play beyond their own box area. A
    /// preferred listed position gets a strong boost. One random draw per call, as before shapes existed.
    /// </summary>
    private string PickPlayer(Side side, double p, PlayerPosition? prefer = null, string? exclude = null, double y = 50)
    {
        TeamInfo team = side == Side.Home ? _match.Home : _match.Away;
        IReadOnlyList<RoleSlot> slots = Shape(side).Slots;
        double total = 0;
        var weights = new double[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            RoleSlot slot = slots[i];
            PlayerInfo player = team.Players.First(pl => pl.PlayerId == slot.PlayerId);
            double dx = (p - slot.X) / SpreadX, dy = (y - slot.Y) / SpreadY;
            double w = Math.Exp(-0.5 * (dx * dx + dy * dy)) + 0.002;
            if (slot.Role == PlayerRole.Goalkeeper && p > 25) w = 0;
            if (prefer is not null && player.Position == prefer) w *= 8;
            if (slot.PlayerId == exclude) w = 0;
            weights[i] = w;
            total += w;
        }

        double roll = _rng.NextDouble() * total;
        for (int i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll <= 0 && weights[i] > 0) return slots[i].PlayerId;
        }
        return slots.Last(sl => sl.PlayerId != exclude).PlayerId;
    }
}
