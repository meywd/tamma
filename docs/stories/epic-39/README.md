# Epic 39: Typed Work Documents & the Universal Workflow Lifecycle

## Overview

Tamma's workflows each invented a private micro-language: an informal JSON shape, a
hand-rolled parser, and a `parse-ok? → done : dead` decision at the end. There is no
uniform answer to "who decides the result is acceptable," no review-with-notes loop
outside the plan family, and no standard for resuming a workflow that stopped — most
restart from scratch. PR #475 fixed the delivery pipeline and pinned every prompt
contract to its caller's parser (one cell = one contract, CI-enforced), which exposed
the deeper truth: the platform's domain language exists but is implicit, scattered,
and per-workflow.

Epic 39 makes that language explicit. It defines the small set of **work documents**
the platform actually reasons about (Decomposition, Plan, Review, Findings, …) as
first-class typed artifacts, gives every producing workflow one shared quality
lifecycle, and makes every workflow resumable by design.

**The three pillars:**

1. **Typed documents.** ~10 document types as static C# types in `Tamma.Core`
   (the same pattern as the `AgentRole`/`AgentAction` taxonomy and the file-backed
   prompt registry): schema + executable domain rules (a `Decomposition` rejects
   dangling/cyclic `dependsOn`; a `Review` cannot approve with blocking issues) +
   a prompt-contract renderer + examples. Instances are JSON flowing through
   events, the store, and the API, always carrying `issueId` lineage.

2. **One lifecycle, written once.** `produce → validate (deterministic) →
   review (with notes) → revise (bounded rounds) → accept` as a generic
   sub-workflow. The internal loop exits only on **done** or a **typed
   unhandleable outcome** (`ReviewUndecidable`, `AmbiguityAboveThreshold`,
   `RoundsExhausted`, `ValidationExhausted`) — which escalates to the
   orchestrator/human **with the full document lineage attached**, never a bare
   failure. The accept gate is **mode-aware**: full-auto = the orchestrator
   applies a configured acceptance policy; supervised (70%) = a human gate via
   suspend/resume. "Who decides" becomes: the document's validator, then a
   Review document about it, then the policy.

3. **Resumable by design.** Every lifecycle workflow either suspends on a
   bookmark awaiting input (the generalized Design-Proposal pattern) or, after a
   crash/restart, **re-enters from the latest accepted state** reconstructed from
   the document store + DCB events — never from scratch. This becomes an
   authoring standard with a structural test, not a per-workflow favor.

## Design principles (settled)

- **Vocabulary static, composition dynamic.** Document types, their rules, roles,
  and actions are compile-time and drift-tested. Which workflows run, in what
  order, who reviews, and how many rounds are runtime policy/config.
- **Workflows declare their interface.** Each workflow statically declares
  `consumes: [X]` / `produces: Y`. A build-time test (the `ContractBindingTests`
  pattern) walks the graph and verifies every producer/consumer pair type-checks.
- **Code is NOT a document type.** Code's store is git, its validator is the
  build/test/gate stack (the strongest in the system), and its review is a
  `Review` whose subject is a diff. No schema needed.
- **Issues are anchors, not documents.** Documents reference issues
  (`issueId` on every instance — the existing DCB tag convention formalized),
  giving a queryable lineage per issue: Issue → Findings → Decomposition → Plan
  → Reviews → outcome.
- **Humans in full-auto sit at three positions only**: intent (the issue and its
  acceptance criteria), policy (thresholds/config, set once), exceptions (the
  escalation sink). Whether any action class always escalates (e.g. breaking
  changes) is acceptance-policy configuration, not a hardcoded rule.
- **Prose stays prose.** Tech-writer outputs (changelog, ADR, postmortem,
  release notes) are markdown with an audience tag — no forced structure.

## Document types

| Type | Produced by (today's informal shape) | Domain rules beyond schema |
|---|---|---|
| Findings | research, context-scan | every finding cites evidence; relevance/confidence ∈ [0,1]; ranked |
| AmbiguityAssessment | score-ambiguity | score ∈ [0,1]; typed ambiguities; clear ⇒ empty list valid |
| Clarification (Questions → Resolution) | clarify, incorporate-answers | ≥1 open-ended question; resolution states the clarified requirement |
| Decomposition | decompose-issue | unique IDs; no dangling/self/cyclic dependsOn; 2–8h sizing; prerequisite order |
| Plan | plan-generation, create-tasks | file map per task; dependencies resolvable; testing stated per task |
| Design | propose-design | ≥1 alternative with trade-offs; recommendation references an alternative |
| Review | ALL review/verdict producers (3 forked shapes today) | subject reference; issues carry severity+category+fix; decision enum; blocking issues ⇒ not approvable |
| TriageDecision | triage-* | closed enums for every classification field; reasoning required |
| Diagnosis | debug, diagnose-incident | hypotheses ranked by confidence; fix references affected files |
| TestSpec | write-tests, test-case-creation | each case bound to a task ID; one behavior per case |

## The lifecycle

```
            +------------------------------------------------------+
            |               DocumentLifecycle (generic)             |
            |                                                      |
 dispatch -->  PRODUCE (llm-call, role/action per doc type)        |
            |     |                                                |
            |  VALIDATE (document validator, deterministic)        |
            |     |  invalid: domain-phrased errors -> bounded      |
            |     |  repair turn (innermost ring)                  |
            |  REVIEW (single reviewer or panel -> Review doc)     |
            |     |  concerns: notes -> REVISE (bounded rounds)     |
            |  ACCEPT GATE (mode-aware)                            |
            |     |  full-auto: orchestrator applies policy        |
            |     |  supervised: bookmark suspend -> human resume   |
            +-----|------------------------------------------------+
                  |
        done ----+---- typed unhandleable outcome
                        (ReviewUndecidable | AmbiguityAboveThreshold |
                         RoundsExhausted  | ValidationExhausted)
                        -> ESCALATION with full document lineage
```

Every transition emits `DOCUMENT.*` DCB events; every instance is persisted with
lineage, which is also what re-entry reads to resume from the latest state.

## Stories

| Story | Title | Priority | Status | Est. Effort |
|-------|-------|----------|--------|-------------|
| 39-1 | Workflow I/O & Lifecycle Audit (consumes/produces map, gap analysis) | P0 | drafted | 3-4 days |
| 39-2 | Document Core — Envelope, Type Registry, Lineage, Drift Tests | P0 | drafted | 4-5 days |
| 39-3 | Document Types Batch 1 — Decomposition, Findings, AmbiguityAssessment, Clarification | P0 | drafted | 4-5 days |
| 39-4 | Document Types Batch 2 — Plan, Design, Review (unified), TriageDecision, Diagnosis, TestSpec | P0 | drafted | 5-6 days |
| 39-5 | Acceptance Policy — per-mode accept/escalation configuration | P0 | drafted | 3-4 days |
| 39-6 | DocumentLifecycleWorkflow — generic produce/validate/review/revise/accept | P0 | drafted | 6-8 days |
| 39-7 | Review Producers — single reviewer + panel onto the unified Review type | P0 | drafted | 4-6 days |
| 39-8 | Escalation & Approval Surface — events, suspend/resume, lineage payload | P0 | drafted | 4-5 days |
| 39-9 | Deterministic Repair Ring — validator feedback repair in the managed layer | P1 | drafted | 5-7 days |
| 39-10 | Resumable-by-Design Standard — bookmarks + latest-state re-entry + structural test | P0 | drafted | 5-7 days |
| 39-11 | Document Store & Lineage API | P1 | drafted | 4-5 days |
| 39-12 | Pilot Migration — IssueDecomposition onto the lifecycle | P0 | drafted | 4-5 days |
| 39-13 | Assessment Family Migration — Research, Ambiguity, Clarify, DesignProposal | P1 | drafted | 5-7 days |
| 39-14 | Planning Family Migration — PlanGeneration + PlanReview onto unified Review | P1 | drafted | 5-7 days |
| 39-15 | Remaining Producers Migration — Triage, TestSpec, TaskCreation, Diagnosis | P2 | drafted | 5-7 days |
| 39-16 | Prompt Contracts Generated From Document Types (single source) | P1 | drafted | 3-4 days |

## Supersedes / absorbs

- **Story 4-6 (approval & escalation event capture)** — absorbed by 39-8: the
  `APPROVAL.*` / `ESCALATION.*` event family ships as part of the escalation
  surface, with channel/resolution/time-to-resolve data.
- **PlanReviewWorkflow's bespoke discussion/revision loop and DesignProposal's
  bespoke approval gate** — both become instances of the generic lifecycle
  (39-6/39-7/39-8); the one-off implementations are retired in 39-13/39-14.
- **The three forked review/verdict shapes** — unified into the `Review`
  document (39-4); the verdict-shape class of bug becomes unrepresentable.
- **`ContractBindingTests`' hand-maintained binding map** — 39-16 flips it from
  "tokens present in the template" to "contract block generated from the
  document type," making prompt/parser drift impossible rather than caught.
- **NOT superseded:** Stories 2-15/2-16 (dependency mapping / sequencing) become
  the first *consumers* of the typed `Decomposition`; the quality gates (Epic 3)
  remain code's validator layer; the DCB store remains the substrate everything
  writes through.

## Dependencies

- PR #475 substrate: file-backed prompt registry, one-cell-one-contract taxonomy,
  `ContractBindingTests`, working prompt delivery.
- Elsa 3 bookmarks + the secure tenant-folded resume pattern (DesignProposal /
  Clarify endpoints) as the suspend/resume mechanism to generalize.
- DCB event store + Story 4-7 query API + Story 4-8 replay for latest-state
  reconstruction.
- Operating-mode detection (single-user vs SaaS; full-auto vs supervised) for the
  acceptance policy's per-mode ownership — the CLAUDE.md two-scoping-models rule
  applies to policy configuration.
