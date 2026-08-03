# Wave A adversarial review — verified notes (2026-08-03)

Commit 64436f0 reviewed by execution. No claim falsified. Three items
recorded here so the commit narrative is not read as more than it is.

## 1. 39-25: "twelve bindings thread the score" — live count is ~10, not 12
Two of the twelve threaded workflows fetch the assessment at a SCOPED
anchor where an ambiguity-assessment is never persisted, so they thread
null in every practical run (a dead thread == omitted key == pre-story
behaviour, no regression):
- BacklogPrioritizationWorkflow — fetches at BacklogBindingHelper.BuildAnchor
- TriageContextGatheringWorkflow — fetches at ScopeIssueId(baseId,"triage-context")
The two BASE-id bindings the commit DID caveat (TaskCreation, AdrAuthoring)
resolve fine. The coverage-map fixture is already honest ("honest null in
practice"); only the commit prose overstated. If either workflow ever needs
a real score, it must fetch at the base issue id like the other ten.

## 2. 40-8: "a crash re-run cannot double-create" is bounded at 1000 issues
Dedupe caps at MaxDedupePages=10 × 100 = 1000 existing issues; beyond that
it degrades to within-run only with a loud warning (pinned by
DedupeListTruncation_RecordsWarning). Practically safe only if the engine
lists newest-first (a crashed run's issues stay on page 1). Above 1000
open issues with an old-first listing, a re-run CAN double-create page-11+
titles. Not a defect — the ceiling is deliberate and tested — but the
guarantee is bounded, not absolute.

## 3. 43-13: a Human caller bypasses enabled=false on machinery, not just the dial
CallerKind.Human short-circuits before EVERY check (AutonomyGateEvaluator
~:295), so a human invoking a DISABLED machinery action returns
Automated/caller-human. The doc frames "off-switch = enabled=false" as
stopping the DIAL; for a human caller it does not stop anything. NOT
reachable today: all 16 enforced routes are EngineServiceOnly, so a human
credential never reaches a machinery effect. Recorded as a latent
consequence of "a human is never gated" — revisit if any machinery effect
ever gains a human-reachable route.

## Wave C review (2026-08-03) — three minor findings, fixed/recorded

- F1 (fixed): three live example strings still cited the RETIRED
  effect:deploy.promote-prod as the canonical key example — including a
  user-facing 400 error body (GovernanceEvaluateEndpoints.cs:76). A user
  copying it verbatim would get reason=uncatalogued. Repointed all three
  (that endpoint + CheckActionGateActivity Input desc + GovernanceEvaluateModels
  XML doc) to effect:deploy.prod. 43-12's AC2 "grep returns zero hits" was
  contradicted by these; now true for live example strings (retirement
  annotations legitimately still name the old wire).
- F2 (recorded, not fixed — legacy): packages/orchestrator/src/transports/
  remote.ts:80 still POSTs to the deleted /api/engine/command. packages/* is
  the superseded TS runtime (CLAUDE.md); the C# route/handler/DTO are gone, so
  the behaviour changes from a fake 200 to a 404 on a dead caller. Left as-is;
  the active runtime is clean.
- F3 (fixed): my own 43-19 commit + sprint-status line used present tense
  ("Adds the two stage-entry gates") for a DRAFTED, docs-only story, which read
  as claiming code. The reviewer rightly flagged it — the exact overstatement
  class this session keeps correcting in others. sprint-status line reworded to
  "WILL add … (DRAFTED, no code yet)". The story is Status: drafted; no gate
  code was added.
