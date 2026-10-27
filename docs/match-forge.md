# Match Forge

Match Forge generates synthetic, football-realistic matches as an event stream, and writes alongside each match
the ground truth Second Ball IQ should find in it. The same files drive the live demo, the automated evaluation and
the threshold tuning.

## How a match is generated

A possession-chain simulator. On every step:

1. The defending team may **press**, more often when the attacker is deep in its own half. A press sometimes wins
   the ball outright (a tackle).
2. Well inside the final third, the team on the ball may **shoot**. Expected goals come from distance to goal
   (0.02 to 0.60), reduced under pressure. The keeper collects afterwards.
3. Otherwise it **passes**: short or long, forward by a style-dependent amount. Completion depends on the
   team's accuracy, the zone, pressure and pass length. A failed pass is intercepted.

Each team's behaviour comes from a `TeamStyle` (accuracy, progression, directness, shot propensity, press, tackle,
final-third entry). A scenario changes styles for **phases** of the match and injects **scripted moments**:
event chains at fixed times, such as a high turnover followed by a shot. That way the demo's key moments always
happen, whatever the seed.

Organic shots never become goals, so the scoreline is exactly what the script says.

Players are picked by zone: defenders deep, midfielders in the middle, forwards high. The generator is
deterministic: the same scenario and seed always produce the same events.

## Realism

Averages per team over 40 balanced matches in the Python prototype the C# port mirrors:

| Measure | Generated | Rough typical range |
| --- | --- | --- |
| Passes | 490 | 300 to 700 |
| Pass completion | 81% | 70% to 90% |
| Shots | 12.8 | 5 to 22 |
| xG | 1.29 | 0.3 to 3.0 |
| Possessions | 96 | 80 to 130 |

The ranges are rules of thumb used as a sanity check, not sourced league averages. `forge check` recomputes the
generated column from the C# engine.

## Scenarios

| Id | What happens | Ground truth rule |
| --- | --- | --- |
| `balanced` | Even match, nothing scripted | None; baseline for false positives |
| `possession-illusion` | Harbor City dominate the ball 15'-40' against a deep block | Home has ≥ 65% of completed passes in 20'-35', ≤ 1 shot, ≤ 0.15 xG |
| `pressing-shift` | Northvale press high after half-time; four scripted high turnovers, two followed by shots | Away wins the ball in the opponent's defensive third ≥ 5 times in 46'-58', and at least twice as often as in the 12 minutes before |
| `demo-match` | The demo story: sterile possession, a home goal at 41', the press, and a turnover goal at 61' for 1-1 | All three of the above |

Scenarios rely partly on random play, so a seed can miss a rule. In the prototype that happened on 7% of seeds
for possession illusion and 13% for the pressing shift. Each expectation records whether it was **verified** and
the measured values. `generate --find-seed` picks the first seed that satisfies every rule, and `check` reports the
rate across many seeds.

In balanced matches, about 4% of 12-minute windows meet the pressing-shift rule by chance. Real football has that
noise too, and the detectors need to cope with it.

## Tracking

`TrackingSynth` turns a generated match into tracking: the 22 players on the pitch and the ball, ten frames a second,
in metres (105 × 68, home attacking towards x = 105), with 12 cm of measurement noise (`Frame(t, observed: false)`
gives the noise-free truth). Each event gets a time on a continuous clock, late enough for the previous pass to have
travelled at its reported speed. Players stand in their shape spell's formation slot, shifted with the ball and with
possession, wander a little, and are pulled to the spot of each of their touches; a speed limiter keeps everyone
under a personal top speed (31 to 35 km/h, keepers 27). The ball rolls with a carrier, flies along passes and
shots (lofted for long balls), goes out for throw-ins, and shots carry on past the goal line or stop at the keeper
or the blocker. An interceptor starts to read a pass only half a second before it is played, so what is measured
at the moment of a pass (its lane) does not already contain its outcome. Deterministic like the events; about 2.5 s
per match.

## Files

`forge generate <scenario>` writes `data/<matchId>/`:

- `match.json`: teams, players, scenario and seed.
- `events.jsonl`: one `MatchEvent` per line, in order. Coordinates are in the acting team's frame (0..100 from its
  own goal line to the opponent's).
- `ground-truth.json`: expectations with rule, verified flag, measured values and evidence event ids, plus the
  ids of every scripted moment's events.
