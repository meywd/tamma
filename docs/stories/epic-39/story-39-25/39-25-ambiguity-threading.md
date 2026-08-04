# Story 39-25: Ambiguity Threading — the Dead Score Leg, Wired

Status: drafted

Implements: Story 43-11 **Amendment 2, section F** ("the 'at 100' escape hatch is currently one document type wide — say so, then widen it"). Sits in Epic 39 because the deliverable is lifecycle plumbing, not catalog policy.

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **platform operator running at a high dial**,
I want the ambiguity score the assessment family already computes to follow the work into every downstream lifecycle dispatch,
So that the product rule "at 100, only ambiguity or no-agreement pulls in a person" is true across document types — not just for the one type that scores itself.

## Priority

P1 — Amendment 2-F's verified finding: the rule is "one wired type away from being false advertising". The escape hatch exists, is tested, and is dead on every path but one.

## Architectural Context (READ FIRST)

- **The comparison lives in one place and has two legs.** `DocumentLifecycleHelper.IsAmbiguityAboveThreshold` (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DocumentLifecycleHelper.cs:363-377`): leg 1 — a **threaded** `inputScore` at or above `rules.AmbiguityEscalationThreshold` escalates; leg 2 — a **self-read** of the payload, but only when the type is `ambiguity-assessment` itself.
- **Leg 1 is dead.** `DocumentLifecycleWorkflow` reads the input (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:179`: `ctx.GetInput<object>("ambiguityScore")`) and threads it through the helper (`DocumentLifecycleHelper.cs:167,192`) — but **no dispatcher passes it**: a grep for `["ambiguityScore"] =` over `Tamma.ElsaServer` returns zero call sites (re-verified 2026-08-02). The only type ever scored is `ambiguity-assessment` via leg 2.
- **The producer exists.** `AmbiguityScoringWorkflow` (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AmbiguityScoringWorkflow.cs:44`, definition id `ambiguity-scoring`) outputs `score` / `ambiguityCount` / `confidence` and persists the `ambiguity-assessment` document. Nothing carries `score` forward.
- **The consumers are the lifecycle dispatchers** — the workflows that dispatch `document-lifecycle` (`IssueDecompositionWorkflow`, `PlanGenerationWorkflow`, `TaskCreationWorkflow`, `ClarifyingQuestionsWorkflow`, `DebugDiagnosisWorkflow`, `TestCaseCreationWorkflow`, `BacklogPrioritizationWorkflow`, `TriagePODecisionWorkflow`, `TriageContextGatheringWorkflow`, `AdrAuthoringWorkflow`, `ResearchWorkflow`, `AcceptanceCriteriaAuthoringWorkflow`, `DesignProposalWorkflow`, and the shared `LifecycleBindingHelper` binding they build on).
- **The wiring**: within an issue run, the most recent accepted `ambiguity-assessment` score for that `issueId` is carried into downstream lifecycle dispatches as the `ambiguityScore` input. Concretely: the orchestrating composites that already hold the run's variables (the triage/intake path and `SingleIssueCycleWorkflow`'s planning chain) capture `score` when the assessment completes and pass it in each subsequent dispatch's input dictionary; producers with no assessment upstream pass nothing (null stays null — no fabricated zero, which would read as "measured unambiguous").
- **The honesty table (Amendment 2-F's second AC)** — the coverage map ships **in this story** as a fixture, family × signal:

  | Family | Ambiguity signal | No-agreement signal |
  |---|---|---|
  | `ambiguity-assessment` (self-scored) | leg 2 (payload read) | panel where configured |
  | Documents downstream of an assessment in the same run | **leg 1 (this story)** | panel where configured |
  | Documents with no upstream assessment | none — stated, not implied | panel where configured |
  | Tool calls / effects | none (classification only) | none |

  No-agreement already works where a panel is configured (split decision, below-quorum, empty panel, Critical veto → `review-undecidable` — verified in 2-F). Tool/effect paths have no content signal beyond the denylist; the table says so instead of hiding it.

## Acceptance Criteria

1. **Leg 1 is live**: an issue run whose assessment scored at or above the threshold escalates the **next** downstream lifecycle dispatch (e.g. decomposition) to a person before REVIEW, at dial 100 — pinned end to end. A run scoring below the threshold does not escalate on the threaded leg.
2. **Null is honest**: a producer with no upstream assessment passes no `ambiguityScore`; a test asserts the dispatch input omits the key entirely (never `0.0`).
3. **The score follows the run, not the process**: the threaded value is the latest accepted assessment for the run's `issueId`; a stale score from a different issue is never picked up (pinned with two interleaved runs).
4. **Coverage map is a fixture, and stays current**: the table above ships as a test-readable fixture; a structural test derives, per lifecycle dispatcher, whether it can thread a score (does its composite hold one?) and compares against the fixture — a dispatcher gaining or losing the signal without a fixture edit fails the build. (Same pattern as 39-24 AC10.)
5. **The two escape signals remain the only level-independent human pulls** — 39-24 AC4's assertion is re-run against the widened wiring: at dial 100, escalation happens on ambiguity-above-threshold and review-undecidable, and on nothing else.
6. **`IsAmbiguityAboveThreshold` is unchanged** — the fix is callers passing the input that exists, not new comparison logic. A diff-scope check in review; the helper's tests pass unmodified.
7. **`dotnet test` green; no schema change.**

## Dependencies

- **39-6 (document lifecycle)** and **39-13/39-15 (producer migrations)** — landed; the dispatchers this story edits are theirs.
- **AmbiguityScoringWorkflow (39-5 lane)** — landed; the producer of the score.
- **Story 43-11 Amendment 2-F** — the requirement and the verified findings this story implements.
- **Not blocked by 43-13/43-14** — the escalation path is the lifecycle's own, not the gate ledger's.
- **Verified in tree**: `DocumentLifecycleHelper.cs:72,167,192,363-377`; `DocumentLifecycleWorkflow.cs:179,199`; `AmbiguityScoringWorkflow.cs:23-24,44,70`; zero `["ambiguityScore"] =` dispatch sites.

## Out of Scope

- A content-ambiguity signal for tool/effect paths — stated as "none" in the map; inventing one is research, not this story.
- Changing `AmbiguityEscalationThreshold` semantics or defaults.
- Scoring documents that have no upstream assessment (an auto-assessment step per producer would be a product decision about latency/cost — record it as a candidate follow-up, do not build it here).

## Estimated Effort

2–3 days — 1 for the capture-and-thread wiring in the composites and dispatchers, 1 for the end-to-end pins (AC1/AC3/AC5), 0.5 for the coverage-map fixture and structural test.

## Change Log

| Date       | Version | Changes                                                              | Author |
| ---------- | ------- | --------------------------------------------------------------------- | ------ |
| 2026-08-02 | 1.0.0   | Initial story — thread the assessment score into downstream lifecycle dispatches; coverage map as a pinned fixture (43-11 Amendment 2-F) | Claude |
