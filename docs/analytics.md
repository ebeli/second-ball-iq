# Analytics engine

`MatchLens.Analytics` turns the live event stream into candidates: things that might deserve airtime, each with
measured facts, cited events and the two texts the decision gates read. It is plain deterministic code; no model
is involved.

## How it runs

`LiveMatch.Ingest(event)` takes events one at a time, in order, as they would arrive live. It rejects duplicate
ids and events whose clock goes backwards instead of silently corrupting its state.

- **Event detectors** run on every event: turnover-to-chance and player milestones.
- **Window detectors** run once per 60 seconds of match time, never per event: pressing shift and possession
  illusion.

Every candidate carries:

| Field | For |
| --- | --- |
| `Facts` | Measured numbers. The Validator checks insight figures against these. |
| `EvidenceEventIds` | Every event the candidate rests on. |
| `WindowSummary` | Neutral prose about **both** teams, for the triage gate. It is written the same way whichever detector fired, so triage judges the match rather than echoing the detector. |
| `Evidence` | Kind-specific derived facts in plain language, for the grounding gate and the Narrator. |

The prose formats come straight from the Decision-1 spike. Triage scored 18/18 on prose but missed on raw numbers.
Grounding only worked once evidence stated derived facts (counts, which recovery led to which shot, what did not
happen) rather than a raw event list.

## Detectors

| Detector | Fires when | Cooldown |
| --- | --- | --- |
| Pressing shift | In the last 8 minutes a team won the ball in the opponent's defensive third at least 5 times, at least 3 more than in the 8 minutes before, and at least one of those recoveries led to a shot | 10 min per team |
| Possession illusion | In the last 10 minutes a team had at least 72% of completed passes (at least 45 of them), at most 1 shot and at most 0.15 xG | 15 min per team |
| Turnover to chance | A shot of at least 0.08 xG (or any goal) within 15 seconds of the same team's high recovery, with no opponent possession in between. A goal is reported on the goal event, so the score in the texts includes it | Per occurrence |
| Goal | Any goal that did not come straight from a high recovery (those are turnovers to chance). Reports the move behind it: passes and seconds from winning the ball to the shot, and who made the final pass | Per goal |
| Player milestone | The match's leading passer completes pass number 50, 75 or 100 | Per milestone |

The rules are deliberately different from Match Forge's ground-truth rules, so the evaluation compares two
independent implementations rather than the generator grading itself.

Tuning in the Python prototype (50 seeds per scenario):

| Detector | Recall | False alarms per balanced match |
| --- | --- | --- |
| Pressing shift | 47/50 | 0.44 |
| Possession illusion | 50/50 | 0.04 |

Shorter windows found the press slightly sooner but fired 2.4 times per balanced match, because ordinary play has
short bursts of high recoveries. Some remaining "false alarms" are probably genuine pressing spells in random play.
The triage gate decides what reaches the screen either way.

Player milestones are honest about scope: synthetic matches have no season or career history, so the evidence
says the counts cover this match only.

## Pass quality and ball speed

`BallMetrics` measures every pass and shot as it arrives: pass length in metres (105 × 68 m pitch), a difficulty
rating from 0 to 100, and the ball speed in km/h that the event carries. Difficulty adds up length (up to 40),
pressure on the passer (20), forward progress (up to 15), the target (15 into the box, 8 into the final third) and a
lofted long ball (10); hard from 60, medium from 35. It is deliberately not the generator's completion rule, so the
two check each other: in the demo match hard passes are completed 48% of the time and easy ones 89%.
`PlayerTally` keeps each player's pass metres, hard passes attempted and completed, longest completed pass and
hardest shot; `BallMetrics.Summarize` gives the same numbers for any set of events. The thresholds that tag a player
on screen (a shot from 110 km/h, a completed pass from 40 m) live there too.

## Running and auto-eventing from tracking

`Kinematics` takes tracking frames (`TrackingFrame`: every player and the ball in metres, 10 Hz) as they arrive and
keeps each player's distance, high-intensity distance (from 19.8 km/h), sprints (25.2 km/h for at least 1 s), current
and top speed. Speed compares the mean of the last five frames with the five before; distance adds up half-second
means, so 12 cm of position noise does not inflate it. It raises alerts at 33.5 km/h (once per two minutes per
player) and at 5 and 10 km.

`AutoEventer` reads the same frames and nothing else. A player controls the ball when it is within 1.3 m, low and
slow; a change of control between teammates is a pass, to an opponent a failed pass (interception) or, without a
flight, a ball win. A ball redirected at a player (more than 35° or much faster) is a one-touch pass or shot. A
shot is a strike from 69 km/h within 42 m whose line passes near the goal; it is a goal if it crosses the line
between the posts under the bar, saved if the keeper takes it, blocked if an outfield player stops it within 8 m.
Speed is the larger of the strike speed and the flight speed. `AutoEventScore` matches detections to the events (1 s,
2 s for ball wins) and reports recall, precision, outcome and receiver agreement and the length and speed errors.

## Spatial analytics

`src/MatchLens.Analytics/Spatial`: `ExpectedThreat` (a 16 × 12 xT grid fitted by value iteration on simulated
matches, `XtGrid` written by `forge xt`), `PitchControl` (time-to-intercept: 0.7 s reaction, 7.8 m/s, σ = 0.45 s; a
32 × 21 map), `PassLanes` (opponents who can cut a pass out, a marked target, difficulty with the lane, the chance a
pass arrives, and the better option by completion × xT) and `SpatialMonitor` (pitch control every match second, every
pass judged at the moment it was played, and the Territory and Better pass detectors, raised through
`LiveMatch.Raise` so their ids sit in one sequence with the event detectors'). `forge spatial --seeds 4` checks it all
on seeds the fits never saw: difficulty with and without the lane against completion (by band and AUC), completion by
the number of opponents who could cut a pass out, how often a better pass was missed, pitch control against shots and
xG, and how often each detector fires.

## Queries (future MCP tools)

`MatchQueries` exposes what the agents need, one method per future MCP tool: `get_match_state`, `get_window_stats`,
`describe_window`, `get_events`, `get_candidates` and `get_player_stats`. Each returns small typed records with event
ids, so every claim stays traceable. Wrapping them as MCP tools is a thin layer once the ModelContextProtocol package
can be restored.

## Evaluation

```bash
dotnet run --project tools/MatchLens.Forge.Cli -- evaluate --seeds 30
```

It replays every scenario through the engine and reports:

- recall per expectation, and when it was detected
- false alarms per match
- candidates per match by kind
- engine time per event

It also runs independent integrity checks on every candidate:

- cited events exist and sit inside the window
- recovery-to-shot chains are unbroken
- counts match a recount from the raw events

`autoevent --seeds 4` generates tracking for every scenario and seed, runs the auto-eventer on the positions alone
and scores it against the events, and compares running distances from the noisy frames with the noise-free truth.

`replay <scenario>` prints each candidate with its facts and both texts, to review the prose the gates will read.
