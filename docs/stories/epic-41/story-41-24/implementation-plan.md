# Implementation Plan — Story 41-24: Release Notes & Changelog Workflow

## Scope & Deliverable

When this story is done, a cut release produces **two audience-tagged prose documents** on the Epic 39
spine, each through its own thin lifecycle binding:

| New workflow | DefinitionId | produces | producer cell |
|---|---|---|---|
| Release notes | `release-notes-authoring` | `prose` (kind `release-notes`, audience `user`) | `(tech_writer, write-release-notes)` |
| Changelog entry | `changelog-authoring` | `prose` (kind `changelog`, audience `developer`) | `(tech_writer, update-changelog)` |

Both are the `TaskCreationWorkflow`/`DebugDiagnosisWorkflow` skeleton: one `DispatchWorkflow` with literal
id `document-lifecycle`, zero `llm-call`, zero `Finish`, no retry plumbing, a declared
`feedbackVariableName`, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, one
`ComputeReEntryPositionActivity`, one `WorkflowDocumentInterface` row each (edge pin 16 → 18). A shared
`ReleaseWindowResolver` derives the window deterministically from the landed release anchor; a new
`ReleaseDocsEvents` family rides alongside `DOCUMENT.*`. **This story additionally owns the fix that all
three docs stories need**: rewriting `(tech_writer, review-docs)` from a PR-diff prompt into a real
document-review cell (**C5** / **D6**) — without it, the review stage of 41-24, 41-25 and 41-26 renders an
empty prompt and returns an unparseable shape.

## Pre-Reading

- `docs/stories/epic-41/story-41-24/41-24-release-notes-and-changelog.md` — the story (ACs are source of truth, modulo **Corrections** below)
- `docs/stories/epic-41/README.md` — rules 1–5; the 41-1a review-selector gap (`:476-483`); the Epic 42 publish row (`:429`)
- `docs/stories/epic-41/story-41-1/41-1c-prose-documents-and-audience-tags.md` — the `prose` type, `Audience`, the kind/audience vocabularies, D2's prose acceptance row
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — Scope 3 / D1: the `TechWriter` arm on `GetReviewActionForRole` and the 7 → 8 roster
- `docs/stories/epic-39/story-39-12/implementation-plan.md` — THE thin-binding recipe
- `docs/stories/epic-39/story-39-10/implementation-plan.md` — the resume standard + structural gate
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — the template; producer-scoped issue id `:112`, `feedbackVariableName` `:190`, single terminal region `:227-240`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/CreationBindingHelper.cs` — `ScopeIssueId` `:95` and its verbatim rationale `:84-94` (the 39-11 read has **no** producer filter)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` — Init reads `:169-202`; **`IngestDraft` carves the first JSON object out of the reply** `:1170-1197`; review dispatch inputs `:451-466`; `BuildReviewEnvelope` → unguarded `RolePhaseMap.GetReviewActionForRole` at **`:1212`**; persist `:765-777`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentReviewWorkflow.cs` — **`BuildReviewerVariables` supplies exactly `planJson` + `documentJson`** `:256-265`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewProducerHelper.cs` — `DefaultFeedbackVariable = "workItemJson"` `:203`, `BuildRepairVariables` `:168-201` (an **undeclared** feedback variable is silently dropped at render)
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:376-387` — `GetReviewActionForRole` throws for `TechWriter`; `:430-433` `GetPanelActionForRole`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs:60-70` — the 7-role `s_documentRoster`; `:178` `AllDispatchablePairs`
- `apps/tamma-elsa/src/Tamma.Api/Prompts/tech_writer/write-release-notes.md`, `update-changelog.md`, `review-docs.md` — read in full; front matter and instructed shapes quoted in **C4**/**C5**
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs` — release tag `deploy-{shortSha}` `:334-346`, `CreateRelease` `:354-382`, outputs `:438-451`; dispatched from `SingleIssueCycleWorkflow.cs:725`
- `apps/tamma-elsa/src/Tamma.Activities/ADL/DeployEvents.cs:77,84` — `RELEASE.CREATED.SUCCESS` / `.FAILED`; `ADL/MergeEvents.cs:42-52` — `MERGE.SUCCESS` etc.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs:200-206` — `QueryEventsAsync(...typeIsPrefix..., from, to...)`, the 4-7 window surface; semantics `:177-198`
- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IWebhookHandler.cs` + `Tamma.Platforms/Webhooks/WebhookEventDispatcher.cs` — the inbound-event seam (**zero handlers registered anywhere** — C7)
- `.github/workflows/release.yml` — `on: push: tags: ['v*']` `:3-5`; the `create-release` job `:151-209` whose body is a hardcoded install template `:179-204`
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the reference structure test
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` (`Bindings` `:82`, `ReviewProducerDispatchablePairs` `:505`, roster pin `:598` `HaveCount(16)`, universal pins `:626`/`:655`), `ReviewerSelectionHelperTests.cs:97`, `TaxonomyDriftBuildTests.cs:125`/`:460`, `Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:44`
- **NOT FOUND:** `DocumentTypeKey.Prose` / `ProseDocumentType` / `DocumentEnvelope.Audience` / `DocumentInstance.Audience` (41-1c); the `TechWriter` arm on `GetReviewActionForRole` (41-1a); any `CHANGELOG.md` anywhere in the repo; any git tag; any `RELEASE_NOTES.*` or `CHANGELOG.*` event constant; any registered `IWebhookHandler`. Everything else above exists and was read.

## Corrections to the story

- **C1 — AC3's `[ResumeBehavior(Both)]` fails the 39-10 gate.** `Both` requires a canonical suspend node
  **in this workflow's own graph** (`ResumableStandardStructuralTests.cs:158-198`, plus the honesty
  inverse at `:202-236`). A thin binding never suspends — the accept gate lives inside the dispatched
  `document-lifecycle` child (39-12 D7; the landed precedents `TaskCreationWorkflow.cs:47` and
  `DebugDiagnosisWorkflow.cs:38` both declare `LatestStateReEntry`, and
  `TaskCreationWorkflowStructureTests:106` pins "no `Wait*` activity"). **Correct declaration:
  `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`.**
- **C2 — two prose documents cannot ride one lifecycle dispatch.** `DocumentLifecycleWorkflow` reads
  exactly one `producerRole`/`producerAction`/`documentType` (`:169-172`). Release notes and changelog are
  **two producing workflows**, each a single-dispatch thin binding (D1). Rule 1(a) holds per binding.
- **C3 — there is no changelog and no release convention in this repo to follow.** Verified: **no
  `CHANGELOG.md` anywhere** (root, `packages/*`, `apps/*`, `docs/`), no `.changeset/`, no `RELEASING.md`,
  no `semantic-release`/`release-please`/`standard-version`/`commitlint`. **Zero git tags exist**, so
  `.github/workflows/release.yml`'s `on: push: tags: ['v*']` (`:3-5`) has never fired. Its `create-release`
  job body is a hardcoded install/checksums template with no notes and no `generate_release_notes`
  (`:179-204`) — that is precisely the hole this story fills, but it also means **41-24 introduces the
  convention rather than conforming to one.** Version fields are inconsistent: every npm package is
  `0.1.0` (root `package.json:3`, `packages/cli/package.json:3`, …) while every `.csproj` hardcodes
  `<Version>1.0.0</Version>` (line 9 of each; `Directory.Build.props` carries none). There is no single
  source of truth linking them. **D3** picks the anchor that actually exists in the running system.
- **C4 — both produce prompts are the wrong shape and instruct raw markdown.**
  `write-release-notes.md` and `update-changelog.md` share byte-identical front matter
  (`variables: role, workItemJson, findings, audience` / `enableTools: false` / `maxTokens: 2048` /
  `version: 1`) and a byte-identical `## Summary / ### Key Findings / ### Action Items / ### Details`
  block; only line 18 differs. Two problems: (i) `DocumentLifecycleWorkflow.IngestDraft` (`:1177-1180`)
  carves the **first JSON object** out of the reply and fails the produce turn when there is none, so raw
  markdown cannot be a lifecycle payload — the markdown must move inside 41-1c's prose envelope
  `{kind, audience, title, body}`; (ii) `update-changelog.md` is a **misnomer** — it updates no changelog,
  emits no version heading, no date, no file target, no Added/Changed/Fixed/Removed structure beyond
  "phrase Key Findings as changelog lines". Both templates are rewritten (D4). The good news: both
  already **declare an `audience` variable**, so 41-1c's audience tag threads with no front-matter fight.
- **C5 — `(tech_writer, review-docs)` is a PR-diff cell, and 41-1a does not fix that.** 41-1a Scope 3 / D1
  adds only the **selector arm** so `GetReviewActionForRole(TechWriter)` stops throwing. The prompt itself
  is unusable as a document reviewer, three ways over:
  1. it declares `variables: role, prDescription, diff, conventions`, and
     `DocumentReviewWorkflow.BuildReviewerVariables` supplies **only** `planJson` and `documentJson`
     (`:256-265`) — so every substantive placeholder renders empty;
  2. it does not declare `workItemJson`, which is `ReviewProducerHelper.DefaultFeedbackVariable`
     (`:203`), and `BuildRepairVariables` (`:168-201`) writes repair feedback only into a **declared**
     variable — so repair notes are silently dropped (the render-drop lesson);
  3. it instructs a diff-review JSON (`issues[{file,line,severity,category,issue,fix}]` +
     `summary{decision,text,filesReviewed,issuesBySeverity}`), not the `Review` wire
     (`subject`/`decision`/`summary`/`issues[{severity,category,description,suggestedFix,file?,line?}]`,
     `Review.cs:155-194`).
  **Nobody owns this.** As the first consumer, this story does (D6). 41-25 and 41-26 inherit the fix.
- **C6 — "publish has no tool" is right about tools and understates the reach.** Exactly six
  `IToolExecutor`s are registered (`Tamma.Api/Program.cs:753-764`), none of which publishes. **But** the
  platform already cuts real git-platform releases: `CreateReleaseActivity` inside `deployment-pipeline`
  (`:354-382`) posts through the mediated `POST /api/v1/git/{owner}/{repo}/releases` seam and emits
  `RELEASE.CREATED.SUCCESS`/`.FAILED` (`DeployEvents.cs:77,84`). So publishing accepted notes **to the
  release body is reachable today as an activity**, exactly as 41-23's signal read is (D3 there). What is
  genuinely missing is publishing to an arbitrary wiki/docs host — that is 42-9. **D7** scopes publication
  accordingly instead of deferring all of it.
- **C7 — the release trigger has a seam but no wiring.** `IWebhookHandler` /
  `IWebhookEventDispatcher` exist (`Tamma.Platforms.Abstractions/IWebhookHandler.cs`,
  `Tamma.Platforms/Webhooks/WebhookEventDispatcher.cs`), the generalised receiver is mapped
  (`Program.cs:2980-2981`), and event-type patterns support `"release.*"` — but **`RegisterHandler` is
  called from exactly zero production sites.** A GitHub-`release`-webhook trigger would be the first
  handler in the codebase. **D3** avoids taking that on: the in-repo release anchor is used, and the
  webhook route is recorded as the natural extension.
- **C8 — the epic's `(tech_writer, review-docs)` claim is otherwise accurate.**
  `RolePhaseMap.GetReviewActionForRole` covers 7 of 8 roles and throws for `TechWriter` (`:376-387`);
  `DocumentLifecycleWorkflow` calls it unguarded — at **`:1212`**, not `:1199` (the story's cite is 13
  lines early); `ReviewerSelectionHelper.ResolveDocumentAction` rethrows it as an invalid-reviewer error
  (`:153-168`). `ReviewerSelectionHelper.s_documentRoster` has 7 entries (`:61-70`).
- **C9 — `.dev/findings/document-lifecycle-persist-not-wired.md` is STALE.** Persistence *is* wired
  (`DocumentLifecycleWorkflow.cs:770-777`). Do not plan around it.

## Design Decisions

- **D1 — Two producing bindings, no orchestrating parent (per C2).** `release-notes-authoring` and
  `changelog-authoring` are independent single-dispatch thin bindings over the SAME window. They are
  dispatched in parallel by whatever triggers them (D3), not sequenced — neither consumes the other. Each
  passes the `TaskCreationWorkflowStructureTests` clause set verbatim and declares its own
  `WorkflowDocumentInterface` row; the edge pin moves **16 → 18** in this story's commit (epic rule 1(f)).
  No sequencer is built, so this story ships **no** workflow that dispatches anything other than
  `document-lifecycle`.
- **D2 — Producer-scoped, window-scoped issue identity is MANDATORY for prose.** 41-1c gives all ten
  prose kinds one `DocumentTypeKey`, and the 39-11 latest-accepted / re-entry read scopes by
  `(issueId, documentType)` with **no producer and no kind filter** — stated verbatim in
  `CreationBindingHelper.cs:84-94` and filed to 39-11. Without scoping, the changelog binding would
  re-enter on the accepted release-notes document (same issue, same type `prose`) and short-circuit
  without ever producing. Therefore:
  `releaseId = "release#{repository}#{releaseTag}"`, and each binding keys its lifecycle on
  `ScopeIssueId(releaseId, "release-notes")` / `ScopeIssueId(releaseId, "changelog")`. This is the 39-15
  D2 move, and prose makes it non-optional rather than merely prudent. **The prose-collision hazard is
  general to 41-4, 41-5, 41-8, 41-9, 41-22, 41-25 and 41-26 too** — this plan is the first to state it;
  it belongs in 41-1c's own guidance.
- **D3 — the window is derived from the landed in-repo release anchor, not from git tags (per C3/C7).**
  `deployment-pipeline` computes `releaseTag = "deploy-{shortSha}"` from the merged SHA
  (`DeploymentPipelineWorkflow.cs:334-346`), cuts the real platform release, and emits
  `RELEASE.CREATED.SUCCESS` (`DeployEvents.cs:77`). That is the only release event the running system
  produces, and it is **per merged issue**, not a batched version tag. So:
  `ReleaseWindowResolver.Resolve(repository, releaseTag, previousReleaseTag?)` returns a half-open
  `[from, to)` and the merged work in it, read via
  `IEventRepository.QueryEventsAsync(tenantId, type: "MERGE.", typeIsPrefix: true, from, to, …)` — the
  4-7 surface, whose half-open `from <= t < to` semantics are documented at `IEventRepository.cs:177-179`.
  `from` = the previous `RELEASE.CREATED.SUCCESS` for the repository (or the epoch on the first release),
  `to` = this release's timestamp. **Deterministic and replayable** — AC2's real content. Two extensions
  are recorded but **not built here**: a `release`-webhook `IWebhookHandler` (C7, the first in the
  codebase) and a `v*`-tag batched window once tags exist (C3).
- **D4 — both produce templates are rewritten to 41-1c's prose envelope, and `update-changelog` gets a
  real changelog shape (per C4).** Front matter becomes
  `variables: role, workItemJson, findings, audience, releaseTag, conventions`; `findings` stays the
  declared `feedbackVariableName` carrier (it is already declared, so repair/revise notes land, not drop).
  Body instructs `{"kind": "release-notes"|"changelog", "audience": "user"|"developer", "title": …,
  "body": "<markdown>"}`. Inside `body`: release notes lead with user-visible improvements and call out
  breaking changes / upgrade steps (the current line-18 intent, preserved); the changelog emits a
  **Keep-a-Changelog-shaped** section — `## {releaseTag} — {ISO date}` followed by
  `### Added / ### Changed / ### Fixed / ### Removed` with one user-visible change per line, omitting
  empty groups. Since C3 establishes there is no incumbent convention, **this story's template is the
  convention**, and that is recorded in the story rather than assumed.
- **D5 — `enableTools` stays `false` on both produce cells.** The window content is assembled by the
  binding from the event query (D3) and handed in through `producerVariablesJson`; the producer does not
  need to go looking. This keeps the produce step reachable with zero Epic 42 dependency and keeps the
  reply deterministic enough for the prose envelope.
- **D6 — THIS story rewrites `(tech_writer, review-docs)` into a document-review cell (per C5).** New
  front matter: `variables: role, workItemJson, planJson, documentJson, conventions` — `planJson` and
  `documentJson` because those are what `DocumentReviewWorkflow.BuildReviewerVariables` actually supplies
  (`:256-265`), and `workItemJson` because it is `ReviewProducerHelper.DefaultFeedbackVariable` (`:203`)
  and must be declared for repair feedback to render. Body: review the prose document for accuracy against
  the release window, completeness, audience fit and house conventions; output the **`Review` wire**
  (`subject`/`decision`/`summary`/`issues[]`). The pair is then classified in
  `ContractBindingTests.ReviewProducerDispatchablePairs` (policy-only, no compiled emitter — the same
  bucket the other 7 document-review pairs sit in, `:505-517`). **41-1a's AC9 anticipates exactly this**
  ("classifies every newly-dispatchable reviewer pair"); this plan pins who writes it. Lockstep with the
  41-1a owner: 41-1a moves the roster pins (`ContractBindingTests.cs:598` and
  `ReviewerSelectionHelperTests.cs:97`, `HaveCount(16)` → `17`), this story supplies the classification
  entry and the template.
- **D7 — publication is split: release body = an activity today; docs host = 42-9 (per C6).** Post-accept,
  the accepted release-notes prose `body` can be written to the git-platform release created by
  `deployment-pipeline`, through the same mediated seam `CreateReleaseActivity` already uses. **This story
  builds no publish step** — it exposes `documentId`, `documentJson`, `releaseTag` and `audience` as
  outputs and emits `RELEASE_NOTES.ACCEPTED` so a publisher can act. The reason is scope discipline, not
  impossibility, and the plan says which of the two it is: the release-body path is *buildable now*; the
  wiki/docs-host path is *blocked on 42-9*. The story's blanket "publication is human-assigned until Epic
  42 lands" is refined accordingly.
- **D8 — a new `ReleaseDocsEvents` family; nothing named `RELEASE_NOTES.*`/`CHANGELOG.*` exists.**
  Constants: `RELEASE_NOTES.STARTED`, `RELEASE_NOTES.DRAFTED`, `RELEASE_NOTES.ACCEPTED`,
  `RELEASE_NOTES.FAILED`, `CHANGELOG.STARTED`, `CHANGELOG.UPDATED`, `CHANGELOG.FAILED`.
  `StatusForEvent`: `FAILED` → `"error"`, `STARTED` → `"started"`, else `"success"`. Tags `repository`,
  `releaseTag`, `windowStart`, `windowEnd`, `documentId`, `audience`, `correlationId`, `tenantId`. One
  `EmitReleaseDocsEventActivity`, copying `EmitDecompositionEventActivity`'s shape (pure static
  `BuildTammaEvent` + `TammaEventEmitter.Emit`). `RELEASE.CREATED.*` stays `deployment-pipeline`'s and is
  not touched.
- **D9 — acceptance is policy passthrough; the customer-facing escalation is a rules row.** Each binding
  forwards `acceptanceRulesJson` verbatim (39-12 D8). "A customer-facing release can be a configured
  always-escalate class" is `AcceptorRequirement.Human` in the caller's rules — never an if-else here.
  Note 41-1c D2 sets prose's *default* to a `tech_writer` single-reviewer row; the changelog (audience
  `developer`) is the natural self-accept case and the release notes (audience `user`) the escalate case,
  and both are expressed as rules, not code.

## Implementation Steps

1. **Precondition gate (no code).** Verify in tree and compiling: **41-1c** (`DocumentTypeKey.Prose`,
   `ProseDocumentType` registered, `DocumentEnvelope.Audience`, `DocumentInstance.Audience` + migration,
   the kind vocabulary containing `release-notes` and `changelog`, the audience vocabulary containing
   `user` and `developer`) and **41-1a** (`GetReviewActionForRole(TechWriter) == ReviewDocs`,
   `ReviewerSelectionHelper.DocumentPanelRoster` has 8 entries, the roster pins moved to 17). Any gap
   blocks the corresponding step — file it, do not work around it. **Note the asymmetry the story records
   correctly: 41-1a blocks the REVIEW stage only.** Steps 2–8 (produce + accept) can be built and
   unit-tested against a non-`tech_writer` reviewer before 41-1a lands; only step 9's review path and the
   end-to-end execution scenarios need it.

2. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReleaseWindowResolver.cs`** (D3) —
   pure, Elsa-free, total: `ComposeReleaseId(repository, releaseTag)`, `ScopeFor(releaseId, kind)`,
   and a `ReleaseWindow` record. The I/O half (`FetchReleaseWindowActivity`, step 3) calls it.
   Determinism is the point: same `(repository, releaseTag)` → byte-identical ids and window bounds.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/ReleaseDocs/FetchReleaseWindowActivity.cs`** (D3) —
   inputs `TenantId`, `Repository`, `ReleaseTag`; outputs `WindowStart`, `WindowEnd`, `MergedWorkJson`.
   Resolves `IEventRepository` via `context.GetService<T>()` (the `ComputeReEntryPositionActivity`
   pattern); reads `RELEASE.CREATED.SUCCESS` (for the previous boundary) and `MERGE.` prefixed events for
   the half-open window; fail-loud `TammaError RELEASE.WINDOW.SERVICE_UNREGISTERED` /
   `RELEASE.WINDOW.UNRESOLVED` — never an empty window that would silently produce empty notes.

4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/ReleaseDocs/ReleaseDocsEvents.cs` +
   `EmitReleaseDocsEventActivity.cs`** (D8) — copy `Decomposition/DecompositionEvents.cs` +
   `EmitDecompositionEventActivity.cs` shape exactly.

5. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/tech_writer/write-release-notes.md`** (D4) —
   prose envelope, `version: 2`, front matter per D4, body per D4's release-notes half.

6. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/tech_writer/update-changelog.md`** (D4) — prose
   envelope, `version: 2`, Keep-a-Changelog `body` shape. **Rename intent, not the cell**: the action
   token `update-changelog` stays (renaming an `AgentAction` is a taxonomy change and belongs to 41-1a);
   the doc-comment records that the cell *authors a changelog entry document*, and does not write a file.

7. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/tech_writer/review-docs.md`** (C5/D6) — the
   document-review cell. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`**:
   add `[("tech_writer","review-docs")]` to `ReviewProducerDispatchablePairs` with the justification
   *"document-review producer pair: tech_writer reviews a prose document via review-docs; policy-only, no
   compiled emitter."* (Lockstep: 41-1a moves `ContractBindingTests.cs:598` and
   `ReviewerSelectionHelperTests.cs:97` from `HaveCount(16)` to `17`.)

8. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ReleaseNotesAuthoringWorkflow.cs`
   (`release-notes-authoring`) and `ChangelogAuthoringWorkflow.cs` (`changelog-authoring`)** (D1/D2) —
   both the `TaskCreationWorkflow` skeleton, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (C1),
   graph `ReadInputs → ComputeReEntryPosition → FreshRun? → FetchReleaseWindow → DispatchLifecycle →
   ReadLifecycleExit → ExposeOutput`, zero `Finish`. Dispatch inputs:
   ```csharp
   ["documentType"]          = "prose",
   ["producerRole"]          = AgentRole.TechWriter.ToWire(),
   ["producerAction"]        = AgentAction.WriteReleaseNotes.ToWire(),   // / UpdateChangelog
   ["producerVariablesJson"] = { workItemJson = mergedWorkJson, findings = "",
                                 audience = "user" /* "developer" */, releaseTag, conventions = "" },
   ["feedbackVariableName"]  = "findings",
   ["issueId"]               = ScopeIssueId(releaseId, "release-notes" /* "changelog" */),
   ["correlationId"]         = same, ["tenantId"], ["acceptanceRulesJson"],
   ```
   Outputs: `status`, `outcome`, `documentId`, `documentJson`, `audience`, `releaseTag`,
   `windowStart`, `windowEnd` (D7's publisher hooks).

9. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs` (`BuildSeed`)** — two rows,
   `Provisional=false`:
   `new WorkflowDocumentInterface("release-notes-authoring", empty, DocumentTypeKey.Prose, false)` and
   `new WorkflowDocumentInterface("changelog-authoring", empty, DocumentTypeKey.Prose, false)`.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:44`** —
   `HaveCount(16)` → `HaveCount(18)` with the one-line reason.
   *(`Consumes` is left empty: the window is read from the event stream, not from an accepted document.)*

10. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:125`** —
    add `ReleaseNotesAuthoringWorkflow` and `ChangelogAuthoringWorkflow` to
    `ExpectedContributingWorkflows`, one comment each.

11. **CREATE the test suites** (see Test Plan).

12. **Finish** with full `dotnet test` and `dotnet ef migrations has-pending-model-changes` (clean —
    this story adds no schema; `Audience` is 41-1c's migration).

## Data & Migrations

None. Documents persist through 39-11's `document_instances` via the lifecycle's own
`PersistAccepted`/`PersistRevised`/`PersistRejected`/`PersistEscalated` nodes
(`DocumentLifecycleWorkflow.cs:770-777` — C9); `RELEASE_NOTES.*`/`CHANGELOG.*` ride the existing
`TammaEventEmitter` drain → `EventRepository` → `domain_events`. The `Audience` column belongs to
**41-1c**. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new):** `RELEASE_NOTES.STARTED`/`.DRAFTED`/`.ACCEPTED`/`.FAILED`,
  `CHANGELOG.STARTED`/`.UPDATED`/`.FAILED` — tags `repository`, `releaseTag`, `windowStart`, `windowEnd`,
  `documentId`, `audience`, `correlationId`, `tenantId`.
- **Emitted by the machinery this story wires in:** the `DOCUMENT.*` family (`DocumentEvents.cs:28-53`),
  `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`, `DOCUMENT.REENTERED`.
- **Consumes (reads, does not emit):** `RELEASE.CREATED.SUCCESS` (`DeployEvents.cs:77`) as the window
  boundary and `MERGE.*` (`MergeEvents.cs:42-52`) as the window content, both via
  `IEventRepository.QueryEventsAsync` (D3).
- **Not minted here:** `RELEASE.CREATED.*` stays `deployment-pipeline`'s.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suite).

- **`ReleaseWindowResolverTests`** (pure) — **AC2's real content**: same `(repository, releaseTag)` twice
  → byte-identical release id, scoped ids and window bounds; the window is half-open (an event at
  `windowEnd` is excluded, matching `IEventRepository.cs:177-179`); the first release (no previous
  `RELEASE.CREATED.SUCCESS`) yields an epoch-anchored window rather than throwing; distinct tags yield
  distinct scoped ids (the D2 collision guard).
- **`FetchReleaseWindowActivityTests`** (Moq'd `IEventRepository`) — queries with
  `typeIsPrefix: true` and the right `(type, from, to)`; unresolvable service ⇒ typed `TammaError`;
  a window with no merges is reported explicitly, never as an empty success.
- **`ReleaseNotesAuthoringWorkflowStructureTests` and `ChangelogAuthoringWorkflowStructureTests`** — the
  `TaskCreationWorkflowStructureTests` clause set verbatim, per workflow: builds; stable `DefinitionId`;
  threads `TenantId`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`; **exactly one**
  `DispatchWorkflow`, literal id `document-lifecycle`; **zero** `llm-call`;
  `ScanLifecycleBindingDispatches()` contains the canonical pair; `MaterializeDispatchInput` shows
  `documentType == "prose"` and `feedbackVariableName == "findings"`; **zero** `Finish`; one
  `ComputeReEntryPositionActivity`; one `FetchReleaseWindowActivity`;
  `[ResumeBehavior(LatestStateReEntry)]` (C1); no `Wait*` node. **Covers AC1, AC3.**
- **`ProseDocumentType` fixtures (`Tamma.Core.Tests`, against 41-1c's type)** — a release-notes payload
  (`kind=release-notes, audience=user`) and a changelog payload (`kind=changelog, audience=developer`)
  each validate; an unknown `kind`/`audience` each fail with 41-1c's distinct codes; an empty `body`
  fails. **Covers AC1's "audience-tagged prose" half.**
- **`ReviewDocsCellTests`** (D6) — the rewritten template declares `workItemJson` (so
  `ReviewProducerHelper.BuildRepairVariables` can write into it) and `planJson`/`documentJson` (so
  `DocumentReviewWorkflow.BuildReviewerVariables` renders); it instructs the `Review` wire tokens; **a
  regression pin that no undeclared variable is relied on** — the render-drop guard. **This suite is the
  shared asset 41-25 and 41-26 inherit.**
- **Contract/drift guards (self-verifying, steps 5–7, 9–10)** — `ContractBindingTests` green:
  both produce cells appear in `Bindings` with authority `ProseDocumentType.Validate`
  (**not** `IntentionallyUnbound` — a document producer must be bound, `:655-674`);
  `(tech_writer, review-docs)` classified in `ReviewProducerDispatchablePairs` and
  `ReviewProducerDispatchablePairs_HasNoStaleEntries` green; `EveryReviewProducerDispatchablePair_IsClassified`
  green after 41-1a's roster grows; both universal pins green;
  `LifecycleBindingWalk_FindsPairs_NotANoOp` finds both new bindings;
  `WorkflowInterfaceGraphTests` `HaveCount(18)`.
- **`ResumableStandardStructuralTests`** — green with **no** allowlist entry for either new workflow.
  **Covers AC3.**
- **`ReleaseDocsExecutionTests`** (Testcontainers, on the 39-6/39-10/39-12 shared fixture: real
  `DocumentLifecycleWorkflow` + `DocumentReviewWorkflow` + both new bindings, stub `llm-call`, real Elsa
  EF persistence + event drain + `IDocumentInstanceRepository`, decisions injected via
  `DocumentDecisionResumeEndpoint.Resume`) —
  (a) **Happy path, both documents:** seeded `MERGE.*` + `RELEASE.CREATED.SUCCESS` rows → both bindings
  run → two accepted `document_instances` rows, **distinct scoped issue ids** (D2), types `prose`,
  `Audience` = `user` and `developer` respectively; both `RELEASE_NOTES.*` and `CHANGELOG.*` present
  alongside `DOCUMENT.*` with matching `releaseTag` tags. **AC1.**
  (b) **The D2 collision guard, as a real test:** run the changelog binding for a release whose
  release-notes document is already accepted → it still **produces** (it does not short-circuit on the
  sibling prose document). This is the scenario that fails without producer scoping; it is the single
  most valuable test in the suite.
  (c) **Idempotent re-run (AC2):** re-dispatch the same `(repository, releaseTag)` → re-entry
  short-circuits to `Complete`, `DOCUMENT.REENTERED`, exactly **one** `DOCUMENT.ACCEPTED` and one
  `RELEASE_NOTES.ACCEPTED` on the stream.
  (d) **Review by `tech_writer` (needs 41-1a):** acceptance rules naming `tech_writer` as reviewer →
  the review stage completes and produces a `Review` whose `ParentDocumentId` is the prose document.
  **Asserted to THROW today** (`GetReviewActionForRole` at `RolePhaseMap.cs:385-386`), with the test
  flipped to the positive assertion when 41-1a merges — so the block is visible in CI, not in prose.
  (e) **Always-escalate:** rules with `AcceptorRequirement.Human` at autonomy 100 → the release-notes
  accept gate suspends and publishes an `AcceptanceRequest`; the changelog self-accepts under permissive
  rules. **Policy, not code (D9).**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; both outputs ride the lifecycle as audience-tagged prose reviewed by a `Review` | 5, 6, 7, 8, 9 (D1/D2/D4/D6) | Both `*StructureTests`; `ProseDocumentType` fixtures; `ContractBindingTests` (bound, not allowlisted); Execution (a)(b)(d). **"Reviewed by a `Review`" is unreachable until 41-1a AND D6's template rewrite both land — D6 supplies the half nobody owned** |
| 2 — deterministic window derivation; idempotent re-run | 2, 3, 8 (D3/D2) | `ReleaseWindowResolverTests`; Execution (c). **The anchor is `deployment-pipeline`'s per-issue `deploy-{sha}` release, not a version tag — zero tags exist (C3)** |
| 3 — `[ResumeBehavior]`; 39-10 gate green without allowlist | 8 (C1) | `ResumableStandardStructuralTests`; both `*StructureTests`. **Declaration is `LatestStateReEntry`, not `Both` (C1)** |

## Blocks / Blocked by

- **Blocked by — hard:**
  - **41-1c** — `prose` `DocumentTypeKey` + `ProseDocumentType` + `DocumentEnvelope.Audience` +
    `DocumentInstance.Audience` (+ migration) + the `release-notes`/`changelog` kinds and
    `user`/`developer` audiences. Nothing in this story produces a document without it.
  - **Epic 39** — 39-2 (registry/envelope), 39-6 (`document-lifecycle`), 39-7 (`document-review`), 39-8
    (accept gate + resume endpoint), 39-10 (resume standard + gate), 39-11 (store + persist wiring —
    landed, C9). All in tree.
  - **4-7** — `IEventRepository.QueryEventsAsync` for the window (`:200-206`). Landed.
- **Blocked by — REVIEW STAGE ONLY (not the produce stage):**
  - **41-1a** — the `(tech_writer, review-docs)` selector arm. `RolePhaseMap.GetReviewActionForRole`
    throws for `TechWriter` (`:376-387`) and `DocumentLifecycleWorkflow` calls it unguarded at
    **`:1212`**, so a `tech_writer`-reviewed lifecycle faults at runtime. **This is a review-stage block,
    not a produce-stage one**: steps 2–8 build and unit-test without 41-1a, and the produce→validate→
    accept path runs end-to-end with a non-`tech_writer` reviewer. Only Execution (d) and the
    `tech_writer` roster pins wait. The plan is written that way deliberately and does **not** work
    around the gap — Execution (d) asserts the throw until 41-1a lands.
  - **This story owns the other half of that block (C5/D6)**: 41-1a fixes the *selector*; the *prompt* is
    a PR-diff cell that nobody owned. Without D6, adding the selector arm produces a review that renders
    empty placeholders and returns the wrong shape.
- **Blocked by — partial (AC-level, named):** **39-17/39-19** — the accept gate publishes and parks;
  **39-20** — no role-addressed delivery (`InitiatorOnlyTaskAudienceResolver`, `Program.cs:445-447`).
  Neither is on an AC.
- **NOT blocked by:** **41-1b** (no new document type); the **scheduled-trigger seam** (this workflow is
  release-triggered, not cron — 41-24 is *not* in the seam's seven-consumer list); **Epic 42** for the
  produce and accept path (D5 keeps `enableTools: false`; D7 splits publication and shows the
  release-body path is buildable today via `CreateReleaseActivity`'s mediated seam — only the
  wiki/docs-host path needs 42-9).
- **Blocks:**
  - **41-25** and **41-26** — both specify review via `(tech_writer, review-docs)` and both inherit
    **D6**'s rewritten cell, its `ReviewProducerDispatchablePairs` classification and `ReviewDocsCellTests`.
    Whichever of the three lands first must carry D6; this plan assigns it to 41-24 as the first consumer.
    If 41-24 slips, the D6 work moves with the first docs story to ship — it must not be dropped.
  - **41-25** additionally inherits **D2**'s prose scoping convention and **D4**'s prose-envelope template
    pattern.
- **Files, does not fix:** a producer/kind filter on the 39-11 latest-accepted read → 39-11 (already
  filed at `CreationBindingHelper.cs:84-94`; prose makes it acute — D2). A `release`-webhook
  `IWebhookHandler` → the first handler in the codebase (C7). Reconciling the `0.1.0` npm / `1.0.0`
  .NET version split and `release.yml`'s notes-free release body (C3) → release-engineering, not this
  story.

## Risks & Mitigations

- **Prose is one `DocumentTypeKey` for ten kinds and the 39-11 read has no kind filter.** Two prose
  documents for one release is the *first* place this bites, and it bites silently: the second binding
  short-circuits as "already accepted" and produces nothing. Mitigation: D2's mandatory producer scoping
  plus Execution (b), which is written specifically to fail without it. Flagged for 41-1c's guidance —
  every prose consumer needs this rule.
- **The review path has two independent breakages and only one is owned.** Mitigation: D6 takes the
  unowned half explicitly and Execution (d) asserts the current throw so CI shows the block rather than a
  document quietly reviewed by an empty prompt.
- **No incumbent release convention (C3), so the template *is* the spec.** A later real changelog file or
  a `v*` tagging scheme could contradict it. Mitigation: D4 picks Keep-a-Changelog (the most common,
  least surprising shape), D3's window is anchored on an event rather than a tag so a future tagging
  scheme is additive, and the choice is recorded in the story rather than buried in a prompt.
- **The release anchor is per-issue, not per-version.** "Release notes" for a single merged issue is
  thinner than the phrase implies. Mitigation: `ReleaseWindowResolver` takes an optional
  `previousReleaseTag`, so batching across N deploys is a caller decision, not a rewrite; the batched
  `v*`-tag window is recorded as the extension (C3/D3).
- **`update-changelog` authors a document and updates no file.** A reader of the action name will expect
  a file write. Mitigation: D4 records the semantics in the template's doc-comment and D7 states where
  publication would land; renaming the action token is a 41-1a taxonomy change and is deliberately not
  done here.
- **Story-vs-canon tensions:** C1 (resume mode) and C4 (markdown vs JSON payload) are genuine
  contradictions, resolved in favour of the code. C2, C3, C5 and C7 are gaps the story does not mention.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition gate + 41-1a/41-1c lockstep coordination | 0.25 |
| 2–3 | `ReleaseWindowResolver` + `FetchReleaseWindowActivity` | 0.75 |
| 4 | `ReleaseDocsEvents` + emit activity | 0.25 |
| 5–6 | Two produce templates → prose envelope + Keep-a-Changelog shape | 0.5 |
| 7 | **`review-docs.md` rewrite + classification entry (the shared, previously unowned fix)** | 0.5 |
| 8 | Two producing bindings | 0.75 |
| 9–10 | Registry seed + edge pin + drift contributor entries | 0.25 |
| 11 | Structure tests ×2, resolver/activity/prose/review-cell unit tests | 0.75 |
| 11 | Testcontainers scenarios (a)–(e) | 0.75 |
| 12 | Full-suite green, migration check, review polish | 0.25 |
| **Total** | | **5.0** (story estimate: 3–4 days) |

The overrun is C2 (two bindings, not one), C4 (two template rewrites the story did not anticipate) and
step 7 (D6's shared review-cell fix, which the story assumed 41-1a covered). **If 41-25 or 41-26 ships
first and carries D6, drop 0.5 d and the total is 4.5.**
