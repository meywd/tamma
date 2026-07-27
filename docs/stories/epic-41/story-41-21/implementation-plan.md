# Implementation Plan — Story 41-21: Security Incident Analysis Workflow

## Scope & Deliverable

When this story is done, a security alert becomes a typed `Diagnosis` on the Epic 39 lifecycle. A new
thin binding `DefinitionId = "security-incident"` assembles the incident context (the alert, the
issue's DCB event slice, an optional 41-20 audit `Findings`, the affected-surface context),
dispatches `document-lifecycle` with `documentType = "diagnosis"` and the existing
`(security, analyze-security-incident)` producer cell, and routes typed exits. It contributes no
parse, no `Finish`, no `llm-call`. An active/high-severity incident cannot reach a silent acceptance,
and the confirmed-active path dispatches the landed `rotate-secret` saga. The accepted `Diagnosis` is
retrievable through 39-11 and feeds 41-22's postmortem when the incident is cross-cutting.

**This story is unblocked at its produce step:** the cell, the prompt file, the document type and
`rotate-secret` all exist today. Its only real design work is the two AC2 mechanisms and the
blast-radius field gap.

## Pre-Reading

- `docs/stories/epic-41/story-41-21/41-21-security-incident-analysis.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/README.md` — rule 1's six thinness clauses (a)–(f)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DebugDiagnosisWorkflow.cs` — **the closest landed
  sibling and the template to copy**: `DefinitionId = "debug-diagnosis"` (`:41`, `:47`),
  `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (`:38`), the single
  `DispatchWorkflow("document-lifecycle")` (`:119-122`) with `documentType = "diagnosis"` (`:125`),
  `producerAction = AgentAction.DebugRootcause.ToWire()` (`:127`) and the **declared**
  `feedbackVariableName = "errorContext"` (`:140`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DiagnosisBindingHelper.cs` — the sibling's pure helper
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Diagnosis.cs` — **read before writing AC1**: the
  `Diagnosis` record (`:28-32` — `analysisSummary`, `hypotheses` only), `DiagnosisHypothesis`
  (`:12-19` — `rank`, `description`, `confidence`, `suggestedFix`, `affectedFiles`), and the five
  violation constants (`:133-145`: `MALFORMED_PAYLOAD`, `CONFIDENCE_OUT_OF_RANGE`, `DUPLICATE_RANK`,
  `RANK_CONFIDENCE_MISMATCH`, `FIX_MISSING_AFFECTED_FILES`). **There is no blast-radius member and no
  severity member.**
- `apps/tamma-elsa/src/Tamma.Core/Documents/IDocumentType.cs:32-44` — the `ValidateWithContext` seam
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs:148` — the only landed consumer of `validationContextJson`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` + `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the reference binding + reference structure-test set
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/RotateSecretWorkflow.cs:34` (`DefinitionId = "rotate-secret"`) and the Epic 29 secrets subsystem behind it (`apps/tamma-elsa/src/Tamma.Api/Services/Secrets/` — `ISecretStore`, `Rotation/RotationTriggerService`'s per-secret concurrency guard, `Rotation/SecretAutoRotationScheduler`, the three rotation handlers) — **AC2's integration target; all landed**
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:195-210` (`EscalationClass`, `EscalationClassKind` = `document-type` | `agent-action` **only**) + `AcceptanceGuardrails.cs:45-80` (`TryPreGate`) / `:96-134` (`Clamp`, the `BlockingReviewViolation` arm at `:103-110`) — **read before designing AC2**
- `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs:95` (`[Wire("analyze-security-incident")] AnalyzeSecurityIncident`), `RolePhaseMap.cs:136` (in `Security`'s eligible set), `apps/tamma-elsa/src/Tamma.Api/Prompts/security/analyze-security-incident.md` — **all three exist; this story mints no cell**
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:376-436` — the review lens: `diagnosis` is not `triage-decision`, so `GetPanelActionForRole` falls through to `GetReviewActionForRole` → `security` gets `plan-review-security`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — the 4-7 event-query surface the incident context reads
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` (`Bindings`, incl. the sibling `(senior_developer, debug-rootcause)` entry at `:214-219`; the universal-authority pin `:626`; the staleness guard `:724-737`), `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:134-174` (note the two existing `diagnosis` producers: `debug-diagnosis` at `:169`, plus provisional `blocker-diagnosis`/`debugging` at `:165-166`), `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`
- `docs/stories/epic-41/story-41-20/implementation-plan.md` — the sibling security story; its D4 (three-mechanism escalation) and D2 (`ValidateWithContext` for a shared type) are the same patterns applied here, and its `Findings` extension is a coordination point

## Corrections to the story

1. **CONFIRMED — the cell exists and this story mints none.** `(security, analyze-security-incident)`
   is real: `AgentAction.cs:95`, `RolePhaseMap.cs:136` (`Security`'s `FreezeSet`), and
   `src/Tamma.Api/Prompts/security/analyze-security-incident.md`. It is currently **unbound in both
   directions** (in neither `Bindings` nor `IntentionallyUnbound`), which is legal only because no
   compiled dispatch site emits it. **No `AgentAction` member, no `RolePhaseMap` edit, no new prompt
   file, and no bump of `AgentActionTests.cs:38` (`Be(80)`) or `RolePhaseMapTests.cs:64`
   (`HaveCount(80)`).** This story has **no 41-1a dependency**.

2. **CONFIRMED — `Diagnosis` is registered** (`DocumentTypeRegistry.cs:38`) and has a landed thin
   binding to copy (`DebugDiagnosisWorkflow`, 39-15 D4). **No 41-1b dependency.**

3. **CONFIRMED — `rotate-secret` exists** (`RotateSecretWorkflow.cs:34`) with a substantial Epic 29
   subsystem behind it (secret store + envelope encryption, KEK provider/rotation coordinator, a
   per-secret concurrency guard in `RotationTriggerService`, three rotation handlers, a reveal service
   with a token sweeper). **AC2 needs no new rotation machinery** — one `DispatchWorkflow` at the
   confirmed-active edge. Do not build a second rotation path.

4. **NEW — AC1's "blast radius stated" and "remediation references affected files/assets" are only
   half-expressible against the shipped type.**
   - `FIX_MISSING_AFFECTED_FILES` (`Diagnosis.cs:145`) **already** enforces "a hypothesis with a
     suggested fix must name affected files" — so the *remediation-references-files* half of AC1 is
     satisfied by the existing validator with **zero new code**. Good.
   - **Blast radius is not a field.** `Diagnosis` has exactly `analysisSummary` + `hypotheses`
     (`:28-32`); `DiagnosisHypothesis` has exactly `rank`/`description`/`confidence`/`suggestedFix`/
     `affectedFiles` (`:12-19`). "Blast radius stated" and "affected assets" have nowhere to live and
     nothing to check.
   **Correction (D2):** add `blastRadius` and `severity` as **optional** members of `Diagnosis`
   (additive, nullable — `debug-diagnosis`, `blocker-diagnosis` and `debugging` are unaffected), and
   make them **required only for this story's producer** via
   `DiagnosisDocumentType.ValidateWithContext` gated on a `validationContextJson` this binding
   supplies. This is the same seam 41-18 uses for `Plan` and 41-20 uses for `Findings`, and the same
   seam `TestCaseCreationWorkflow` already drives (`:148`).

5. **NEW, and it is the story's real design problem — AC2's "always escalates" is NOT expressible as
   an escalation class.** Verified: `EscalationClassKind` is `document-type` or `agent-action`
   **only** (`AcceptanceRules.cs:200-210`), matched by exact string equality in
   `AcceptanceGuardrails.TryPreGate` (`:50-68`). There is **no payload-conditional escalation class**
   in the tree. `{"kind":"agent-action","key":"analyze-security-incident"}` escalates **every**
   incident diagnosis, including a low-severity one — contradicting the story's own 85–100 autonomy
   row ("agent diagnoses and self-accepts low-severity findings"). Two payload-aware mechanisms do
   exist — `IDocumentType.Validate`/`ValidateWithContext` and `AcceptanceGuardrails.Clamp`'s
   `BlockingReviewViolation` arm (`:103-110`) — plus the binding's own routing edge. **See D3.**
   Adding a payload-predicate `EscalationClassKind` would be a 39-5 generic-layer change; it is
   recorded as a gap, not built here. *(41-19 and 41-20 hit the identical wall — the three stories
   should agree one answer, and this plan's D3 is it.)*

6. **NEW — AC3's `[ResumeBehavior(Both)]` is the wrong mode for a thin binding.**
   `ResumableStandardStructuralTests` clause (b) requires a `Both`-declaring workflow's graph to
   contain a canonical suspend activity; a thin binding has none (the accept-gate suspend lives in
   the dispatched `document-lifecycle` child, waited on with `WaitForCompletion=true`). The landed
   sibling `DebugDiagnosisWorkflow` declares `LatestStateReEntry` (`:38`), as does every other thin
   binding. **Correction: `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`.** AC3's substance —
   39-10 green with no allowlist entry — is unaffected.

7. **NEW — `diagnosis` already has three declared producers, so the re-entry anchor must be
   producer-scoped.** `DocumentTypeRegistry.BuildSeed` declares `blocker-diagnosis` (`:165`,
   provisional), `debugging` (`:166`, provisional) and `debug-diagnosis` (`:169`, non-provisional)
   all producing `diagnosis`. 39-11's latest-accepted read scopes by `(issueId, documentType)` with
   **no producer filter** — the exact problem `TaskCreationWorkflow` D2 solved for `plan`. Adding a
   fourth `diagnosis` producer without scoping would make a debug diagnosis and a security incident
   diagnosis on the same issue collide on re-entry and on "the accepted diagnosis". **See D4.**

8. **NEW — the story omits the two rule-1(f) lockstep obligations and the `ContractBindingTests`
   obligation.** A new producing workflow must (i) declare a `WorkflowDocumentInterface` row in
   `DocumentTypeRegistry.BuildSeed` and (ii) bump
   `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (`:45`, `HaveCount(16)` today) in the
   same change; and (iii) the moment this binding compiles,
   `(security, analyze-security-incident)` becomes a discovered dispatch pair and
   `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` (`:681`) fails until it is classified in
   `Bindings` with a `*DocumentType.Validate` authority (mandated by the universal-authority pin at
   `:626`). All three are added to this plan's DoD.

9. **NEW — "reactive trigger (security alert / event)" has no seam and the story does not need one.**
   Unlike 41-20, this workflow is **event-driven, not scheduled**, so it does **not** depend on the
   scheduled-trigger seam (story 41-30). It is dispatched by whatever detects the incident — including
   41-20's `SECURITY_AUDIT.SECRET_EXPOSED` edge, an operator, or the API. The binding takes the alert
   as an input; no trigger infrastructure is built or required.

## Design Decisions

- **D1 — New `DefinitionId = "security-incident"`; no incumbent, no rewiring.** Inputs: `issueId?`,
  `repository`, `tenantId`, `alertJson` (the triggering alert — required), `incidentId`,
  `auditDocumentId?` (a 41-20 `Findings`), `contextIds?`, `acceptanceRulesJson?`. Outputs: `status`,
  `outcome`, `documentId`, `parentDocumentId`, `diagnosisJson`, `severity`, `active`.
  `builder.Version = WorkflowVersions.ComputedVersion`. Structure copies
  `DebugDiagnosisWorkflow.cs` — this is the least novel workflow in the epic and should look it.

- **D2 — `blastRadius` + `severity` are additive optional members on `Diagnosis`, made required for
  this producer through `ValidateWithContext` (Correction 4).** In
  `Tamma.Core/Documents/Types/Diagnosis.cs`:
  - `Diagnosis` gains `[JsonPropertyName("blastRadius")] public string? BlastRadius { get; init; }`
    and `[JsonPropertyName("severity")] public string? Severity { get; init; }` — **nullable**, so
    every existing `debug-diagnosis` / `blocker-diagnosis` / `debugging` fixture round-trips
    byte-identically.
  - `DiagnosisDocumentType` gains `BLAST_RADIUS_REQUIRED`, `SEVERITY_REQUIRED` and
    `SEVERITY_OUT_OF_VOCABULARY` (closed set `low|medium|high|critical`), applied **only** from
    `ValidateWithContext` when the context carries `{"requireIncidentFields":true}`. With an empty
    context, `ValidateWithContext` is byte-identical to `Validate` — the no-regression guarantee,
    asserted by test.
  - **Why not a new document type:** the README's reuse-first rule names `Diagnosis` for
    incident/security-incident analysis; forking it would add a vocabulary member, two count-pin
    bumps and an `AcceptanceDefaults` arm for two fields.
  - **Why not unconditional:** three live producers have no incident concept; an unconditional rule
    would invalidate all their documents.
  - The *remediation-references-assets* half of AC1 needs **no new rule** —
    `FIX_MISSING_AFFECTED_FILES` (`:145`) already covers it (Correction 4).

- **D3 — AC2's "always escalates" rides three existing mechanisms, none of them a new escalation
  class (Correction 5). This is the agreed answer for 41-19/41-20/41-21 alike.**
  1. **Validation** — a `critical`-severity incident diagnosis with an empty `blastRadius`, or a
     hypothesis with a `suggestedFix` naming no `affectedFiles`, is rejected before it can be
     accepted. The model cannot report a confirmed active incident without stating scope and
     remediation targets.
  2. **The landed clamp** — `AcceptanceGuardrails.Clamp`'s `BlockingReviewViolation` arm
     (`:103-110`) already forces `Accept` → `Escalate` whenever the review is not a clean approval or
     carries a blocking issue. The security reviewer raising a critical issue on an active incident
     escalates with **zero new code**. This is the primary "active incident always escalates" path.
  3. **The routing edge this story owns** — after the lifecycle exits, `SecurityIncidentBindingHelper`
     reads the accepted `Diagnosis` for `severity ∈ {high, critical}` **and** an active-incident
     marker; when present the binding takes a `DispatchWorkflow("rotate-secret")` edge
     (`WaitForCompletion=false`) and emits `SECURITY_INCIDENT.ACTIVE`. A **side-effect dispatch on a
     typed value**, not a quality decision — the direct analogue of `DeploymentPipelineWorkflow`'s
     rollback branch and of 41-20's D4.3. This is the one place this story adds a second
     `DispatchWorkflow` node; **declare and justify the deviation from a literal reading of thinness
     clause (a) in the story's ACs**, as rule 1 requires. The structure test pins that the second
     dispatch's literal definition id is `rotate-secret` and that it is unreachable except from the
     active/high-severity edge.
  4. **Deliberately NOT chosen:** `{"kind":"agent-action","key":"analyze-security-incident"}` in
     `AlwaysEscalate` — it escalates every incident diagnosis, contradicting the 85–100 autonomy row.
     It stays a valid *deployment* choice for a paranoid tenant, and the tests prove it works; it is
     not the mechanism AC2 rests on.

- **D4 — Producer-scoped resume anchor, because `diagnosis` already has three producers
  (Correction 7).** The binding computes
  `scopedIssueId = CreationBindingHelper.ScopeIssueId(issueId, "security-incident")`
  (→ `{issueId}#security-incident`) and uses it for `ComputeReEntryPositionActivity`, the lifecycle's
  `issueId` and `correlationId`. The **base** `issueId` is used only for the consumed-document read.
  Where no `issueId` exists (an alert with no issue), the anchor derives from `incidentId` instead —
  the binding always has a stable anchor, never an empty one.

- **D5 — Consumed 41-20 audit `Findings` via the store read seam, fail-loud.**
  `FetchLatestAcceptedDocumentActivity` (documentType `findings`, on the base issue/repository)
  supplies the optional audit context; its resolved `documentId` becomes the output
  `parentDocumentId` and rides `SECURITY_INCIDENT.STARTED`'s data. Absent ⇒ `null` and the run
  proceeds (the story's `Findings (41-20)?` is optional). A **supplied-but-unreadable**
  `auditDocumentId` routes to the loud failure edge — never silently `null`.

- **D6 — Incident context is assembled from the DCB stream, read-only, with no new query surface.**
  The `producerVariablesJson` carries: the alert payload, the affected-surface context, and a slice
  of the issue's/repository's DCB events read through the landed 4-7 surface
  (`IEventRepository.QueryAsync`/`QueryEventsAsync`). No new repository method, no new endpoint. Cap
  the slice size in the helper so a noisy stream cannot blow the prompt budget — a pure, testable
  bound, not an LLM concern.

- **D7 — Zero parse, zero `Finish`, exactly three typed `FlowDecision`s.** `FreshRun` (re-entry
  position == produce — gates the STARTED emission and the audit fetch), `LifecycleAccepted` (typed
  lifecycle `status`), and `ActiveHighSeverity` (D3.3, reading typed fields off the accepted
  payload — never raw model text). Nothing else branches. The structure test pins the exact
  `FlowDecision` id set.

- **D8 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, no allowlist entry** (Correction 6), with
  one `ComputeReEntryPositionActivity` node (39-10 clause (c)) and no `Wait*` activity — identical to
  `DebugDiagnosisWorkflow.cs:38`.

- **D9 — New event family `SECURITY_INCIDENT.*`.** New file
  `apps/tamma-elsa/src/Tamma.Activities/Security/SecurityIncidentEvents.cs` in the
  `ResearchEvents.cs` shape: `Started` = `SECURITY_INCIDENT.STARTED`,
  `Diagnosed` = `SECURITY_INCIDENT.DIAGNOSED`, `Accepted` = `SECURITY_INCIDENT.ACCEPTED`,
  `Active` = `SECURITY_INCIDENT.ACTIVE` (LOUD, D3.3), `Failed` = `SECURITY_INCIDENT.FAILED` (LOUD, on
  `rejected`/`escalated`, detail names the typed outcome wire — the story's three-event list has no
  failure member and every landed family has one). `ParseTenantId` + `StatusForEvent`.
  Tags: `repository`, `issueId`, `incidentId`, `tenantId`, `correlationId`.

- **D10 — "Pages the security role" and "follow-ups route to owning roles" are out of scope for this
  cut.** Both are orchestrator/Task-View concerns: 39-17 (the deciding orchestrator agent), 39-19
  (chat + Task View) and 39-20 (teams/roles/task routing) are all stubbed fail-closed in the tree —
  `AgentOfflineChatRelay` refuses every message (`Program.cs:448-451`) and
  `InitiatorOnlyTaskAudienceResolver` admits only the issue initiator (`:445-447`). Neither is an AC
  here. Record them as downstream consumers of the accepted `Diagnosis`, not deliverables. What this
  story *does* deliver is that the accept gate publishes an `AcceptanceRequest` and suspends —
  the correct half of rule 3, which is all that is reachable today.

## Implementation Steps

1. **Precondition check (no code).** `dotnet build` green. Confirm in tree: `DiagnosisDocumentType`
   registered (`DocumentTypeRegistry.cs:38`), `IDocumentType.ValidateWithContext` present, the
   lifecycle's `validationContextJson` forwarding present (`DocumentLifecycleWorkflow.cs:338-343`),
   `RotateSecretWorkflow` present, `FetchLatestAcceptedDocumentActivity` present,
   `(security, analyze-security-incident)` + its prompt file present,
   `DebugDiagnosisWorkflow` compiling as the template. **All verified present at plan time.**

2. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Diagnosis.cs`** (D2, AC1) —
   add `BlastRadius` + `Severity` nullable members to `Diagnosis`; add `BLAST_RADIUS_REQUIRED`,
   `SEVERITY_REQUIRED`, `SEVERITY_OUT_OF_VOCABULARY` constants; **override
   `ValidateWithContext(JsonElement, string)`**: empty/whitespace context ⇒ `Validate(payload)`
   verbatim; non-empty with `requireIncidentFields` ⇒ `Validate` then the three rules, with
   domain-phrased violations. Extend the `Contract` const (`:224`) with one sentence describing the
   optional `blastRadius`/`severity` pair — **shared by all `diagnosis` producers**, so word it as
   optional guidance that `debug-rootcause.md` remains valid without.
   **Coordinate with 41-20**, which makes the structurally identical edit to `Findings.cs`; the two
   should land the same shape of `ValidateWithContext` override so the pattern reads as one idiom.

3. **HAND-EDIT `apps/tamma-elsa/src/Tamma.Api/Prompts/security/analyze-security-incident.md`** —
   canonical `Diagnosis` wire (`"analysisSummary"`, `"hypotheses"` with `"rank"`, `"description"`,
   `"confidence"`, `"suggestedFix"`, `"affectedFiles"`) **plus** `"blastRadius"` + `"severity"`.
   Bump `version`; note the cell's declared `variables` — the binding's `feedbackVariableName` must
   name one of them (clause (e), the render-drop lesson; `DebugDiagnosisWorkflow` uses
   `"errorContext"` for the same reason). No 39-16 generated-region marker exists in any prompt file
   (verified), so this is a hand edit.

4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Security/SecurityIncidentEvents.cs`** (+ an
   `EmitSecurityIncidentEventActivity` if the house per-family emitter pattern applies) — D9.

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/SecurityIncidentBindingHelper.cs`** —
   pure, Elsa-free, total, fail-closed (model it on `DiagnosisBindingHelper.cs`):

   ```csharp
   public static class SecurityIncidentBindingHelper
   {
       public static string ScopeIncidentId(string? issueId, string incidentId);        // D4, never empty
       public static string BuildValidationContext();                                    // D2's requireIncidentFields
       public static string BuildIncidentContext(string alertJson, string eventSliceJson, int maxEvents); // D6
       public static (string Severity, bool Active, int HypothesisCount) ReadIncidentFacts(string documentJson);
       public static string? ResolveParentDocumentId(bool auditFound, string? auditDocId);
       public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit);
   }
   ```

6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SecurityIncidentWorkflow.cs`** — the
   binding, copying `DebugDiagnosisWorkflow.cs`'s skeleton.
   `builder.DefinitionId = "security-incident"`,
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (D8). Graph:
   `ReadInputs (computes scopedIncidentId, D4) → ComputeReEntryPosition(scopedIncidentId, "diagnosis")
   → ReadPositionStage → FreshRun(FlowDecision)`
   → *(True)* `EmitIncidentStarted → FetchConsumedAudit` (`FetchLatestAcceptedDocumentActivity`,
   documentType `findings`) `→ BuildIncidentContext` (`SetVariable`, D6) → join; *(False)* join
   → `DispatchLifecycle` (`document-lifecycle`, `WaitForCompletion=true`) with
   `documentType = "diagnosis"`, `producerRole = AgentRole.Security.ToWire()`,
   `producerAction = AgentAction.AnalyzeSecurityIncident.ToWire()`, `producerVariablesJson` (alert,
   event slice, audit findings, affected-surface context), a **declared** `feedbackVariableName`,
   `validationContextJson` (D2), `issueId = scopedIncidentId`, `correlationId`, `tenantId`,
   `acceptanceRulesJson`
   → `ReadLifecycleExit` → `LifecycleAccepted(FlowDecision)`
   → *(True)* `EmitIncidentAccepted → ActiveHighSeverity(FlowDecision)` → *(True)*
   `DispatchRotateSecret` (`rotate-secret`, `WaitForCompletion=false`) + `EmitIncidentActive` → join;
   *(False)* join; *(LifecycleAccepted False)* `EmitIncidentFailed` → join
   → `ExposeOutput` (the single terminal `Sequence` of `SetOutput`s).
   **Zero `Finish`; zero `DispatchWorkflow("llm-call")`; exactly TWO `DispatchWorkflow` nodes —
   `document-lifecycle` and `rotate-secret`** (the declared, justified deviation from clause (a),
   D3.3 — state it in the story's ACs); no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`
   variables.

7. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`**
   (Correction 8) — add to `Bindings`, mirroring the sibling `(senior_developer, debug-rootcause)`
   entry at `:214-219`:

   ```csharp
   // Story 41-21 — SecurityIncidentWorkflow binds (security, analyze-security-incident) as the
   // produce step of its document-lifecycle binding; shape authority is
   // Tamma.Core/Documents/Types/Diagnosis.cs (DiagnosisDocumentType.Validate /
   // ValidateWithContext for the incident blast-radius + severity ring).
   [("security", "analyze-security-incident")] = new("DiagnosisDocumentType.Validate",
   [
       One("\"analysisSummary\""), One("\"hypotheses\""), One("\"rank\""),
       One("\"description\""), One("\"confidence\""), One("\"suggestedFix\""),
       One("\"affectedFiles\""), One("\"blastRadius\""), One("\"severity\""),
   ]),
   ```

   Run the whole fixture: the pair must be *discovered* via the lifecycle-binding walk (clause (c)
   staleness — this dispatch uses a **constant** pair, unlike 41-20's loop, so materialisation is
   straightforward); the universal-authority pin must pass; no `IntentionallyUnbound` entry exists to
   contradict it.

8. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (Correction 8,
   rule 1(f)) — add
   `new WorkflowDocumentInterface("security-incident", new[] { DocumentTypeKey.Findings }, DocumentTypeKey.Diagnosis, false)`
   to `BuildSeed`. **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`**
   — bump `HaveCount(16)` by one, with a comment naming Story 41-21 (and note the file's existing
   comment already tracks the multi-producer `diagnosis` situation).
   **MODIFY `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`** — add
   `"SecurityIncidentWorkflow"`.

9. **CREATE the tests** — see Test Plan. Finish with full `dotnet test` and
   `dotnet ef migrations has-pending-model-changes` (must stay clean).

## Data & Migrations

None. `Diagnosis` documents persist through 39-11's existing `document_instances` table; the two new
`Diagnosis` members are additive JSON payload fields inside the existing `jsonb` payload — **no
schema change**. `SECURITY_INCIDENT.*` rides the existing `TammaEventEmitter` →
`EventPersistenceMiddleware` → `EventRepository` → `domain_events` drain.
`dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new constants, `Tamma.Activities/Security/SecurityIncidentEvents.cs`):**
  `SECURITY_INCIDENT.STARTED` (fresh runs only; data `incidentId`, `parentDocumentId`),
  `SECURITY_INCIDENT.DIAGNOSED` (data `hypothesisCount`),
  `SECURITY_INCIDENT.ACCEPTED` (data `documentId`, `severity`, `active`),
  `SECURITY_INCIDENT.ACTIVE` (LOUD; emitted with the `rotate-secret` dispatch, D3.3),
  `SECURITY_INCIDENT.FAILED` (LOUD, on `rejected`/`escalated`, detail names the typed outcome wire).
  Tags: `repository`, `issueId`, `incidentId`, `tenantId`, `correlationId`.
- **Emitted by the machinery this binding wires in (not by this story's code):** the `DOCUMENT.*`
  family (incl. `DOCUMENT.VALIDATED.FAILED` carrying `BLAST_RADIUS_REQUIRED` /
  `FIX_MISSING_AFFECTED_FILES`, and `DOCUMENT.ESCALATED`), `APPROVAL.REQUESTED`/`PROVIDED`,
  `ESCALATION.TRIGGERED`, and the `SECRET.ROTATION.*` family from the dispatched `rotate-secret` saga.
- **Consumes (read-only, at context-assembly time, D6):** the issue's/repository's DCB event slice via
  `IEventRepository` — no new query surface.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suite).

- **`DiagnosisIncidentFieldsTests`** (`Tamma.Core.Tests`, AC1, D2) — **the no-regression proof
  first:** `ValidateWithContext(payload, "")` is byte-identical to `Validate(payload)` for a
  `debug-rootcause`-shaped fixture, and it still round-trips with the two new members absent. Then,
  with the `requireIncidentFields` context: no `blastRadius` ⇒ `BLAST_RADIUS_REQUIRED`; no
  `severity` ⇒ `SEVERITY_REQUIRED`; `severity: "urgent"` ⇒ `SEVERITY_OUT_OF_VOCABULARY` naming the
  value; a well-formed incident diagnosis validates. Plus a pin that the **existing**
  `FIX_MISSING_AFFECTED_FILES` (`:145`) still fires for a hypothesis with a `suggestedFix` and no
  `affectedFiles` — AC1's remediation-references-assets half, satisfied by landed code (Correction 4).
  Plus the existing ranked-hypotheses rules (`DUPLICATE_RANK`, `RANK_CONFIDENCE_MISMATCH`,
  `CONFIDENCE_OUT_OF_RANGE`) exercised once each. **Covers AC1.**
- **`SecurityIncidentWorkflowStructureTests`** (modelled on `TaskCreationWorkflowStructureTests`) —
  thinness clauses as executable pins, with the declared deviation: exactly two `DispatchWorkflow`
  nodes, literal def ids `{document-lifecycle, rotate-secret}`; zero `llm-call` dispatches;
  `OfType<Finish>()` empty; no retry-plumbing variables;
  `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches()` contains
  `(SecurityIncidentWorkflow, DispatchLifecycle, security, analyze-security-incident)` and
  `MaterializeDispatchInput` yields `documentType == "diagnosis"` + a declared
  `feedbackVariableName` + a non-empty `validationContextJson`;
  `DefinitionId == "security-incident"`; threads `TenantId`; one `ComputeReEntryPositionActivity`;
  no `Wait*`; `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. **Plus** the exact `FlowDecision` id
  set `{FreshRun, LifecycleAccepted, ActiveHighSeverity}` (D7) and a reachability pin:
  `DispatchRotateSecret` is reachable **only** from the `ActiveHighSeverity` True edge.
  **Covers AC1 (structure), AC3.**
- **`SecurityIncidentBindingHelperTests`** — `ScopeIncidentId` determinism, never-empty, and
  collision-freedom against a `debug-diagnosis` anchor on the same issue (the D4 proof);
  `BuildValidationContext` produces exactly the shape `DiagnosisDocumentType.ValidateWithContext`
  reads (**pin both sides in one test** so they cannot drift); `BuildIncidentContext` respects
  `maxEvents` and is deterministic; `ReadIncidentFacts` on valid / unreadable → fail-closed
  (`severity` empty, `Active == false`); `ResolveParentDocumentId` across
  found/not-found/supplied-but-unreadable; `BuildFailureDetail` names each reachable
  `DocumentLifecycleOutcome` wire + `rejected`.
- **Drift-guard runs (steps 7–8, self-verifying)** — full `ContractBindingTests` fixture green;
  `ResumableStandardStructuralTests` green with **no** `SecurityIncidentWorkflow` allowlist entry;
  `WorkflowInterfaceGraphTests` at the bumped count. **Covers AC3.**
- **`SecurityIncidentLifecycleExecutionTests`** (Testcontainers, on the shared 39-6/39-10 fixture) —
  (a) **happy path from a 41-20-shaped trigger:** seed an accepted audit `Findings`; dispatch
  `security-incident` with an alert; scripted valid low-severity `Diagnosis` → security-lens review
  approve → orchestrator `Accept` resume → outputs carry `parentDocumentId` = the seeded findings id;
  `SECURITY_INCIDENT.STARTED`/`.DIAGNOSED`/`.ACCEPTED` present with matching tags; **no**
  `SECURITY_INCIDENT.ACTIVE` and **no** `rotate-secret` dispatch. **Covers AC1.**
  (b) **AC2, mechanism 3:** a scripted `critical`-severity active-incident `Diagnosis` →
  `SECURITY_INCIDENT.ACTIVE` emitted **and** a `rotate-secret` dispatch observed (capture the
  dispatcher). **Covers AC2.**
  (c) **AC2, mechanism 2 (the escalation half):** a representable active-incident diagnosis whose
  security reviewer raises a critical-severity issue — an orchestrator-side `Accept` is clamped to
  `Escalate` (`BlockingReviewViolation`) by the existing guardrail; assert the escalation-reason wire
  and that no `DOCUMENT.ACCEPTED` appears. **Covers AC2.**
  (d) **AC2, mechanism 1:** a `critical`-severity draft with an empty `blastRadius` is rejected at
  VALIDATE with `BLAST_RADIUS_REQUIRED` and drives a repair/revise round; an always-invalid stub
  exhausts the ring and exits as a typed `validation-exhausted` escalation with lineage plus
  `SECURITY_INCIDENT.FAILED`; `status = escalated`; **no `Finish` reached**. **Covers AC2.**
  (e) **always-escalate-class control:** a rules JSON with
  `{"kind":"agent-action","key":"analyze-security-incident"}` escalates via `TryPreGate` — proving
  the deployment option works while the default path does not use it (D3.4).
  (f) **D4 scoping proof:** run `debug-diagnosis` and `security-incident` for the **same** issue;
  assert two distinct accepted `diagnosis` documents under distinct scoped ids, and that each
  workflow's re-entry sees only its own.
  (g) **re-entry:** crash after acceptance → fresh `security-incident` dispatch for the same incident
  re-enters at `Complete`; exactly one `DOCUMENT.ACCEPTED` and one `SECURITY_INCIDENT.ACCEPTED` on
  the stream. **Covers AC3.**
  (h) **unreadable reference:** a supplied `auditDocumentId` that resolves to nothing → the loud
  failure edge, `SECURITY_INCIDENT.FAILED` with a typed detail (D5).

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; `Diagnosis` validated (ranked hypotheses, remediation references) | 2, 6 (D1/D2/D7) | `DiagnosisIncidentFieldsTests`; `SecurityIncidentWorkflowStructureTests`; execution (a) |
| 2 — active-incident path always escalates and can dispatch `rotate-secret`/remediation | 2, 6 (D3) | execution (b), (c), (d); control (e) |
| 3 — `[ResumeBehavior]`; 39-10 green without allowlist | 6 (D8) | `ResumableStandardStructuralTests`; execution (g) |
| 3b — *(added, rule 1(f) — Correction 8)* interface row + edge-pin bump | 8 | `WorkflowInterfaceGraphTests` at the bumped count |
| 3c — *(added, Correction 8)* cell classified in `Bindings` with the typed authority | 7 | full `ContractBindingTests` fixture |

## Risks & Mitigations

- **`Diagnosis` is a shared type with three live producers (D2).** Mitigation: both new members are
  nullable and the rules are context-gated; the first test in the suite is the byte-identical
  no-regression proof against a `debug-rootcause`-shaped fixture; the `Contract` sentence is worded as
  optional guidance so the sibling `(senior_developer, debug-rootcause)` `ContractBindingTests` entry
  (`:214-219`) stays green unchanged.
- **The `diagnosis` re-entry collision is silent if D4 is skipped.** A security incident diagnosis
  silently short-circuiting because a debug diagnosis was accepted for the same issue would be a real,
  hard-to-diagnose correctness bug. Mitigation: `ScopeIncidentId` is mandatory, unit-tested for
  collision-freedom, and execution scenario (f) proves it end-to-end against the live sibling.
- **Two `DispatchWorkflow` nodes is a declared deviation from thinness clause (a).** Mitigation: rule
  1 permits it *if named and justified in the story's ACs*; D3.3 gives the justification and the
  structure test pins the reachability. **Add the deviation to the story file's ACs before merging.**
  41-20 makes the identical deviation for the identical reason — review them together.
- **AC2 gets "solved" with an always-escalate class.** That contradicts the story's own 85–100
  autonomy row. Mitigation: D3 records the rejection; the tests pin the three real mechanisms and the
  deployment option separately, so a reviewer can see they are different things.
- **Rule 3's promise is unreachable end-to-end today (D10).** The accept gate publishes and suspends,
  but 39-17 is not in the tree so nothing decides; 39-19/39-20 mean there is no human surface either.
  Mitigation: no AC here depends on it; the story claims only the half that works, and D10 says so
  explicitly rather than leaving a reader to discover it.
- **Story-vs-code tensions:** Corrections 4 (blast radius has no field), 5 (escalation-class
  expressiveness) and 6 (resume mode) deviate from story text with reasons recorded. The residual gap
  — a payload-predicate `EscalationClassKind` — is stated, not papered over, and is shared with 41-19
  and 41-20.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition check | 0.1 |
| 2 | `Diagnosis` additive members + `ValidateWithContext` + `Contract` sentence | 0.5 |
| 3 | `analyze-security-incident.md` rewrite onto the canonical wire | 0.3 |
| 4–5 | `SecurityIncidentEvents` + `SecurityIncidentBindingHelper` | 0.5 |
| 6 | `SecurityIncidentWorkflow` binding (+ the rotate-secret edge) | 0.75 |
| 7–8 | Contract entry + registry row + edge-pin bump + drift-guard | 0.25 |
| 9 | `Tamma.Core.Tests` incident-fields suite | 0.4 |
| 9 | Structure + helper tests | 0.4 |
| 9 | Testcontainers scenarios (a)–(h) | 0.9 |
| **Total** | | **4.1** (story estimate: 3–4 days — slightly over; the `Diagnosis` extension and the eight execution scenarios are the excess) |

## Blocks / Blocked by

- **Blocked by:** Epic 39 only — `Diagnosis` + `DiagnosisDocumentType` (39-4), `document-lifecycle` +
  `validationContextJson` forwarding (39-6/39-15), `document-review`/`review-panel` (39-7), the
  accept gate + escalation surface (39-8), the resume standard (39-10), the document store + lineage
  API (39-11), the 4-7 event-query surface. **All landed and verified in tree.** Also depends on
  `rotate-secret` (`RotateSecretWorkflow.cs:34`) and the Epic 29 secrets subsystem — **landed**
  (Correction 3).
- **NOT blocked by 41-1a** — `(security, analyze-security-incident)` exists at `AgentAction.cs:95`
  and `RolePhaseMap.cs:136`, with its prompt file (Correction 1).
- **NOT blocked by 41-1b or 41-1c** — `Diagnosis` is an existing registered type; nothing prose here.
- **NOT blocked by the scheduled-trigger seam (story 41-30)** — this workflow is *reactive*, not scheduled
  (Correction 9). It is one of the few security/ops stories in Epic 41 with no scheduler dependency.
- **NOT blocked by Epic 42 for its first cut** — D6's context is assembled from the DCB stream and the
  alert payload, so the produce cell runs with tools off. A tool-enabled forensic variant would need
  Epic 42, but no AC requires it.
- **Blocked by (soft, for the intended `consumes` edge): [41-20](../story-41-20/41-20-scheduled-security-audit.md)**
  — the audit `Findings` this workflow prefers to consume. This story does **not** inherit 41-20's
  scheduler blocker: `Findings` is optional (D5), scenario (a) seeds it directly into the store, and
  the run completes with `parentDocumentId = null` when none exists.
- **Blocks (soft):** **41-22** (incident response & postmortem) consumes this `Diagnosis` when an
  incident is cross-cutting. 41-22 has its own blockers (41-1a's `incident-rootcause` cell, 41-1c's
  prose type) that this story does not share.
- **Shared-file register (coordinate before editing):** `Tamma.Core/Documents/Types/Diagnosis.cs`
  (also produced by `blocker-diagnosis`, `debugging`, `debug-diagnosis`, and read by **41-22**);
  `ContractBindingTests.Bindings` (41-17, 41-18, 41-19, 41-20, 41-1a);
  `DocumentTypeRegistry.BuildSeed` + `WorkflowInterfaceGraphTests.cs:45` (every producing-workflow
  story — serialize the bumps); `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`.
  **Land the `ValidateWithContext` idiom jointly with 41-18 (`Plan`) and 41-20 (`Findings`)** — three
  stories are making the same structural edit to three shared document types in the same window.
