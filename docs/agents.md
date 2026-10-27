# Agents and workflow

Every candidate from the analytics engine runs through one workflow on Microsoft Agent Framework
(`Microsoft.Agents.AI.Workflows`). Each step is an executor; the shared `InsightWork` object carries every result
and a trace step from one executor to the next.

```
Triage ──drop──────────────────────────────────────────────┐
  │ proceed / review                                         ▼
  └─► Tactical Analyst ─► Narrator ─► Validator ─► Grounding ─► Publish
       (agent + tools)    (agent,      (code)      (Decision-1)
                          per persona)
```

| Step | Kind | What it does | If it fails |
| --- | --- | --- | --- |
| Triage | Decision-1 choice | Reads the neutral window summary and picks what deserves airtime. A confident "none" drops the candidate. A confident different story **folds** it into that story (e.g. a chance inside a pressing spell, which has its own insight). Low confidence sends it to the editor. Goals always proceed. | Editor review |
| Tactical Analyst | Agent with engine tools | Explains why the moment matters, picks the 2 to 4 facts that show it and the events to cite. Calls `get_window_stats`, `describe_window`, `get_events` and `get_player_stats` on the live engine, and gets the roster. Never introduces numbers. Unknown players, distant events and non-existent facts are removed from its output rather than failing the moment. | Template analysis |
| Narrator | Agent, one call per persona in parallel | Writes headline and summary for each audience and language, and picks fact keys to show as figures. | Template draft |
| Validator | Deterministic code | Every cited event exists and belongs to the moment; every number in the text is a measured fact. A failing model draft is replaced by the template. | Template draft |
| Grounding | Decision-1, three checks | Events listed, outcomes match, fair reading; thresholds from the spike. Numbers are left to the Validator (see below). A dropped model draft is replaced by the template and grounded again. | Editor review on timeout |
| Publish | Code | Builds `Insight` v1 records. **Auto** when every gate passed, **Editor** when any gate was unsure, **Drop** when validation failed or grounding rejected a model draft. A template rejected by grounding goes to the editor, never the bin: it is built from verified facts, so a true moment (a goal above all) cannot vanish on a model's doubt. | — |

## Recap Writer

At half time and at full time the live server asks the Recap Writer agent for one recap per persona: a headline,
two or three paragraphs and the 2 to 5 key moments. It sees only what the pipeline already stood behind: the
insights that went to air, their measured facts, each team's stats for the match and the score. Every number in its
text is checked against that input, and key moments must cite published insight ids. A recap that fails, times out
or errors is replaced by a template built from the score, the goals and the published headlines, so a recap always
exists.

## Why numbers cannot be hallucinated

- The Narrator never writes figure values. It chooses fact keys, and code copies the values from the engine's
  measured facts.
- Every number in a headline or summary must match a fact (also as a percentage or rounded), the score, the
  window length, or a minute of the moment. Anything else fails validation, and the template takes over.
- The grounding gate catches claims that use real numbers misleadingly.

## Lessons from the first live run (Oct 15, demo-match 45'-65', gpt-5.4-mini)

- The analyst and the Narrator answered every call (5/5, 20/20), and every draft passed the Validator's number checks.
- Grounding's counts check rejected even fact-built templates (p 0.01 to 0.29), so the goal at 61' was dropped in
  English. Decision models are weak at matching numbers, and the Validator already checks them exactly, so the check
  was removed. Template drafts can no longer be dropped by grounding.
- Triage chose "pressing shift" for chances inside Northvale's press with 0.87 to 0.92 confidence. That is right: the
  press is the story and has its own insight. Those chances now fold into it instead of going to the editor.
- The analyst once returned the player id "nor-?". It now gets the roster, and unknown ids are removed from its output.
- Latency: analyst about 2 s, Narrator 2 to 8 s (four personas in parallel), grounding 0.3 s per persona.

## Second live run (Oct 15, same window, after the fixes)

- 5 candidates gave 12 insights: 7 auto-published, 5 to the editor, 0 dropped, 0 template fallbacks, 0 rejections.
- Both chances inside Northvale's press folded into the pressing insight (triage 0.91 and 0.89). The goal at 61'
  went out in all four personas.
- Four of the five editor items came from one Decision-1 request that hung for the full 10 s. Requests now time out
  after 4 s and are retried once (`DecisionOptions.MaxAttempts`).
- The fifth was the Finnish press draft: grounding was unsure (0.35) whether every event it described was in the
  English evidence. Finnish drafts stay a little below English ones; the editor queue is the right home for them.
- Narrator latency reached 21 s for one candidate (four personas in parallel). The live server processes two
  candidates at a time, so a slow narration does not hold up a goal.

## First full live match (Oct 16, demo-match at 10x, live server)

- 9 candidates, each through the whole workflow in 4 to 7 s; the queue never backed up at 10x. MCP answered
  `get_match_state` from an outside client mid-match.
- The 41' Harbor City goal came from a build-up move, which no detector covered, so a goal went unmentioned. A goal
  detector now reports every goal that is not a turnover to chance, with the move behind it.
- Triage folded a passing milestone at 55' into the press ("part of a bigger story"). Folding now applies only to
  chances; a different kind of story goes ahead unless triage is confident nothing deserves airtime.

## Full live run with the coach's corner (Oct 26, judge mode from 38' at 8x, live server)

- No errors or timeouts: Decision-1 207 calls (median 161 ms, slowest 612 ms), chat 180 calls (median 1.3 s,
  slowest 6.7 s). The coach agents wrote their own reasons; Decision-1 picked between them every time (record 0
  right, 2 wrong, 2 level over the match). What-if: Harbor City pushing up 8 m at 43:02 took their final-third control
  from 2% to 19%, and Decision-1 put "would help" at 0.78.
- Most moments went to the editor. Formation, role and better-pass reads scored 0.00 to 0.15 on "every event the
  claim describes is in the evidence" (they describe patterns), so they are now grounded on outcomes and fair reading
  only; triage now proceeds below 0.70 when it names the moment's own kind at 0.55 or more with a 0.20 margin; the
  territory read was dropped by a rounding mismatch in the Validator (fixed).
- The Recap Writer wrote "0.68 possession" and "xg": its instructions now ask for percentages and xG.
- Second run with those fixes (same window): 90 of 128 persona insights went to air automatically, up from 38 (70%
  against 30%), none dropped. Still held: a turnover-to-chance where triage itself preferred another story at 0.54
  (rightly unsure), and a 50-passes milestone whose "every event listed" check scored 0.01 to 0.15, so milestones are
  now grounded like the other counted reads. One narrator draft called a player "he" despite the instruction; the
  Validator now rejects he and she, so the template takes over.
- Third run (milestones grounded like counted reads, he/she rejected): 95 of 128 to air automatically (74%), none
  dropped, no errors; the 50-passes milestone now airs for 7 of 8 personas. Decision-1 median 142 ms (slowest 609 ms),
  chat median 1.2 s (slowest 7.7 s). Coach's corner over the match: 0 right, 2 wrong, 4 level.

## Personas

`Persona(Audience, Language, FavoriteTeamId?, FocusPlayerId?)`. The demo set covers casual, analyst, broadcast and beginner, each in
English and Finnish (eight narrations per moment, in parallel). The live-run notes above are from Oct 15–16, when the
set had four personas; it grew to six with Finnish analyst and broadcast, then to eight with the beginner style.
`docs/ARCHITECTURE.md` (Personas, languages and voices) explains how these narration styles relate to the fan's
explain depth and the studio's on-air viewer. Favourite club frames the story from that side, and player-focus mode centres the
story on one player when they are involved.

## Running it

```bash
# No models: template analyst and narrator, gates skipped. Tests the workflow end to end.
dotnet run --project tools/MatchLens.Pipeline.Cli -- demo-match --offline

# Live: a Foundry chat model plus Decision-1.
# Needs MATCHLENS_DECISION_ENDPOINT / _KEY / _DEPLOYMENT and MATCHLENS_CHAT_DEPLOYMENT.
dotnet run --project tools/MatchLens.Pipeline.Cli -- demo-match --from 45 --to 65
```

The chat model is reached through the Foundry resource's OpenAI-compatible endpoint
(`https://<resource>.services.ai.azure.com/openai/v1/`), with the same key as Decision-1. Insights are written to
`out/<matchId>-insights.jsonl` for the dashboard.

## Open questions

- Grounding a Finnish draft against English evidence is untested; Decision-1's spike was English only.
- Package versions float to the latest 1.x of Microsoft Agent Framework (1.25.0 on Oct 15). Pin them once the
  build is green.
