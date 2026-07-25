# Implementation Plan — Story 41-28: Design Review & Accessibility Audit Workflow

## Scope & Deliverable

When this story is done, a new Elsa workflow `DesignReviewWorkflow` (DefinitionId `design-review`, a free
kebab-case id — verified against all 45 live ids) is a **thin binding over `DocumentLifecycleWorkflow`**
producing a typed `Review` whose subject is an accepted `UxSpec` (41-27) or a diff: it fetches the reviewed
artifact through the 39-11 store, dispatches `document-lifecycle` once with `documentType = "review"` and the
`(ux_designer, review-design)` producer cell, and exposes the accepted `Review` plus typed
`status`/`outcome`/`documentId`/`parentDocumentId`. Usability and accessibility findings ride the **one**
landed `Review` schema — `ReviewIssue { severity, category, description, suggestedFix, file?, line? }` — with
a11y findings carrying an `a11y` category namespaced to the configured standard (WCAG by default). The
"blocking a11y issues cannot be laundered into approval" invariant is **already enforced by landed code**
(`ReviewDocumentType.APPROVE_WITH_BLOCKING_ISSUES`); this story's job is to make the a11y lens *reach* it by
emitting `severity: critical`, and to pin that with fixtures. The workflow declares
`[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, passes 39-10's `ResumableStandardStructuralTests` with no
allowlist entry, emits `DESIGN_REVIEW.*` alongside `DOCUMENT.*`, and declares its `WorkflowDocumentInterface`
edge (`design-review`: consumes `ux-spec`, produces `review`).

**The shipped-UI half of the story is explicitly NOT delivered** and has no owner even after Epic 42 — see
**Corrections to the story** C4. This story delivers the `UxSpec`/diff half; auditing a running interface
stays human-assigned (Epic 41 rule 4).

Like 41-27, this is greenfield in the design domain: `grep -rn "ux_designer\|UxDesigner\|UxSpec"` over
`apps/tamma-elsa/src/` returns **zero hits**, `Prompts/` holds exactly the eight legacy role directories, and
the only occurrence of "accessibility" anywhere in the C# tree is one line of prose inside
`Tamma.Api/Services/Conventions/ConventionTemplates.cs:152`. The epic README's finding — UX/designer was not
modelled as a role at all before Epic 41 — is literal.

## Pre-Reading

- `docs/stories/epic-41/story-41-28/41-28-design-review-and-accessibility-audit.md` — the story (ACs are source of truth; **Corrections to the story** supersedes its AC4, its two-produce-cell Scope line, and its Dependencies)
- `docs/stories/epic-41/story-41-27/implementation-plan.md` — **the sibling plan; read it first.** C1/C2 there are the same corrections, argued once; D1–D5 are the shared recipe this plan does not re-derive
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f), rule 4 (human-or-agent), rule 5, the Epic 42 dependency table (`41-28 audit of a shipped UI → browser/render capability → no executor`)
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — Scope 2 (`review-design`, `audit-accessibility`), **Scope 3 + D1/D2** (the selector maps; D2 decides `ux_designer`'s panel membership), AC4 (panel membership asserted in both directions), AC7 (the count-pin list), AC9 (newly dispatchable reviewer pairs must be classified)
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md` — the `UxSpec` row; needed here for the **review subject**, not for the produced document (see C3)
- **`apps/tamma-elsa/src/Tamma.Core/Documents/Types/Review.cs`** — the whole file matters. `ReviewDecision` `:18-23`; `ReviewSeverity` `:36-42`; **`ReviewSeverityExtensions.IsBlocking` `:108` — `severity == ReviewSeverity.Critical`, and nothing else**; `ReviewSubject` `:153-161` (the closed `document` | `diff` union); `ReviewIssue` `:167-173`; `Review` `:182-296`; `AggregatedFrom` `:194`
- **`apps/tamma-elsa/src/Tamma.Core/Documents/Types/ReviewDocumentType.cs`** — violation codes `:17-38`; `Validate` `:44-112`; **the flagship rule `:89-98`**; `ValidateSubject` `:114-148` (kind `document` requires `documentId` **and** a `DocumentTypeKeyExtensions.TryParse`-able `documentType`, `:124-132`); `IssueMissingCategory` `:29`/`:78-80`; `IssueMissingFix` `:32`/`:82-86`; **the SHARED `Contract` const `:160-180`** and the comment `:154-159` recording that it serves every review-producing cell
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:128-133` — `For`: **`Plan or Review => s_panelRules`** (the 7-role majority panel, roster `:60-69`), `Design => s_humanAcceptorRules`, `_ => Rules`
- **`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:1200-1218`** — `BuildReviewEnvelope`, with the `RolePhaseMap.GetReviewActionForRole(reviewerRole)` call at **`:1212`** (unguarded). *Note the line drift: the epic README and 41-1a both cite `:1199`; the call is at `:1212` in the current tree.*
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:376-387` (`GetReviewActionForRole`, 7 arms + throw), `:404-412` (`GetTriageActionForRole`, 4 arms + throw), **`:430-433` (`GetPanelActionForRole(role, docTypeKey)` — the 39-15 composing seam)**, `:436` (`TriageDecisionDocTypeKey`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs` — **`:12` records that `RolePhaseMap`'s surface is deliberately frozen ("AC9 freezes that file"), so a new lens is added by *composing* `GetPanelActionForRole`, never by a third map**; `s_documentRoster` `:61-70` (7 roles); `ResolveDocumentAction` `:153-168` (delegates to `GetPanelActionForRole` at `:160`); `AllDispatchablePairs` `:178` / `BuildAllPairs` `:180-193` (7 + 5 + 4 = 16)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentReviewWorkflow.cs` — the review router; `:36` `document-review`, `:88` panel-vs-single gate, `:114-132` dispatch to `review-panel`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` + `tests/.../TaskCreationWorkflowStructureTests.cs` — the binding recipe and its ten assertions (see the 41-27 plan's Pre-Reading for the node-by-node map)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs` — clause (b) `:158-198` (why `Both` fails), clause (c) `:240-261`, and **the universal pin `:265-290`: a workflow that dispatches a document-lifecycle binding may not be allowlisted**
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `Bindings` `:82-254` (16 entries), `IntentionallyUnbound` `:286-354` (17), **`ReviewProducerDispatchablePairs` `:505-544` (11)**, **the roster pin `:598` `HaveCount(16)`**, `UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual` `:626`, `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode` `:655`, `EveryReviewProducerDispatchablePair_IsClassified` `:547`
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45` (`HaveCount(16)`) and **`:96-133`** (`Seeded_declarations_are_provisional_except_reconciled_bindings`, whose `reconciled` array `:102-123` a new non-provisional edge must join)
- **Verified absent from the entire C# tree:** any Playwright / Puppeteer / Selenium / chromium / headless / screenshot / render capability, and any seventh `IToolExecutor`. The six registered executors are `file_read`, `file_write`, `search_code`, `shell_execute`, `run_tests`, `git_operations` (`Tamma.Api/Program.cs:753-764`). **NOT FOUND (Wave-0 enablers):** `AgentRole.UxDesigner`, `AgentAction.ReviewDesign`/`AuditAccessibility`, `Prompts/ux_designer/**` (41-1a); `DocumentTypeKey.UxSpec` (41-1b); an accepted `UxSpec` to review (41-27).

## Corrections to the story

- **C1 — AC4's `[ResumeBehavior(Both)]` is wrong and self-defeating.** Identical to 41-27's C1 and argued
  there in full: `ResumableStandardStructuralTests.EveryBookmarkSuspendWorkflow_HasACanonicalSuspendNode`
  (`:158-198`) fails any `Both`-declaring workflow whose built graph holds no node from
  `LifecycleBookmarks.CanonicalSuspendActivities` (which has exactly two entries —
  `WaitForDocumentDecisionActivity`, `WaitForDocumentInputActivity`). A thin binding holds neither; the accept
  gate suspends inside the dispatched child. **Corrected AC4:
  `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` + a `ComputeReEntryPositionActivity` node**, and no
  allowlist entry — which the universal pin at `:265-290` makes mandatory anyway, since a workflow that
  dispatches a document-lifecycle binding may not be allowlisted.

- **C2 — "two produce cells as lenses aggregating into one `Review`" is not expressible as written, and the
  role→lens map is one-to-one.** The story asks for `(ux_designer, review-design)` **and**
  `(ux_designer, audit-accessibility)` as lenses. Two independent landed constraints forbid the literal
  reading: (i) a thin binding has exactly one `DispatchWorkflow` materializing exactly one `(role, action)`
  pair (thinness clauses (a)/(e), pinned by `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches`); and
  (ii) the panel lens selectors are keyed **by role**, one action per role —
  `GetReviewActionForRole` (`:376-387`) and `GetTriageActionForRole` (`:404-412`) are single-arm-per-role
  `switch` expressions, so `ux_designer` cannot have two lenses in the same panel. Resolution in **D3**: both
  cells stay live, each in the mechanism that fits it — `review-design` is the standalone binding's producer,
  `audit-accessibility` becomes the **`ux-spec`-specific panel lens** via a new arm on the doc-type-aware
  `GetPanelActionForRole` (`:430-433`), which is precisely the composing seam 39-15 added and which
  `ReviewerSelectionHelper.cs:12` says is the sanctioned way to add a lens without touching the frozen map.

- **C3 — the story's Dependencies omit 41-1b, and 41-1b is a hard blocker here too.** The story lists only
  41-1a plus Epic 39. But `ReviewDocumentType.ValidateSubject` (`:124-132`) requires a `document`-kind subject
  to carry a `documentType` that `DocumentTypeKeyExtensions.TryParse` accepts. Until 41-1b registers
  `ux-spec`, **every `Review` whose subject is a `UxSpec` fails validation with `SUBJECT_INCOMPLETE`** — on
  the human path as much as the agent path. 41-28 is blocked on 41-1b for its *subject*, independently of
  41-27 producing the artifact.

- **C4 — the shipped-UI half has no owner, in this epic or the next.** The story's Epic 42 caveat is correct
  but incomplete. Verified: there is no browser/render/screenshot executor in the C# tree, and Epic 42's five
  tool families are cloud/VPS (42-7), feature flags (42-8A), deploy control (42-8B), authenticated HTTP
  (42-9) and MCP (42-6) — **none of them renders a page**. So "human-assigned *until Epic 42 supplies one*"
  overstates the roadmap: Epic 42 does not supply one, and the only plausible future path is an external MCP
  server exposing a browser tool (42-6 Part B). There is a **second, independent** reason the shipped-UI half
  is unbuildable that the story does not mention: `ReviewSubject.Kind` is a **closed two-kind union**
  (`document` | `diff`, `ReviewDocumentType.cs:122-147`), and a running interface is neither — a `diff`
  subject requires a `repository` plus a `prNumber` or `commitSha`. So even with a browser tool, the shipped
  UI could not be *named* as a review subject without a third subject kind, which is a change to 39-4's
  keystone type. **Corrected posture: the shipped-UI half is out of scope, permanently as far as this plan
  can see, and its re-entry requires (a) a render capability and (b) a `ReviewSubject` kind.** Recorded, not
  quietly inherited.

- **C5 — AC1's headline invariant is already enforced; this story must not re-implement it.** *"Blocking a11y
  issues cannot be laundered into approval"* is landed as `ReviewDocumentType.Validate`'s flagship rule
  (`:89-98`): `decision == approve` while any issue satisfies `Severity.IsBlocking()` yields
  `APPROVE_WITH_BLOCKING_ISSUES`. Two consequences the story does not state. (i) **"Blocking" means exactly
  `critical`** — `IsBlocking` (`Review.cs:108`) is `severity == ReviewSeverity.Critical` and nothing else, so
  a `major` a11y failure does **not** block. The a11y lens must therefore emit `critical` for a genuinely
  blocking failure, and that mapping is a prompt-and-fixture obligation, not a code one. (ii) This is a
  **document-validity** rule that routes to the repair ring — it is not an acceptance veto. Whether a
  `request-changes` review blocks the *lifecycle's* acceptance is decided by
  `DocumentLifecycleHelper.ExtractReviewFacts` / `ComputeReviewRoute`. AC1 is satisfied by delegation plus
  fixtures, and the plan says so rather than adding a second guard.

- **C6 — AC2 has no field to land in.** *"a11y lens references a configured standard (WCAG default) per
  issue"* has no home on `ReviewIssue` (`:167-173`): the six members are `Severity`, `Category`,
  `Description`, `SuggestedFix`, `File?`, `Line?`. `Category` is deliberately *"a free non-empty string"*
  (`ReviewDocumentType.cs:28`, D10). Resolved in **D4**.

## Design Decisions

- **D1 — `DesignReviewWorkflow`, DefinitionId `design-review`, cloned from `TaskCreationWorkflow`.** Id free
  and kebab-case (`WorkflowInterfaceGraphTests.cs:49-57` enforces the shape). Graph: `ReadInputs` →
  `ComputeReEntryPosition` → `ReadPositionStage` → `FreshRun` → `FetchReviewedArtifact` →
  `DispatchLifecycle` → `ReadLifecycleExit` → `ExposeOutput`. One `FlowDecision`, zero `Finish`, zero
  `llm-call` dispatch, zero `Wait*`. It takes new DefinitionIds rather than rewiring `code-review` or
  `task-review` — the same disposition 41-17 records for `diff-review`/`pr-triage-sweep`.

- **D2 — it produces `documentType = "review"`, and it is the FIRST lifecycle binding to do so.** `review`
  is already a registered `DocumentTypeKey` with a full `IDocumentType`, so **no 41-1b type work is needed
  for the produced document** (unlike 41-27). But `task-review` and `code-review` — the only existing
  `Produces = Review` rows (`DocumentTypeRegistry.cs:157-158`) — are `Provisional: true` and are on the
  39-10 `LegacyResumeAllowlist`; neither is a binding. So this story is novel in a way the story text does
  not flag, with one concrete consequence handled in D6: the lifecycle's own REVIEW stage will review a
  `Review`, producing a second `Review` whose subject is `{ kind: "document", documentType: "review" }` —
  legal (`review` parses), bounded (the child review is not itself lifecycled), but **expensive under the
  default rules**.

- **D3 — one produce cell, `(ux_designer, review-design)`; `audit-accessibility` becomes the `ux-spec` panel
  lens.** Per C2. Concretely, and split across the right owners:
  - **This story** binds `(ux_designer, review-design)` as the producer of the standalone `design-review`
    workflow. The `Prompts/ux_designer/review-design.md` template (minted by 41-1a) must instruct **both**
    lenses — heuristic usability *and* accessibility — because `ReviewDocumentType.RenderContract()` returns
    one shared `Contract` const (`:160-180`) for all review-producing cells and cannot be specialized per
    cell. Same structural reason as 41-27's D2 and 41-10's `design-*` facets.
  - **41-1a** (lockstep, not this story) adds the doc-type-aware arm so that the REVIEW stage of 41-27's
    `ux-spec` lifecycle dispatches `(ux_designer, audit-accessibility)`: extend `GetPanelActionForRole`
    (`:430-433`) with a `ux-spec` branch beside the existing `triage-decision` one. This is the composing
    pattern `ReviewerSelectionHelper.cs:12` mandates — **do not add a third selector map**, and do not widen
    `GetReviewActionForRole`, whose contract is doc-type-agnostic.
  - Net: both cells are live and neither is orphaned, satisfying 41-1a AC2 and AC9 without inventing
    machinery.

- **D4 — the a11y standard rides `ReviewIssue.Category` as a namespaced token; `ReviewIssue` is not
  changed.** Per C6. Convention: `a11y:<standard>:<criterion>` (e.g. `a11y:WCAG2.2:1.4.3`), with the standard
  configurable and WCAG the default; usability findings use `usability:<heuristic>`. Rejected alternative:
  adding a `standardRef` member to `ReviewIssue`. That is a change to 39-4's keystone type, shared by **all
  nine** review-producing cells, and it forces an edit to the shared `Contract` const (`:160-180`) that every
  review prompt renders and that 39-16 regenerates — a cross-cutting diff for one story's benefit, and one
  that would land a mostly-null field on every code-review issue in the repo. The category convention costs
  nothing, and `ISSUE_MISSING_CATEGORY` (`:29`, `:78-80`) already guarantees the field is non-empty.
  **Enforcement:** an additive rule in `ReviewDocumentType.Validate` — *an issue whose category begins with
  `a11y` must name a standard and criterion* — scoped so it cannot fire on any existing producer, with a new
  violation code `A11Y_ISSUE_MISSING_STANDARD`. It is the minimum shared-type change that makes AC2
  executable rather than aspirational; it is additive and lockstep-reviewed with the 39-4 owner.

- **D5 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, no allowlist entry.** Per C1, and doubly forced
  by the universal pin at `ResumableStandardStructuralTests.cs:265-290`.

- **D6 — the acceptance posture is overridden per binding, never globally.** `AcceptanceDefaults.For(Review)`
  routes to `s_panelRules` — a **7-role majority panel** (`:60-69`) — so under the default a design/a11y
  review would itself be reviewed by architect + developer + tester + security + devops + product_owner +
  senior_developer. That is the wrong cost and the wrong audience. **Do not change the global arm** — it
  governs `task-review` and `code-review` too, and every value in that file is pinned by
  `AcceptanceDefaultsDriftTests`. Instead this binding passes an `acceptanceRulesJson` override selecting a
  single reviewer (product_owner for scope, or ux_designer once 41-1a D2 seats the role) with the acceptor
  set per autonomy. The override path is already a first-class lifecycle input
  (`DocumentLifecycleWorkflow.cs:184`, `ResolveRules`), so this is configuration, not code.

- **D7 — subject construction is the binding's one real piece of logic, and it is pure.**
  `DesignReviewBindingHelper.BuildSubject(...)` produces `{ kind: "document", documentId, documentType:
  "ux-spec" }` from the fetched artifact, or `{ kind: "diff", repository, prNumber }` from the inputs, and
  fails **closed** (a subject it cannot build is a typed escalation, never a fabricated `diff` subject with
  an empty repository — which `ValidateSubject` `:134-140` would reject anyway, one ring later and less
  legibly). The subject is threaded into the producer variables so the model fills a pre-resolved shape
  rather than inventing document ids. This is deterministic code, not a branch on LLM output, so it does not
  violate thinness.

- **D8 — `DESIGN_REVIEW.*` mirrors lifecycle exits; new constants file.**
  `apps/tamma-elsa/src/Tamma.Activities/Review/DesignReviewEvents.cs` with `DESIGN_REVIEW.STARTED`,
  `DESIGN_REVIEW.VERDICT`, `DESIGN_REVIEW.FAILED`, following the `DeployEvents` shape. `VERDICT` carries
  `decision`, `blockingCount`, `a11yIssueCount`, `documentId`. The story names only `STARTED` → `VERDICT`;
  `FAILED` is added for the same reason as in 41-27 — a typed escalation must be loud, matching
  `TRIAGE.CONTEXT.FAILED` / `DEPLOY.STAGE.FAILED`. Emission is gated on the re-entry position so a resumed
  run does not double-emit (39-15 D6).

- **D9 — one `WorkflowDocumentInterface` edge, one pin bump, one `reconciled` entry.** Append
  `new WorkflowDocumentInterface("design-review", new[] { DocumentTypeKey.UxSpec }, DocumentTypeKey.Review,
  false)` to `BuildSeed()`; move `WorkflowInterfaceGraphTests.cs:45` by **+1**; **and add `design-review` to
  the `reconciled` array at `:102-123`**, without which
  `Seeded_declarations_are_provisional_except_reconciled_bindings` fails a non-provisional row. (41-27's plan
  owns its own `+1`; whichever lands second takes 17→18. The two stories must not both claim the same
  number — coordinate in the same wave.)

- **D10 — drift-gate classification.** `ContractBindingTests`: `(ux_designer, review-design)` joins
  **`Bindings`** with parser authority `"ReviewDocumentType.Validate"` — satisfying the universal pin at
  `:626` that every binding authority ends in `DocumentType.Validate`. `(ux_designer, audit-accessibility)`
  joins **`ReviewProducerDispatchablePairs`** (`:505-544`) once 41-1a's `GetPanelActionForRole` arm makes it
  dispatchable; `EveryReviewProducerDispatchablePair_IsClassified` (`:547`) fails the build otherwise. If
  41-1a D2 seats `ux_designer` on `s_documentRoster`, `AllDispatchablePairs` grows and the **`:598`
  `HaveCount(16)`** pin moves — that bump belongs to **41-1a** (its AC7 already names this pin), not here.

## Implementation Steps

1. **Precondition gate (no code).** Verify compiling: `AgentRole.UxDesigner`, `AgentAction.ReviewDesign` +
   `AuditAccessibility`, `Prompts/ux_designer/{_system,review-design,audit-accessibility}.md`, the
   `GetPanelActionForRole` `ux-spec` arm and the `GetReviewActionForRole(UxDesigner)` arm (41-1a);
   `DocumentTypeKey.UxSpec` registered (41-1b); an accepted `UxSpec` producible by `ux-spec-drafting`
   (41-27, for the integration scenarios only — unit and structure work does not need it). File gaps against
   the enabler owners.

2. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Types/ReviewDocumentType.cs`** (D4, lockstep with the
   39-4 owner) — add `A11yIssueMissingStandard = "A11Y_ISSUE_MISSING_STANDARD"` and the scoped rule inside
   the existing per-issue loop (`:73-86`): an issue whose `Category` starts with `a11y` must carry a
   standard + criterion segment. Add one positive and one negative `DocumentExample` to `s_examples`
   (`:182-224`). **Do not touch the shared `Contract` const's existing lines**; append one rule sentence only
   if the 39-4 owner agrees, otherwise the a11y instruction lives entirely in the `review-design` template.

3. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DesignReviewBindingHelper.cs`** — pure,
   Elsa-free, `CreationBindingHelper` posture, reusing `LifecycleBindingHelper.ReadLifecycleResult`/
   `IsAccepted`:

   ```csharp
   public static class DesignReviewBindingHelper
   {
       public static string BuildSubjectJson(string? uxSpecDocumentId, string? repository, int? prNumber); // D7; "" ⇒ fail-closed
       public static string ProjectReviewJson(string documentJson);        // "{}" fail-closed
       public static string ReadDecisionWire(string documentJson);         // "" on unreadable
       public static int CountBlockingIssues(string documentJson);         // Severity.IsBlocking() only
       public static int CountA11yIssues(string documentJson);             // category prefix "a11y"
       public static string BuildFailureDetail(LifecycleExit exit);
   }
   ```

4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Review/DesignReviewEvents.cs` + the emit activity** (D8).

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DesignReviewWorkflow.cs`** (D1/D5/D6/D7). The one
   dispatch:

   ```csharp
   ["documentType"]          = "review",
   ["producerRole"]          = AgentRole.UxDesigner.ToWire(),
   ["producerAction"]        = AgentAction.ReviewDesign.ToWire(),
   ["producerVariablesJson"] = /* { subjectJson, uxSpecJson | diffRef, a11yStandard, <declared carrier> } */,
   ["feedbackVariableName"]  = "<the declared carrier — read it off the template, do not invent it>",
   ["issueId"] / ["correlationId"] / ["tenantId"],
   ["acceptanceRulesJson"]   = <D6 single-reviewer override, or the caller's>,
   ["parentDocumentId"]      = <the reviewed UxSpec's documentId>,
   ```

6. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (`BuildSeed()`) +
   **`apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs`** — D9's row, the `:45`
   pin bump, and the `reconciled` entry at `:102-123`.

7. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`** — D10's
   `Bindings` entry (token groups matched against the shipped `review-design.md`) and, once 41-1a lands the
   panel arm, the `ReviewProducerDispatchablePairs` entry. Re-run `TaxonomyDriftBuildTests` and add
   `DesignReviewWorkflow` to `ExpectedContributingWorkflows`.

8. **CREATE the test suites** (Test Plan), then full `dotnet test`;
   `dotnet ef migrations has-pending-model-changes` clean; no `[Ignore]`/`Skip` in the new suites.

## Data & Migrations

None. The `Review` persists as a `DocumentInstance` row through 39-11's landed store;
`DESIGN_REVIEW.*` and `DOCUMENT.*` ride the existing drain → `EventRepository` → `domain_events` path.
`dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new, `Tamma.Activities/Review/DesignReviewEvents.cs`):** `DESIGN_REVIEW.STARTED` (fresh runs
  only), `DESIGN_REVIEW.VERDICT` (data `decision`, `blockingCount`, `a11yIssueCount`, `documentId`),
  `DESIGN_REVIEW.FAILED` (data `detail` = the typed outcome wire). Tags `issueId`, `repository`, `tenantId`,
  `correlationId`.
- **Emitted by the machinery this binding wires in (asserted, not authored):** the full `DOCUMENT.*` family
  including `DOCUMENT.REVIEW_PANEL_*`, plus `APPROVAL.*` and `ESCALATION.TRIGGERED`.

## Test Plan

- **`DesignReviewWorkflowStructureTests`** (clone of `TaskCreationWorkflowStructureTests`) — builds;
  `DefinitionId == "design-review"`; threads `TenantId`; no retry-plumbing variables; exactly one
  `DispatchWorkflow`, literal id `document-lifecycle`, zero targeting `llm-call`;
  `ScanLifecycleBindingDispatches` contains `(DesignReviewWorkflow, DispatchLifecycle, ux_designer,
  review-design)` and `MaterializeDispatchInput` yields `documentType == "review"`; zero `Finish`; one
  `ComputeReEntryPositionActivity`; `[ResumeBehavior(LatestStateReEntry)]`; zero `Wait*` nodes. **Covers AC1
  (structure), corrected AC4 (declaration).**
- **`ReviewDocumentTypeA11yRuleTests`** (`Tamma.Core.Tests`, D4) — an `a11y:WCAG2.2:1.4.3` issue validates;
  a bare `a11y` category is rejected with `A11Y_ISSUE_MISSING_STANDARD`; **a regression matrix asserting the
  new rule fires on NO existing category** (`correctness`, `security`, `style`, `blocking`, `usability:…`),
  so nine other review producers are provably unaffected. **Covers AC2.**
- **`ReviewBlockingA11yTests`** (`Tamma.Core.Tests`, C5) — the laundering matrix on the **landed** rule:
  `decision=approve` + a `critical` a11y issue → `APPROVE_WITH_BLOCKING_ISSUES`; `approve` + a `major` a11y
  issue → **valid** (the honest consequence of `IsBlocking` being `critical`-only, pinned so nobody assumes
  otherwise); `request-changes` + `critical` → valid. **Covers AC1 (invariant half).**
- **`DesignReviewBindingHelperTests`** — `BuildSubjectJson` produces a `ValidateSubject`-passing `document`
  subject for a `ux-spec` and a `diff` subject for a repo+PR; **fails closed** (empty) when neither is
  derivable; counters fail-closed to 0 on garbage; `BuildFailureDetail` names every reachable outcome wire.
  **Covers AC1, AC3 (shape half).**
- **`ResumableStandardStructuralTests`** — green with no allowlist entry (clauses (a)/(c) + the universal pin
  at `:265-290`), plus a negative test asserting that flipping the declaration to `Both` makes clause (b)
  fail. **Covers corrected AC4.**
- **Drift gates post-edit** — `ContractBindingTests` green including `UniversalPin_EveryBindingAuthority…`
  (`:626`) and `EveryReviewProducerDispatchablePair_IsClassified` (`:547`); `WorkflowInterfaceGraphTests`
  green at the bumped count with `design-review` in `reconciled`.
- **`DesignReviewLifecycleExecutionTests`** (Testcontainers, shared 39-6/39-12 fixture; 39-9 ring OFF) —
  (a) **happy path over a `UxSpec`**: seeded accepted `UxSpec` → scripted `Review` with a `major` a11y issue
  and decision `request-changes` → single-reviewer review (D6 override asserted — **not** a 7-role panel) →
  accept resume → accepted `Review` readable through the store with `parentDocumentId` = the spec, and
  `DESIGN_REVIEW.VERDICT` carrying `decision`/`blockingCount`/`a11yIssueCount` (AC1, AC3);
  (b) **blocking a11y cannot be laundered**: scripted `approve` + `critical` a11y issue → validator rejects
  with `APPROVE_WITH_BLOCKING_ISSUES` → repair turn → the model downgrades the decision → accepted; the
  stream never contains an accepted `Review` that both approves and carries a critical issue (AC1);
  (c) **escalation**: always-approving-with-blocking stub → `ValidationExhausted` with lineage +
  `DESIGN_REVIEW.FAILED`, and the escalation payload carries the a11y issues so a human sees why (AC3);
  (d) **diff subject**: repo + PR inputs → a `diff`-kind subject validates and the run completes without any
  `UxSpec` in the store (proving the two subject kinds are independent);
  (e) **crash re-entry**: kill mid-review, fresh dispatch re-enters at review of the same revision, exactly
  one `DESIGN_REVIEW.VERDICT` on the whole stream (corrected AC4);
  (f) **AC3 gate input**: the accepted `Review`'s decision + blocking count are readable by a merge-gate-shaped
  consumer through the 39-11 store.
  **Covers AC1, AC2, AC3, corrected AC4 (runtime halves).**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; validated unified `Review`; blocking a11y not launderable into approval | 3, 5 (D1/D7), C5 | `DesignReviewWorkflowStructureTests`; `ReviewBlockingA11yTests`; ExecutionTests (b)(c) |
| 2 — a11y lens references a configured standard (WCAG default) per issue | 2 (D4) | `ReviewDocumentTypeA11yRuleTests` incl. the no-regression matrix |
| 3 — verdict integrates as a gate input for UI-affecting merges | 5, 6 (D9) | `DesignReviewBindingHelperTests`; ExecutionTests (a)(f); `WorkflowInterfaceGraphTests` |
| 4 — **corrected**: `[ResumeBehavior(LatestStateReEntry)]`; 39-10 gate green, no allowlist entry | 5 (D5) | `ResumableStandardStructuralTests` + its negative `Both`-fails test; ExecutionTests (e) |
| — shipped-UI audit | **out of scope (C4)** | Recorded, with both blockers named: no render executor in Epic 41 *or* 42, and no `ReviewSubject` kind for a running UI |

## Blocks / Blocked by

- **Blocked by — 41-1a (hard, unlanded).** `AgentRole.UxDesigner`; `AgentAction.ReviewDesign` +
  `AuditAccessibility` + their prompt files; **the `GetReviewActionForRole(UxDesigner)` arm** (because
  `DocumentLifecycleWorkflow.cs:1212` calls it unguarded for the review envelope and it throws for any
  unlisted role); and **the `GetPanelActionForRole` `ux-spec` arm** from D3. Also owns the
  `ReviewerSelectionHelperTests.cs:97` / `ContractBindingTests.cs:598` roster-pin bumps if D2 seats
  `ux_designer` on the roster.
- **Blocked by — 41-1b (hard, unlanded; C3).** `DocumentTypeKey.UxSpec` must parse, or every
  `document`-kind subject naming a `ux-spec` fails `ValidateSubject` with `SUBJECT_INCOMPLETE`. The story's
  Dependencies omit this.
- **Blocked by — 41-27 (soft).** Needed only for the integration scenarios that review a real `UxSpec`;
  scenario (d) (diff subject) and every unit/structure test are independent of it. So 41-27 → 41-28 is the
  natural order but not a compile-time gate.
- **Blocked by — Epic 39 (satisfied).** 39-2/39-4 (`Review` + `ReviewDocumentType`), 39-5 (rules), 39-6
  (lifecycle), 39-7 (`document-review`/`review-panel`/`review-single-reviewer`), 39-8, 39-10, 39-11 and the
  39-12→39-15 recipe are all landed and verified in tree.
- **Lockstep — 39-4 owner** for D4's additive `ReviewDocumentType` rule (the type is shared by nine
  producers); **41-27 owner** for the `WorkflowInterfaceGraphTests.cs:45` pin, which both stories move by one
  and neither may double-count.
- **Not blocked by — Epic 42, for the delivered scope.** The `UxSpec`/diff half needs no tool beyond
  `file_read`/`search_code`. Only the excluded shipped-UI half needs a capability that does not exist (C4).
- **Degraded until 39-17 / 39-19 / 39-20** — the accept gate publishes and suspends but nothing decides, and
  there is no Task View for a human reviewer. This story's ACs are written so none depends on them.
- **Blocks — 41-27's review stage quality** (the a11y panel lens from D3 runs there), the UI-affecting merge
  gate (AC3), and any future `design` `TaskKind` review path in 41-29.

## Risks & Mitigations

- **`BuildReviewEnvelope` records a lens it did not dispatch — a latent inconsistency this story would
  inherit and widen.** `DocumentLifecycleWorkflow.cs:1212` builds the review envelope's `ProducedBy.Action`
  from `GetReviewActionForRole(role)` (doc-type-**agnostic**), while the reviewer that actually ran was
  selected by `ReviewerSelectionHelper.ResolveDocumentAction` → `GetPanelActionForRole(role, docTypeKey)`
  (doc-type-**aware**, `:160`). For `triage-decision` reviews these already disagree today: a devops
  panellist reviews with `diagnose-incident` but the envelope records `review-operability`. D3's `ux-spec`
  arm would reproduce the divergence (dispatch `audit-accessibility`, record `review-design`). Mitigation:
  **file the fix against 39-7/39-15** (make `:1212` call `GetPanelActionForRole` with the state's
  `documentType`) rather than patching it here or designing around it; add a test asserting envelope-action
  equals dispatched-action for the `ux-spec` case so the divergence cannot be shipped silently under this
  story's name.
- **First lifecycle binding producing `review`, under a 7-role default panel (D2/D6).** Left unaddressed,
  every design review would be panel-reviewed by seven roles. Mitigation: the per-binding
  `acceptanceRulesJson` override, asserted explicitly in ExecutionTests (a) — not left to whatever the
  default resolves to. The global `AcceptanceDefaults.For` arm is deliberately untouched.
- **`IsBlocking` is `critical`-only, and a11y severity judgement is a prompt.** A model that grades a
  contrast failure `major` produces an approvable review. No code can catch this. Mitigation:
  `ReviewBlockingA11yTests` pins the honest semantics so the limit is visible rather than assumed; the
  `review-design.md` template states the mapping from a11y conformance level to severity explicitly; the
  always-escalate acceptance class the story mentions for legal/compliance a11y is Epic 39 **policy**
  (`AcceptanceRules.AlwaysEscalate`), configured, not coded here.
- **Touching `ReviewDocumentType` touches nine producers (D4).** Mitigation: the rule is scoped by category
  prefix, additive, and its no-regression matrix explicitly exercises every category string currently in the
  repo's fixtures; the shared `Contract` const is left alone unless the 39-4 owner opts in.
- **The shipped-UI half looks deliverable and is not (C4).** Mitigation: stated twice — in Scope and in the
  DoD table — with both blockers named (no render executor anywhere in Epic 41 *or* Epic 42's five families;
  no `ReviewSubject` kind for a running UI), so no downstream story plans against it.
- **Story-vs-canon tensions:** the six in **Corrections to the story**. All are mechanism corrections; every
  stated requirement survives.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition gate against 41-1a/41-1b/41-27 | 0.25 |
| 2 | `ReviewDocumentType` a11y-standard rule + examples + no-regression matrix (lockstep) | 0.5 |
| 3 | `DesignReviewBindingHelper` (subject construction + projections) | 0.5 |
| 4 | `DesignReviewEvents` + emit activity | 0.25 |
| 5 | `DesignReviewWorkflow` binding (+ D6 rules override, D7 subject threading) | 1.0 |
| 6–7 | Interface edge + `reconciled` entry + pin bump; `ContractBindingTests` classification | 0.5 |
| 8 | Structure/unit suites + Testcontainers scenarios (a)–(f) + full green | 1.25 |
| — | 39-4 lockstep, 41-1a D3 coordination, filing the `:1212` envelope-lens divergence | 0.25 |
| **Total** | | **4.5** (story estimate: 4 days) |

Slightly above the story's 4 days, for two reasons the story did not price: D4's shared-type change with its
no-regression matrix, and D7's subject construction (which 41-27 does not need). Neither Wave-0 enabler's
effort is included.
