# Story 39-13: Assessment Family Migration — Research, Ambiguity, Clarify, DesignProposal

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As the **orchestrator running the assessment phase** of an issue,
I want Research, AmbiguityScoring, Clarify, and DesignProposal rebuilt on `DocumentLifecycleWorkflow` — producing typed `Findings`, `AmbiguityAssessment`, `Clarification`, and `Design` documents, with DesignProposal's bespoke approval gate replaced by the generic accept gate and ambiguity-above-threshold expressed as a typed unhandleable outcome routing to Clarification,
So that the entire assessment family shares one quality loop, one resume surface, and one lineage trail — and the two remaining bespoke suspend/approval implementations are retired.

## Priority

P1 — First family fan-out after the 39-12 pilot proves the stack. The assessment family is chosen second because it contains **the two workflows whose bespoke patterns the lifecycle generalized** (DesignProposal's approval gate, Clarify's suspend) — migrating them is the proof that the generalization actually covers its origins.

## Architectural Context (READ FIRST)

**The four workflows being migrated (all `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`):**
- `ResearchWorkflow.cs` → produces `Findings` (39-3: every finding cites evidence; relevance/confidence ∈ [0,1]; ranked). Parser today: `ResearchParsing` (see `ContractBindingTests` binding map).
- `AmbiguityScoringWorkflow.cs` → produces `AmbiguityAssessment` (39-3: score ∈ [0,1]; typed ambiguities; clear ⇒ empty list valid). Parser today: `AmbiguityParsing`.
- `ClarifyingQuestionsWorkflow.cs` → produces `Clarification` (39-3: Questions → Resolution; ≥1 open-ended question; resolution states the clarified requirement). Suspend activities: `apps/tamma-elsa/src/Tamma.Activities/Clarify/WaitForClarifyingAnswersActivity.cs` + `DeliverClarifyingQuestionsActivity.cs`, resumed via `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/ClarifyResumeEndpoint.cs`; fail-closed helpers in `ClarifyParsing.cs`.
- `DesignProposalWorkflow.cs` → produces `Design` (39-4: ≥1 alternative with trade-offs; recommendation references an alternative). **Its bespoke approval gate** — `apps/tamma-elsa/src/Tamma.Activities/Design/WaitForDesignApprovalActivity.cs` + `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/DesignResumeEndpoint.cs` — is the pattern 39-8 generalized; this story **replaces it with the generic accept gate** while preserving the endpoint's observable behavior through the generalized resume surface.
- Structure tests to rewrite: `tests/Tamma.Activities.Tests/Workflows/ResearchWorkflowStructureTests.cs`, `AmbiguityScoringWorkflowStructureTests.cs`, `ClarifyingQuestionsWorkflowStructureTests.cs`, `DesignProposalWorkflowStructureTests.cs`; resume behavior pinned by `tests/Tamma.Activities.Tests/Clarify/ClarifyResumeReadBackTests.cs` and `Design/DesignResumeReadBackTests.cs` (+ the endpoint tests under `tests/Tamma.Activities.Tests/Endpoints/` and `tests/Tamma.Api.Tests/Endpoints/`).

**Key semantic change — ambiguity routing:** today "ambiguity above threshold" is an inline branch decision. Under the lifecycle it becomes the **typed unhandleable outcome `AmbiguityAboveThreshold`** (part of the 39-6 outcome union) raised at the AmbiguityAssessment accept gate, which the orchestrator routes to the Clarification lifecycle — with the assessment document attached as lineage — instead of a bespoke edge between two workflow graphs. The threshold itself is acceptance-policy configuration (39-5), not a constant in the workflow.

## Acceptance Criteria

1. **Four lifecycle bindings.** Each of the four workflows is re-implemented as a `DocumentLifecycleWorkflow` binding declaring its `consumes` / `produces` document interface (`Findings`, `AmbiguityAssessment`, `Clarification`, `Design` respectively), with produce cells unchanged (`(product_owner, research)`, `(product_owner, score-ambiguity)`, `(product_owner, clarify-requirements)` + `(product_owner, incorporate-answers)`, `(architect, propose-design)` — per the existing `Prompts/{role}/{action}.md` cells). Bespoke parse branches and error terminals are deleted; `ResearchParsing`/`AmbiguityParsing`/`DesignParsing` shape knowledge moves into the document types.

2. **DesignProposal on the generic accept gate.** `WaitForDesignApprovalActivity` and the bespoke approval branch are retired; supervised-mode design acceptance suspends via 39-8's generic gate on the canonical tenant-folded bookmark. **`DesignResumeEndpoint` behavior is preserved through the generalized resume surface**: the existing route contract (payload shape, approve/reject semantics, serialization-tolerant read-back, tenant folding, response codes) keeps working — either as a thin forwarder onto the generic resume endpoint or via route aliasing — and the existing `DesignResume*` test suites pass unchanged or with mechanical-only updates.

3. **Clarify's suspend generalized.** The Clarification lifecycle's wait-for-answers step uses the generic suspend/resume machinery (39-8/39-10) with `ClarifyResumeEndpoint` behavior preserved the same way as AC2. The Questions → Resolution two-phase shape maps to lifecycle stages: Questions document produced/validated/delivered → suspend → answers resumed → Resolution produced (`incorporate-answers`) → validated → accepted. `ClarifyResumeReadBackTests`' serialization-tolerance matrix still passes.

4. **`AmbiguityAboveThreshold` typed routing.** When an accepted `AmbiguityAssessment`'s score exceeds the policy threshold, the lifecycle exits with the typed `AmbiguityAboveThreshold` outcome carrying the assessment lineage, and the orchestrator routes it into the Clarification lifecycle for the same issue. Below-threshold proceeds. A test drives both branches through the real policy read (39-5) and asserts the routing plus the absence of any bespoke inter-workflow branch edge.

5. **Events preserved alongside `DOCUMENT.*`.** Each family workflow's existing event vocabulary (the `ClarifyEvents` / `DesignEvents` constants in `apps/tamma-elsa/src/Tamma.Activities/Clarify|Design/`, and the research/ambiguity event types) continues to be emitted at equivalent transitions alongside the generic `DOCUMENT.*` family — replay tests assert both streams per workflow with matching `issueId` tags.

6. **Resumable per the standard, allowlist shrinks by four.** All four bindings declare resume behavior, pass the 39-10 structural test without allowlist entries (the allowlist loses four entries in this story), and the crash-re-entry integration test pattern passes for at least one family member with a mid-suspend crash (suspended Clarification survives restart and resumes on the right branch).

7. **Lineage complete for the assessment phase.** After a full assessment run, the 39-11 lineage query for the issue renders Issue → Findings → AmbiguityAssessment → (Clarification, when triggered) → Design with all revisions and Reviews — asserted by an integration test that runs the family end-to-end against the store.

8. **Test migration, none skipped.** The four structure-test suites are rewritten against lifecycle bindings; parser unit tests port to document-type validator tests or retire with their parsers; full `dotnet test` passes with no assessment-family test disabled.

## Technical Notes

- **Order within the story:** Research → AmbiguityScoring (pure produce+accept, no suspend) first; then Clarify (suspend mid-lifecycle); DesignProposal last (accept-gate replacement). Each lands independently green.
- **Endpoint preservation strategy.** Prefer thin forwarders over route aliasing: keep `DesignResumeEndpoint` / `ClarifyResumeEndpoint` files as adapters that validate the legacy payload then call the generic resume service — deleting them entirely is 39-8's future cleanup once dashboards migrate, not this story's job.
- **Clarification is one document, two phases** — resist modeling Questions and Resolution as two document types. The 39-3 `Clarification` type owns the phase semantics; the lifecycle suspend sits between its phases.
- **Threshold config:** the ambiguity threshold moves to the acceptance rules (39-5) with per-mode ownership per the CLAUDE.md two-scoping-models rule (single-user: user-owned; SaaS: tenant-owned). Do not leave it in `appsettings` as a global.
- The `ContractBindingTests` binding-map entries for the four cells follow the 39-12 pattern (point at document-type validators) until 39-16 replaces the mechanism.

## Dependencies

- **Blocking:** 39-12 (pilot template proven), 39-3/39-4 (the four document types), 39-5 (threshold/acceptance rules), 39-6/39-7/39-8 (lifecycle, review, gate), 39-10/39-11 (resume standard, store).
- **Existing surface preserved:** `ClarifyResumeEndpoint` / `DesignResumeEndpoint` contracts and their test suites.
- **Unblocks:** full assessment-phase lineage for dashboards; 39-14 proceeds in parallel after 39-12 (no ordering constraint between 39-13 and 39-14).

## Estimated Effort

5–7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
