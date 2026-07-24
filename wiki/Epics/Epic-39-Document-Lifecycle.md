# Epic 39: Typed Work Documents & the Universal Lifecycle

**Status:** Implemented — the document-lifecycle spine is complete and every document-producing workflow now rides it. Producer migrations 39-12…39-15 are all merged. 39-1 (I/O audit), 39-16 (generated prompt contracts), 39-17 (resident orchestrator agent), and 39-21 (C# RAG) remain.
**Stories:** 21 (39-1 through 39-21) — 17 landed, 4 remaining
**Layer:** Layer 4 (integration/orchestration)
**Depends on:** PR #475 substrate (file-backed prompt registry, one-cell-one-contract taxonomy, `ContractBindingTests`), Elsa 3 bookmarks + tenant-folded resume, Epic 4 (DCB store + 4-7 query API + 4-8 replay)

> **Overview**: [Document Lifecycle](Document-Lifecycle) — root-level topic page with the document catalog, the produce→validate→review→revise→accept lifecycle, and the resumable-by-design standard.

## 1. Overview

Tamma's workflows each invented a private micro-language: an informal JSON shape, a hand-rolled parser, and a `parse-ok? → done : dead` decision at the end. There was no uniform answer to "who decides the result is acceptable," no review-with-notes loop outside the plan family, and no standard for resuming a workflow that stopped. PR #475 pinned every prompt contract to its caller's parser (one cell = one contract, CI-enforced), which exposed the deeper truth: the platform's domain language existed but was implicit, scattered, and per-workflow.

Epic 39 makes that language explicit. It defines the small set of **work documents** the platform actually reasons about (Decomposition, Plan, Review, Findings, …) as first-class typed artifacts, gives every producing workflow one shared quality lifecycle, and makes every workflow resumable by design.

### The three pillars

1. **Typed documents.** ~10 document types as static C# types in `Tamma.Core` (the same pattern as the `AgentRole`/`AgentAction` taxonomy and the file-backed prompt registry): schema + executable domain rules (a `Decomposition` rejects dangling/cyclic `dependsOn`; a `Review` cannot approve with blocking issues) + a prompt-contract renderer + examples. Instances are JSON flowing through events, the store, and the API, always carrying `issueId` lineage.
2. **One lifecycle, written once.** `produce → validate (deterministic) → review (with notes) → revise (bounded rounds) → accept` as a generic sub-workflow. The internal loop exits only on **done** or a **typed unhandleable outcome** (`ReviewUndecidable`, `AmbiguityAboveThreshold`, `RoundsExhausted`, `ValidationExhausted`) — which escalates to the orchestrator/human **with the full document lineage attached**, never a bare failure. The accept gate **always submits the document to the orchestrator** — never an if-else that skips the decision, never an embedded `llm-call`.
3. **Resumable by design.** Every lifecycle workflow either suspends on a bookmark awaiting input (the generalized Design-Proposal pattern) or, after a crash/restart, **re-enters from the latest accepted state** reconstructed from the document store + DCB events — never from scratch. This is an authoring standard with a structural test (Story 39-10), not a per-workflow favor.

## 2. Design principles (settled)

- **Vocabulary static, composition dynamic.** Document types, their rules, roles, and actions are compile-time and drift-tested. Which workflows run, in what order, who reviews, and how many rounds are runtime policy/config.
- **Workflows declare their interface.** Each workflow statically declares `consumes: [X]` / `produces: Y`. A build-time test (the `ContractBindingTests` pattern) walks the graph and verifies every producer/consumer pair type-checks.
- **Code is NOT a document type.** Code's store is git, its validator is the build/test/gate stack (the strongest in the system), and its review is a `Review` whose subject is a diff. No schema needed. (This is why Epic 40 gives the coding step its own resumable pattern rather than the document lifecycle.)
- **Issues are anchors, not documents.** Documents reference issues (`issueId` on every instance), giving a queryable lineage per issue: Issue → Findings → Decomposition → Plan → Reviews → outcome.
- **Autonomy is a dial, not a mode.** A configured autonomy level from **70** (supervised baseline — the orchestrator assigns nearly every decision to a human) to **100** (full auto — the orchestrator decides everything the rules allow), admin-editable, per-document-type overridable, and read live — never cached into a running workflow.
- **The acceptor is an actor, not a branch.** Accepting a document is always a decision taken by someone — the orchestrator, or a holder of the role it assigns the decision to — against the configured acceptance rules. Deterministic code enforces only the hard guardrails (round bounds, the blocking-review invariant, always-escalate classes).
- **The orchestrator is a resident agent, not a per-turn call — one per tenant** (Story 39-17). A long-running LLM process holding platform-wide context, with tools over git, the DCB event store, logs, workflow control, the document store, and the acceptance rules, reachable over real-time channels (39-18).
- **Chat is the front door; the Task View is the inbox.** Users talk WITH the orchestrator as their primary interface; the Task View lists the concrete decisions/reviews/approvals assigned to them (each backed by a suspended workflow). Every chat turn is a `CHAT.*` DCB event.
- **Access is a model, enforced server-side.** Users receive tasks only for workflows they initiated or repos they have access to. One audience resolver implements that predicate for task delivery, Task View listing, chat answers, workflow initiation, and the orchestrator's assignment choices.
- **Prose stays prose.** Tech-writer outputs (changelog, ADR, postmortem, release notes) are markdown with an audience tag — no forced structure.

## 3. Document types

| Type | Produced by | Domain rules beyond schema |
|---|---|---|
| Findings | research, context-scan | every finding cites evidence; relevance/confidence ∈ [0,1]; ranked |
| AmbiguityAssessment | score-ambiguity | score ∈ [0,1]; typed ambiguities; clear ⇒ empty list valid |
| Clarification | clarify, incorporate-answers | ≥1 open-ended question; resolution states the clarified requirement |
| Decomposition | decompose-issue | unique IDs; no dangling/self/cyclic dependsOn; 2–8h sizing; prerequisite order |
| Plan | plan-generation, create-tasks | file map per task; dependencies resolvable; testing stated per task |
| Design | propose-design | ≥1 alternative with trade-offs; recommendation references an alternative |
| Review | ALL review/verdict producers | subject reference; issues carry severity+category+fix; decision enum; blocking issues ⇒ not approvable |
| TriageDecision | triage-* | closed enums for every classification field; reasoning required |
| Diagnosis | debug, diagnose-incident | hypotheses ranked by confidence; fix references affected files |
| TestSpec | write-tests, test-case-creation | each case bound to a task ID; one behavior per case |

The three previously-forked review/verdict shapes are unified into the single `Review` document — the verdict-shape class of bug becomes unrepresentable.

## 4. The lifecycle

```
            +------------------------------------------------------+
            |               DocumentLifecycle (generic)            |
 dispatch -->  PRODUCE (llm-call, role/action per doc type)        |
            |     |                                                |
            |  VALIDATE (document validator, deterministic)        |
            |     |  invalid -> bounded repair turn (innermost)    |
            |  REVIEW (single reviewer or panel -> Review doc)     |
            |     |  concerns: notes -> REVISE (bounded rounds)    |
            |  ACCEPT (submit to the orchestrator -- always)       |
            |     |  publish AcceptanceRequest + suspend on gate;  |
            |     |  orchestrator reads rules + autonomy (70-100)  |
            |     |  and routes: decide itself, or assign to an    |
            |     |  eligible user's Task View                     |
            +-----|------------------------------------------------+
                  |
        done ----+---- typed unhandleable outcome -> ESCALATION
                        (ReviewUndecidable | AmbiguityAboveThreshold |
                         RoundsExhausted | ValidationExhausted)
```

Every transition emits `DOCUMENT.*` DCB events; every instance is persisted with lineage, which is also what re-entry reads to resume from the latest state.

## 5. Stories

| Story | Title | Priority | Status |
|-------|-------|----------|--------|
| 39-1 | Workflow I/O & Lifecycle Audit (consumes/produces map, gap analysis) | P0 | Drafted (remains) |
| 39-2 | Document Core — Envelope, Type Registry, Lineage, Drift Tests | P0 | Done |
| 39-3 | Document Types Batch 1 — Decomposition, Findings, AmbiguityAssessment, Clarification | P0 | Done |
| 39-4 | Document Types Batch 2 — Plan, Design, Review (unified), TriageDecision, Diagnosis, TestSpec | P0 | Done |
| 39-5 | Acceptance Rules — configurable policy, admin UI, orchestrator read path | P0 | Done |
| 39-6 | DocumentLifecycleWorkflow — generic produce/validate/review/revise/accept | P0 | Done |
| 39-7 | Review Producers — single reviewer + panel onto the unified Review type | P0 | Done |
| 39-8 | Escalation & Approval Surface — events, suspend/resume, lineage payload | P0 | Done |
| 39-9 | Deterministic Repair Ring — validator feedback repair in the managed layer | P1 | Done |
| 39-10 | Resumable-by-Design Standard — bookmarks + latest-state re-entry + structural test | P0 | Done |
| 39-11 | Document Store & Lineage API | P1 | Done |
| 39-12 | Pilot Migration — IssueDecomposition onto the lifecycle | P0 | Done |
| 39-13 | Assessment Family Migration — Research, Ambiguity, Clarify, DesignProposal | P1 | Done |
| 39-14 | Planning Family Migration — PlanGeneration + PlanReview onto unified Review | P1 | Done |
| 39-15 | Remaining Producers Migration — Triage, TestSpec, TaskCreation, Diagnosis | P2 | Done |
| 39-16 | Prompt Contracts Generated From Document Types (single source) | P1 | Drafted (remains) |
| 39-17 | Orchestrator Agent — long-running LLM process, platform context & tools | P0 | Drafted (remains) |
| 39-18 | Real-Time Channels — workflow↔orchestrator + user↔orchestrator (SignalR) | P0 | Done |
| 39-19 | Orchestrator Chat — primary user interface, and the Task View | P0 | Done |
| 39-20 | Teams, Roles, Repo Access & Task Routing | P0 | Done |
| 39-21 | RAG in C# — per-tenant knowledge isolation and grounding | P1 | Drafted (remains) |

**Producer-migration spine complete.** 39-12 (pilot) → 39-13 (assessment family) → 39-14 (planning family) → 39-15 (remaining producers) are all merged, so every document-producing workflow now rides `DocumentLifecycleWorkflow` rather than a bespoke parse/branch/terminal path.

## 6. Supersedes / absorbs

- **Story 4-6 (approval & escalation event capture)** — absorbed by 39-8: the `APPROVAL.*` / `ESCALATION.*` event family ships as part of the escalation surface.
- **PlanReviewWorkflow's bespoke discussion/revision loop and DesignProposal's bespoke approval gate** — both become instances of the generic lifecycle (39-6/39-7/39-8); the one-offs are retired in 39-13/39-14.
- **The three forked review/verdict shapes** — unified into the `Review` document (39-4).
- **`ContractBindingTests`' hand-maintained binding map** — 39-16 flips it from "tokens present in the template" to "contract block generated from the document type."
- **NOT superseded:** Stories 2-15/2-16 (dependency mapping / sequencing) become the first *consumers* of the typed `Decomposition`; the quality gates (Epic 3) remain code's validator layer; the DCB store remains the substrate everything writes through.

## 7. Dependencies

- PR #475 substrate: file-backed prompt registry, one-cell-one-contract taxonomy, `ContractBindingTests`, working prompt delivery.
- Elsa 3 bookmarks + the secure tenant-folded resume pattern (DesignProposal / Clarify endpoints) as the suspend/resume mechanism to generalize.
- The agent-dispatch executor stack (`Tamma.Activities/AgentDispatch/*`) and the multi-provider abstraction as the substrate for the 39-17 orchestrator agent; the SSE streaming surface as the one-way precedent the 39-18 bidirectional channels sit beside.
- [Epic 4](Epic-4-Event-Sourcing) DCB event store + 4-7 query API + 4-8 replay for latest-state reconstruction.
- Operating-mode detection (single-user vs SaaS) for the acceptance rules' and access model's per-mode ownership.

## 8. See also

- [Document Lifecycle](Document-Lifecycle) — root-level topic page
- [Resumable Workflows](Resumable-Workflows) — the resumable-by-design standard (39-10) generalized by Epics 40 and 41
- [Epic 40: Resumable Coding](Epics/Epic-40-Resumable-Coding) — the coding step made durable on the 39-10 mechanism
- [Epic 41: Full-Team Workflows](Epics/Epic-41-Full-Team-Workflows) — every remaining SDLC activity as a lifecycle workflow on this spine
- [Epic 42: Tool Layer](Epics/Epic-42-Tool-Layer) — the governed tool catalog the orchestrator and agents act through
- [Epic 4 — Event Sourcing](Epic-4-Event-Sourcing) — the DCB substrate `DOCUMENT.*` events write to
- [Role/Action Taxonomy](Role-Action-Taxonomy) — the (role, action) cells the produce step binds
- Story files: [Epic 39 on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-39)

---

_Last updated: 2026-07-24_
