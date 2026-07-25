# Implementation Plan — Story 41-22: Incident Response & Postmortem Workflow

## Scope & Deliverable

When this story is done, an operational incident runs end-to-end on the Epic 39 spine as **three thin
lifecycle bindings plus one thin sequencer**, with no bespoke parse, no `llm-call`, and no `Finish`:

| New workflow | DefinitionId | produces | producer cell |
|---|---|---|---|
| Incident root-cause | `incident-diagnosis` | `diagnosis` | `(devops, incident-rootcause)` — minted by **41-1a** |
| Incident response plan | `incident-response-plan` | `plan` | `(devops, plan-incident-response)` — exists |
| Postmortem | `incident-postmortem` | `prose` (kind `postmortem`, audience `engineering`) | `(devops, write-postmortem)` — exists |
| Sequencer | `incident-response` | — (produces nothing) | — |

Each producing binding is byte-for-byte the `DebugDiagnosisWorkflow` / `TaskCreationWorkflow` shape:
`ReadInputs → ComputeReEntryPosition → DispatchLifecycle("document-lifecycle") → ReadLifecycleExit →
ExposeOutput`, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, zero `Finish`, zero `llm-call`, zero
validate/retry variables, a declared `feedbackVariableName`, and a `WorkflowDocumentInterface` row. The
sequencer dispatches the three children (and, where a production rollback is warranted, escalates rather
than pretending it can perform one — see **Corrections C5**). A new `INCIDENT.*` event family
(`IncidentEvents` + `EmitIncidentEventActivity`) rides alongside `DOCUMENT.*`/`APPROVAL.*`/`ESCALATION.*`.
Three prompt cells are rewritten to their typed contracts. The edge pin moves 16 → 19.

## Pre-Reading

- `docs/stories/epic-41/story-41-22/41-22-incident-response-and-postmortem.md` — the story (ACs are source of truth, modulo **Corrections to the story** below)
- `docs/stories/epic-41/README.md` — rules 1–5; the enabler table; the `(devops, rollback)` / `(devops, diagnose-incident)` corrections
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — mints `(devops, incident-rootcause)` (Scope 2)
- `docs/stories/epic-41/story-41-1/41-1c-prose-documents-and-audience-tags.md` — the `prose` type, `Audience`, the kind/audience vocabularies
- `docs/stories/epic-39/story-39-12/implementation-plan.md` — THE thin-binding recipe (D1/D2/D3/D5/D7/D8)
- `docs/stories/epic-39/story-39-10/implementation-plan.md` — the resume standard + structural gate
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DebugDiagnosisWorkflow.cs` — **the closest template**: a `Diagnosis` producer, linear graph, zero `FlowDecision`, zero `Finish`, `[ResumeBehavior(LatestStateReEntry)]` at `:38`, lifecycle dispatch input dict at `:123-145`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DiagnosisBindingHelper.cs` — `ToLegacyHypothesesJson` / `HasUsableHypotheses` / `BuildFailureReason`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — the producer-scoped issue id (`ScopeIssueId`, `:112`) and the `feedbackVariableName` carrier (`:190`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs` + `CreationBindingHelper.cs` (`ScopeIssueId` `:95`, `DeriveIssueId` `:80`, `BuildFailureDetail` `:104`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` — Init input reads `:169-202`; `IngestDraft` (the produce reply is parsed as a **JSON object payload**) `:1170-1197`; review dispatch `:451-466`; persist nodes `:765-777`; outputs `:811-822`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Diagnosis.cs` (`analysisSummary`/`hypotheses`/`rank`/`description`/`confidence`/`suggestedFix`/`affectedFiles`; codes at `:133-145`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Plan.cs` — codes `:47-71`, validator `:101-132` (**every** task needs non-empty `files` AND `testing`)
- `apps/tamma-elsa/src/Tamma.Api/Prompts/devops/plan-incident-response.md`, `write-postmortem.md`, `diagnose-incident.md`, `rollback.md`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs` — rollback branch `:303-329`, wiring `:545-553`, `mergeSha` input `:169`, `StageDeployDispatch` `:595-618`
- `apps/tamma-elsa/src/Tamma.Activities/Decomposition/EmitDecompositionEventActivity.cs` — the event-activity shape to copy (pure `BuildTammaEvent` + `TammaEventEmitter.Emit`)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/EmitEscalationEventActivity.cs` — the mid-flow variant (`await context.CompleteActivityAsync()` at `:115`) and the lineage payload
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the reference structure-test shape
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `Bindings` `:82`, `IntentionallyUnbound` `:286`, `ReviewProducerDispatchablePairs` `:505`, `NonDocumentTypeResidual` `:616`, universal pins `:626`/`:655`, coverage guard `:681`
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs` — `ScanLifecycleBindingDispatches` `:460`, `MaterializeDispatchInput` `:507`, `ExpectedContributingWorkflows` `:125`, `MinExpectedDispatchPairs` `:110`
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:36-45` — the `HaveCount(16)` edge pin
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs` — clauses (b) `:158`, (b-inverse) `:202`, (c) `:240`
- **NOT FOUND (owned by prerequisites, no code today):** `AgentAction.IncidentRootcause` + `Prompts/devops/incident-rootcause.md` (41-1a); `DocumentTypeKey.Prose`, `ProseDocumentType`, `DocumentEnvelope.Audience`, `DocumentInstance.Audience` (41-1c). Every other path above exists and was read.

## Corrections to the story

The story was drafted against a snapshot. Verified against the tree on 2026-07-25:

- **C1 — AC5's `[ResumeBehavior(Both)]` fails the 39-10 gate.** `Both` requires a canonical suspend node
  **in this workflow's own graph** (`ResumableStandardStructuralTests.EveryBookmarkSuspendWorkflow_HasACanonicalSuspendNode`,
  `:158-198`) and a non-empty `SuspendActivities`. A thin binding never suspends: the accept gate's
  `WaitForDocumentDecisionActivity` lives inside the dispatched `document-lifecycle` **child** instance
  (39-12 D7, and the landed precedent — `TaskCreationWorkflow.cs:47` and `DebugDiagnosisWorkflow.cs:38`
  both declare `LatestStateReEntry`, and `TaskCreationWorkflowStructureTests:106` pins "no `Wait*`
  activity"). **Every binding in this story declares `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`**
  and carries a `ComputeReEntryPositionActivity`. Same correction applies to 41-24/41-25/41-26.
- **C2 — one lifecycle dispatch cannot carry three produce cells.** `DocumentLifecycleWorkflow` reads
  exactly one `producerRole` / `producerAction` / `documentType` (`:169-172`). "A short sequence" of three
  stages is therefore **three producing workflows**, each a single-dispatch thin binding, plus a
  sequencer. The epic's rule 1(a) ("exactly one `DispatchWorkflow`, id `document-lifecycle`") is satisfied
  per producing binding; the sequencer is not a producing binding and declares the deviation (D2).
- **C3 — AC1 puts `(devops, write-postmortem)` in `IntentionallyUnbound`; the universal pin forbids that.**
  Once 41-1c registers `prose`, the postmortem cell **is** a document producer, and
  `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode` (`ContractBindingTests.cs:655-674`) states
  verbatim "a document producer must be BOUND, never allowlisted (D7c)". The correct classification is a
  `Bindings` entry with authority `ProseDocumentType.Validate`, which also satisfies
  `UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual` (`:626`).
- **C4 — the postmortem prompt cannot stay raw markdown.** `DocumentLifecycleWorkflow.IngestDraft`
  (`:1177-1180`) carves the **first JSON object** out of the reply and fails the produce turn when there
  is none. `Prompts/devops/write-postmortem.md` today instructs a bare `## Summary / ### Key Findings /
  ### Action Items / ### Details` markdown skeleton. It must be rewritten to emit 41-1c's prose envelope
  `{ "kind": "postmortem", "audience": "engineering", "title": …, "body": "<markdown>" }` — the markdown
  moves *inside* `body`. Lockstep with 41-1c's `ProseDocumentType` wire.
- **C5 — "dispatches the landed `deployment-pipeline`" does not perform a rollback.** Verified: the
  rollback branch (`DeploymentPipelineWorkflow.cs:303-329`) is reachable **only** from
  `prodRetryCheck "False" → emitProdFailed → emitRollbackStarted` (`:543-553`), i.e. after a production
  deploy has failed three times (`MaxStageRetries = 3`, `:102`). Dispatching `deployment-pipeline` runs
  qa → uat → **a fresh production deploy**, and requires a `mergeSha` input (`:169`) an incident does not
  have. There is **no** standalone rollback entry point. Scope's "this story dispatches the landed
  `deployment-pipeline`" and AC3's "a rollback is performed by dispatching `deployment-pipeline`" are
  therefore not implementable. See **D6** for what this story does instead (and what it files).
  AC3's *negative* half — "no `(devops, rollback)` llm-call is issued directly by this workflow" — stands
  and is asserted.
- **C6 — `plan-incident-response.md`'s instructed shape fails `PlanDocumentType.Validate` outright.** The
  template asks for `files: [{"path": …, "action": …}]` (objects) while `PlanTask.Files` is
  `IReadOnlyList<string>` (`Plan.cs:16`), and `dependencies` while the wire is `dependsOn` (`:17`); it
  also adds `complexity` / `totalComplexity` / `estimatedDuration`, which the type does not carry. The
  template must be rewritten to the canonical `Plan` wire (the 39-14/39-15 precedent for a cell migrating
  onto a typed validator).
- **C7 — an honest incident-response step cannot satisfy `Plan`.** Beyond C6's shape mismatch,
  `PlanDocumentType.Validate` (`:113-121`) rejects **any** task with an empty `files` list
  (`TASK_MISSING_FILE_MAP`) or an empty `testing` string (`TASK_MISSING_TESTING`). "Page the on-call",
  "flip the checkout feature flag", "restart the worker pool" touch no repository file. AC2 celebrates
  these codes as fixtures, which is fine as *negative* tests, but the story never says how the happy path
  passes. **D5** records the convention chosen and the escalation path if it churns.
- **C8 — the story's other line cites all check out.** `DeploymentPipelineWorkflow.cs:299-329` /
  `:546-553`, `DeployEvents.cs:61,64,70`, `ContractBindingTests.cs:214` / `:246-249` / `:542-543` /
  `:616-623` / `:626` / `:645` / `:579`, `RolePhaseMap.cs:404-412`, `Program.cs:445-447` and
  `Program.cs:753-764` (exactly six `IToolExecutor`s) were each re-verified and are accurate.
- **C9 — `.dev/findings/document-lifecycle-persist-not-wired.md` is STALE.** Persistence *is* wired:
  `PersistDoc(...)` builds `PersistRevised`/`PersistAccepted`/`PersistRejected`/`PersistEscalated` at
  `DocumentLifecycleWorkflow.cs:770-777` (helper at `:1088`). Do not plan around the finding; do not
  re-file it. (Updating that file is outside this story's touch set.)
- **C10 — no `INCIDENT.*` / `POSTMORTEM.*` constant exists anywhere** in `src/` or `tests/`. The nearest
  neighbours are `DeployEvents.DEPLOY.ROLLBACK.*` and `RELEASE.CREATED.*` (`DeployEvents.cs:61-84`). The
  family is net-new.

## Design Decisions

- **D1 — Three producing bindings, each a single-dispatch thin binding, plus a thin sequencer (per C2).**
  `incident-diagnosis`, `incident-response-plan`, `incident-postmortem` each carry exactly one
  `DispatchWorkflow` with literal id `document-lifecycle`, zero `llm-call`, zero `Finish`, no
  `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variable, a materialised canonical `(role,
  action)` + `documentType` + a declared `feedbackVariableName`, and one `WorkflowDocumentInterface` row.
  Each passes the `TaskCreationWorkflowStructureTests` clause set verbatim. Splitting rather than
  branching is exactly the 39-15 D4 precedent (`DebugDiagnosisWorkflow` was carved out of
  `DebuggingWorkflow` for the same reason).
- **D2 — `incident-response` is a sequencer, not a producing binding; the deviation is declared.**
  It dispatches `incident-diagnosis` → `incident-response-plan` → (accept gate inside each child) →
  `incident-postmortem`, emits the `INCIDENT.*` family at the boundaries, and holds the escalate edge of
  D6. It carries **four** `DispatchWorkflow`s and **zero** `document-lifecycle` dispatches, so epic rule
  1(a) does not apply to it — the same posture as `DebuggingWorkflow`/`SingleIssueCycleWorkflow`. It has
  no `produces:` and therefore **no** `WorkflowDocumentInterface` row and **no** edge-pin bump (the pin
  moves with a producing workflow — epic README rule 1, clause (f) note). It declares
  `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` with its own `ComputeReEntryPositionActivity` keyed on
  the postmortem scope (the last document in the chain), so a crashed incident re-enters at the first
  child whose document is not yet accepted. It contains **zero `Finish`** — every exit is the single
  `ExposeOutput` region.
- **D3 — Producer-scoped incident identity.** All three documents share one incident, and two of the three
  types (`plan`, `prose`) collide with other producers on the same issue — `prose` catastrophically so,
  since 41-1c gives every prose kind one `DocumentTypeKey`. The 39-11 latest-accepted / re-entry read
  scopes by `(issueId, documentType)` with **no producer and no kind filter** (recorded verbatim in
  `CreationBindingHelper.cs:84-94`, FILED to 39-11). Therefore, per 39-15 D2:
  `incidentId = CreationBindingHelper.DeriveIssueId(repository, issueNumber)` when the incident is
  issue-anchored, else `incident#{alertId}`; and each binding keys its lifecycle on
  `ScopeIssueId(incidentId, "incident-diagnosis" | "incident-response" | "postmortem")`. The sequencer
  threads the base `incidentId` and each scope. Without this, the postmortem lifecycle would re-enter on
  *any* accepted prose for the issue and short-circuit.
- **D4 — `(devops, incident-rootcause)`: 41-1a mints the cell, THIS story owns the contract.** 41-1a's AC2
  only requires the pair to be taxonomy-eligible and to have *a* file (`PromptFileLoader` refuses to start
  otherwise — `PROMPT.SEED.NO_BODY_FAMILY`, `PromptFileLoader.cs:160-168`). This story rewrites
  `Prompts/devops/incident-rootcause.md` to the `Diagnosis` wire, mirroring
  `Prompts/senior_developer/debug-rootcause.md`, declares variables
  `role, errorContext, stackTrace, relevantCode, recentChanges, conventions` (so `errorContext` can be the
  `feedbackVariableName`, as `DebugDiagnosisWorkflow.cs:135` does), and adds the `Bindings` entry
  `[("devops","incident-rootcause")] = new("DiagnosisDocumentType.Validate", [One("\"analysisSummary\""),
  One("\"hypotheses\""), One("\"rank\""), One("\"description\""), One("\"confidence\""),
  One("\"suggestedFix\""), One("\"affectedFiles\"")])` — token-for-token the landed
  `(senior_developer, debug-rootcause)` entry (`ContractBindingTests.cs:214-219`).
- **D5 — the response `Plan`'s file map is the *operational target*, not a source path (per C7).**
  `PlanTask.Files` is an untyped `IReadOnlyList<string>`; the validator only requires ≥1 non-blank entry.
  The rewritten `plan-incident-response.md` therefore instructs: *every step names the artefact it acts on
  in `files` — a repository path when the step edits code/config, otherwise the runbook, dashboard,
  flag or service it targets (e.g. `docs/runbooks/checkout.md`, `ops://flags/checkout-v2`,
  `ops://service/worker-pool`) — and states its verification in `testing` (the check that proves the step
  worked).* This keeps the type unforked and every step verifiable. **If pilot telemetry shows churn**,
  the fix is a `PlanDocumentType` policy relaxation filed to 39-3/39-9 (their plans pre-authorise exactly
  this — 39-12 D6/Risks), never leniency in this binding. An `ops-plan` document type is explicitly NOT
  proposed here; that would be a 41-1b-class change and is out of scope.
- **D6 — rollback is an *escalation*, not a dispatch (per C5).** The response `Plan` may contain a
  rollback step; **executing** it is out of scope, as Scope already says. Because
  `deployment-pipeline` offers no standalone rollback entry (C5), the sequencer's post-accept behaviour
  for a plan whose steps are classified destructive/prod-rollback is: emit
  `INCIDENT.ROLLBACK_REQUIRED` + an `ESCALATION.TRIGGERED` through the existing
  `EmitEscalationEventActivity` (lineage = the accepted plan document), and expose
  `rollbackDisposition = "escalated"`. AC3's negative assertion (no `(devops, rollback)` llm-call from
  this story) is preserved and pinned. **Filed, not fixed here:** "`deployment-pipeline` has no standalone
  rollback entry point" is recorded in `.dev/findings/` and raised against Epic 40/`deployment-pipeline`'s
  owner — this story does not restructure a landed pipeline.
- **D7 — a new `IncidentEvents` family + one `EmitIncidentEventActivity`, copying
  `EmitDecompositionEventActivity` verbatim in shape.** Constants:
  `INCIDENT.STARTED`, `INCIDENT.DIAGNOSED`, `INCIDENT.RESPONSE_ACCEPTED`, `INCIDENT.ROLLBACK_REQUIRED`,
  `INCIDENT.RESOLVED`, `INCIDENT.FAILED`, `POSTMORTEM.DRAFTED`, `POSTMORTEM.ACCEPTED`. `StatusForEvent`
  maps `FAILED` → `"error"`, `STARTED` → `"started"`, else `"success"` (the `ApprovalEvents.cs:73` shape).
  `BuildTammaEvent` is pure/static and unit-tested without Elsa. Tags: `incidentId`, `issueId`,
  `repository`, `documentId`, `documentType`, `correlationId`, `tenantId`. The generic
  `DOCUMENT.*`/`APPROVAL.*`/`ESCALATION.*` rows are emitted **by the lifecycle**, never by this story.
- **D8 — rules, reviewer and autonomy are policy passthrough (39-12 D8).** Each binding takes an optional
  `acceptanceRulesJson` forwarded verbatim. Nothing about "always-escalate" is compiled in: an active
  incident's escalate-always posture is an `AcceptanceRules` row (`AcceptorRequirement.Human` — the
  landed `s_humanAcceptorRules` shape, `AcceptanceDefaults.cs:112-117`) supplied by the caller. AC3's
  "integration test at autonomy 100 still routes to a human" is therefore a test of the *rules* the
  sequencer forwards, not of an if-else. Note the defaults it overrides:
  `AcceptanceDefaults.For(Plan)` is the 7-role majority panel, `For(Diagnosis)` is the single-architect
  unanimous base row, and prose's default is 41-1c D2's `tech_writer` single-reviewer row — none of the
  three is right for an incident, which is exactly why the rules ride as input.
- **D9 — AC4 (postmortem action items → role-scoped Task View) ships the fail-closed assertion.**
  `InitiatorOnlyTaskAudienceResolver` is the registered `ITaskAudienceResolver` (`Program.cs:445-447`),
  so only the issue initiator is ever admitted. The story already says this. Concretely: the sequencer
  emits `POSTMORTEM.ACCEPTED` with an `actionItems` data array, and the test asserts the audience
  resolver admits exactly the initiator today — with a pinned TODO naming 39-20. No action item is
  silently dropped: unroutable items are emitted on the event and surfaced in the workflow output.
- **D10 — 41-26 dispatch is by definition id, wired later.** A postmortem action item of kind
  "add/update runbook" is exposed as workflow output + event data. The actual `DispatchWorkflow("runbook-authoring")`
  edge lands with **41-26** (which owns that definition id), not here — this story ships no dispatch to a
  workflow that does not exist.

## Implementation Steps

1. **Precondition gate (no code).** Verify in tree and compiling: 41-1a (`AgentAction.IncidentRootcause`
   eligible for `Devops`, `Prompts/devops/incident-rootcause.md` present) and 41-1c
   (`DocumentTypeKey.Prose`, `ProseDocumentType` registered, `DocumentEnvelope.Audience`,
   `DocumentInstance.Audience` + migration, the kind/audience vocabularies). Any gap blocks the
   corresponding steps below — file it, do not work around it. Steps 2–5 (diagnosis + plan halves) need
   only 41-1a; steps 6–7 (postmortem) need 41-1c.

2. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/devops/incident-rootcause.md`** (D4) to the
   `Diagnosis` wire, front matter
   `variables: role, errorContext, stackTrace, relevantCode, recentChanges, conventions` /
   `enableTools: false` / `maxTokens: 8192` / `version: 1`. Body mirrors
   `Prompts/senior_developer/debug-rootcause.md`, re-pitched at an operational incident (signals,
   deploy/health events, affected service) instead of a test failure. **MODIFY
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`**: add the D4
   `Bindings` entry.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Incident/IncidentEvents.cs` and
   `EmitIncidentEventActivity.cs`** (D7) — copy `Decomposition/DecompositionEvents.cs` +
   `EmitDecompositionEventActivity.cs` shape exactly: `[Activity("Tamma.Incident", …)]`, `[JsonConstructor]`
   + logger ctors, nullable `Input<string?>` per tag, `EventType` defaulting fail-closed to
   `IncidentEvents.Failed`, a pure `public static TammaEvent BuildTammaEvent(...)`, `TammaEventEmitter.Emit`.

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/IncidentBindingHelper.cs`** — pure,
   Elsa-free, total, fail-closed (the `DiagnosisBindingHelper`/`CreationBindingHelper` posture):
   ```csharp
   public static class IncidentBindingHelper
   {
       public const string DiagnosisScope  = "incident-diagnosis";
       public const string ResponseScope   = "incident-response";
       public const string PostmortemScope = "postmortem";

       public static string DeriveIncidentId(string? repository, int issueNumber, string? alertId);
       public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit);
       // Classify an accepted response Plan's steps for D6: any step whose files/description
       // names a production rollback/destructive target ⇒ RollbackDisposition.Escalate.
       public static RollbackDisposition ClassifyResponse(string? planDocumentJson);
       // Project the postmortem's "action items" from an accepted prose body; "[]" fail-closed.
       public static string ProjectActionItems(string? proseDocumentJson);
   }
   ```

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IncidentDiagnosisWorkflow.cs`
   (`incident-diagnosis`) and `IncidentResponsePlanWorkflow.cs` (`incident-response-plan`)** — both the
   `DebugDiagnosisWorkflow` skeleton verbatim (D1), `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`,
   nodes `ReadInputs → ComputeReEntryPosition → DispatchLifecycle → ReadLifecycleExit → ExposeOutput`, no
   `FlowDecision`, no `Finish`. Dispatch inputs:
   ```csharp
   // incident-diagnosis
   ["documentType"] = "diagnosis",
   ["producerRole"] = AgentRole.Devops.ToWire(),
   ["producerAction"] = AgentAction.IncidentRootcause.ToWire(),
   ["feedbackVariableName"] = "errorContext",
   ["issueId"] = ScopeIssueId(incidentId, DiagnosisScope), ["correlationId"] = same,
   // incident-response-plan
   ["documentType"] = "plan",
   ["producerRole"] = AgentRole.Devops.ToWire(),
   ["producerAction"] = AgentAction.PlanIncidentResponse.ToWire(),
   ["feedbackVariableName"] = "contextFindings",
   ["issueId"] = ScopeIssueId(incidentId, ResponseScope), ["correlationId"] = same,
   ```
   plus `producerVariablesJson` (`workItemJson`, `contextFindings` = the accepted diagnosis body folded
   into the DECLARED carrier — never a new key, the render-drop lesson), `tenantId`,
   `acceptanceRulesJson`. `incident-response-plan` reads the accepted diagnosis via
   `FetchLatestAcceptedDocumentActivity` on the diagnosis scope (the `TaskCreationWorkflow.cs:155-165`
   pattern), gated on a fresh run.

6. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/devops/plan-incident-response.md`** (C6/D5) to the
   canonical `Plan` wire — `{"summary": …, "tasks":[{"id","description","files":["…"],"dependsOn":[],"testing":"…"}]}` —
   with D5's operational-target convention spelled out. Keep the declared variables
   (`role, workItemJson, contextFindings, conventions`) unchanged so `feedbackVariableName =
   "contextFindings"` renders. **MODIFY `ContractBindingTests.cs`**: add
   `[("devops","plan-incident-response")] = new("PlanDocumentType.Validate", [One("\"tasks\""), One("\"files\""), One("\"testing\""), One("\"dependsOn\"")])`.

7. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/devops/write-postmortem.md`** (C4) to emit 41-1c's
   prose envelope with the existing markdown skeleton moved inside `body`; keep the declared `audience`
   variable. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IncidentPostmortemWorkflow.cs`
   (`incident-postmortem`)** — same skeleton, `documentType = "prose"`,
   `(devops, write-postmortem)`, `feedbackVariableName = "findings"` (the cell's declared carrier),
   `producerVariablesJson` folding the accepted diagnosis + accepted response plan into `findings` and
   setting `audience = "engineering"`, issue id `ScopeIssueId(incidentId, PostmortemScope)`.
   **MODIFY `ContractBindingTests.cs`** (C3): add `[("devops","write-postmortem")] = new("ProseDocumentType.Validate", [One("\"kind\""), One("\"audience\""), One("\"title\""), One("\"body\"")])`
   — **not** an `IntentionallyUnbound` entry.

8. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IncidentResponseWorkflow.cs`
   (`incident-response`)** (D2/D6/D9) — the sequencer. `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`,
   one `ComputeReEntryPositionActivity` on the postmortem scope, `emitIncidentStarted` →
   `DispatchWorkflow("incident-diagnosis")` (`WaitForCompletion=true`) → `emitDiagnosed` →
   `DispatchWorkflow("incident-response-plan")` → `emitResponseAccepted` → `classifyResponse`
   `FlowDecision` → (escalate arm: `EmitIncidentEventActivity(INCIDENT.ROLLBACK_REQUIRED)` +
   `EmitEscalationEventActivity`) → join → `DispatchWorkflow("incident-postmortem")` →
   `emitPostmortemAccepted` → `emitIncidentResolved` → single `ExposeOutput` region. Non-accept exits from
   any child route to `emitIncidentFailed` → the SAME `ExposeOutput` (no `Finish`, no dead end).

9. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs` (`BuildSeed`)** — three new
   rows, all `Provisional=false` (each is backed by a real binding):
   ```csharp
   new WorkflowDocumentInterface("incident-diagnosis",     empty, DocumentTypeKey.Diagnosis, false),
   new WorkflowDocumentInterface("incident-response-plan", new[]{ DocumentTypeKey.Diagnosis }, DocumentTypeKey.Plan, false),
   new WorkflowDocumentInterface("incident-postmortem",    new[]{ DocumentTypeKey.Diagnosis, DocumentTypeKey.Plan }, DocumentTypeKey.Prose, false),
   ```
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:44`** —
   `HaveCount(16)` → `HaveCount(19)` with the one-line reason (the conscious edit, epic rule 1(f)).

10. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs`** — add
    `IncidentDiagnosisWorkflow`, `IncidentResponsePlanWorkflow`, `IncidentPostmortemWorkflow` to
    `ExpectedContributingWorkflows` (`:125`) with a one-line comment each; `MinExpectedDispatchPairs`
    (`:110`) needs no change (it is a floor and the count only rises).

11. **CREATE the test suites** (see Test Plan): three `*StructureTests`, `IncidentResponseWorkflowStructureTests`,
    `IncidentBindingHelperTests`, `IncidentEventsTests`, `IncidentLifecycleExecutionTests`.

12. **Finish** with `dotnet test` (full) and `dotnet ef migrations has-pending-model-changes` (must stay
    clean — this story adds no schema).

## Data & Migrations

None. Documents persist through 39-11's `document_instances` via the lifecycle's own
`PersistAccepted`/`PersistRevised`/`PersistRejected`/`PersistEscalated` nodes
(`DocumentLifecycleWorkflow.cs:770-777` — see C9); `INCIDENT.*`/`POSTMORTEM.*` ride the existing
`TammaEventEmitter` drain → `EventRepository` → `domain_events`. The `Audience` column is **41-1c's**
migration, not this story's. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new, this story):** `INCIDENT.STARTED`, `INCIDENT.DIAGNOSED`, `INCIDENT.RESPONSE_ACCEPTED`,
  `INCIDENT.ROLLBACK_REQUIRED`, `INCIDENT.RESOLVED`, `INCIDENT.FAILED`, `POSTMORTEM.DRAFTED`,
  `POSTMORTEM.ACCEPTED` — tags `incidentId`/`issueId`/`repository`/`documentId`/`documentType`/
  `correlationId`/`tenantId`.
- **Emits (existing constants, reused):** `ESCALATION.TRIGGERED` via `EmitEscalationEventActivity`
  (`ApprovalEvents.cs:55`) on the D6 rollback-required arm, lineage = the accepted response plan.
- **Emitted by the machinery this story wires in (not by this story's code):** the full `DOCUMENT.*`
  family (`DocumentEvents.cs:28-53`), `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED` from the
  lifecycle's own escalate terminal, `DOCUMENT.REENTERED`.
- **Not minted here:** `DEPLOY.ROLLBACK.*` remains `deployment-pipeline`'s (`DeployEvents.cs:61,64,70`),
  untouched.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suite).

- **`IncidentDiagnosisWorkflowStructureTests`, `IncidentResponsePlanWorkflowStructureTests`,
  `IncidentPostmortemWorkflowStructureTests`** — each a verbatim clone of
  `TaskCreationWorkflowStructureTests`: builds; stable `DefinitionId`; threads `TenantId`; **no** retry
  plumbing variables; **exactly one** `DispatchWorkflow` and its literal def id is `document-lifecycle`;
  **zero** `llm-call` dispatches; `ScanLifecycleBindingDispatches()` contains the canonical pair;
  `MaterializeDispatchInput` shows the right `documentType` and `feedbackVariableName`; **zero** `Finish`;
  one `ComputeReEntryPositionActivity`; `[ResumeBehavior(LatestStateReEntry)]`; no `Wait*` node.
  **Covers AC1, AC5.**
- **`IncidentResponseWorkflowStructureTests`** — four `DispatchWorkflow`s with literal ids
  `{incident-diagnosis, incident-response-plan, incident-postmortem}` (D2's declared deviation, asserted
  as an exact set); **zero** `document-lifecycle` and **zero** `llm-call` dispatches; **zero** `Finish`;
  every graph leaf inside the single `ExposeOutput` region; **a standing negative assertion that
  `deployment-pipeline` is not a dispatch target and `(devops, rollback)` appears in no dispatch input**
  (AC3's negative half, C5); `[ResumeBehavior(LatestStateReEntry)]` + one `ComputeReEntryPositionActivity`.
  **Covers AC1, AC3 (negative half), AC5.**
- **`IncidentBindingHelperTests`** (pure) — `DeriveIncidentId` across issue-anchored/alert-anchored/empty;
  `ClassifyResponse` on a destructive plan / a benign plan / unreadable JSON (fail-closed to `Escalate`);
  `ProjectActionItems` on a valid prose body / garbage → `"[]"`; `BuildFailureDetail` names each reachable
  outcome wire. **Covers AC1, AC3.**
- **`IncidentEventsTests`** — `BuildTammaEvent` tag/data matrix; `StatusForEvent` maps `INCIDENT.FAILED` →
  `"error"` (loud), `STARTED` → `"started"`; every constant is `AGGREGATE.ACTION[.STATUS]`-shaped.
- **`PlanDocumentType` fixtures (`Tamma.Core.Tests`)** — AC2, one fixture per rule: a response-plan step
  with no `files` ⇒ `TASK_MISSING_FILE_MAP`; no `testing` ⇒ `TASK_MISSING_TESTING`; a cyclic `dependsOn`
  ⇒ `CYCLIC_DEPENDS_ON`; **plus the positive fixture C7/D5 requires** — a realistic operational response
  plan (page/flag/rollback/verify steps using D5's target convention) that **validates clean**. Without
  the positive fixture AC2 proves only that the type rejects incident plans.
- **Contract/drift guards (self-verifying, steps 2/6/7/10)** — `ContractBindingTests` green with the three
  new `Bindings` entries; `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` clause (c) non-stale;
  `(devops, rollback)`'s `NonDocumentTypeResidual` membership and `(devops, diagnose-incident)`'s
  `ReviewProducerDispatchablePairs` membership **unchanged and green** (AC1's explicit ask);
  `UniversalPin_EveryBindingAuthority_…` and `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode` green;
  `ReviewProducerDispatchablePairs_HasNoStaleEntries` green; `LifecycleBindingWalk_FindsPairs_NotANoOp`
  finds all three new bindings. **Covers AC1.**
- **`ResumableStandardStructuralTests`** — passes with **no** allowlist entry for any of the four new
  workflows. **Covers AC5.**
- **`WorkflowInterfaceGraphTests`** — `HaveCount(19)`; the three new edges' produces keys registered.
- **`IncidentLifecycleExecutionTests`** (Testcontainers Postgres, on the 39-6/39-10/39-12 shared fixture:
  real `DocumentLifecycleWorkflow` + the four new workflows, stub `llm-call`, real Elsa EF persistence +
  event drain + `IDocumentInstanceRepository`, decisions injected via `DocumentDecisionResumeEndpoint.Resume`):
  - (a) **Happy path** — scripted valid diagnosis → accept → valid response plan → accept → valid
    postmortem → accept. Asserts: three accepted `document_instances` rows with the three producer-scoped
    issue ids (D3) and correct types; the postmortem row's `Audience = "engineering"`; the full
    `INCIDENT.*`/`POSTMORTEM.*` sequence present alongside `DOCUMENT.*` with matching `incidentId` tags.
    **AC1.**
  - (b) **Always-escalate at autonomy 100** — rules with `AcceptorRequirement.Human`, autonomy 100: the
    response-plan accept gate still suspends and publishes an `AcceptanceRequest`; no self-accept. **AC3.**
  - (c) **Destructive response** — a plan classified prod-rollback: `INCIDENT.ROLLBACK_REQUIRED` +
    `ESCALATION.TRIGGERED` with the plan document in lineage; workflow output
    `rollbackDisposition = "escalated"`; **no `deployment-pipeline` instance started** (asserted on the
    instance store). **AC3, C5.**
  - (d) **Validation exhaustion** — always-invalid response plan stub: typed escalation with lineage,
    `INCIDENT.FAILED` naming the outcome wire, no error terminal reached. **AC1.**
  - (e) **Crash re-entry** — kill the host mid-chain (39-10 D8 shape), fresh `incident-response` dispatch
    for the same incident: skips the already-accepted children, exactly one `DOCUMENT.ACCEPTED` per
    document on the whole stream, exactly one `INCIDENT.RESOLVED`. **AC5.**
  - (f) **Action-item routing, fail-closed** — `POSTMORTEM.ACCEPTED` carries the `actionItems` array;
    `InitiatorOnlyTaskAudienceResolver` admits exactly the initiator; a pinned TODO names 39-20 as the
    unblocker; **no item is silently dropped** (each unroutable item appears on the event and in output).
    **AC4 (the claimable half).**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin bindings, typed/prose documents, no bespoke terminals; the three CI pins unchanged | 2, 5, 6, 7, 8, 9 (D1/D2/D4, C3) | Three producer `*StructureTests` + `IncidentResponseWorkflowStructureTests`; `ContractBindingTests` (new entries + all four universal/stale pins green); `WorkflowInterfaceGraphTests` |
| 2 — response `Plan` validates; one fixture per rule | 6 (C6/D5) | `PlanDocumentType` fixtures — three negative **plus the positive operational-plan fixture** |
| 3 — always-escalate; destructive never self-accepts; no direct `(devops, rollback)` call | 8 (D6/D8) | Execution (b)(c); `IncidentResponseWorkflowStructureTests` negative assertions. **Rollback-by-dispatch is not implementable (C5)** — replaced by the D6 escalation, recorded here, not silently dropped |
| 4 — action items → role-scoped Task View | 8 (D9) | Execution (f) — the fail-closed half only; the routed half is **blocked on 39-20** |
| 5 — resumable per the standard, 39-10 gate green without an allowlist entry | 5, 7, 8 (C1) | `ResumableStandardStructuralTests`; Execution (e). **Declaration is `LatestStateReEntry`, not `Both` (C1)** |

## Blocks / Blocked by

- **Blocked by — hard:**
  - **41-1a** — `(devops, incident-rootcause)` (cell + eligible-set entry + prompt file). Steps 2 and 5
    cannot start without it. *(This story owns the template's typed contract; 41-1a owns the cell's
    existence — D4.)*
  - **41-1c** — `prose` `DocumentTypeKey` + `ProseDocumentType` + `DocumentEnvelope.Audience` +
    `DocumentInstance.Audience` (+ migration) + the kind/audience vocabularies. Steps 7 and the
    postmortem half of 8/9 cannot start without it. Steps 2–6 (diagnosis + response plan) **can** land
    first — they need only 41-1a.
  - **Epic 39** — 39-2/39-3/39-4 (`Diagnosis`, `Plan`, registry), 39-6 (`document-lifecycle`), 39-7
    (`document-review`), 39-8 (accept gate + resume endpoint), 39-10 (resume standard + gate), 39-11
    (store + persist wiring — landed, C9). All in tree.
- **Blocked by — partial (AC-level, named, not planned around):**
  - **39-20** — AC4's routed half. Ships fail-closed today (D9).
  - **39-17 / 39-19** — every accept gate parks with nothing on the other end; rule 3/4's end-to-end
    promise is unreachable for this story exactly as for every Epic 41 story.
  - **Epic 42 (42-7 cloud/VPS, 42-8 feature-flag)** — the 85–100 band's "execute a low-risk response
    class". Not on any AC; the story already carries the caveat. D6 makes the *rollback* half moot
    regardless.
  - **Epic 40** — only for a code/infra response step that runs the coding agent. Diagnosis, planning and
    postmortem have no Epic 40 dependency and land first.
- **Not blocked by:** `deployment-pipeline` (D6/C5 — no dispatch edge is built), 41-1b (this story needs
  no new document type), the scheduled-trigger seam (this workflow is reactively triggered, not cron).
- **Blocks:**
  - **41-26** — consumes the accepted incident `Diagnosis` and is dispatched from a postmortem action
    item (D10; the dispatch edge lands in 41-26, and 41-26 needs D3's producer-scoped incident id).
- **Related:** **41-23** escalations are one of this workflow's triggers; **41-21** is the security-side
  sibling.
- **Files, does not fix:** "`deployment-pipeline` has no standalone rollback entry point" →
  `.dev/findings/` + Epic 40 / pipeline owner (D6).

## Risks & Mitigations

- **`Plan` cannot honestly type an operational response (C7).** The single largest design risk. Mitigation:
  D5's operational-target convention + the **positive** fixture in the test plan (an incident plan that
  really validates), and a pre-authorised escalation path — a `PlanDocumentType` policy relaxation filed
  to 39-3/39-9 if pilot telemetry shows repair churn. Never leniency inside the binding (39-12 D6).
- **Prose is one `DocumentTypeKey` for ten kinds; the 39-11 read has no kind filter.** A postmortem
  lifecycle keyed on a bare issue id would re-enter on an unrelated accepted prose document and
  short-circuit. Mitigation: D3's mandatory producer scoping + an execution assertion that the three
  documents land under three distinct scoped issue ids. Long-term fix is a 39-11 producer/kind filter —
  file it, do not patch here.
- **Three bindings + a sequencer is more surface than "a short sequence" implies.** Mitigation: the three
  producers are near-identical clones of a landed file; the incremental cost is in step 8 and the tests.
  The alternative (one workflow, three lifecycle dispatches) is unbuildable (C2) and would fail the
  thin-binding structure set.
- **41-1a's `incident-rootcause` template may land generic and get pinned by someone else first.**
  Mitigation: D4 states the ownership split explicitly; coordinate the `Bindings` entry in lockstep with
  41-1a's owner (the same coordination 39-12 step 2 used for the `documentJson` hook).
- **Sequencer re-entry position is keyed on one scope.** A crash between diagnosis-accept and
  plan-produce must not re-run the diagnosis. Mitigation: each child owns its own re-entry (they are
  `LatestStateReEntry`), so the sequencer's job is only to not re-emit `INCIDENT.STARTED` — gated on its
  own position, exactly as 39-12 D3/D7 gate `DECOMPOSITION.STARTED`. Pinned by Execution (e).
- **Story-vs-canon tensions:** C1, C3 and C5 are genuine story-vs-code contradictions and are resolved
  above in favour of the code. C2/C7 are gaps in the story rather than contradictions.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition gate + 41-1a/41-1c lockstep coordination | 0.25 |
| 2 | `incident-rootcause` template to the `Diagnosis` wire + binding entry | 0.5 |
| 3 | `IncidentEvents` + `EmitIncidentEventActivity` | 0.5 |
| 4 | `IncidentBindingHelper` | 0.5 |
| 5 | Two producer bindings (`incident-diagnosis`, `incident-response-plan`) | 1.0 |
| 6 | `plan-incident-response.md` rewrite to the `Plan` wire + binding entry + D5 convention | 0.5 |
| 7 | `write-postmortem.md` → prose envelope + `incident-postmortem` binding + binding entry | 0.75 |
| 8 | `incident-response` sequencer (incl. D6 escalate arm, D9 action items) | 1.0 |
| 9–10 | Registry seed + edge pin + drift-test contributor entries | 0.25 |
| 11 | Structure tests ×4, helper/event unit tests, `Plan` fixtures | 1.0 |
| 11 | Testcontainers scenarios (a)–(f) | 1.0 |
| 12 | Full-suite green, migration check, review polish | 0.25 |
| **Total** | | **7.5** (story estimate: 5–6 days) |

The overrun is C2 (three bindings, not one) plus the three prompt rewrites C4/C6 forced. If the postmortem
half is deferred until 41-1c lands, steps 1–6 + their tests are **4.5 days** and shippable independently.
