# Submission: Second Ball IQ

Text for the hackathon's project page, ready to paste. Fill in the three links at the end before submitting.

---

## Project name

**Second Ball IQ**

## Tagline

Live football explained to every fan, with evidence, and checked before it reaches the screen.

## Elevator pitch (short)

Second Ball IQ watches a live match and explains why moments matter: a press that starts to bite, possession that
goes nowhere, a coach switching to a back three. A deterministic engine measures everything; a team of agents on
Microsoft Agent Framework explains it for each viewer (beginner to analyst, English or Finnish, their club or
player); code and Microsoft-Decision-1 check every number and claim before it airs. Each read on air becomes a
tactical test with a deadline that the match itself settles, in public. It renders as a broadcast overlay, a studio,
a fan companion with watch parties, and an MCP server. Built on Microsoft Foundry, with synthetic data only.

## The problem

Live football is full of meaning most viewers miss, and broadcasters have the data but not the people to explain
every moment to every viewer. A generative model alone cannot be trusted on air: one invented number or a goal
described the wrong way and the credibility is gone. Second Ball IQ is built on one rule: **the engine measures, the
models only explain.** Every figure on screen was computed by code from the match events; the models choose words
and which measured facts to show, never a number.

## What it does: the five stages of the brief

| Stage | How |
| --- | --- |
| **Ingest** | Match Forge, our synthetic generator, streams football-realistic events (passes, shots, tackles, pressures, possession changes, substitutions, set pieces) and 10 Hz tracking for 22 players and the ball. A live-feed adapter would plug into the same `MatchEvent` contract. |
| **Interpret** | Detectors on the live stream (pressing shift, possession illusion, turnover to chance, goal, milestone, tactical shift, player role, territory, better pass); formations and roles; pass difficulty and ball speed; running and auto-eventing from tracking; pitch control and expected threat. |
| **Explain** | A per-moment multi-agent workflow (below) turns each candidate into a checked insight with its evidence, and locks a **tactical test**: a measurable criterion and a deadline the match settles. |
| **Render** | A studio with on-air preview and editor queue; a transparent **broadcast overlay** (`/overlay`) with SMPTE timecode and machine-readable render metadata; a 3D stage with a broadcast camera, DVR and telestrator; a commentator's voice (Azure AI Speech). |
| **Personalize** | Eight personas (beginner, casual, analyst, broadcast × English, Finnish); favourite club, or one player with their live running numbers and a player cam that follows them in 3D; Learn the Game explanations; Ask Second Ball IQ; and Watch Party rooms where the room plays against the AI on polls and predictions, with chat, an energy meter and goal bursts. |

## The agents (Best Multi-Agent System)

Per moment, a Microsoft Agent Framework workflow with shared state and typed handoffs:

1. **Triage** (Decision-1, choice question): is this the story right now? Folds a chance into a running press,
   drops what is not worth airtime, sends doubt to a person.
2. **Tactical Analyst** (agent with engine tools): why it matters; picks the facts and events to cite.
3. **Eight Narrators** in parallel (one per audience and language): headline and summary.
4. **Validator** (code): every number must be a measured fact, every cited event must belong to the moment, no
   invented gender for fictional players. A failing draft is replaced by a template built from facts.
5. **Grounding** (Decision-1, yes/no checks): do the claims follow from the evidence?
6. **Publish**: auto when every gate passed, editor when one was unsure, never a silent drop of a true moment.

Around it: a **Recap Writer** at half time and full time; a **Match Analyst** answering fans with 13 read-only tools
(checked by Decision-1); and the **Decision-1 coach's corner**, where two coach agents with opposite instincts
propose rival plans, Decision-1 picks one with a probability for each, both plans become tests, and the record shows
how often the pick held. A **what-if sandbox** moves a team on the live tracking, measures pitch control and asks
the same arbiter whether the change would help. Failures are planned for: every model call has a timeout and a
fallback, and a **fault drill** plants errors to show the checks catching them.

## Microsoft technology

- **Microsoft Foundry**: one resource for the chat model, Decision-1 and Speech.
- **Microsoft-Decision-1**: triage, grounding, set-piece and next-goal predictions, tactical-test estimates, the
  coach's corner, the what-if, Ask checks and Watch Party screening (ten uses, each with a timeout and fallback).
- **gpt-5.4-mini on Foundry**: analyst, narrators, recap writer, match analyst and coach agents, with structured output.
- **Microsoft Agent Framework**: `ChatClientAgent` agents with tools and the per-moment workflow (`WorkflowBuilder`).
- **Model Context Protocol C# SDK**: an MCP server with 17 tools over the live match.
- **Azure AI Speech**: commentator and narration voices in English and Finnish.
- **Azure Container Apps and Container Registry**: hosting (`infra/deploy.ps1`).
- **.NET 8, ASP.NET Core, SignalR; React with Fluent UI**; GitHub Actions CI.

## Results (measured, not claimed)

- **Live runs** (gpt-5.4-mini + Decision-1, demo match 38′ to full time at 8×): Decision-1 answered in about 0.15 s
  (slowest 0.6 s), the chat model in about 1.2 s; each moment through the whole workflow in 4 to 6 s; no errors or
  timeouts. In the last run 74% of insights went to air automatically and the rest waited for an editor; none were lost.
- **Fault drills**: 370 of 370 planted numbers caught, 0 of 2,960 clean drafts wrongly rejected.
- **Tactical tests**: 134 opened over 60 simulated matches, every one settled by the match in code.
- **Auto-eventing from tracking** (20 matches): passes 96.2% found and 98.4% real; every goal found.
- **Spatial analytics** on matches the fits never saw: hard passes by the lane rating completed 41% of the time
  against 87% for easy ones.
- **55 unit test cases** that call no model, run in CI on every push.

## Honest limits

Synthetic data only, by design. Decision-1's estimates and picks are model estimates, not calibrated
probabilities, and are labelled so everywhere; in the live runs its coach's-corner picks have not beaten the
alternative yet, and the app shows that record. Tracking is synthesised from the events, so auto-eventing is a
consistency test, not proof on real video. One match at a time, state in memory.

## Testing instructions for judges

- **Hosted:** open https://second-ball-iq.icywave-0b7b6c59.swedencentral.azurecontainerapps.io/judge. It starts the demo match at 38′ at 8× (or joins it) and opens the fan view
  in judge mode. The studio is at https://second-ball-iq.icywave-0b7b6c59.swedencentral.azurecontainerapps.io/, the overlay at https://second-ball-iq.icywave-0b7b6c59.swedencentral.azurecontainerapps.io/overlay?mode=preview.
  The app may take a few seconds to wake from idle. The hosted demo runs on the offline templates to keep it free of
  model costs (every number, check and test still works; the video shows the live models). Starting a new match from
  the studio needs an operator key; judge mode does not.
- **Locally:** .NET 8 SDK, then `dotnet run --project src/MatchLens.Server` and open `http://localhost:5080/judge`.
  Without model keys it runs offline on templates (every number and check still works; no model text). With keys,
  set `MATCHLENS_DECISION_ENDPOINT`, `MATCHLENS_DECISION_KEY`, `MATCHLENS_DECISION_DEPLOYMENT` and
  `MATCHLENS_CHAT_DEPLOYMENT`.
- A ten-minute guided walkthrough is in [docs/DEMO.md](DEMO.md).

## Links

- Demo video (under two minutes): https://youtu.be/nBRbEQaTKnc
- Code (public GitHub repository): https://github.com/ebeli/second-ball-iq
- Working project: https://second-ball-iq.icywave-0b7b6c59.swedencentral.azurecontainerapps.io/judge

## Categories

Grand Prize, and **Best Multi-Agent System** as the category prize (Best Use of Microsoft Foundry also fits).
