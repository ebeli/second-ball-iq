# Live server and web front end

`src/MatchLens.Server` plays a Match Forge match on a clock and runs everything live; `web/` is the browser view.

```
replay loop ──events──► LiveMatch ──candidates──► queue ──► 2 pipeline workers ──► insights
     │                                                         │ (frozen copy of the match
     └──── SignalR: events, tick ◄──── steps, candidates ◄─────┘  as it stood at the moment)
```

- **Replay**: events are fed to the analytics engine at the chosen speed (1x to 30x). A start minute replays the
  earlier events at once, without agents.
- **Workers**: each candidate is processed against a private, frozen copy of the match, so the agents' tools never
  race the replay and cannot see the future. Two run at a time.
- **Live trace**: every workflow step is pushed as it finishes (`InsightWork.OnStep`), so the agent desk shows the
  triage gate, the analyst, each narrator and each grounding check arriving.
- **Editor queue**: insights a gate was unsure about wait for a person. Publish, reject, or edit and publish. An edit
  goes through the same Validator as the agents' drafts, so an editor cannot put an unmeasured number on air either.
- **Evaluation**: the scenario's ground truth is matched against the candidates as they are raised (same rule as
  `forge evaluate`), with time to air and publish rates.

## Fan view

`/fan` is the fan's companion, built for a phone next to the TV and laid out wider on a desktop (Fluent UI React
v9). A first-run setup picks level, explain depth, focus (whole match, my club, my player), how to follow (read,
listen, ask) and alone or in a watch party; settings are remembered on the device. A match stage is always on
screen: the stand of fans over the pitch in perspective; an LED ticker runs under the score and can be hidden. Beside it (under it on a
phone) a panel changes with the five sections on the bottom bar: Live (the pinned poll or test, the key moment with
"See how it happened" and "Ask why", earlier moments, the followed player), Room (the watch party: pinned vote,
tifos, one feed of moments, results and chat), Analysis (formations and heat maps on a flat pitch in the panel,
possession, tactical tests, set pieces), Players and Ask. Replays and answers' visuals play on the stage. Learn the Game opens as a side
panel. The studio links to it from its top bar.

## Voice

Every insight and recap can be read aloud. The server turns the text into speech with Azure AI Speech neural voices,
one per audience and language: a brisker British voice for the commentator persona, Finnish voices for Finnish
(`GET /api/speech/insight/{id}`, `GET /api/speech/recap/{id}`, MP3, cached per text). The fan view has a "Read
aloud" switch and a Listen button on recaps; the studio's "Voice" switch (in the on-air preview) reads each on-air graphic.

Speech uses the Foundry resource's key by default. Set `MATCHLENS_SPEECH_REGION` (e.g. `swedencentral`) to use the
regional Speech endpoint, or `MATCHLENS_SPEECH_ENDPOINT` for a full URL; without either, the resource's own
`cognitiveservices.azure.com` domain is tried. If synthesis fails, the browser reads the text with its own voice, so
the switch always works.

## Predictions and the commentator

Before a corner, a direct free kick or a penalty the server asks the Predictor (`src/MatchLens.Agents/Predictions.cs`)
where the ball goes. It gathers the taker's record from the generated season (`SeasonArchive`) and this match, writes
it as plain evidence and asks Decision-1 to choose between the zones; Decision-1's probabilities are the prediction.
If Decision-1 is unavailable, smoothed frequencies are used and the prediction says "history". The replay waits for
the answer (up to 4 s) and then holds 2.5 s, so the call is on screen before the kick. The kick settles it.

"Who scores in the next 10 minutes" is predicted every 15 minutes and whenever a pressing shift or possession
illusion candidate is raised, from the last 15 minutes' xG; it settles at the first goal or when the window closes.

The Commentator (`src/MatchLens.Agents/Commentary.cs`) turns events into calls: kick-off, goals, big chances
(xG 0.15 or more), the prediction before a set piece and its result, and stat flashes (a brace, a player's third and
fifth shot or high regain). Calls are templates over the event and the engine's counts, so they are instant and
exact. They are pushed as `call` messages and spoken by `GET /api/speech/call/{id}?lang=` with en-US-GuyNeural in the
`excited` style (`shouting` for goals) or fi-FI-HarriNeural with faster, higher prosody.

## Tactical tests

When a pressing shift, possession illusion or tactical shift goes to air (a worker publishes it Live, or the editor
approves it), `MatchSession.OpenTestAsync` locks a test (`src/MatchLens.Agents/TacticalTests.cs`): the criterion,
the rule and the deadline are fixed at that match second, one open test per team and kind. Decision-1 is then asked
for an estimate from the match frozen at the lock (3 s, or no estimate). The replay loop measures every open test
after each batch of events (`MeasureTests`), settles it early when a count is reached or can no longer stay within
its limit, at the deadline otherwise, and at full time if the match ends first. Each change goes out as a `test`
message and is in the snapshot; `get_tactical_tests` exposes them over MCP. Watch parties turn each locked test into
a "Will it hold?" poll; the room's votes are kept apart from the result and scored when the match settles it.

## Judge mode

`POST /api/judge` (the `/judge` page calls it) starts the demo match at 38′ at 8×, or joins the demo match already
playing if it is before 80′, so judges arriving together do not restart each other. The page then opens
`/fan?judge=1`: no setup, a short guide, a demo watch party for the judge, and a link to the studio.

`/judge?take=1`, `2` or `3` are the scripted takes for recording the demo video (docs/DEMO.md): the match restarts
from 38′ and the page presses its own buttons at set match times (`web/src/take/`). `scripts/take.ps1` opens one in
a clean full-screen Chrome.

## Public site: spending caps and the operator key

A hosted site is open to anyone, so the server limits what the models can spend. Three layers, all optional to
tune:

- **Model budget** (`ModelBudget`). The server counts every chat-model, Decision-1 and speech call per hour and per day
  (UTC). When the chat or Decision-1 cap is reached, the models pause until the window turns: the match goes on, new
  moments take the offline path (templates from measured facts, every number and check still applied), the watch
  parties screen on the word list, and the studio badge shows **Models paused**. A spent speech cap hands the voice
  to the browser's own. One judge-mode run (38′ to full time) made about 190 chat calls and 170 Decision-1 calls.

  | Variable | Default |
  | --- | --- |
  | `MATCHLENS_MAX_CHAT_PER_HOUR` / `_PER_DAY` | 800 / 4000 |
  | `MATCHLENS_MAX_DECISION_PER_HOUR` / `_PER_DAY` | 1000 / 5000 |
  | `MATCHLENS_MAX_SPEECH_PER_HOUR` / `_PER_DAY` | 300 / 1500 (cached audio is free) |

  `0` turns a cap off. `/api/health` reports what has been used and when paused models resume. The counts live in
  memory, so they start again when the container restarts; a tokens-per-minute limit on the Foundry deployments and
  a budget alert on the subscription are the outer limits.
- **Per-visitor limits.** Ask, the what-if, the coach's corner: 12 calls a minute per address
  (`MATCHLENS_MODEL_CALLS_PER_IP_MINUTE`); voices: 60 (`MATCHLENS_VOICE_CALLS_PER_IP_MINUTE`). Over the limit the
  answer is 429. Requests from the machine itself (a local run) are not limited. Ask keeps its own limits too (one
  question every 5 s per viewer, 400 per match), and the watch parties theirs (one message every 3 s).
- **Operator key.** With `MATCHLENS_ADMIN_KEY` set, starting, stopping, pausing and speeding a match, the fault drill
  and the editor need it: open the studio once as `/?key=<key>` (it is kept for the tab and removed from the address
  bar) or send the `X-MatchLens-Admin-Key` header. Without it those calls answer 401 and the studio shows
  "Operator key needed". Judge mode (`/judge`) and everything a fan does stay open. Without the variable everything
  is open, as in a local run.

`infra/deploy.ps1` passes any of these variables that are set, and stores the operator key as a Container Apps secret.

## API

| Method | Path | What it does |
| --- | --- | --- |
| GET | `/api/health` | Live or offline, which models, whether an operator key is set, and the model budget used |
| GET | `/api/operator` | 200 when the operator key sent is right (or none is set), 401 otherwise |
| GET | `/api/scenarios` | The Match Forge scenarios |
| GET | `/api/session` | Snapshot of the current match |
| POST | `/api/session` | Start a match: `{scenarioId, seed?, speed, startMinute, offline, includeMilestones}` |
| POST | `/api/judge?restart=true` | Judge mode: the demo match from 38′ at 8×, or join it if it is already playing; `restart=true` always starts it again (the scripted recording takes; needs the operator key when one is set) |
| POST | `/api/session/pause`, `/resume`, `/stop`, `/speed` | Match controls; speed takes `{speed}` |
| GET | `/api/insights?status=pending` | Insights, optionally by status |
| GET | `/api/speech/insight/{id}`, `/api/speech/recap/{id}` | The text read aloud (MP3), or 503 when Speech is unavailable |
| GET | `/api/speech/call/{id}?lang=en\|fi` | A commentator call in the excited voice |
| GET | `/api/players/{id}/season` | A player's generated season so far |
| POST | `/api/editor/{id}/approve`, `/reject` | Editor decisions |
| POST | `/api/coach/{teamId}` | Coach's corner now for one team: two plans, Decision-1's pick, two tests; 409 while the team's last plans are still open |
| POST | `/api/whatif` | What-if sandbox on the frame now: `{teamId, change: pushUp\|dropDeep\|widen\|narrow\|pressTheBall, metres?}`; control before and after and Decision-1's "would help"; 409 before tracking is ready |
| PUT | `/api/editor/{id}` | Edit and publish: `{headline, summary}`; 422 with the Validator's problems if it fails |

Starting a match, the match controls, the fault drill (`/api/session/plant`) and the editor need the operator key
when one is set (above).

The log also goes to a file, `logs/server-<date-time>.log` at the repository root (or `MATCHLENS_LOG_DIR`); its path is the first line the server logs. In live mode every chat-model call (time, tokens,
how it finished) and every Decision-1 call (questions, answers with their probabilities, time) is logged, without
message contents or keys. `scripts/capture` records the screens of a run alongside it (see the README).

Hub `/hubs/match` sends `session`, `tick`, `events`, `candidate`, `step`, `insight`, `groundTruth`, `recap`, `test` and `coach` (a coach's corner decision, again when its verdict lands); the browser
calls `Snapshot()` on connect and reconnect. Watch-party members also call `CreateParty`, `JoinParty`, `LeaveParty`, `Vote`, `Pick`,
`AskRoom`, `Upvote`, `Investigate`, `Say` (a chat message: 1 to 160 characters, one every 3 s, word list and a Decision-1 respect check with a
3 s timeout; returns the reason when refused), `React`, `Share`, `StartTifo` and `HoldTifo`; the room's state
(`party`) carries members, polls, picks, questions, the tifo and the last 60 messages.

## MCP server

The engine's queries are also an MCP server (Streamable HTTP) at `http://localhost:5080/mcp`. They are the same
read-only queries the Tactical Analyst calls in-process, so any MCP client can ask the live match what our agents ask.
Each call reads a private copy of the match as it stands, so it never races the replay.

| Tool | Returns |
| --- | --- |
| `get_match_state` | Clock, score, teams, event and candidate counts |
| `get_window_stats` | One team's numbers between two minutes, with event ids |
| `describe_window` | The neutral prose the triage gate reads |
| `get_events` | Raw events by id, to check any claim |
| `get_player_stats` | One player's running numbers |
| `get_candidates` | Flagged moments with facts and what the agents did |
| `get_insights` | Insights by status and persona |
| `get_recaps` | Half-time and full-time recaps by persona |
| `get_predictions` | Set-piece and next-goal predictions and how they turned out |
| `get_tactical_tests` | Tactical tests: locked criterion and deadline, Decision-1 estimate, progress, result and deciding events |
| `get_player_season` | A player's generated season so far |
| `get_team_shape` | A team's formation, positions, roles and style between two minutes |
| `get_space` | Final-third control per team (pitch control from tracking) and the players who most often missed a better pass |
| `get_coach_corner` | The coach's corner: each decision's brief, both proposals, Decision-1's pick and probabilities, the two tests and the verdict |
| `get_running` | Players' running from tracking: distance, top and current speed, sprints, high-intensity distance |
| `get_auto_events` | Passes, shots and ball wins found from tracking positions alone, scored against the event feed, with the latest detections |
| `ask_matchlens` | Ask Second Ball IQ a question; the Match Analyst answers with checked, cited facts |

Try it with the MCP Inspector (`npx @modelcontextprotocol/inspector`, transport Streamable HTTP, URL above) or add it
to VS Code as an HTTP MCP server, while a match is playing.

## Working on the UI

The built UI is checked in under `src/MatchLens.Server/wwwroot`. To change it (Node 20 or newer):

```bash
cd web
npm install
npm run dev      # http://localhost:5173, forwards /api and /hubs to the server on :5080
npm run build    # type-checks and writes the bundle into src/MatchLens.Server/wwwroot
npx vite -c vite.preview.config.ts   # http://localhost:5199: the real UI on a recorded demo match, no server needed
```

The preview swaps the SignalR client for a stand-in (`src/dev/fakeSignalr.ts`) that answers from
`src/dev/fixture.json`, the demo match at 60′ generated by the engine. `/fan?judge=1&script=goal` plays a goal and
`&script=settle` settles the open test, to check the animations.

## Azure

`infra/deploy.ps1` deploys to Azure Container Apps. It builds the image in Azure Container Registry, so local Docker
is not needed, and runs at most one replica (0.5 vCPU, 1 GiB): the match, the editor queue and the SignalR
connections live in memory. The minimum is zero replicas, so the app sleeps when unused and costs nothing for compute;
set `--min-replicas 1` before a live demo to avoid the few-second wake-up. The Foundry key is stored as a Container Apps secret. Rerun the script after code changes to rebuild and
update in place.

```powershell
az login
./infra/deploy.ps1 -Location swedencentral     # with the MATCHLENS_* variables set, from the repo root
```

The deployed app is public, and every live match spends Foundry tokens (roughly 0.25 USD per full match with
gpt-5.4-mini). The model budget and the operator key above bound what visitors can spend; also set a budget with
alerts on the subscription, and delete the resource group
(`az group delete -n rg-matchlens`) after judging.

## Container

```bash
docker build -t matchlens .
docker run -p 8080:8080 -e MATCHLENS_DECISION_ENDPOINT=... -e MATCHLENS_DECISION_KEY=... \
  -e MATCHLENS_DECISION_DEPLOYMENT=Decision-1 -e MATCHLENS_CHAT_DEPLOYMENT=gpt-5.4-mini matchlens
```
