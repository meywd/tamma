# Implementation Plan — Story 41-27: User-Flow & Wireframe/UI-Spec Drafting Workflow

## Scope & Deliverable

When this story is done, a new Elsa workflow `UxSpecDraftingWorkflow` (DefinitionId `ux-spec-drafting`, a
free id — verified against the 45 live ids) is a **thin binding over `DocumentLifecycleWorkflow`**,
byte-identical in shape to the landed `TaskCreationWorkflow` recipe: it reads the issue context, optionally
fetches the accepted `AcceptanceCriteria` for the issue as a lineage anchor, dispatches `document-lifecycle`
once with `documentType = "ux-spec"` and the `(ux_designer, author-ui-spec)` producer cell, and exposes the
accepted `UxSpec` plus typed `status`/`outcome`/`documentId`/`parentDocumentId` outputs. Flow/state and a11y
correctness are enforced by 41-1b's `UxSpecDocumentType.Validate` inside the lifecycle's validate→repair→
review→revise→accept rings — this binding contributes **no** parse, **no** branch on LLM output, and **no**
`Finish`. It declares `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` and passes 39-10's
`ResumableStandardStructuralTests` with no allowlist entry. `UX_SPEC.*` domain events mirror the lifecycle's
typed exits alongside the generic `DOCUMENT.*` family, and the workflow declares its
`WorkflowDocumentInterface` edge (`ux-spec-drafting`: consumes `acceptance-criteria`, produces `ux-spec`),
bumping the declared-edge pin by exactly one.

**This is the first workflow in the repository owned by a UX/design role.** Verified: the taxonomy models
eight roles and `Prompts/` holds exactly eight role directories (`architect`, `developer`, `devops`,
`product_owner`, `security`, `senior_developer`, `tech_writer`, `tester`); `grep -rn
"ux_designer\|UxDesigner\|ux-spec\|UxSpec"` over `apps/tamma-elsa/src/` returns **zero hits**. The epic
README's finding — UX/designer "aren't modelled at all" — is exact, not rhetorical: there is no role, no
action cell, no prompt file, no document type, and no prior art to copy from within the design domain. Every
piece of that surface is minted by the Wave-0 enablers this story is blocked on.

## Pre-Reading

- `docs/stories/epic-41/story-41-27/41-27-user-flow-and-wireframe-drafting.md` — the story (ACs are source of truth; see **Corrections to the story**, which supersedes its AC4 and its two-produce-cell Scope line)
- `docs/stories/epic-41/README.md` — rule 1's checkable thinness clauses (a)–(f), rule 5's resume rule, the Wave-4 gate, and the "New roles & the two role families that don't exist yet" section
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — **Wave-0 enabler #1.** Scope 1 (the `ux_designer` role + its `Prompts/ux_designer/_system.md`), Scope 2 (`draft-user-flow`, `author-ui-spec`, `review-design`, `audit-accessibility`), Scope 3 + D2 (whether `ux_designer` sits on the document-review panel), AC7's count-pin list
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md` — **Wave-0 enabler #2.** The `UxSpec` row (domain rules, producing cell `(ux_designer, author-ui-spec)`), D1 (per-type acceptance posture), D2 (**no** workflow edges in that story — this story owns its own `+1`), AC4's `Be(10)→Be(16)` / `HaveCount(10)→HaveCount(16)` pins, AC6's one-producing-cell-per-type note
- `docs/stories/epic-39/story-39-15/implementation-plan.md` — the fourth-generation binding recipe (D1's per-binding checklist, D6 event mirroring, D7 drift-gate arithmetic, D8 resume declarations)
- `docs/stories/epic-39/story-39-12/implementation-plan.md` — **the original recipe**; D2 ("no bespoke parse/branch/terminal", typed routing allowed) and **D7** (why a thin binding declares `LatestStateReEntry` and never `BookmarkSuspend`/`Both`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — **THE file to clone.** `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` at `:47`; `ReadInputs` `:92`; `ComputeReEntryPositionActivity` `:117`; `ReadPositionStage` `:130`; `FreshRun` `FlowDecision` `:148`; `FetchLatestAcceptedDocumentActivity` `:153`; the single `DispatchWorkflow("document-lifecycle")` `:167` with its nine-key `Input` dictionary `:171-196`; `ReadLifecycleExit` `:202`; the `ExposeOutput` `Sequence` terminal `:226`; the `Flowchart` `:242`
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — **THE test file to clone.** All nine assertions, notably `Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall` `:58`, `DispatchLifecycle_MaterializesCanonicalPair_PlanType_AndFeedbackCarrier` `:70`, `Workflow_HasNoFinishActivity` `:85`, `Workflow_DeclaresLatestStateReEntry` `:97`, `Workflow_HasNoBookmarkSuspendActivity` `:106`
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs` — the 39-10 gate. Clause (a) `:113`; `LegacyResumeAllowlist_HasNoStaleEntries` `:136`; **clause (b) `EveryBookmarkSuspendWorkflow_HasACanonicalSuspendNode` `:158`** (this is the clause that makes `Both` fail for a thin binding); clause (b-inverse) `:200`
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeKey.cs:23-34` — the ten current members; `Parse` throws `DOCUMENT.TYPE.UNKNOWN` `:49-59`
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:134-175` — `BuildSeed()`, the sixteen `WorkflowDocumentInterface` rows this story appends to; `WorkflowDocumentInterface` is `(WorkflowDefinitionId, Consumes, Produces, Provisional)`
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45` — `HaveCount(16)`, the edge pin this story moves to 17
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:126-133` — `For(DocumentTypeKey)`; `Plan or Review => s_panelRules`, `Design => s_humanAcceptorRules`, `_ => Rules` (single-`architect` unanimous). **A newly registered `UxSpec` silently lands on the architect row unless 41-1b gives it an arm** (41-1b D1/AC5)
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:376-387` — `GetReviewActionForRole`, a `switch` that **throws `ArgumentOutOfRangeException`** for any unlisted role; `:430-433` `GetPanelActionForRole(role, docTypeKey)` (the 39-15 doc-type-aware seam)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:1212` — `RolePhaseMap.GetReviewActionForRole(reviewerRole).ToWire()`, called **unguarded**; `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs:61` `s_documentRoster` (7 roles), `:160` `GetPanelActionForRole` delegation, `:198` `DocumentPanelRoster`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/CreationBindingHelper.cs` + `LifecycleBindingHelper.cs` — `ReadLifecycleResult` / `IsAccepted` / `BuildFailureDetail` / `DeriveIssueId` / `ScopeIssueId`, the pure Elsa-free exit adapters this story reuses
- `apps/tamma-elsa/src/Tamma.Activities/Documents/ComputeReEntryPositionActivity.cs`, `FetchLatestAcceptedDocumentActivity.cs` — the two landed store/re-entry seams
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` + `TaxonomyDriftBuildTests.cs` — the binding classification gate and `ScanLifecycleBindingDispatches` / `MaterializeDispatchInput`, which the structure test calls directly
- `docs/stories/epic-41/story-41-2/41-2-acceptance-criteria-authoring.md` — the upstream `AcceptanceCriteria` producer this binding consumes
- **All Epic 39 machinery this plan names EXISTS and compiles** — verified in tree: `DocumentLifecycleWorkflow`, `document-review`/`review-panel`/`review-single-reviewer`, `ResumeBehaviorAttribute`/`ResumeMode`, `LifecycleBookmarks`, `ComputeReEntryPositionActivity`, `FetchLatestAcceptedDocumentActivity`, `LifecycleBindingHelper`, `ResumableStandardStructuralTests`, the document store. **NOT FOUND (minted by the Wave-0 enablers, no code yet):** `AgentRole.UxDesigner`, `AgentAction.DraftUserFlow`/`AuthorUiSpec`, `Prompts/ux_designer/**` (41-1a); `DocumentTypeKey.UxSpec` + `UxSpecDocumentType` (41-1b). See **Blocks / Blocked by**.

## Corrections to the story

Three of the story's statements do not survive contact with the landed code. Each is a code-verified
correction, not a preference.

- **C1 — AC4's `[ResumeBehavior(Both)]` is wrong and would FAIL the very test AC4 requires to pass.**
  The story says: *"`[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist."* These two
  clauses are mutually exclusive for a thin binding. `ResumableStandardStructuralTests
  .EveryBookmarkSuspendWorkflow_HasACanonicalSuspendNode` (`:158-197`) fails any `BookmarkSuspend`/`Both`
  workflow that either (i) names no `SuspendActivities` (`:176-181`) or (ii) whose **built graph contains no
  node** of a declared canonical suspend type (`:190-194`). A thin binding contains no suspend node at all —
  the accept-gate bookmark is created inside the dispatched `document-lifecycle` **child** instance, which
  the parent awaits with `WaitForCompletion = true`. The clause (b-inverse) honesty test (`:200`) closes the
  other direction. The landed precedent is unambiguous: every one of the seven migrated thin bindings
  declares `LatestStateReEntry`, and `TaskCreationWorkflowStructureTests` pins **both**
  `Workflow_DeclaresLatestStateReEntry` (`:97`) and `Workflow_HasNoBookmarkSuspendActivity` (`:106`).
  39-12 D7 states the rule in words: *"the binding itself never suspends on a bookmark … so `BookmarkSuspend`
  would be dishonest."* **Corrected AC4: `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`.** The story's
  intent — a resumable, supervised accept gate — is fully preserved; the suspend simply belongs to the child.
  Epic 41 rule 5 is not violated: a thin binding *is* a run-to-completion producer from the parent's frame.

- **C2 — the Scope line's two produce cells cannot both be bound; the epic already has the pattern for this.**
  The story asks for *"Produce cells `(ux_designer, draft-user-flow)` and `(ux_designer, author-ui-spec)`,
  folded into one `UxSpec` document."* Three landed facts forbid it. (i) Thinness clause (a) allows **exactly
  one** `DispatchWorkflow`, and clause (e) requires it to materialize **one** canonical `(role, action)` pair
  — pinned by `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches`. (ii) `IDocumentType.RenderContract()`
  is per document **type**, not per producing cell: `ReviewDocumentType` returns one `Contract` const
  (`ReviewDocumentType.cs:160`) for all nine of its producing cells, and `Plan.cs` does the same for its two —
  the hazard 41-1b AC6 already names. (iii) 41-1b AC6 therefore states outright that *"each of the six
  declares exactly one producing cell."* **Resolution (D2): bind `(ux_designer, author-ui-spec)` as THE
  producer; `draft-user-flow` becomes a required *section* of the one `UxSpec`** — the identical treatment
  Epic 41 already applies to 41-10, where *"the three `design-*` facet cells stay unbound and become sections
  of the one `Design`"* (README `:171`). `draft-user-flow` is classified `IntentionallyUnbound`.

- **C3 — "Dependencies → Blocking: 41-1a and 41-1b" understates 41-1a.** The story names 41-1a for the role
  and the two produce cells. It also needs 41-1a's **Scope 3 selector work**: `DocumentLifecycleWorkflow.cs:1212`
  calls `RolePhaseMap.GetReviewActionForRole(reviewerRole)` **unguarded**, and that switch
  (`RolePhaseMap.cs:376-387`) throws for any role outside its seven arms. If the `UxSpec` acceptance rules
  name `ux_designer` as a reviewer — which is the only sensible reviewer for a UX spec — the lifecycle's
  REVIEW stage throws at runtime, exactly as it does for `tech_writer` today (41-1a AC3). So 41-1a's **D2**
  (panel membership for the three new roles) is a hard prerequisite of this story's review stage, not an
  adjacent nicety. Recorded in D6 and in Blocks / Blocked by.

## Design Decisions

- **D1 — `UxSpecDraftingWorkflow`, DefinitionId `ux-spec-drafting`, cloned from `TaskCreationWorkflow`
  node-for-node.** The id is free (checked against all 45 `builder.DefinitionId` literals in
  `Tamma.ElsaServer/Workflows/`). Nothing dispatches it yet — 41-29's `design` `TaskKind` row is
  human-assigned until this lands, and 41-29 needs no change when it does (its `kind→workflow` map is data,
  and the story is explicitly "no router change required"). Graph, in order: `ReadInputs` →
  `ComputeReEntryPosition` → `ReadPositionStage` → `FreshRun` `FlowDecision` → `FetchAcceptanceCriteria` →
  `DispatchLifecycle` → `ReadLifecycleExit` → `ExposeOutput`. Exactly one `FlowDecision` (`FreshRun`); zero
  `Finish`; zero `DispatchWorkflow("llm-call")`; zero `Wait*` nodes; every leaf inside the single
  `ExposeOutput` region.

- **D2 — one produce cell, `(ux_designer, author-ui-spec)`; `draft-user-flow` is a section, not a binding.**
  Per C2. The producer prompt (`Prompts/ux_designer/author-ui-spec.md`, minted by 41-1a) must therefore
  instruct **both** halves — the user flows *and* the screen/state spec — because the `UxSpec` contract block
  rendered by `UxSpecDocumentType.RenderContract()` is the only contract the model sees, and it is shared. The
  flow-completeness rule 41-1b names (*"every flow has entry + success + error states"*) is executable
  validation in `UxSpecDocumentType.Validate`, so a spec that drafts the wireframe and skips the flows fails
  validation and enters the repair ring — the section is enforced, not merely requested.

- **D3 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` with the `ComputeReEntryPositionActivity` node.**
  Per C1. Clause (c) of the 39-10 gate requires the re-entry node for this mode, and the node's
  `PositionJson` output is exactly what `FreshRun` consumes: on a non-`produce` position the binding skips
  the `AcceptanceCriteria` fetch and the `UX_SPEC.STARTED` emission, and the child lifecycle performs its own
  skip-produce / resume-at-review / short-circuit-accepted re-entry. No `LegacyResumeAllowlist` entry is ever
  added — a new workflow that declares from birth never enters the ratchet.

- **D4 — the issue id is *not* producer-scoped, deliberately, and the contrast is the reason.**
  `TaskCreationWorkflow` scopes its lifecycle anchor to `{issueId}#task-creation` (`:112`, `ProducerScope`
  `:51`) **only because** `plan-generation` and `task-creation` both produce documentType `plan` for one
  issue, making `GetLatestAcceptedAsync(issueId, "plan")` ambiguous (39-15 D2, filed to 39-11). `ux-spec` has
  exactly one producer, so the plain `issueId` is unambiguous and the scoping hack must **not** be copied —
  copying it would break 41-28's and `plan-generation`'s ability to read the accepted spec by
  `(issueId, "ux-spec")`. A structure test pins the absence of a `ProducerScope` constant so a future
  copy-paste of the TaskCreation recipe does not silently reintroduce it.

- **D5 — `AcceptanceCriteria` is consumed optionally, through the store, folded into a DECLARED variable.**
  `FetchLatestAcceptedDocumentActivity(issueId, "acceptance-criteria", tenantId)` runs on the fresh-run
  branch only; `Found == false` is a normal path (41-2 may not have run) and the binding proceeds with an
  empty carrier — AC2's *"consumes `AcceptanceCriteria` when present"* is a conditional, not a prerequisite.
  The fetched body is folded into a variable the `author-ui-spec` template **declares**, never a new key —
  the render-drop lesson that 39-15 D2 and `ValidationFeedbackHelper` exist to teach. When found, its
  `documentId` becomes the dispatch's `parentDocumentId`, giving AC2's traceability a persisted edge rather
  than a prose claim. `feedbackVariableName` is set to that same declared carrier so repair/revise notes land
  where the template reads them.

- **D6 — the acceptance posture is chosen here and implemented in 41-1b, and it forces 41-1a's D2.**
  `AcceptanceDefaults.For` (`:126-133`) ends in `_ => Rules` — a single-`architect`, unanimous row. A newly
  registered `UxSpec` **compiles and runs** on it, silently making an architect the sole reviewer and acceptor
  of a UX spec. That is the trap 41-1b D1 was written to catch. This story declares the row it needs:
  reviewer `ux_designer` (+ `product_owner` for scope), acceptor per autonomy (human at 70–84, self-accept at
  85–100 outside an always-escalate class). Two consequences: (i) 41-1b must add a `DocumentTypeKey.UxSpec`
  arm to `AcceptanceDefaults.For` and pin it (its AC5); (ii) because the resolved reviewer is `ux_designer`,
  `RolePhaseMap.GetReviewActionForRole(UxDesigner)` **must not throw** — 41-1a D2 must put `ux_designer` on
  the document-review panel with a named arm (its own default position already says so) and extend
  `ReviewerSelectionHelper.s_documentRoster` accordingly. Both are lockstep items, not assumptions.

- **D7 — `UX_SPEC.*` mirrors lifecycle exits, gated on re-entry position; a new constants file, no generic
  events authored.** New `apps/tamma-elsa/src/Tamma.Activities/UxSpec/UxSpecEvents.cs` with
  `UX_SPEC.STARTED` / `UX_SPEC.DRAFTED` / `UX_SPEC.ACCEPTED` / `UX_SPEC.FAILED`, emitted by the binding at the
  equivalent transitions (39-13 D8 / 39-15 D6 pattern): `STARTED` before dispatch on a fresh run only;
  `DRAFTED` + `ACCEPTED` on an `accepted` exit; `FAILED` on `rejected`/`escalated` with the typed outcome wire
  in `detail`. The generic `DOCUMENT.*` / `APPROVAL.*` / `ESCALATION.*` families are emitted by the 39-6/39-8
  machinery **inside** the child instance — this story authors none of them. The story's event list omits a
  failure member; adding `UX_SPEC.FAILED` keeps the family honest (a typed escalation must be loud), matching
  `TRIAGE.CONTEXT.FAILED` / `DECOMPOSITION.FAILED`. Tags: `issueId`, `repository`, `tenantId`,
  `correlationId`.

- **D8 — this story owns exactly one `WorkflowDocumentInterface` edge and one pin bump.** Append
  `new WorkflowDocumentInterface("ux-spec-drafting", new[] { DocumentTypeKey.AcceptanceCriteria },
  DocumentTypeKey.UxSpec, false)` to `BuildSeed()` and move
  `WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)` → `HaveCount(17)` with a one-line reason in the comment.
  `Provisional: false` — the edge is backed by a real binding on day one, per the 39-12 D9 / 39-15 convention.
  Epic 41 rule 1 clause (f) makes this per-story bump mandatory, and 41-1b D2/AC7 explicitly declines to move
  this pin, so there is no double-counting. Do **not** touch 41-1b's two vocabulary pins
  (`DocumentTypeKeyTests.cs:20`, `DocumentTypeRegistryTests.cs:37`) — they are that story's.

- **D9 — drift-gate classification is part of the story, not follow-up.** `ContractBindingTests`: add
  `(ux_designer, author-ui-spec)` to `Bindings` with parser authority `"UxSpecDocumentType.Validate"`; add
  `(ux_designer, draft-user-flow)` to `IntentionallyUnbound` justified as a **facet/prose** cell folded into
  the `author-ui-spec` producer (D2) — 39-15 D7's universal pin requires every allowlist justification to be
  tagged `prose` or `code`, so the justification wording is load-bearing. If 41-1a's D2 places `ux_designer`
  on the review roster, `ReviewerSelectionHelper.AllDispatchablePairs` grows and 41-1a AC9 requires the new
  reviewer pair to be classified — that classification belongs to **41-1a**, not here; this story only
  verifies the gate is green after both land.

- **D10 — the binding validates nothing.** Flow entry/success/error states, per-screen a11y requirements and
  the criteria mapping are all `UxSpecDocumentType.Validate` rules owned by 41-1b (AC2 there requires one
  rejecting and one accepting fixture per rule, asserting the violation code). AC1 of *this* story is
  satisfied by delegation plus a structure test proving no validation logic exists in the binding — the
  charter of the whole migration.

## Implementation Steps

1. **Precondition gate (no code).** Verify compiling in tree: `AgentRole.UxDesigner` +
   `AgentAction.AuthorUiSpec` / `DraftUserFlow` + `Prompts/ux_designer/{_system,author-ui-spec,draft-user-flow}.md`
   (41-1a); `DocumentTypeKey.UxSpec` + `UxSpecDocumentType` registered in `DocumentTypeRegistry.s_registrations`
   + the `AcceptanceDefaults.For` arm from D6 (41-1b); and `RolePhaseMap.GetReviewActionForRole(UxDesigner)`
   returning without throwing (41-1a D2). Any gap blocks the step below that consumes it — **file it against
   the enabler owner, never work around it** (a local shim would fork the taxonomy this epic exists to unify).

2. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/UxSpecBindingHelper.cs`** — pure,
   Elsa-free, in the `CreationBindingHelper` posture (total functions, fail-closed, never throws out of a
   routing lambda). Reuses `LifecycleBindingHelper.ReadLifecycleResult` / `IsAccepted`:

   ```csharp
   namespace Tamma.ElsaServer.Workflows.Helpers;
   public static class UxSpecBindingHelper
   {
       public static string ProjectUxSpecJson(string documentJson);   // accepted body; "{}" fail-closed
       public static int CountFlows(string documentJson);             // 0 on unreadable — UX_SPEC.DRAFTED data
       public static int CountA11yRequirements(string documentJson);  // 0 on unreadable
       public static string BuildFailureDetail(LifecycleExit exit);   // names status + typed outcome wire
   }
   ```

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/UxSpec/UxSpecEvents.cs` + `EmitUxSpecEventActivity.cs`**
   (D7) — constants + the emit activity, copied from the `DecompositionEvents` / `EmitDecompositionEventActivity`
   pair. Four constants, `StatusForEvent` mapping, `detail`/`flowCount`/`a11yCount` data members.

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/UxSpecDraftingWorkflow.cs`** — the binding (D1),
   cloning `TaskCreationWorkflow` node-for-node. Class carries
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. Inputs: `issueId`, `issueTitle`, `repository`,
   `issueNumber`, `workItemJson`, `tenantId`, optional `acceptanceRulesJson`. The one dispatch:

   ```csharp
   ["documentType"]          = "ux-spec",
   ["producerRole"]          = AgentRole.UxDesigner.ToWire(),
   ["producerAction"]        = AgentAction.AuthorUiSpec.ToWire(),
   ["producerVariablesJson"] = /* {workItemJson, <declared a11y/criteria carrier>, repository} */,
   ["feedbackVariableName"]  = "<the declared carrier from D5 — read it off the template, do not invent it>",
   ["issueId"] / ["correlationId"] / ["tenantId"] / ["acceptanceRulesJson"],
   ["parentDocumentId"]      = <accepted AcceptanceCriteria documentId, or "">,
   ```

   Outputs: `uxSpecJson`, `status`, `outcome`, `documentId`, `parentDocumentId`, `error`. **No `Finish`, no
   `llm-call` dispatch, no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variable.**

5. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (`BuildSeed()`, after the
   `test-case-creation` row) — the D8 edge; **MODIFY
   `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`** — `HaveCount(16)` →
   `HaveCount(17)` with the reason comment.

6. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`** — D9's two
   entries. Re-run `TaxonomyDriftBuildTests` and consciously recount `MinExpectedDispatchPairs` if the new
   lifecycle-binding dispatch moves the census; add `UxSpecDraftingWorkflow` to
   `ExpectedContributingWorkflows` with a one-line comment.

7. **CREATE the test suites** (see Test Plan): `UxSpecDraftingWorkflowStructureTests.cs`,
   `UxSpecBindingHelperTests.cs`, and the execution scenarios appended to the shared Testcontainers fixture.

8. **Finish:** full `dotnet test`; `dotnet ef migrations has-pending-model-changes` must stay clean (this
   story adds no schema — documents persist through 39-11's existing `document_instances`); grep for
   `[Ignore]`/`Skip` in the new suites must be empty.

## Data & Migrations

None. The `UxSpec` payload persists as a `DocumentInstance` row through 39-11's landed store (its migration,
not this story's); `UX_SPEC.*` and `DOCUMENT.*` ride the existing drain → `EventRepository` → `domain_events`
path. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new constants, `Tamma.Activities/UxSpec/UxSpecEvents.cs`, D7):** `UX_SPEC.STARTED` (fresh runs
  only), `UX_SPEC.DRAFTED` (data `flowCount`, `a11yCount`, `documentId`), `UX_SPEC.ACCEPTED`,
  `UX_SPEC.FAILED` (data `detail` = the typed outcome wire). Tags `issueId`, `repository`, `tenantId`,
  `correlationId`.
- **Emitted by the machinery this binding wires in (asserted, not authored):** `DOCUMENT.PRODUCED/VALIDATED/
  REVIEW_REQUESTED/REVIEWED/REVISION_STARTED/ACCEPTED/REJECTED/ESCALATED/REENTERED`,
  `APPROVAL.REQUESTED/PROVIDED`, `ESCALATION.TRIGGERED`.
- **Consumes:** none at runtime; the re-entry position read is a store + event-query seam owned by 39-10.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers where marked), under `apps/tamma-elsa/tests/`.

- **`UxSpecDraftingWorkflowStructureTests`** (clone of `TaskCreationWorkflowStructureTests`) — builds;
  `DefinitionId == "ux-spec-drafting"`; threads `TenantId`; **no** `ValidationErrors`/`RetryCount`/
  `MaxRetries`/`UxSpecValid` variables; exactly one `DispatchWorkflow` whose literal id is
  `document-lifecycle` and zero targeting `llm-call`; `ScanLifecycleBindingDispatches` contains
  `(UxSpecDraftingWorkflow, DispatchLifecycle, ux_designer, author-ui-spec)` and
  `MaterializeDispatchInput` yields `documentType == "ux-spec"` plus the declared `feedbackVariableName`;
  **zero `Finish`**; `ComputeReEntryPositionActivity` and `FetchLatestAcceptedDocumentActivity` each present
  exactly once; `[ResumeBehavior(LatestStateReEntry)]` pinned; **zero `Wait*` nodes**; **no `ProducerScope`
  constant** (D4). **Covers AC1 (structure half), AC2 (consumption half), corrected AC4 (declaration half).**
- **`UxSpecBindingHelperTests`** — projections fail-closed on garbage (`"{}"`, `0`); `BuildFailureDetail`
  names every reachable outcome wire; a `UxSpec` serialized through `DocumentJson.Options` round-trips to the
  shape 41-28 and `plan-generation` read (the downstream-consumer pin). **Covers AC1, AC3 (shape half).**
- **`ResumableStandardStructuralTests`** — green with **no** `LegacyResumeAllowlist` entry for
  `UxSpecDraftingWorkflow`; clause (a) satisfied by the declaration, clause (c) by the re-entry node, clause
  (b-inverse) trivially (no canonical suspend node). **This suite is the executable proof of corrected AC4**
  — and a deliberate negative test asserts that flipping the declaration to `Both` makes clause (b) fail, so
  C1 cannot be silently reverted. **Covers corrected AC4.**
- **Drift gates post-edit** — `ContractBindingTests` green with D9's classification and no unclassified pair;
  `WorkflowInterfaceGraphTests` green at 17; `TaxonomyDriftBuildTests` discovers the new pair through the
  lifecycle-binding walk. **Covers AC1 (gate half).**
- **`UxSpecLifecycleExecutionTests`** (Testcontainers, on the shared 39-6/39-12 fixture: real
  `UxSpecDraftingWorkflow` + `DocumentLifecycleWorkflow`, stub `llm-call`, stub-or-real `document-review`,
  real Elsa EF persistence + event drain + `IDocumentInstanceRepository`, decisions injected via
  `DocumentDecisionResumeEndpoint.Resume`; 39-9 repair ring OFF) —
  (a) **happy path**: seeded accepted `AcceptanceCriteria` → scripted valid `UxSpec` → review approve →
  accept resume → outputs `status=completed`, accepted instance readable by `(issueId, "ux-spec")` **without**
  a producer filter (D4), `parentDocumentId` lineage = the criteria document (AC2, AC3);
  (b) **repair ring**: scripted spec with a flow missing its error state → `UxSpecDocumentType` violation →
  repair turn → valid → accepted; `UX_SPEC.STARTED` emitted exactly once (AC1);
  (c) **no criteria present**: `Found == false` → run completes, `parentDocumentId` empty, spec accepted
  (AC2's "when present" is conditional);
  (d) **validation exhaustion**: always-invalid stub → typed `ValidationExhausted` escalation with lineage +
  `UX_SPEC.FAILED` naming the outcome; no error terminal reached (AC1);
  (e) **supervised accept + crash re-entry**: instance suspended on the child's canonical tenant-folded
  bookmark, wrong-tenant resume 404s, correct resume completes; then kill the host mid-review (39-10 D8
  shape), fresh dispatch for the same issue re-enters at review of the same revision with no second
  `DOCUMENT.PRODUCED.*` and exactly one `UX_SPEC.ACCEPTED` on the whole stream (corrected AC4);
  (f) **downstream read**: a `plan-generation`-shaped reader and a 41-28-shaped reader both resolve the
  accepted spec through the 39-11 store (AC3).
  **Covers AC1, AC2, AC3, corrected AC4 (runtime halves).**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; `UxSpec` validated (flow states, a11y per screen, criteria mapping) | 2, 4 (D1/D2/D10) | `UxSpecDraftingWorkflowStructureTests` (one dispatch, zero llm-call, zero `Finish`, no plumbing vars); ExecutionTests (b)(d); 41-1b's own type tests own the rule fixtures |
| 2 — consumes `AcceptanceCriteria` when present; spec traces to criteria | 4 (D5) | StructureTests (fetch node present); ExecutionTests (a) lineage assert, (c) absent-criteria path |
| 3 — consumable by `plan-generation`/41-28 via 39-11 | 4 (D4), 5 (D8) | `UxSpecBindingHelperTests` shape pin; ExecutionTests (a)(f); `WorkflowInterfaceGraphTests` at 17 |
| 4 — **corrected**: `[ResumeBehavior(LatestStateReEntry)]`; 39-10 gate green with no allowlist entry | 4 (D3) | `ResumableStandardStructuralTests` + its negative `Both`-fails test; StructureTests attribute + no-`Wait*` pins; ExecutionTests (e) |

## Blocks / Blocked by

- **Blocked by — 41-1a (hard, unlanded).** `AgentRole.UxDesigner`; `AgentAction.AuthorUiSpec` +
  `AgentAction.DraftUserFlow`; `Prompts/ux_designer/_system.md` + `author-ui-spec.md` + `draft-user-flow.md`
  (`PromptFileLoader` refuses to start on a taxonomy cell with no file — 41-1a AC8); **and its Scope 3 / D2
  selector decision**, because `DocumentLifecycleWorkflow.cs:1212` calls `GetReviewActionForRole` unguarded
  and it throws for any unlisted role (C3, D6). Steps 1, 4 and the review stage of every ExecutionTest are
  blocked on this.
- **Blocked by — 41-1b (hard, unlanded).** `DocumentTypeKey.UxSpec` + `UxSpecDocumentType` +
  its `DocumentTypeRegistry` registration; without them `DocumentTypeKeyExtensions.Parse("ux-spec")` throws
  `DOCUMENT.TYPE.UNKNOWN` (`DocumentTypeKey.cs:49-59`) and `DocumentTypeRegistry.Resolve` throws
  `DOCUMENT.TYPE.NOT_REGISTERED` — so the document is unpersistable on the **human** path too, not just the
  agent path. Also owes the `AcceptanceDefaults.For(UxSpec)` arm from D6 (its AC5) and the two vocabulary
  count pins (its AC4).
- **Blocked by — Epic 39 (satisfied).** 39-2/39-4 pattern, 39-5 rules, 39-6 lifecycle, 39-7 review producers,
  39-8 accept gate + resume endpoint, 39-10 resume standard + structural test, 39-11 store, and 39-12→39-15's
  binding recipe are **all landed and verified in tree**. This story adds no Epic 39 hooks.
- **Not blocked by — 41-29.** The router's `kind→workflow` map is data; `design` is human-assigned until this
  lands and needs no router change afterwards.
- **Not blocked by — Epic 42.** Unlike 41-28, this story's produce step needs no tool at all: it authors a
  structured document from issue context. It is reachable end-to-end the day Wave 0 clears.
- **Degraded until 39-17 / 39-19 / 39-20.** Per the epic README's Dependencies table, the accept gate
  publishes and suspends but nothing decides (39-17), there is no Task View for a human assignee (39-19), and
  `ITaskAudienceResolver` is stubbed fail-closed to the initiator (39-20). This story claims the **workflow +
  document + persistence + resume** half; the routing half is unreachable until those land. Its ACs are
  written so none of them depends on 39-17/19/20 — deliberately, unlike 41-6/41-7/41-8.
- **Blocks — 41-28.** Its `ReviewSubject { kind: "document", documentType: "ux-spec" }` needs an accepted
  `UxSpec` to point at, and `ReviewDocumentType.ValidateSubject` (`:124-132`) requires both a `documentId` and
  a `documentType` that `DocumentTypeKeyExtensions.TryParse` accepts.
- **Blocks — 41-2 / 41-15 traceability** (the spec's a11y + state requirements become acceptance criteria),
  `plan-generation` consumption, and 41-29's `design` `TaskKind` agent path.

## Risks & Mitigations

- **Both Wave-0 enablers are unlanded, and this story is at the far end of both chains.** Largest schedule
  risk. Mitigation: steps 2, 3 and the helper tests depend only on landed Epic 39 types and can be built
  early; every consumed enabler name (`UxDesigner`, `AuthorUiSpec`, `UxSpec`) is pinned in the enabler
  stories, so drift is a mechanical rename; step 1 is a real gate, not a formality.
- **The silent-architect-acceptor trap (D6).** `AcceptanceDefaults.For`'s `_ => Rules` catch-all means a
  `UxSpec` registered without an arm **compiles, runs, and quietly routes UX acceptance to an architect**. No
  test fails. Mitigation: D6 names the row; 41-1b AC5 pins it per type; this story's ExecutionTests assert the
  resolved reviewer/acceptor explicitly rather than accepting whatever the default yields.
- **41-1a D2 has a blast radius wider than this story.** `ReviewerSelectionHelper.s_documentRoster` is
  **global**: adding `ux_designer` to it changes the majority panel for `plan` and `review` documents too
  (`AcceptanceDefaults.For` sends both to `s_panelRules`), moving quorum arithmetic and the
  `ReviewerSelectionHelperTests.cs:97` / `ContractBindingTests.cs:598` roster pins. Mitigation: flagged to
  41-1a as a cross-cutting consequence; this story needs only that `GetReviewActionForRole(UxDesigner)` not
  throw, which is satisfiable by the map arm alone if 41-1a chooses to keep the roster at eight.
- **Two produce cells collapsing to one loses the "user flow" emphasis (C2/D2).** Mitigation: the flow
  section is not a prompt suggestion but an executable `UxSpecDocumentType` rule (41-1b: entry + success +
  error states per flow), so a spec that skips it fails validation and repairs. The 41-10 `design-*` facet
  precedent means this is the epic's established answer, not an improvisation.
- **`draft-user-flow` becomes an unbound cell whose file must still exist.** `PromptFileLoader` is fail-loud
  in **both** directions (a taxonomy cell with no file, and a file outside the taxonomy). Mitigation: 41-1a
  ships the file; D9 classifies the pair `IntentionallyUnbound` with a `prose`/facet justification so 39-15
  D7's universal pin stays green.
- **Story-vs-canon tensions:** the three in **Corrections to the story**, all resolved with the story's
  intent preserved. No design decision here deviates from a requirement — only from a stated mechanism the
  code cannot support.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition gate against 41-1a/41-1b | 0.25 |
| 2 | `UxSpecBindingHelper` (pure exit/projection adapters) | 0.5 |
| 3 | `UxSpecEvents` + `EmitUxSpecEventActivity` | 0.5 |
| 4 | `UxSpecDraftingWorkflow` binding (TaskCreation clone + criteria fetch + event mirrors) | 1.0 |
| 5–6 | Interface edge + pin bump; `ContractBindingTests` / `TaxonomyDriftBuildTests` classification | 0.5 |
| 7 | Structure suite + helper suite + Testcontainers scenarios (a)–(f) | 1.5 |
| 8 | Full green, D6 lockstep with 41-1b, D2/C3 lockstep with 41-1a | 0.25 |
| **Total** | | **4.5** (story estimate: 4–5 days) |

The figure holds **only** if 41-1a and 41-1b have landed. Neither enabler's effort (4–5 d and 5–6 d) is
included here, and neither is this story's to spend.
