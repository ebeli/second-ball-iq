# Decision-1 spike

Goal: before building any gate, prove that Microsoft-Decision-1 works from .NET and that its answers are
good enough to gate Second Ball IQ insights. Time box: 2 hours. If it fails, the gates switch to a small
Foundry LLM with structured output behind the same `IDecisionClient` interface. Nothing else changes.

## What is in here

| Path | What it is |
| --- | --- |
| `src/MatchLens.Decisions/` | The reusable library: typed questions and answers, the Foundry client, gate thresholds, the triage and grounding gates. No NuGet dependencies. |
| `spikes/DecisionSpike/` | Console runner with 6 triage windows (each written as prose and as numbers) and 5 grounding claims, all with known right answers. |
| `scripts/mock_systemone.py` | Offline fake of the endpoint, to check the code builds and runs before Azure is ready. Fake answers, so ignore its accuracy numbers. |

Wire protocol (from the Foundry launch material, cross-checked against the MIT-licensed
[ElBruno.AI.Decisions](https://github.com/elbruno/ElBruno.AI.Decisions) client, which was smoke-tested live on Oct 9):

```
POST https://<resource>.services.ai.azure.com/providers/microsoft/v1/systemone
api-key: <key>
{ "model": "<deployment name>", "state": "<text>",
  "questions": { "<name>": { "type": "choice" | "score" | "noul", "instructions": "...", "criteria": ... } } }
```

## 1. Build and dry run (no Azure needed)

Requires the .NET 8 SDK or newer.

```bash
dotnet run --project spikes/DecisionSpike -- --dry-run
```

This prints the exact request bodies. Optional: run against the mock.

```bash
python scripts/mock_systemone.py
# second terminal (PowerShell: $env:NAME="value")
export MATCHLENS_DECISION_ENDPOINT=http://localhost:8765 MATCHLENS_DECISION_KEY=x MATCHLENS_DECISION_DEPLOYMENT=mock
dotnet run --project spikes/DecisionSpike
```

## 2. Deploy Microsoft-Decision-1

In the Microsoft Foundry portal, find Microsoft-Decision-1 in the model catalog and deploy it (GlobalStandard,
or DataZoneStandard where offered). Note three things: the resource endpoint
(`https://<resource>.services.ai.azure.com`), a key, and the **deployment name** you chose. The deployment
name, not the catalog name, goes in the request.

## 3. Run the spike

```bash
export MATCHLENS_DECISION_ENDPOINT=https://<resource>.services.ai.azure.com
export MATCHLENS_DECISION_KEY=<key>
export MATCHLENS_DECISION_DEPLOYMENT=<deployment name>
dotnet run --project spikes/DecisionSpike -- --repeat 3
```

Keep the key out of the repo. Results, including raw responses, are saved to `spike-results/` (git-ignored).

## Pass criteria

| Check | Pass | If it fails |
| --- | --- | --- |
| Connectivity | Step 1 prints OK | Fix endpoint, key or deployment name; check region availability |
| Latency | p95 under 1,000 ms | Gate per window only; consider the LLM fallback |
| Triage accuracy | 5 of 6 windows right in the better format, every repeat | Rewrite option descriptions once, then fall back |
| No confident mistakes | 0 rows marked "wrong AND acted on automatically" | Raise τ/δ using the threshold sweep |
| Ambiguous window | Held for review, or a plausible choice | Acceptable either way; note it |
| Grounding | Wrong-number and invented-event claims never get Proceed | Keep the gate advisory only, rely on the Validator |
| State format | Note whether prose or numbers scored better | Use the winner in the analytics engine's summaries |
| Multi-question | Informational | If unsupported, send one question per request |

## Results, run 1 (Oct 15, 3 repeats, 52 calls)

| Check | Result | Decision |
| --- | --- | --- |
| Connectivity | OK; first call 1,139 ms (cold), then p50 201 ms, p95 335 ms | Keep Decision-1. Warm the deployment before a demo. |
| Triage, prose | 18/18 correct, right answer always ≥ 0.83, answers stable across repeats | Pass |
| Triage, numbers | 15/18; the pressing-shift window tied "none" and "possessionIllusion" at 0.40 (safely held) | Gate states are written as prose by the analytics engine |
| Ambiguous window | Picked possessionIllusion at 0.83 to 0.88, an accepted answer | Ambiguity is not reliably signalled by low confidence; detectors flag overlaps in code |
| Thresholds | Any τ from 0.50 to 0.80 gives the same split on these cases | Keep τ = 0.70, δ = 0.20; re-tune on the full scenario set on Oct 23 |
| Grounding v1 | All false claims dropped (p ≤ 0.06), but the main true claim was also dropped (p 0.06) | Fail; redesigned as v2 |
| Multi-question | Supported, 3 questions in 282 ms | Persona relevance rides along with triage |

Grounding v2 makes two changes. The evidence states the derived facts the engine already knows: counts,
which recovery led to which shot, and what did not happen. One broad question becomes four narrow checks,
which also tell the editor which check failed. Run 2 compares v1, v2-checks and v2 side by side:

```bash
dotnet run --project spikes/DecisionSpike -- --skip-triage --repeat 3
```

## Results, run 2 (Oct 15, grounding A/B, 8 claims x 3)

| Variant | True claims blocked | False claims passed | Held for review |
| --- | --- | --- | --- |
| v1: raw events, one question | 3 | 0 | 6 |
| v2-checks: raw events, four checks | 3 | 0 | 9 |
| v2: derived facts, four checks | 0 | 0 | 10 |

The derived-facts evidence is what fixed grounding; the four checks alone made it worse. With v2, every false
claim had a check at or below 0.03 and every true claim's lowest check was at least 0.47. The 10 reviews came from
the first thresholds (pass only when all checks ≥ 0.80), which were far too strict.

Decisions:

- Grounding uses v2: the analytics engine writes evidence as derived facts in plain language.
- Grounding thresholds: pass when every check ≥ 0.40, drop when any check ≤ 0.15, review in between.
  Replaying run 2 at these values gives 24/24. Eight hand-written claims is a small set, so this is re-checked
  on Oct 23 against claims the Narrator generates.
- Grounding v2 is slower (p50 about 450 ms, one 4.4 s outlier). It gets a 2.5 s timeout; a timeout, error or
  refusal returns Review, never a silent pass or a crash.
- The countsMatch check is weak on claims without numbers (0.47 to 0.65 on false claims), but those claims are
  always caught by another check. The deterministic Validator remains the authority on numbers.

## Results, run 3 (Oct 15, v2 with the new defaults, 8 claims x 3)

24/24 correct: no true claims blocked, no false claims passed, none held for review. True claims' lowest check
was 0.47; false claims' highest lowest check was 0.00. Latency was poor on a train connection (p95 7.5 s), which is
the network, not the model; the 2.5 s timeout would have sent those calls to review. The spike is closed.

## Change after the first live pipeline run (Oct 15)

In production the countsMatch check rejected even drafts built only from measured facts (p 0.01 to 0.29), as
expected from a model that reads words better than numbers. It was removed: numbers are checked exactly by the
deterministic Validator. In run 3, every false claim was also caught by another check (events 0.01 to 0.02,
outcomes 0.00, fair reading 0.00 to 0.05), so the gate loses nothing it was actually doing.
