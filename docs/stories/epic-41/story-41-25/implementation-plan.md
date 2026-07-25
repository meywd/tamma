# Implementation Plan — Story 41-25: User & API Documentation Workflow

## Scope & Deliverable

When this story is done, a merged feature produces **audience-tagged prose documentation** on the Epic 39
spine, through two independent thin lifecycle bindings:

| New workflow | DefinitionId | produces | producer cell |
|---|---|---|---|
| User documentation | `user-docs-authoring` | `prose` (kind `user-docs`, audience `user`) | `(tech_writer, write-user-docs)` |
| API reference | `api-docs-authoring` | `prose` (kind `api-docs`, audience `developer`) | `(tech_writer, write-api-docs)` |

Both are the `TaskCreationWorkflow`/`DebugDiagnosisWorkflow` skeleton: exactly one `DispatchWorkflow` with
literal id `document-lifecycle`, zero `llm-call`, zero `Finish`, no retry plumbing, a declared
`feedbackVariableName`, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, one
`ComputeReEntryPositionActivity`, one `WorkflowDocumentInterface` row each (edge pin +2). Each consumes the
merged diff, the accepted `Plan`, the accepted `AcceptanceCriteria` when present, **and the previously
accepted doc of the same kind** — which is what makes AC2's "updates rather than duplicates" real
(**C4**/**D3**). A new `FeatureDocsEvents` family rides alongside `DOCUMENT.*`. Both templates are
rewritten to 41-1c's prose envelope (**C3**/**D4**).

## Pre-Reading

- `docs/stories/epic-41/story-41-25/41-25-user-and-api-documentation.md` — the story (ACs are source of truth, modulo **Corrections** below)
- `docs/stories/epic-41/README.md` — rules 1–5; the 41-1a review-selector gap (`:476-483`); the Epic 42 publish row (`:429`)
- **`docs/stories/epic-41/story-41-24/implementation-plan.md`** — the sibling plan. **D2 (prose issue-id scoping), D4 (prose-envelope template pattern) and D6 (the `(tech_writer, review-docs)` rewrite) are shared assets; this plan inherits rather than re-derives them.**
- `docs/stories/epic-41/story-41-1/41-1c-prose-documents-and-audience-tags.md` — the `prose` type, `Audience`, the kind/audience vocabularies, D2's prose acceptance row
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — Scope 3 / D1: the `TechWriter` selector arm and the 7 → 8 roster
- `docs/stories/epic-39/story-39-12/implementation-plan.md` — the thin-binding recipe; `story-39-10/implementation-plan.md` — the resume standard
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — the template; the consumed-document read `:155-165`, producer-scoped issue id `:112`, feedback carrier `:190`
- `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchLatestAcceptedDocumentActivity.cs:37-64` — inputs `IssueId`/`DocumentTypeKey`/`TenantId`, outputs `Found`/`DocumentId`/`DocumentJson`/`LineageJson`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/CreationBindingHelper.cs:84-96` — `ScopeIssueId` and the verbatim note that the 39-11 read has **no producer filter** (filed to 39-11)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` — Init reads `:169-202` (**no `supersedesDocumentId` input**); `IngestDraft` carves the first JSON object `:1170-1197`; `BuildReviewEnvelope` → unguarded `GetReviewActionForRole` at **`:1212`**; persist `:765-777`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DocumentLifecycleHelper.cs:239-259` — **`ResolveSupersedes`: a `Produce` origin supersedes NOTHING**; the chain is intra-run only
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentReviewWorkflow.cs:256-265` — `BuildReviewerVariables` supplies exactly `planJson` + `documentJson`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewProducerHelper.cs:168-203` — `DefaultFeedbackVariable = "workItemJson"`; an **undeclared** feedback variable is silently dropped
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:376-387` — `GetReviewActionForRole` throws for `TechWriter`
- `apps/tamma-elsa/src/Tamma.Api/Prompts/tech_writer/write-user-docs.md`, `write-api-docs.md`, `review-docs.md` — read in full; shapes quoted in **C3**/**C5**
- `apps/tamma-elsa/src/Tamma.Activities/ADL/MergeEvents.cs:42-52` — `MERGE.SUCCESS` / `ISSUE.CLOSED.SUCCESS` / `BRANCH.DELETED.SUCCESS`; the merge signal
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` — `WaitForPRMerged` `:701-708`, the post-merge region and the `deployment-pipeline` dispatch `:725`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs:200-206` — `QueryEventsAsync`, the 4-7 surface
- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IWebhookHandler.cs` + `Tamma.Platforms/Webhooks/WebhookEventDispatcher.cs` — the inbound seam (**zero handlers registered** — C6)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs`; `ContractBindingTests.cs` (`Bindings` `:82`, `ReviewProducerDispatchablePairs` `:505`, roster pin `:598`, universal pins `:626`/`:655`); `TaxonomyDriftBuildTests.cs:125`/`:460`; `Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:44`
- **NOT FOUND:** `DocumentTypeKey.Prose` / `ProseDocumentType` / `DocumentEnvelope.Audience` / `DocumentInstance.Audience` (41-1c); the `TechWriter` selector arm (41-1a); `AcceptanceCriteria` (41-1b, optional here); any `USER_DOCS.*` / `API_DOCS.*` event constant; any registered `IWebhookHandler`. Everything else above exists and was read.

## Corrections to the story

- **C1 — AC3's `[ResumeBehavior(Both)]` fails the 39-10 gate.** Identical to 41-24 C1: `Both` requires a
  canonical suspend node in *this* workflow's graph (`ResumableStandardStructuralTests.cs:158-198`, plus
  the honesty inverse `:202-236`), and a thin binding has none — the accept gate lives inside the
  dispatched `document-lifecycle` child (39-12 D7; landed precedents `TaskCreationWorkflow.cs:47`,
  `DebugDiagnosisWorkflow.cs:38`). **Correct declaration: `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`.**
- **C2 — two prose documents cannot ride one lifecycle dispatch.** `DocumentLifecycleWorkflow` reads one
  `producerRole`/`producerAction`/`documentType` (`:169-172`). `write-user-docs` and `write-api-docs` are
  **two producing workflows** (D1). Rule 1(a) holds per binding. *(The story's "and/or" already implies
  they are independently triggerable — the split makes that structural.)*
- **C3 — both produce prompts instruct raw markdown in a generic skeleton.** `write-user-docs.md` and
  `write-api-docs.md` carry byte-identical front matter
  (`variables: role, workItemJson, findings, audience` / `enableTools: false` / `maxTokens: 2048` /
  `version: 1`) and a byte-identical `## Summary / ### Key Findings / ### Action Items / ### Details`
  body; only line 18 differs. Two consequences: (i) `DocumentLifecycleWorkflow.IngestDraft` (`:1177-1180`)
  carves the **first JSON object** out of the reply and fails the produce turn if there is none — so raw
  markdown cannot be a lifecycle payload and must move inside 41-1c's `{kind, audience, title, body}`
  envelope; (ii) a Summary/Key-Findings/Action-Items skeleton is the wrong shape for user docs (task-
  oriented how-to) and for an API reference (endpoints, parameters, examples) — the intent is in line 18
  but the *format block* overrides it. Both are rewritten (D4). Helpfully, both already declare an
  `audience` variable, so the 41-1c tag threads cleanly.
- **C4 — AC2's "updates existing docs rather than duplicating" is NOT implementable as the lifecycle
  stands.** Two independent blockers, both verified:
  1. `DocumentLifecycleWorkflow` accepts **no `supersedesDocumentId` input** (Init reads, `:169-202`), and
     `DocumentLifecycleHelper.ResolveSupersedes` (`:255-259`) returns `null` for a `Produce` origin. The
     supersession chain is **intra-run only** — a revise supersedes the draft it revised, and a fresh run
     starts a new chain. There is no way today to tell the lifecycle "this document supersedes the one
     you accepted last month."
  2. Worse, a second run keyed on the same issue id does not produce at all: 39-10 re-entry sees an
     accepted document of that `(issueId, documentType)` and short-circuits to `Complete` with
     `DOCUMENT.REENTERED` — the *correct* behaviour for idempotency, the *wrong* behaviour for "update
     the docs when the feature changes."
  **D3** resolves this without touching the generic layer, and files the generic fix rather than
  smuggling it in.
- **C5 — `(tech_writer, review-docs)` is a PR-diff cell, and 41-1a does not fix that.** Same finding as
  41-24 C5, restated because this story's AC1 leans on it hardest ("a `Review` that checks accuracy
  against the merged diff"): the cell declares `variables: role, prDescription, diff, conventions`, while
  `DocumentReviewWorkflow.BuildReviewerVariables` supplies only `planJson` + `documentJson`
  (`:256-265`); it does not declare `workItemJson`, which is `ReviewProducerHelper.DefaultFeedbackVariable`
  (`:203`), so repair notes are dropped at render; and it instructs a diff-review JSON, not the `Review`
  wire. **41-24 D6 owns the rewrite.** If 41-25 ships first, 41-25 carries it — see Blocks / Blocked by.
  Note the irony worth stating: this story's review really *does* want the diff, and the rewritten cell
  must therefore accept the diff **through a declared variable the producers actually supply**, which is
  `documentJson` + whatever the binding folds into the review subject — not the undeclared `{{diff}}`
  placeholder the current template hopes for.
- **C6 — the merge trigger has a seam but no wiring.** `IWebhookHandler` / `IWebhookEventDispatcher`
  exist and the generalised receiver is mapped (`Program.cs:2980-2981`), but **`RegisterHandler` is called
  from zero production sites** — a `pull_request.closed`/`push` handler would be the first in the
  codebase. In-repo, the merge signal that already exists is `MERGE.SUCCESS` (`MergeEvents.cs:42-52`),
  emitted by `MergeWorkflow` inside `single-issue-cycle` after `WaitForPRMerged`
  (`SingleIssueCycleWorkflow.cs:701-708`). **D2** uses the in-repo signal and records the webhook route
  as the extension, rather than taking on the first handler registration.
- **C7 — `.dev/findings/document-lifecycle-persist-not-wired.md` is STALE.** Persistence *is* wired
  (`DocumentLifecycleWorkflow.cs:770-777`). Do not plan around it.
- **C8 — the story's cite of the unguarded selector call is 13 lines early.** It is
  `DocumentLifecycleWorkflow.cs:1212`, inside `BuildReviewEnvelope` (`:1200`), not `:1199`. The behaviour
  is exactly as described.

## Design Decisions

- **D1 — Two independent producing bindings, no orchestrating parent (per C2).** `user-docs-authoring`
  and `api-docs-authoring` are single-dispatch thin bindings over the same feature. Neither consumes the
  other; a caller dispatches one, the other, or both, which is what the story's "and/or" asks for. Each
  passes the `TaskCreationWorkflowStructureTests` clause set verbatim and declares its own
  `WorkflowDocumentInterface` row; the edge pin moves **+2** in this story's commit (epic rule 1(f)).
  This story ships **no** workflow that dispatches anything other than `document-lifecycle`.
- **D2 — the trigger is the in-repo `MERGE.SUCCESS` signal, or an explicit on-demand dispatch (per C6).**
  The binding takes `repository`, `issueNumber`, `mergeSha` and `featureKey` as inputs and is dispatched
  by definition id — from `single-issue-cycle`'s post-merge region, from 41-29's `docs`-kind task route
  once it lands, or by hand. **No trigger infrastructure is built here.** A `pull_request.closed`
  `IWebhookHandler` (C6) and the 41-29 `docs` route are recorded as the two natural wirings; both are
  additive to a workflow that is dispatchable today.
- **D3 — "update, not duplicate" is a REVISION-SCOPED lifecycle that consumes the previous accepted doc
  (per C4).** The plan of record, requiring zero generic-layer change:
  - `featureId = "docs#{repository}#{featureKey}"` where `featureKey` is the issue id when the docs
    follow one issue, else a caller-supplied stable key;
  - each binding keys its lifecycle on `ScopeIssueId(featureId, "user-docs" | "api-docs") + "#" + mergeSha`
    — so **every merge gets its own lifecycle**, which is exactly why a re-dispatch for the *same* merge
    is a no-op re-entry (idempotency, AC2's first half) while a *new* merge produces a fresh document
    (freshness, AC2's second half);
  - before dispatching, `FetchLatestAcceptedDocumentActivity` reads the previous accepted doc on the
    **previous** merge's scope (via a small `DocsRevisionResolver.PreviousScope`, resolved from the
    binding's own `DOCS.*` event history) and folds its `body` into the DECLARED `findings` carrier as
    *"the current documentation — revise it, do not restate it"*, along with `parentDocumentId` on the
    output;
  - the produce template (D4) is instructed to emit a **complete revised document**, not a delta.
  Result: successive docs form a lineage by convention (`parentDocumentId` + the `DOCS.*` chain), and no
  document is duplicated. **What this does NOT give**: a `supersedes_document_id` edge in
  `document_instances` across runs — that needs a `supersedesDocumentId` input on `document-lifecycle` +
  a `ResolveSupersedes` extension. **Filed to 39-6/39-11, not built here** (the 39-12 D4 precedent for
  filing a generic-layer hook back to its owner rather than special-casing a binding). AC2 is therefore
  satisfied in behaviour and only partially in storage, and the Definition of Done says so.
- **D4 — both produce templates are rewritten to the prose envelope with kind-appropriate bodies (per
  C3).** Front matter becomes
  `variables: role, workItemJson, findings, audience, diffSummary, planJson, acceptanceCriteriaJson, conventions`;
  `findings` stays the declared `feedbackVariableName` carrier and also carries D3's previous document.
  Body instructs `{"kind": "user-docs"|"api-docs", "audience": "user"|"developer", "title": …,
  "body": "<markdown>"}`. Inside `body`: **user docs** are task-oriented ("how do I…"), leading with what
  the user can now do, with no internal implementation vocabulary (the current line-18 intent, made the
  *format*); **API docs** cover endpoints/signatures, parameters, responses, errors and at least one
  worked example, documenting behaviour not implementation. The generic Summary/Key-Findings/Action-Items
  skeleton is deleted from both. This inherits 41-24 D4's pattern.
- **D5 — `enableTools` stays `false`.** The merged diff, the accepted `Plan`, the optional
  `AcceptanceCriteria` and the previous doc are all assembled by the binding and handed in through
  `producerVariablesJson`. Keeping the producer tool-free makes the agent path reachable with **zero**
  Epic 42 dependency and keeps the reply deterministic enough for the prose envelope. *(This is the
  refinement of the story's Epic 42 caveat: drafting needs no tool at all — only publication does.)*
- **D6 — consumed documents are read through the store seam, gated on a fresh run.** `FetchLatestAcceptedDocumentActivity`
  ×2 (accepted `Plan` on the base issue id; accepted `AcceptanceCriteria` on the base issue id when 41-1b
  has landed, `Found=false` tolerated) plus D3's previous-doc read, all behind the `FreshRun?`
  `FlowDecision` — the `TaskCreationWorkflow.cs:150-166` pattern. Their content is folded into
  **declared** variables only (the render-drop lesson), and their document ids ride the
  `WorkflowDocumentInterface` `Consumes` list so the graph type-checks.
- **D7 — review inherits 41-24 D6 and adds the diff to the subject.** The rewritten
  `(tech_writer, review-docs)` cell reviews the prose document; AC1's "accuracy against the merged diff"
  is served by folding `diffSummary` into the review subject the binding hands the lifecycle, not by the
  undeclared `{{diff}}` placeholder (C5). If a richer review subject proves necessary, the fix belongs in
  `DocumentReviewWorkflow.BuildReviewerVariables` (`:256-265`) — filed to 39-7, not special-cased here.
- **D8 — a new `FeatureDocsEvents` family; nothing named `USER_DOCS.*`/`API_DOCS.*` exists.**
  Constants: `USER_DOCS.STARTED`, `USER_DOCS.DRAFTED`, `USER_DOCS.ACCEPTED`, `USER_DOCS.FAILED`,
  `API_DOCS.STARTED`, `API_DOCS.UPDATED`, `API_DOCS.FAILED`. `StatusForEvent`: `FAILED` → `"error"`,
  `STARTED` → `"started"`, else `"success"`. Tags `repository`, `featureKey`, `mergeSha`, `issueId`,
  `documentId`, `parentDocumentId`, `audience`, `correlationId`, `tenantId`. One
  `EmitFeatureDocsEventActivity`, copying `EmitDecompositionEventActivity`'s shape. The
  `parentDocumentId` tag is what makes D3's lineage queryable without the storage edge.
- **D9 — acceptance is policy passthrough.** `acceptanceRulesJson` rides as an input (39-12 D8).
  "Contract-affecting API-doc changes can be always-escalate" is an `AcceptorRequirement.Human` rules row
  supplied by the caller — never an if-else here. 41-1c D2 sets prose's default to a `tech_writer`
  single-reviewer row.

## Implementation Steps

1. **Precondition gate (no code).** Verify in tree and compiling: **41-1c** (`DocumentTypeKey.Prose`,
   `ProseDocumentType`, `DocumentEnvelope.Audience`, `DocumentInstance.Audience` + migration, kinds
   `user-docs`/`api-docs`, audiences `user`/`developer`) and — **for the review stage only** — **41-1a**
   (`GetReviewActionForRole(TechWriter) == ReviewDocs`, 8-role roster). Confirm whether **41-24** has
   landed: if yes, step 5 is a no-op and this story inherits its `review-docs.md`; if no, step 5 is this
   story's. Confirm whether **41-1b** has landed: if not, the `AcceptanceCriteria` read (D6) is skipped
   and its `Consumes` entry omitted — the story already marks it optional ("consumes 41-2
   AcceptanceCriteria **when present**").

2. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DocsRevisionResolver.cs`** (D3) —
   pure, Elsa-free, total: `ComposeFeatureId(repository, featureKey)`,
   `ScopeFor(featureId, kind, mergeSha)`, `PreviousScope(featureId, kind, previousMergeSha?)`,
   `BuildFailureDetail(exit)`. Determinism is the point: same inputs → byte-identical ids.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/FeatureDocs/FetchPreviousDocRevisionActivity.cs`**
   (D3) — inputs `TenantId`, `FeatureId`, `Kind`; outputs `Found`, `PreviousDocumentId`, `PreviousBody`.
   Resolves `IEventRepository` via `context.GetService<T>()`; reads this binding's own
   `USER_DOCS.ACCEPTED` / `API_DOCS.ACCEPTED` history (prefix + `featureKey` tag) to find the previous
   merge's scope, then delegates the body read to `FetchLatestAcceptedDocumentActivity`'s repository
   seam. Fail-loud on an unresolvable service; **`Found = false` is a legitimate first-run answer**, not
   a failure.

4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/FeatureDocs/FeatureDocsEvents.cs` +
   `EmitFeatureDocsEventActivity.cs`** (D8) — copy `Decomposition/DecompositionEvents.cs` +
   `EmitDecompositionEventActivity.cs` shape exactly.

5. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/tech_writer/review-docs.md` + classify
   `(tech_writer, review-docs)` in `ContractBindingTests.ReviewProducerDispatchablePairs`** — **only if
   41-24 has not already done it** (C5; the work and its rationale are 41-24 D6). Skip if inherited.

6. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/tech_writer/write-user-docs.md` and
   `write-api-docs.md`** (C3/D4) — prose envelope, `version: 2`, front matter and bodies per D4.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`**: add
   `[("tech_writer","write-user-docs")]` and `[("tech_writer","write-api-docs")]` to **`Bindings`** with
   authority `"ProseDocumentType.Validate"` and token groups
   `[One("\"kind\""), One("\"audience\""), One("\"title\""), One("\"body\"")]` — **not**
   `IntentionallyUnbound` (a document producer must be bound, `:655-674`).

7. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/UserDocsAuthoringWorkflow.cs`
   (`user-docs-authoring`) and `ApiDocsAuthoringWorkflow.cs` (`api-docs-authoring`)** (D1/D3/D6) — both
   the `TaskCreationWorkflow` skeleton, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (C1), graph
   `ReadInputs → ComputeReEntryPosition → FreshRun? → FetchConsumedPlan → FetchAcceptanceCriteria? →
   FetchPreviousDocRevision → DispatchLifecycle → ReadLifecycleExit → ExposeOutput`, zero `Finish`,
   single terminal region. Dispatch inputs:
   ```csharp
   ["documentType"]          = "prose",
   ["producerRole"]          = AgentRole.TechWriter.ToWire(),
   ["producerAction"]        = AgentAction.WriteUserDocs.ToWire(),      // / WriteApiDocs
   ["producerVariablesJson"] = { workItemJson, diffSummary, planJson, acceptanceCriteriaJson,
                                 findings = previousBody,   // D3: revise, do not restate
                                 audience = "user" /* "developer" */, conventions = "" },
   ["feedbackVariableName"]  = "findings",
   ["issueId"]               = ScopeFor(featureId, "user-docs" /* "api-docs" */, mergeSha),
   ["correlationId"]         = same, ["tenantId"], ["acceptanceRulesJson"],
   ```
   Outputs: `status`, `outcome`, `documentId`, `documentJson`, `parentDocumentId` (= the previous
   revision's id, D3), `audience`, `featureKey`, `mergeSha`.

8. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs` (`BuildSeed`)** — two rows,
   `Provisional=false`:
   ```csharp
   new WorkflowDocumentInterface("user-docs-authoring", new[]{ DocumentTypeKey.Plan }, DocumentTypeKey.Prose, false),
   new WorkflowDocumentInterface("api-docs-authoring",  new[]{ DocumentTypeKey.Plan }, DocumentTypeKey.Prose, false),
   ```
   (add `DocumentTypeKey.AcceptanceCriteria` to both `Consumes` lists **iff** 41-1b has landed).
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:44`** — bump
   the pin by **+2** from whatever it is at merge time (16 today; 18 if 41-24 landed first), with the
   one-line reason.

9. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:125`** —
   add `UserDocsAuthoringWorkflow` and `ApiDocsAuthoringWorkflow` to `ExpectedContributingWorkflows`,
   one comment each.

10. **CREATE the test suites** (see Test Plan).

11. **Finish** with full `dotnet test` and `dotnet ef migrations has-pending-model-changes` (clean — no
    schema here; `Audience` is 41-1c's migration).

## Data & Migrations

None. Documents persist through 39-11's `document_instances` via the lifecycle's own persist nodes
(`DocumentLifecycleWorkflow.cs:770-777` — C7); `USER_DOCS.*`/`API_DOCS.*` ride the existing
`TammaEventEmitter` drain → `EventRepository` → `domain_events`. The `Audience` column is **41-1c's**.
`dotnet ef migrations has-pending-model-changes` stays clean.

**Filed, not built:** a `supersedesDocumentId` input on `document-lifecycle` + a `ResolveSupersedes`
extension would give D3's revision chain a real storage edge in `document_instances.SupersedesDocumentId`
across runs. That is a 39-6/39-11 generic-layer change (C4/D3).

## Events

- **Emits (new):** `USER_DOCS.STARTED`/`.DRAFTED`/`.ACCEPTED`/`.FAILED`,
  `API_DOCS.STARTED`/`.UPDATED`/`.FAILED` — tags `repository`, `featureKey`, `mergeSha`, `issueId`,
  `documentId`, `parentDocumentId`, `audience`, `correlationId`, `tenantId`.
- **Emitted by the machinery this story wires in:** the `DOCUMENT.*` family (`DocumentEvents.cs:28-53`),
  `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`, `DOCUMENT.REENTERED`.
- **Consumes (reads, does not emit):** its own `USER_DOCS.ACCEPTED`/`API_DOCS.ACCEPTED` history for the
  D3 previous-revision lookup; `MERGE.SUCCESS` (`MergeEvents.cs:42-52`) as the trigger signal.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suite).

- **`DocsRevisionResolverTests`** (pure) — determinism (same inputs twice → byte-identical ids);
  distinct kinds yield distinct scopes for one feature (**the D3/prose collision guard**); distinct merge
  SHAs yield distinct scopes for one kind (freshness); `PreviousScope` on a first run yields none.
- **`FetchPreviousDocRevisionActivityTests`** (Moq'd `IEventRepository` + document repository) — a first
  run yields `Found = false` and an empty previous body (not an error); a second run yields the prior
  accepted body and its id; an unresolvable service ⇒ typed `TammaError`.
- **`UserDocsAuthoringWorkflowStructureTests` and `ApiDocsAuthoringWorkflowStructureTests`** — the
  `TaskCreationWorkflowStructureTests` clause set verbatim, per workflow: builds; stable `DefinitionId`;
  threads `TenantId`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`; **exactly one**
  `DispatchWorkflow`, literal id `document-lifecycle`; **zero** `llm-call`;
  `ScanLifecycleBindingDispatches()` contains the canonical pair; `MaterializeDispatchInput` shows
  `documentType == "prose"` and `feedbackVariableName == "findings"`; **zero** `Finish`; one
  `ComputeReEntryPositionActivity`; the expected `FetchLatestAcceptedDocumentActivity` /
  `FetchPreviousDocRevisionActivity` nodes; `[ResumeBehavior(LatestStateReEntry)]` (C1); no `Wait*` node.
  **Covers AC1, AC3.**
- **`ProseDocumentType` fixtures (`Tamma.Core.Tests`, against 41-1c's type)** — a user-docs payload
  (`kind=user-docs, audience=user`) and an api-docs payload (`kind=api-docs, audience=developer`) each
  validate; unknown `kind`/`audience` fail with 41-1c's distinct codes; an empty `body` fails.
- **`ReviewDocsCellTests`** — **inherited from 41-24 D6** if that story landed; otherwise authored here
  (step 5): the rewritten template declares `workItemJson` (so repair feedback renders) and
  `planJson`/`documentJson` (so the producer's supplied variables render); it instructs the `Review`
  wire; a regression pin that no undeclared variable is relied on.
- **Contract/drift guards (self-verifying, steps 5–6, 8–9)** — `ContractBindingTests` green with both new
  `Bindings` entries at `ProseDocumentType.Validate`; `(tech_writer, review-docs)` classified;
  `ReviewProducerDispatchablePairs_HasNoStaleEntries` and `EveryReviewProducerDispatchablePair_IsClassified`
  green; both universal pins green; `LifecycleBindingWalk_FindsPairs_NotANoOp` finds both new bindings;
  `WorkflowInterfaceGraphTests` pin bumped by 2.
- **`ResumableStandardStructuralTests`** — green with **no** allowlist entry for either new workflow.
  **Covers AC3.**
- **`FeatureDocsExecutionTests`** (Testcontainers, on the 39-6/39-10/39-12 shared fixture) —
  (a) **First run:** accepted `Plan` seeded, no previous doc → both bindings produce, review, accept; two
  accepted `document_instances` rows with **distinct scoped issue ids** and `Audience` = `user` /
  `developer`; `USER_DOCS.*`/`API_DOCS.*` present alongside `DOCUMENT.*`.
  (b) **The prose collision guard:** run `api-docs-authoring` for a feature whose user-docs document is
  already accepted → it still **produces** (no short-circuit on the sibling prose document). Written to
  fail without D3's kind-scoped ids; the single most valuable test in the suite.
  (c) **AC2 — update, not duplicate:** run for merge #1, accept; run for merge #2 → the second run
  **produces** (does not short-circuit), its `producerVariablesJson.findings` contains merge #1's body,
  and its output `parentDocumentId` is merge #1's document id. **Assert exactly two `prose` documents for
  the feature+kind, not two unrelated ones** — and assert explicitly that
  `document_instances.SupersedesDocumentId` is **null** across runs, pinning the known storage gap (C4)
  so it cannot be quietly assumed fixed.
  (d) **Idempotent per merge:** re-dispatch the same `(feature, kind, mergeSha)` → re-entry
  short-circuits to `Complete`, `DOCUMENT.REENTERED`, exactly one `DOCUMENT.ACCEPTED` and one
  `USER_DOCS.ACCEPTED`. **AC2's first half.**
  (e) **Review by `tech_writer` (needs 41-1a):** rules naming `tech_writer` → the review stage completes
  and produces a `Review` whose `ParentDocumentId` is the prose document. **Asserted to THROW today**
  (`RolePhaseMap.cs:385-386`), flipped to the positive assertion when 41-1a merges — so the block is
  visible in CI, not only in prose.
  (f) **Contract-affecting escalation:** rules with `AcceptorRequirement.Human` for the api-docs binding
  at autonomy 100 → the accept gate suspends; the user-docs binding self-accepts under permissive rules.
  **Policy, not code (D9).**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; prose reviewed by a `Review` that checks accuracy against the merged diff | 6, 7, 8 (D1/D4); 5 or inherited (D7) | Both `*StructureTests`; `ProseDocumentType` fixtures; `ContractBindingTests`; Execution (a)(e). **The "against the merged diff" half needs 41-24 D6's rewritten cell — the current template's `{{diff}}` is undeclared and renders empty (C5)** |
| 2 — idempotent per feature; updates existing docs rather than duplicating | 2, 3, 7 (D3) | Execution (c)(d). **Behaviourally satisfied; the `supersedes_document_id` storage edge across runs is NOT (C4) and Execution (c) pins that gap rather than hiding it** |
| 3 — `[ResumeBehavior]`; 39-10 gate green without allowlist | 7 (C1) | `ResumableStandardStructuralTests`; both `*StructureTests`. **Declaration is `LatestStateReEntry`, not `Both` (C1)** |

## Blocks / Blocked by

- **Blocked by — hard:**
  - **41-1c** — `prose` `DocumentTypeKey` + `ProseDocumentType` + `DocumentEnvelope.Audience` +
    `DocumentInstance.Audience` (+ migration) + kinds `user-docs`/`api-docs` + audiences
    `user`/`developer`. Nothing in this story produces a document without it.
  - **Epic 39** — 39-2 (registry/envelope), 39-6 (`document-lifecycle`), 39-7 (`document-review`), 39-8
    (accept gate + resume endpoint), 39-10 (resume standard + gate), 39-11 (store + persist wiring —
    landed, C7). All in tree.
- **Blocked by — REVIEW STAGE ONLY (not the produce stage):**
  - **41-1a** — the `(tech_writer, review-docs)` selector arm. `RolePhaseMap.GetReviewActionForRole`
    throws for `TechWriter` (`:376-387`); `DocumentLifecycleWorkflow` calls it unguarded at **`:1212`**.
    **This is a review-stage block, not a produce-stage one**: steps 2–4 and 6–9 build and unit-test
    without 41-1a, and produce → validate → accept runs end-to-end with a non-`tech_writer` reviewer.
    Only Execution (e) and the roster pins wait. The plan does not work around the gap — Execution (e)
    asserts the throw until 41-1a lands.
  - **41-24 D6** — the *other half* of the review block, which 41-1a does not cover: the `review-docs`
    prompt itself. Inherit it if 41-24 landed; otherwise carry it (step 5).
- **Blocked by — partial (AC-level, named):** **39-17/39-19** (the accept gate publishes and parks),
  **39-20** (no role-addressed delivery — `InitiatorOnlyTaskAudienceResolver`, `Program.cs:445-447`).
  Neither is on an AC.
- **Soft / optional:** **41-1b + 41-2** — `AcceptanceCriteria` is consumed **when present** (D6); its
  absence is tolerated (`Found = false`) and its `Consumes` entry is simply omitted. This story does not
  wait on it.
- **NOT blocked by:** the **scheduled-trigger seam** (merge-triggered, not cron — 41-25 is not in the
  seam's seven-consumer list); **Epic 42** for drafting (D5 keeps `enableTools: false`; only publication
  to a docs host needs 42-9); **41-29** (the `docs` task route is one future trigger, not a prerequisite —
  D2).
- **Blocks:** nothing in Epic 41 depends on 41-25's output. It is a leaf.
- **Shares with 41-24 and 41-26:** the rewritten `(tech_writer, review-docs)` cell + its classification
  entry + `ReviewDocsCellTests`. Exactly one of the three stories authors it; the other two inherit.
  **It must not be dropped if 41-24 slips.**
- **Files, does not fix:** `supersedesDocumentId` as a `document-lifecycle` input + `ResolveSupersedes`
  extension → 39-6/39-11 (C4/D3). A producer/kind filter on the 39-11 latest-accepted read → 39-11
  (already filed at `CreationBindingHelper.cs:84-94`). A richer review subject in
  `DocumentReviewWorkflow.BuildReviewerVariables` → 39-7 (D7). A `pull_request.closed` `IWebhookHandler`
  → the first handler in the codebase (C6).

## Risks & Mitigations

- **AC2 is the hard part and the story treats it as a one-liner.** "Updates existing docs rather than
  duplicating" runs straight into re-entry short-circuiting and the missing cross-run supersedes edge
  (C4). Mitigation: D3 delivers the behaviour with zero generic-layer change, Execution (c) proves it,
  and the storage gap is pinned by an explicit null-assert rather than glossed. If the generic hook lands
  later, D3's scoping stays valid and the edge simply starts being written.
- **Prose is one `DocumentTypeKey` for ten kinds; the 39-11 read has no kind filter.** Two prose docs per
  feature is the collision case, and it fails silently (the second binding short-circuits and produces
  nothing). Mitigation: D3's kind-scoped ids + Execution (b), written specifically to fail without them.
- **The review path has two independent breakages and 41-1a covers only one.** Mitigation: step 5's
  conditional ownership, and Execution (e)'s throw-assert so CI shows the block.
- **"Accuracy against the merged diff" may exceed what the review subject carries.** The producers supply
  `planJson` + `documentJson` only (`DocumentReviewWorkflow.cs:256-265`). Mitigation: D7 folds
  `diffSummary` into the subject the binding hands the lifecycle and files the richer-subject fix to
  39-7 rather than special-casing the reviewer here.
- **A revision-per-merge scheme can produce many prose rows for one feature.** Mitigation: each is a
  distinct accepted revision with a `parentDocumentId` chain and a `mergeSha` tag, which is a lineage,
  not litter; retention/compaction is a store concern (39-11), not this binding's.
- **Story-vs-canon tensions:** C1 (resume mode), C3 (markdown vs JSON payload) and C4 (update-not-
  duplicate) are genuine contradictions, all resolved in favour of the code. C2, C5 and C6 are gaps the
  story does not mention.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition gate + 41-24/41-1a/41-1c lockstep coordination | 0.25 |
| 2–3 | `DocsRevisionResolver` + `FetchPreviousDocRevisionActivity` (the D3 core) | 0.75 |
| 4 | `FeatureDocsEvents` + emit activity | 0.25 |
| 5 | `review-docs.md` rewrite + classification — **0 if inherited from 41-24** | 0.0–0.5 |
| 6 | Two produce templates → prose envelope + kind-appropriate bodies + binding entries | 0.5 |
| 7 | Two producing bindings (incl. three consumed-document reads) | 0.75 |
| 8–9 | Registry seed + edge pin + drift contributor entries | 0.25 |
| 10 | Structure tests ×2, resolver/activity/prose unit tests | 0.75 |
| 10 | Testcontainers scenarios (a)–(f) | 0.75 |
| 11 | Full-suite green, migration check, review polish | 0.25 |
| **Total (inheriting 41-24 D6)** | | **4.5** (story estimate: 3–4 days) |
| **Total (carrying 41-24 D6)** | | **5.0** |

The overrun is C2 (two bindings, not one), C3 (two template rewrites the story did not anticipate) and
D3 (AC2's "update, not duplicate", which is a design problem rather than a line of code).
