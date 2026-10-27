using MatchLens.Contracts;

namespace MatchLens.Forge;

/// <summary>A designated set-piece taker and where they tend to put the ball.</summary>
/// <param name="Zones">Probability of each zone (see <see cref="SetPieceZones"/>); sums to 1.</param>
public sealed record TakerProfile(string PlayerId, SetPiece Kind, IReadOnlyDictionary<string, double> Zones);

/// <summary>
/// Who takes each club's set pieces, and their habits. The same habits drive both the generated matches and the
/// generated season history, so a taker's past really does say something about the next kick, and the predictions
/// have something honest to learn from.
/// </summary>
public static class SetPieceTakers
{
    public static IReadOnlyList<TakerProfile> All { get; } =
    [
        // Harbor City
        new("har-10", SetPiece.Corner, Z((SetPieceZones.NearPost, 0.50), (SetPieceZones.Central, 0.25), (SetPieceZones.FarPost, 0.15), (SetPieceZones.Short, 0.10))),
        new("har-10", SetPiece.FreeKick, Z((SetPieceZones.Left, 0.20), (SetPieceZones.Centre, 0.20), (SetPieceZones.Right, 0.60))),
        new("har-09", SetPiece.Penalty, Z((SetPieceZones.Left, 0.60), (SetPieceZones.Centre, 0.10), (SetPieceZones.Right, 0.30))),
        // Northvale
        new("nor-10", SetPiece.Corner, Z((SetPieceZones.NearPost, 0.15), (SetPieceZones.Central, 0.30), (SetPieceZones.FarPost, 0.45), (SetPieceZones.Short, 0.10))),
        new("nor-07", SetPiece.FreeKick, Z((SetPieceZones.Left, 0.55), (SetPieceZones.Centre, 0.15), (SetPieceZones.Right, 0.30))),
        new("nor-09", SetPiece.Penalty, Z((SetPieceZones.Left, 0.30), (SetPieceZones.Centre, 0.15), (SetPieceZones.Right, 0.55))),
    ];

    /// <summary>The club's taker for this kind of set piece.</summary>
    public static TakerProfile For(TeamInfo team, SetPiece kind) =>
        All.First(t => t.Kind == kind && team.Players.Any(p => p.PlayerId == t.PlayerId));

    /// <summary>Draws a zone from a taker's habits.</summary>
    public static string Draw(TakerProfile taker, Random rng)
    {
        double roll = rng.NextDouble();
        foreach (KeyValuePair<string, double> zone in taker.Zones)
        {
            roll -= zone.Value;
            if (roll <= 0) return zone.Key;
        }
        return taker.Zones.Keys.Last();
    }

    /// <summary>
    /// Where the ball ends up for a zone, in the taking team's 0..100 frame. Corners are taken from
    /// (99.5, 0.5) or (99.5, 99.5); <see cref="SetPieceZones.Corner"/> and <see cref="SetPieceZones.Shot"/> classify the
    /// result back into the same zone.
    /// </summary>
    public static PitchPoint Target(SetPiece kind, string zone, PitchPoint from, Random rng)
    {
        double jitter = (rng.NextDouble() - 0.5) * 2;   // -1..1
        if (kind == SetPiece.Corner)
        {
            double toward = from.Y < 50 ? -1 : 1;   // the near post is on the taker's side
            return zone switch
            {
                SetPieceZones.Short => new PitchPoint(91 + jitter, from.Y < 50 ? 9 : 91),
                SetPieceZones.NearPost => new PitchPoint(95 + jitter, 50 + toward * (7 + jitter)),
                SetPieceZones.FarPost => new PitchPoint(94 + jitter, 50 - toward * (8 + jitter)),
                _ => new PitchPoint(91 + jitter, 50 + jitter),
            };
        }
        return zone switch
        {
            SetPieceZones.Left => new PitchPoint(100, 46.3 + jitter * 0.8),
            SetPieceZones.Right => new PitchPoint(100, 53.7 + jitter * 0.8),
            _ => new PitchPoint(100, 50 + jitter * 0.8),
        };
    }

    private static IReadOnlyDictionary<string, double> Z(params (string Zone, double P)[] zones) =>
        zones.ToDictionary(z => z.Zone, z => z.P);
}

/// <summary>
/// A generated season behind every match: the players' numbers over the earlier rounds and every set piece their
/// takers hit, drawn from the same habits the match uses. Deterministic for a scenario and seed. Clearly synthetic,
/// like everything else in Match Forge.
/// </summary>
public static class SeasonArchive
{
    public const int Rounds = 24;

    public static MatchHistory Generate(Scenario scenario, int seed)
    {
        var rng = new Random(seed * 7919 + 11);
        var records = new List<SetPieceRecord>();
        TeamInfo[] teams = [Rosters.HarborCity, Rosters.Northvale];

        foreach (TakerProfile taker in SetPieceTakers.All)
        {
            PlayerInfo player = teams.SelectMany(t => t.Players).First(p => p.PlayerId == taker.PlayerId);
            int n = taker.Kind switch
            {
                SetPiece.Corner => rng.Next(34, 49),
                SetPiece.FreeKick => rng.Next(8, 15),
                _ => rng.Next(5, 10),
            };
            for (int i = 0; i < n; i++)
            {
                string zone = SetPieceTakers.Draw(taker, rng);
                string outcome = taker.Kind switch
                {
                    SetPiece.Penalty => rng.NextDouble() < 0.78 ? "Goal" : rng.NextDouble() < 0.7 ? "Saved" : "Off target",
                    SetPiece.FreeKick => rng.NextDouble() < 0.1 ? "Goal" : rng.NextDouble() < 0.5 ? "Saved" : "Off target",
                    _ => "Delivered",
                };
                records.Add(new SetPieceRecord(taker.PlayerId, player.Name, taker.Kind, zone, outcome,
                    $"Round {rng.Next(1, Rounds + 1):D2}"));
            }
        }

        var players = new List<PlayerSeason>();
        foreach (TeamInfo team in teams)
        {
            foreach (PlayerInfo p in team.Players)
            {
                int matches = p.Position == PlayerPosition.Goalkeeper ? Rounds - rng.Next(0, 2) : rng.Next(15, Rounds + 1);
                (double shotsPer, double goalShare, double passesPer, double accuracy) = p.Position switch
                {
                    PlayerPosition.Forward => (2.6, 0.17, 22, 0.76),
                    PlayerPosition.Midfielder => (1.1, 0.09, 52, 0.86),
                    PlayerPosition.Defender => (0.4, 0.06, 48, 0.88),
                    _ => (0.0, 0.0, 30, 0.70),
                };
                int shots = (int)Math.Round(matches * shotsPer * (0.8 + 0.4 * rng.NextDouble()));
                double xg = Math.Round(shots * (p.Position == PlayerPosition.Forward ? 0.13 : 0.08) * (0.85 + 0.3 * rng.NextDouble()), 2);
                int pensTaken = records.Count(r => r.PlayerId == p.PlayerId && r.Kind == SetPiece.Penalty);
                int pensScored = records.Count(r => r.PlayerId == p.PlayerId && r.Kind == SetPiece.Penalty && r.Outcome == "Goal");
                int goals = (int)Math.Round(shots * goalShare * (0.7 + 0.6 * rng.NextDouble())) + pensScored;
                int passes = (int)Math.Round(matches * passesPer * (0.85 + 0.3 * rng.NextDouble()));
                int completed = (int)Math.Round(passes * Math.Min(0.95, accuracy + (rng.NextDouble() - 0.5) * 0.06));
                int assists = p.Position == PlayerPosition.Goalkeeper ? 0 : (int)Math.Round(rng.NextDouble() * (p.Position == PlayerPosition.Midfielder ? 7 : 4));
                players.Add(new PlayerSeason(p.PlayerId, p.Name, team.Name, matches, goals, assists, shots, xg,
                    passes, completed, pensTaken, pensScored));
            }
        }

        return new MatchHistory(
            $"Generated season: rounds 1-{Rounds} of the Second Ball IQ Synthetic League before this match (Match Forge, seed {seed})",
            Rounds, records, players);
    }
}
