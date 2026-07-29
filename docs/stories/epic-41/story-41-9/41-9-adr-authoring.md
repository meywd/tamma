# Story 41-9: ADR Authoring Workflow

Status: done (2026-07-29) — `AdrAuthoringWorkflow` (`DefinitionId = adr-authoring`) ships as a thin binding over `document-lifecycle` producing `prose` with `kind=adr` / `audience=engineering`; **no `adr` document type was minted** (Correction 2 — pinned by a test). The `write-adr` template was rewritten from the markdown issue-comment report (which carried **no JSON fence at all**, so a produce through the cell could not even be ingested) to the prose envelope (version 1 → 2) and its example validates with **zero** violations; the cell moved from `TemplateExampleConformanceTests.KnownNonConformingTemplates` (pin 15 → 14) into `ContractBindingTests.Bindings` (17 → 18). Claim boundary per the plan's Correction 5: AC1–AC4 are claimed; the "architect accepts" half is wired, not reachable end-to-end (39-17/39-19/39-20 unlanded). See the dated amendments below.

## User Story

As an **architect** (or eligible role-holder), I want a workflow that captures a significant technical
decision as an **Architecture Decision Record** — a prose document with an audience tag — on the standard
lifecycle, so that decisions are drafted, reviewed, accepted, and stored with issue lineage instead of
living only in chat or a reviewer's memory.

## Priority

P1 / Wave 1 — cheap, high-value, and the reference implementation of the **prose-on-lifecycle** path that
the whole tech-writer / devops / PM prose family (41-4, 41-5, 41-22, 41-24, 41-25, 41-26, 41-8) reuses.

## Scope

Thin binding over `document-lifecycle`. `consumes: [issue, Design?, Findings?]` / `produces: prose (ADR,
audience=engineering)`. Produce cell `(architect, write-adr)`. *Prose stays prose* (Epic 39): markdown +
audience tag, no forced schema; the review stage is a `Review` over the prose.

## Produced document

Prose ADR (context / decision / consequences / alternatives-considered, but structure is convention, not
validated schema), audience-tagged, `issueId`-lineaged.

## Events

`ADR.STARTED` → `.DRAFTED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; a decision affecting a public contract can be a configured always-escalate
class. Accepted ADRs are queryable per issue and per repo.

## Autonomy behavior

- **70–84:** agent drafts, architect accepts.
- **85–100:** agent drafts and self-accepts unless the decision touches an always-escalate class.

## Acceptance Criteria

1. Thin lifecycle binding; prose rides the lifecycle with an audience tag; review stage produces a `Review`
   over the ADR text.
2. No bespoke parse/terminal; non-success exits are typed escalations with lineage.
3. Accepted ADR persisted with lineage and retrievable via the 39-11 store.
4. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched `document-lifecycle` child); 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** **41-1c** (the `prose` type + `Audience` field — 41-9 is the designated *reference
  implementation* of the prose path, so it cannot precede the story that builds it; *corrected: was
  "Epic 39 (prose-document handling)", which 39-1:58 records as out of Epic 39's scope*), Epic 39
  (lifecycle, review, store).
- **Related:** consumes 41-10 System Design output when present.

## Estimated Effort

2–3 days

## Amendments from the implementation pass (2026-07-29)

1. **AC4 reads `LatestStateReEntry` and that is what landed** (the plan's Correction 1). A thin binding
   owns no canonical suspend node, so `Both` would fail clause (b) of `ResumableStandardStructuralTests`;
   the accept-gate bookmark lives inside the dispatched `document-lifecycle` child. No allowlist entry —
   the workflow declares.

2. **No `adr` document type was minted** (Correction 2). The binding produces 41-1c's `prose` with
   `kind = adr`, `audience = engineering`, and `AdrAuthoringWorkflowStructureTests.NoAdrDocumentTypeWasMinted`
   pins the absence so a later story cannot quietly add one. `.dev/decisions/`'s nine markdown files are
   untouched; migrating them into the store stays out of scope.

3. **The template rewrite was load-bearing** (Correction 3). The shipped body instructed a markdown
   issue-comment report (`## Summary` / `### Key Findings` / `### Action Items`) with **no `json` fence**,
   so the lifecycle's first-`{`…last-`}` ingest carve could never have produced an ingestible reply, let
   alone a validating one. Front matter `variables` (`role, workItemJson, findings, audience`),
   `enableTools` and `maxTokens` are byte-identical; only `version` moved 1 → 2 (the D5 plan).
   `feedbackVariableName = "findings"` therefore lands in a DECLARED carrier.

4. **The `Bindings` entry pins the prose ENVELOPE, not prose structure** (D4). Four token groups —
   `"kind"`, `"audience"`, `"title"`, `"body"` — because that is exactly what `ProseDocumentType.Validate`
   checks. The ADR shape convention (context / decision / alternatives / consequences) is prose guidance
   in the template body per 41-1c D3, deliberately NOT a token group. The contract being thin is honest,
   not vacuous: `AdrBindingHelperTests.TheProseValidator_AcceptsArbitraryMarkdown_ButRejectsAnEmptyBody`
   proves the envelope rules bite in both directions.

5. **D8(i)'s `empty` consumes list was not taken.** The registry row declares
   `consumes: [design, findings]` — the graph carries both `FetchLatestAcceptedDocumentActivity` nodes,
   epic rule 1 requires the binding to declare `consumes: [...]`, and the story's own Scope line says
   `consumes: [issue, Design?, Findings?]`. Declaring `empty` would have made the interface graph lie
   about a real edge.

6. **The `ADR.*` family uses the SHARED emitter, not a copied `EmitAdrEventActivity`** (a deviation from
   this story's D6, resolved in favour of 41-2's D7). 41-2 landed
   `Tamma.Activities/Documents/EmitDomainLifecycleEventActivity.cs` in the same wave — one activity with
   the family as an input and the status derived from the type suffix — precisely so the Epic 41 producer
   batch does not ship five near-identical copies. `AdrEvents.cs` therefore ships only the four
   constants, and D6's substantive requirement (a four-member family with a LOUD `ADR.FAILED` terminal)
   is met and pinned.

7. **A third `FlowDecision` (`DocumentDrafted`) exists**, within the plan's own "three `FlowDecision`s
   max" (D2). It routes a TYPED lifecycle value — whether the exit carries a `documentId` — so `.DRAFTED`
   fires when and only when the lifecycle actually minted a document.

8. **D3's producer-scoped issue id is the decision the other seven prose stories inherit.**
   `{issueId}#adr` via `CreationBindingHelper.ScopeIssueId`, and `AdrBindingHelper.ProducerScope` is the
   named constant they copy. The general fix — a producer or `kind` filter on the 39-11 latest-accepted
   read — stays FILED against 39-11 rather than solved locally seven times.

9. **`ResolveAudience` is a caller-input guard, added beyond the plan.** 41-1c's audience vocabulary is
   closed and ordinal, so a caller typo would otherwise reach the producer and burn a repair round on
   `PROSE_AUDIENCE_OUT_OF_VOCABULARY`. An unparseable *caller input* falls back to `engineering`; a
   *model reply* with a bad audience still fails validation loudly — this is not a normalisation of the
   produced document.

10. **Test-coverage boundary — no new Testcontainers execution suite.** The plan's
    `AdrAuthoringLifecycleExecutionTests` (a)–(e) was NOT written, for the reason 41-1c's follow-up F1
    established: every lifecycle execution fixture in the tree is `[Explicit]`, no CI job selects
    `[Explicit]` fixtures, and under the bare-provider harness such a fixture fails deterministically
    (the lifecycle suspends forever on its first `ActivityKind.Task` node with no bookmark to resume) —
    `ProseLifecycleExecutionTests.ProseAdr_FullCycle_ReviewedByTechWriter_ResumesToAccepted` is exactly
    that fixture and exactly that state. Executing coverage is `AdrAuthoringWorkflowStructureTests`
    (15 tests) + `AdrBindingHelperTests` (23 cases) + the drift gates. **What that leaves unproven by
    THIS story:** AC3's persisted-lineage/retrievability is carried by 41-1c's
    `ProseStoreAndLineageTests` and 41-1b's `NewDocumentTypeStoreRoundTripTests` (both real Postgres 17
    Testcontainer suites that already round-trip `prose` with its audience through the store and the
    lineage API), and AC1's review-over-prose half by 41-1c's `BuildReviewEnvelopeTests` — not by a run
    of this workflow. The always-escalate scenario (D7 / plan test (c)) is proven at the policy level by
    the existing `AcceptanceRulesModelTests` / `AcceptanceGuardrailsTests` coverage of
    `EscalationClass(AgentAction, "write-adr")`, not end-to-end here.
