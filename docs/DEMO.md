# Demo walkthrough

Two things in one page: how to record the **two-minute video** (what to record, in which order, the cut and the
voice-over) and a **ten-minute walkthrough** for a judge who opens the app. Both use judge mode: the demo match from 38′ at 8×, the same match every time, so the
moments below arrive at the same match minutes in every run.

## What happens in the demo match, and when

At 8×, one match minute takes 7.5 seconds; 38′ to full time takes about 6½ minutes.

| Match time | What arrives | Where to look |
| --- | --- | --- |
| 41:00 | **Possession isn't the story**: Harbor City have 79% of the ball and no shots. A tactical test locks (55% of the ball but no more than 0.30 xG by 51:15) | Studio On air, fan Live, overlay lower third |
| 41:35 | **Goal, Ravi Sharma**, 1-0 Harbor City: commentator call, goal splash, 3D broadcast cut. The goal breaks the possession test (not met at 46:32) | Everywhere |
| ~43:00 | **Coach's corner** (press "Ask the coaches now" for both teams): two coach agents, Decision-1's pick with a probability for each, two tests lock | Studio Coach's corner, overlay |
| 45:00 | Half-time recap, written by the Recap Writer for every persona | Studio On air, fan Live |
| 49:44 | Northvale turn a high regain into a chance | |
| 53:00 | **Northvale's press is biting**: test locks (2 shots straight after high regains by 63:00), met at **59:53** | Tactical tests, Room poll |
| 57:00 | **Northvale switch to a 3-4-1-2** after a substitution: test locks, confirmed at 72:01 | |
| 60:00 | Coach's corner runs on its own for both teams; Harbor City sit deeper, Northvale go direct | |
| 61:21 | **Goal, Arlo Quinn**, 1-1, seven seconds after a high regain | |
| 70–77 | Better passes missed (Quinn, Laine), player roles (Varga second striker, Laine shadow striker) | Analysis › Space and threat |
| 80:14 | Penalty: Second Ball IQ predicts the side before the kick | Predictions, Room |
| 90:00 | Full-time recap | |

## The two-minute video

### The short version

- **One screen at a time**, recorded full screen at **1920 × 1080**. No split screens.
- **Three separate recordings ("takes")**, each one a fresh run of the demo match, each showing one thing: the fan
  app, the studio, and (optional) the broadcast graphics on top of the match picture. Each take is scripted and
  plays itself (`/judge?take=1`, `2` or `3`). The match is the same every run, so the moments arrive at the same
  minutes and you can cut freely between takes.
- **Fan app on desktop, not phone size.** On a 1920 × 1080 video the desktop fan view (3D stadium, cards on the
  right) looks far better than a narrow phone column. The phone view gets at most one three-second shot, or none.
- **Your own voice-over**, recorded after the edit, over the app's own sound turned low (the commentator's goal call
  can be turned up for a moment). The script is below; read it at a calm pace and it fits.
- **Tools:** Xbox Game Bar (`Win + G`, then record) or OBS Studio to record; Clipchamp (free in Windows 11) to cut,
  add the voice-over and export 1080p.

### What the three screens are

| Screen | Address | What it is for | Use it in the video for |
| --- | --- | --- | --- |
| **Fan app** | `http://localhost:5080/judge` (starts the demo match and opens `/fan?judge=1`) | What a supporter sees: 3D match, key moments, tests, the Room, Analysis, Ask | Most of the video |
| **Studio** | `http://localhost:5080/` | What a broadcaster or editor sees: the agents at work, the checks, the editor queue, the tests, the coach's corner | The "how it works" part |
| **Broadcast overlay** | `http://localhost:5080/overlay` | Only the graphics (score bug, lower third, goal splash, test and coach cards), on a transparent background, so a TV production can lay them over its own match picture. On its own it looks empty: green or transparent with pop-ups. | One shot, **laid over the 3D match picture** (Take 3 does this in the browser), so it looks like television. Skip it if there is no time. |

All three show the same running match: the server plays one match, and every screen follows it.

### Scripted takes: the app records itself

Each take is scripted. Open the take's address, start the recorder, and the page does the rest: it restarts the demo
match from 38′, and at the right match minutes it moves a pointer to the right button and presses it (Personalise,
the player cam, Room, Analysis, Ask; in the studio the fault drill, the preview persona, the agent trace, the
coaches, the what-if). You only watch. The match is the same every run, so every take hits the same moments.

| Take | Address | Runs for | What it records |
| --- | --- | --- | --- |
| 1 | `http://localhost:5080/judge?take=1` | 38′ to full time, about 7 minutes | The fan app |
| 2 | `http://localhost:5080/judge?take=2` | 38′ to about 61′, 3 minutes | The studio |
| 3 | `http://localhost:5080/judge?take=3` | 38′ to about 47′, 75 seconds | Broadcast graphics over the full-window match picture (no OBS needed) |

Each take restarts the match itself, so the server does not need restarting between takes.

**The easy way: `scripts/take.ps1`.** It opens a clean Chrome window (a fresh profile with no extensions or saved
settings) full screen at 1920 × 1080 CSS pixels whatever the Windows display scaling, with sound allowed to play
on its own (the commentator), straight on the take:

```powershell
# Server running in another terminal (Live mode). Start the screen recorder first, then:
./scripts/take.ps1 -Take 1        # or 2, or 3; Alt + F4 closes the window when the take is done
./scripts/take.ps1 -Take 2 -Windowed   # a 1920 × 1080 window instead of full screen (for a larger monitor)
```

Without the script, open the address in Chrome, press `F11`, and click once anywhere on the page in the first
second (browsers only play sound after a click, so without it the take is silent).

Before the first take:

1. Start the server from a terminal with the `MATCHLENS_*` variables set (the first log line says **Live mode**),
   plus `MATCHLENS_SPEECH_KEY` and `MATCHLENS_SPEECH_REGION` for the voices. After any code change, `npm run build`
   in `web` first.
2. Notifications off (Windows Focus), and no mouse movement over the window during a take: the real pointer is
   hidden, and the one you see is the script's.
3. If `MATCHLENS_ADMIN_KEY` is set on the server, add `&key=<key>` to the address (restarting the match needs it).

At 8×, one match minute is 7.5 seconds. The "take time" column below is roughly how long after the page opens
each step happens (moments need about 5 seconds to pass the agents and the checks).

### Take 1: the fan app (about 7 minutes)

| Take time | Match | What the take does |
| --- | --- | --- |
| 0:00 | 38:00 | Broadcast camera, commentator on. The 3D stadium runs: players with name and speed tags. |
| ~0:12 | 39:30 | Closes the judge-mode note. |
| ~0:25 | 41:00 | The first key moment card arrives on the right ("Harbor City have the ball, not the threat") with its figures and its tactical test. |
| ~0:30 | 41:35 | **Goal.** The camera cuts to the goal, the crowd and the commentator react. |
| ~0:32 | 42:10 | `…` › **Personalise** › **New to football** › Save: the same cards in plain words, with Learn the Game. |
| ~0:43 | 43:40 | Personalise › **Suomi** › Save: everything in Finnish (the half-time recap at 45:00 too). |
| ~0:55 | 45:20 | Personalise › **English**, **I live for tactics**, **My player** › **Arlo Quinn** › Save. |
| ~1:08 | 47:00 | Quinn's card on Live (distance, top speed, speed now, sprints) › **◎ Watch with the player cam**: the camera follows him. |
| ~1:20 | 48:40 | Camera back to **Auto**. |
| ~1:24 | 49:10 | **Room**: the room's score against Second Ball IQ, the energy meter, the members, the chat. |
| ~1:54 | 53:10 | The pressing test arrives as a poll ("Will it hold?"); the take votes **It will hold**. |
| ~2:44 | 59:53 | The test settles: stamped **HELD**, the score moves. |
| ~2:54 | 61:21 | **Equaliser**: the GOAL! burst over the room in Northvale's colour. |
| ~3:02 | 62:10 | **Analysis › Coach's corner**: the coaches' plans, Decision-1's pick and probabilities. |
| ~3:14 | 63:40 | **Space and threat**: pitch control and the better passes missed. |
| ~3:23 | 65:00 | **Ask**: types "How did that goal happen?" and asks; the answer with its evidence and "Checked by Decision-1". |
| ~3:49 | 68:30 | **Live analytics, side by side**: Analysis › **Formations** (both teams, last 15 minutes) while the 3D stage switches to the **Tactical** camera with **Shape** on (each team's outfield hull with width and depth). Both update as play goes on. |
| ~4:00 | 70:00 | **Heat maps** for HAR, with 3D **Space** (pitch control on the grass); at 70:50 the heat map for NOR to compare. |
| ~4:21 | 71:40 | **Tests and calls**: Northvale's 3-4-1-2 test is confirmed at 72:01. 3D **Runs** (arrows ahead of high-intensity runs). |
| ~4:31 | 73:10 | **Running, passes, speed**. |
| ~4:41 | 74:30 | Layers off, camera back to Auto, **Live** to full time (the penalty prediction at 80:14, the full-time recap). Stop recording after 90:00. |

### Take 2: the studio (about 3 minutes)

| Take time | Match | What the take does |
| --- | --- | --- |
| 0:01 | 38:10 | **Evaluation** › Fault drill › **Plant a wrong number**: the next moment will carry a wrong number. |
| ~0:20 | 40:45 | **On air**: the on-air preview fills at 41:00. |
| ~0:24 | 41:15 | The preview's viewer: **Analyst (English)**, then **Broadcast (Suomi)** at 41:30, then **Casual (English)**: the same moments written for each. |
| ~0:31 | 42:05 | **Agent trace** › the goal: Triage, Analyst, eight Narrators, Validator, Grounding, Publish, each with its result and time. |
| ~0:40 | 43:20 | **Evaluation**: the planted number was caught. |
| ~0:50 | 44:40 | **Coach's corner** › **Ask the coaches now** for both teams: two plans each, Decision-1's probabilities, the PICK badge, the tests locking. |
| ~1:05 | 46:40 | **What if?** › **Push up**, 8 m › **Try it on the frame now**: before and after pitch control and Decision-1's "would help". |
| ~1:13 | 47:40 | **Tactical tests**: the possession test **Not met** (the goal broke it); the pressing test locks at 53:00. |
| ~2:08 | 55:00 | **On air**. |
| ~2:13 | 55:40 | The live board switches to **Shape**: average positions, so Northvale's change to a back three at 57:00 shows on it. |
| ~2:31 | 58:10 | Board **Heat map**, Northvale. |
| ~2:43 | 59:40 | Board back to **Live**. |
| ~2:47 | 60:20 | **Coach's corner**: the first decisions with their verdicts. Stop recording at about 61′. |

### Take 3 (optional): broadcast graphics on the match picture

The page shows the 3D match full window on the clean feed (no controls), with the broadcast overlay (`/overlay`,
transparent) keyed over it, as a vision mixer would: score bug, lower thirds, the goal splash, the tactical-test
card, and the coach's corner card (the take asks the coaches at 43:00). Record from the start to about 47′: the
lower third at 41:00, the goal splash at 41:35, the coach card at about 43:30.

The overlay on its own (`/overlay`) still works as a browser source in OBS or a vision mixer, as before.

### The cut (about 1:55)

| Video time | From | Match | Picture |
| --- | --- | --- | --- |
| 0:00–0:04 | Title card | — | Opening title (animated, made separately) |
| 0:04–0:11 | Take 1 | 38–41′ | The 3D stadium, players with speed tags |
| 0:11–0:23 | Take 1 | 41–44′ | The first key moment card, then the beginner and the Finnish versions of it |
| 0:23–0:30 | Take 3 (or Take 1) | 41:35 | The goal, with the broadcast graphics over the picture |
| 0:30–0:35 | Take 1 | ~47′ | Your player: the running numbers, then a few seconds of the player cam |
| 0:35–0:51 | Take 2 | 41–43′ | Agent trace of the goal, then the fault drill: the planted number caught |
| 0:51–1:06 | Take 2 | 44–47′ | Coach's corner: two plans, Decision-1's pick; then the what-if maps |
| 1:06–1:16 | Take 1 | 68–71′ | **Live analytics side by side**: Formations in the panel with the 3D Shape outlines on the tactical camera (68:30), then the heat maps with pitch control on the grass (70:00–71:00) |
| 1:16–1:32 | Take 1 | 53–61′ | The Room: the poll on the pressing test, **HELD** at 59:53, the score moving, the GOAL! burst at 61:21 |
| 1:32–1:44 | Take 1 | ~65′ | Ask: the answer with its evidence and the Decision-1 check |
| 1:44–1:54 | Title card | — | Closing card: "Second Ball IQ · The engine measures. The models explain." and the link |

Alternative for 1:06–1:16: Take 2 at 55:40–58:30, the studio's live board in Shape mode as Northvale switch to a
back three at 57:00, then the heat map.

Short animated captions between sections ("Every number measured", "Checked before it airs", "The match settles
it") work well over the cuts at 0:35, 0:51 and 1:16; keep each under two seconds.

### Voice-over script (your voice, about 270 words)

> **0:00** The scoreline never tells you why. Second Ball IQ watches a live match and explains it to every viewer, in
> seconds, with the evidence.
>
> **0:11** Here the engine spots that Harbor City have seventy-nine percent of the ball and no shots. Agents write it
> for each viewer: a beginner, a tactics fan, in English or Finnish. Every number comes from the engine.
>
> **0:23** Then the goal, against the run of play. The same moments drive broadcast graphics a TV production can lay
> over its own picture. Follow one player, and the camera follows him too.
>
> **0:35** Behind each moment is a Microsoft Agent Framework workflow: Decision-1 triage, an analyst agent with engine
> tools, eight narrators in parallel, a code validator and Decision-1 grounding. Plant a wrong number, and it is
> caught before it airs.
>
> **0:51** In the coach's corner, two coach agents argue for different plans, and Decision-1 picks one, with a
> probability for each. The what-if moves a team on the live tracking and asks whether it would help.
>
> **1:06** The analytics run live, side by side: formations and heat maps from the events, and the same shapes and
> pitch control drawn on the match from tracking.
>
> **1:16** Every read on air becomes a test with a deadline. Fans vote in the watch party, the room plays against
> the AI, and the match decides, in public, even when the AI was wrong.
>
> **1:32** Fans can ask anything: the answer cites the events and is checked first. The same tools are an MCP server
> for any other agent.
>
> **1:44** Second Ball IQ. The engine measures, the models explain. Built on Microsoft Foundry.

Notes:
- Keep it under two minutes; judges may stop watching at 2:00.
- Everything in the app is fictional (clubs, players, music-free), so any screen can be shown. Do not add music you
  do not have the rights to.
- If you prefer not to record your own voice, Clipchamp's text-to-speech reads the script with a Microsoft neural voice.

## Walkthrough for a judge (about ten minutes)

1. **Open `/judge`.** It starts the demo match at 38′ at 8× (or joins it if it is already playing) and opens the fan
   view in judge mode with a demo watch party. Close the judge-mode note when you have read it.
2. **Fan view, Live (desktop).** The 3D stage: the broadcast camera, players from tracking with name and speed tags,
   the big screens with the tests and polls. Camera buttons (keys `1` to `5`: auto, broadcast, tactical, drone,
   player cam) and `H`/`F` (clean feed, full screen) are under the pitch; `2D` switches to the board. At 41:00 the
   first key moment arrives with its figures and its test.
3. **Change who you are.** The `…` menu: level (beginner, casual, tactics fan), English or Finnish, favourite club or
   player. The same moments are rewritten for you; Learn the Game adds plain explanations. With a player chosen,
   Live pins their running numbers (distance, top speed, speed now, sprints) and **Watch with the player cam** puts
   the 3D camera behind them.
4. **Room.** The watch party: the room's score against Second Ball IQ (settled picks and tests), its energy meter and
   members; polls on the open test (the room votes, Second Ball IQ shows Decision-1's estimate, the match stamps it
   held or broken), set-piece predictions against the AI, chat, tifos, and a burst over the room on every goal.
   Simulated members are labelled as such.
5. **Analysis.** Formations and heat maps; Space and threat (pitch control, expected threat, better passes
   missed); **Coach's corner** with the what-if sandbox; Tests and calls.
6. **Ask.** Ask about the match. Each answer cites the events (shown on the pitch) and is checked by Decision-1.
7. **Studio (`/`).** On air with its on-air preview; **Agent trace** (every step, every gate, with timings);
   **Evaluation** (the fault drill, auto-eventing scored against the events, detectors against ground truth);
   Tactical tests; Coach's corner; Editor queue (anything a gate was unsure about waits here for a person).
8. **Broadcast overlay (`/overlay`).** Transparent by default for OBS or a vision mixer; `?mode=preview` shows it on
   a dark pitch. `window.__overlay` and `#render-metadata` say what is on screen and where it came from.
9. **MCP (`/mcp`).** 17 read-only tools over the live match, for example with the MCP Inspector
   (`npx @modelcontextprotocol/inspector`, transport Streamable HTTP).
