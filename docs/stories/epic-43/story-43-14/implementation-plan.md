# Implementation Plan — Story 43-14: Approval Scopes and Grant Minting

Verified against the working tree 2026-08-02. Every file:line below was re-checked; where the
story's citation drifted, the corrected coordinate is given here and noted in
"Blocked / contradictions".

## Scope & Deliverable

One approval system observed from two places. Four deliverables:

1. **`Scope` on `action_authorizations`** — `single-use` (today's CAS consume, untouched) |
   `correlation-standing` (satisfies every ask in its correlation without being consumed).
   The only schema change in the story.
2. **Workflow approvals mint grants.** The merge-approval "merge" decision and the
   deploy-approval "approve" decision mint correlation-standing grants for their chain's gated
   targets, before the resumed workflow executes the approved work — so Seam C's next 409
   becomes a pass via `ReasonCoveredByAuthorization`.
3. **The run correlation is on the wire.** The cycle's instance id becomes the Elsa correlation
   of the whole run (dispatcher-inherited), and `TammaApiClient` sends it as
   `X-Tamma-Correlation-Id` on every mediation call — today ZERO enforced routes put a
   correlation where `ResolveCorrelationId` looks except the branch delete
   (pinned at `SeamEMediationTests.cs:381-390`).
4. **One shell ask per run** (Seam B denial-path ledger consult + idempotent pending mint),
   the five-chain fixture, and the chain-monotonicity build-time test.

## Pre-Reading

| File:line | Why |
|---|---|
| `docs/stories/epic-43/story-43-14/43-14-approval-scopes-and-grant-minting.md` | the ACs — source of truth |
| `docs/stories/epic-43/story-43-11/…md` — Amendment 2 §A/B/C (`:656-731`), Amendment 3 (`:936-989`), Amendment 4 (`:1458-1507`), caller-kind re-audit (`:991-1030`) | the ruling model: dial governs the LLM only; approval scopes; chains; zone levels |
| `apps/tamma-elsa/src/Tamma.Data/Entities/ActionAuthorization.cs:21-26,39,44-47,67` | open-row unique index doc, `CorrelationId`, `TargetKind/TargetKey`, `ConsumedAtUtc` — where `Scope` joins |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs:1300-1338` | the entity mapping + check constraints + `ux_action_authorizations_open` the migration must stay aligned with |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/20260729070256_AddActionGovernance.cs:73-104` | the idempotent-SQL migration posture to copy (IF NOT EXISTS, hand-written `Sql`) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/ActionAuthorizationLedger.cs:39-128` (RequestAsync, idempotent open-row), `:132-200` (TryConsumeAsync, CAS at `:183-189`), `:203-249` (DecideAsync CAS) | every state transition is a single-statement conditional UPDATE; the story's `:132-190` range is right, the CAS is `:183-189` |
| `apps/tamma-elsa/src/Tamma.Api/Services/Actions/AutonomyGateService.cs:254-320` | `ConsultLedgerAsync` — the one production caller of `TryConsumeAsync`; `ReasonCoveredByAuthorization` stamped at `:316`; guards at `:265-269` |
| `apps/tamma-elsa/src/Tamma.Api/Infrastructure/GovernanceEnforcement.cs:209` (header const), `:355-368` (pending-row mint on 409), `:425-440` (`ResolveCorrelationId`), `:444` (`route:` prefix), `:453-461` (200-char bound) | Seam C's correlation resolution: header → query → route-derived; NEVER the body |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` — helpers `GetAsync :988`, `PostAsync :1034`, `PatchAsync→SendJsonAsync :1076-1130`, `PostVoidAsync :1132`; inline senders `DeleteBranchAsync :382-413`, `DisposeProviderAsync :702-724`, `AppendEventsAsync :743`, `PersistDocumentAsync :799`, `SetDocumentStatusAsync :834`, channel-outbox `:891-907`, `AppendPlatformEventsAsync :945`; `AddTenantHeader :1185` | the COMPLETE send-path enumeration the header must cover — 5 shared helpers + 7 inline `HttpRequestMessage` builders |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs:135,149` | the sub-workflow sends its OWN instance id as the body correlation — the claim AC4 exists to kill |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/DispatchCycleActivity.cs:127-136` | the cycle's instance id is minted here; `DispatchWorkflowDefinitionRequest` carries a `CorrelationId` property (Elsa 3.5.3) that nothing sets |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MergeApprovalWorkflow.cs:56` (class), `:148-163` (`waitMerge` — story said `:146-163`, the comment header starts at `:145`), `:359-360` (the approve edge: `ConnectOutcome(waitMerge, "Merge", …)` → `DispatchMerge`) | where the merge approval decision lands and what runs next |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/WaitForDeploymentApprovalActivity.cs:52` (class), `:114-120` (`BookmarkName`), `:134-141` (`CreateBookmark`), `:146-167` (decision callback → `Approve` outcome) | story cited `:114-128`; the bookmark creation is actually `:134-141` |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdlEndpoints.cs:67-133` (`ResumeMergeApproval`), `:158-221` (`ResumeDeploymentApproval`), `:530-536` (`ResolveApprover` — server-derived identity) | the REAL decide surface: `POST /api/adl/merge-approval/resume` + `/deploy-approval/resume`, registered `Program.cs:3246-3251`, RBAC `WorkflowsManage`. The story's `POST /api/adl/{instanceId}/merge-approval` route does not exist (see contradictions) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/MergeApprovalResumeEndpoint.cs:115-136` | `RunInstanceAsync` runs the approved work SYNCHRONOUSLY before returning — the fact that forces mint-BEFORE-resume; instance id known at `:115` before the run |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/DeploymentApprovalResumeEndpoint.cs` + `Tamma.ElsaServer/Program.cs:448,458` | the deploy twin of the same seam |
| `apps/tamma-elsa/src/Tamma.Api/Services/IElsaWorkflowService.cs:141-145` (`MergeApprovalResumeResult`), `ElsaWorkflowService.cs:319-355` | the API→engine forwarding this plan extends with a locate step |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs:112-125` (`correlationId` param), `:332-391` (Seam B), `:379-390` (denial writes `rejectedToolCalls`) | where the one-ask-per-run consult joins — on the DENIAL path only |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IToolLoopAutonomyGate.cs:15-19` | Seam B's gate is sync/non-DB BY DESIGN — this plan does not change it (D6) |
| `apps/tamma-elsa/src/Tamma.Activities/Core/EventPersistenceMiddleware.cs:364-378,405-421` | the workflow-execution-middleware precedent + the registration trap (`UseTammaEventPersistence` — do NOT use `ConfigureDefaultActivityExecutionPipeline`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Rotation/RotationTriggerService.cs:53` | `rot_{guid}` — the saga-entry correlation precedent, verified |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:482` | `MaxSteps = 20` — the anti-rubber-stamp arithmetic |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Actions/SeamEMediationTests.cs:313-391` | the characterization pin this story MOVES: visible = exactly the branch delete; invisible = 15 |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Actions/AutonomyGateLedgerConsultTests.cs:177-333` | the consult fixture the standing-scope tests join |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/ActionAssignmentStorageTests.cs:263-521` | the Testcontainers ledger tests AC1 requires stay UNMODIFIED |
| `docs/stories/epic-43/story-43-12/implementation-plan.md` (steps 1, 4, 6) and `story-43-13/implementation-plan.md` (steps 2, 5) | file overlap: 43-12 owns the per-target keys and Seam E; 43-13 owns the `AutonomyQuery`/gate-service signature |

## Design Decisions

- **D1 — "Consume" for a correlation-standing grant is a MATCH, not a write.** The candidate
  query in `TryConsumeAsync` widens to admit standing rows
  (`State='granted' AND not expired AND (ConsumedAtUtc IS NULL OR Scope='correlation-standing')`
  — for standing rows `ConsumedAtUtc` is in fact always NULL, see below); a matched standing row
  is returned as covering WITHOUT any UPDATE. `ConsumedAtUtc` is never set on a standing row
  (AC1's letter), so the grant satisfies every ask in its correlation and dies only by expiry
  (`ExpiresAtUtc`, existing TTL) or by its correlation ending (nothing else carries that
  correlation). The CAS UPDATE at `ActionAuthorizationLedger.cs:183-189` remains the
  single-use-only path, byte-identical. Why no write is safe where single-use needed CAS: the
  only mutations a granted row can undergo are consumption (single-use only) and time expiry —
  `DecideAsync` requires `State='pending'` (`:234`), so no concurrent transition can invalidate
  a standing row between the read and the caller acting; expiry is checked in the same SELECT
  predicate. **Per-use audit stays per-use** without touching the row:
  `AutonomyGateService.EmitAuthorizedAsync` (`:308-311`) already fires on every covered ask.
  *Rejected:* stamping `ConsumedAtUtc` as a "last used" timestamp (overloads one column with two
  meanings and violates AC1's explicit "without setting ConsumedAtUtc"); a separate usage table
  (schema creep the story forbids — AC9: the only schema change is `Scope`).
  **Candidate order:** standing before single-use — `.OrderBy(a.TargetKind)` gains
  `.ThenBy(a.Scope)` ("correlation-standing" < "single-use" ordinally), so a standing grant
  covers repeat asks instead of burning a person's one-call grant that happens to coexist.

- **D2 — The migration is additive, idempotent, backfill-free.** One hand-written-SQL migration
  in the 43-5 posture (`AddActionGovernance.cs:73-104`):
  `ALTER TABLE action_authorizations ADD COLUMN IF NOT EXISTS "Scope" character varying(32) NOT
  NULL DEFAULT 'single-use';` plus check constraint
  `ck_action_authorizations_scope CHECK ("Scope" IN ('single-use','correlation-standing'))`.
  Existing rows take the default and keep today's semantics exactly. The open-row unique index
  (`ux_action_authorizations_open`) is deliberately UNTOUCHED: at most one open row per
  (principal, correlation, target) regardless of scope — a standing grant occupies the slot for
  its correlation, which is what makes a re-ask idempotently find it. Entity + 
  `TammaModelConfiguration` + snapshot updated in the same commit so
  `dotnet ef migrations has-pending-model-changes` stays clean.

- **D3 — Minting happens in Tamma.Api at the decide endpoints, ordered locate → mint → resume.**
  The engine has no DB (`Tamma.ElsaServer` mediates everything), and
  `MergeApprovalResumeEndpoint.cs:130-136` runs the approved work SYNCHRONOUSLY — a mint after
  the resume returns loses the race by construction (the merge call has already 409'd). So the
  engine seam gains a LOCATE half (find the bookmark, return
  `{workflowInstanceId, correlationId}` from the instance store, run nothing), and
  `AdlEndpoints.ResumeMergeApproval`/`ResumeDeploymentApproval` become: locate → mint (approve
  decisions only) → resume. RBAC (`WorkflowsManage`) and the server-derived approver
  (`ResolveApprover`, `AdlEndpoints.cs:530-536`) are already at this surface; the ledger lives in
  this process. The mint correlation is `located.CorrelationId ?? located.WorkflowInstanceId` —
  exactly what D5's ambient rule makes the downstream calls carry. Failure shapes: locate 404 →
  no mint, today's behaviour; mint ok + resume fails → a standing grant scoped to one correlation
  that expires on TTL, and the retry finds it idempotently — benign, logged.
  *Rejected:* minting inside the workflow via a new activity + engine→API mint route (a second
  trust surface, a circular hop, and the approver identity would have to travel); minting on the
  `route:`-derived correlations (one grant per route per target, and the composite spans three
  routes — defeats the design); making the engine resume asynchronous (still races the mint).

- **D4 — Grants mint as GRANTED rows via a new `MintStandingGrantAsync`, upgrading any pending
  row in place.** A workflow approval is not a request: the row is born
  `State='granted'`, `Scope='correlation-standing'`, `DecidedAtUtc=now`,
  `DecidedByUserId=approver`, TTL as today. The open-row unique index forces the semantics: if a
  pending row already exists for (principal, correlation, target) — e.g. Seam C 409'd earlier
  and minted one at `GovernanceEnforcement.cs:361-368` — the mint DECIDES it granted and sets
  `Scope='correlation-standing'` with one conditional UPDATE (the `DecideAsync` CAS shape plus
  the scope SetProperty); if a granted row exists, return it; else insert fresh, with the same
  bounded unique-violation retry `RequestAsync` uses (`:63-128`). Same CAS discipline as every
  other transition in the file (F1).

- **D5 — The run correlation is Elsa's correlation, seeded once and inherited ambiently — not 90
  edited dispatch sites.** Three small pieces, zero per-workflow churn:
  1. `DispatchCycleActivity.cs:128-132` sets `request.CorrelationId = instanceId` — the cycle's
     correlation IS the cycle instance id (`DispatchWorkflowDefinitionRequest.CorrelationId`
     exists in Elsa 3.5.3, verified against the package).
  2. A `RunCorrelationWorkflowMiddleware` (the `EventPersistenceWorkflowMiddleware` pattern,
     `EventPersistenceMiddleware.cs:364-378`; registered the `UseTammaEventPersistence` way —
     NEVER via `ConfigureDefaultActivityExecutionPipeline`, see the trap documented at
     `:380-404`) sets an `AsyncLocal` ambient
     `RunCorrelation.Current = context.CorrelationId ?? context.Id` for the duration of every
     workflow execution.
  3. A `CorrelationPropagatingWorkflowDispatcher` decorator over `IWorkflowDispatcher` stamps
     `request.CorrelationId ??= RunCorrelation.Current` — so EVERY `DispatchWorkflow` in the
     tree (16 sites in the cycle alone, ~90 total) inherits the run correlation with no edits,
     and an explicitly-set correlation is never overridden.
  `TammaApiClient` reads the same ambient in an `AddCorrelationHeader` sibling of
  `AddTenantHeader` (`:1185`) on all 12 send paths. In Tamma.Api host contexts with no workflow
  ambient the header is absent and Seam C's `route:` fallback stands, as the story requires.
  *Rejected:* setting `CorrelationId` inputs on every `DispatchWorkflow` (~90 sites, and every
  future site is a silent hole); a settable per-call client property (touches every activity that
  resolves the client); threading via workflow Inputs (same churn plus serialization surface).

- **D6 — Seam B stays sync; the ledger joins the DENIAL path only.** `IToolLoopAutonomyGate`
  (`:15-19` — sync, non-DB, by 43-9 design) is untouched. A new scoped
  `ToolLoopAuthorizationBroker` is consulted by `InlineToolLoopRunner` ONLY when the sync gate
  says `Denied` (`:379`): first `TryConsumeAsync` (a standing grant covers → the call executes),
  else `RequestAsync` mints the pending ask — idempotent per (principal, correlation, target) via
  the open-row index, which is exactly AC5's "exactly one pending row" for ≥3 shell calls. The
  hot path (allowed calls) never touches the DB; the principal resolves the same way the gate
  service does (`IGovernancePrincipalResolver.ResolveAsync(caller: null)`); the correlation is
  the runner's `correlationId` parameter (`:123`), which D5 + step 6 make the run correlation.
  This is a deliberate, recorded amendment of 43-9's "Seam B cannot consult the ledger" —
  narrowed to "Seam B's ALLOW path cannot".

- **D7 — The chain fixture is production code, shared by the minter and the tests.** A static
  `ApprovalChains` table (chain → entry approval, links with target keys, minted-grant set,
  per-link "has own resumable human wait" flag) drives BOTH `ApprovalGrantMinter` (what to mint)
  and the AC6/AC7 tests (fixture ↔ minting code ↔ catalog levels). The monotonicity checker is a
  pure function over (fixture, catalog) and carries a self-test with a synthetic violating chain
  so its red state is demonstrable. Under Amendment 4's caller-kind re-audit, the rotation,
  tenant-move and tenant-delete chains' links are MACHINERY (never dial-gated), so their
  minted-target sets are EMPTY — recorded in the fixture with justification strings, wired
  through the same minter seam (a future machinery→dial reclassification fails the fixture test
  and forces a mint decision). See contradictions #5.

- **D8 — Merge-composite grant targets: the endpoint cannot know the PR base branch, so the mint
  names all three per-target merge keys.** Post-43-12 the composite mint is
  `effect:git.merge.dev|qa|main` + `effect:git.issue.patch` + `effect:git.branch.delete`
  (Amendment 2-C3: the in-composite delete rides the merge grant; the standalone delete route
  keeps its 95). Pre-43-12 it is the coarse `effect:git.pull-request.merge` plus the same two.
  The overbreadth (three merge keys when one PR has one base) is bounded by the correlation, the
  TTL, and the LLM-only attachment; recorded in the fixture. Deploy tail mints
  `effect:deploy.prod` + `effect:git.release.create` (pre-43-12: `effect:deploy.promote-prod`).
  *Rejected:* group-scoped grants (`source-control-write` covers branch/PR creation the human
  never looked at); fetching the PR base at decide time (a platform read on the approval path,
  and a second source of truth for what 43-12 resolves at the route).

- **D9 — The mint is audited with a new event type**, `ACTION.GATE.GRANT_MINTED`, carrying
  actor (`DecidedByUserId` + approver string), workflow instance, chain name, scope, correlation
  and target set (AC8). It joins `ActionGateEventsService` (constants `:35-52`) on the
  SWALLOWING path — a mint that cannot be audited still mints (the block-not-recorded rule of
  D12/43-9 protects denials, not grants; the grant row itself is the durable record).

## Blocked / contradictions

1. **The story's decide route does not exist.** Story `:30` says approvals are "decided via
   `POST /api/adl/{instanceId}/merge-approval` with RBAC". The tree's route is
   `POST /api/adl/merge-approval/resume` (`Program.cs:3246-3247`, `AdlEndpoints.cs:65`), keyed by
   issue+PR+repo+tenant, not instance id; the `{instanceId}` shape appears only in
   `MergeApprovalWorkflow.cs:53` as a DEFERRED note. Not blocking — this plan targets the real
   route — but the story text should be corrected when this lands.
2. **Line drift, corrected in Pre-Reading:** `MergeApprovalWorkflow` bookmark is `:148-163`
   (story: `:146-163`); `WaitForDeploymentApprovalActivity` bookmark creation is `:134-141` with
   `BookmarkName` at `:114-120` (story: `:114-128`); `GovernanceEnforcement.ResolveCorrelationId`
   spans `:425-440` (story: `:425-439`). All substantive claims hold.
3. **AC2's "end-to-end" cannot run engine and API in one test host.** The bookmark decision
   executes in `Tamma.ElsaServer`; Seam C in `Tamma.Api`. Resolved by composition, not narrowed
   silently: (a) a workflow test pins the approve edge, (b) an endpoint test pins
   locate→mint→resume ORDER against a stubbed engine seam, (c) a Testcontainers test pins
   mint→mediated-call-passes + the no-approval 409 control. Together they cover AC2's claim; no
   single test does.
4. **AC5 vs Seam B's stated design.** 43-9 pre-reading says the Seam B gate "cannot consult the
   ledger" (`IToolLoopAutonomyGate.cs:15-19` posture). AC5 requires exactly that, on denials.
   Resolved by D6 (denial-path-only broker; sync gate untouched) and recorded here as an
   amendment of the 43-9 note, not a silent contradiction.
5. **AC6's five chains, honestly.** Under Amendment 4 / the caller-kind re-audit
   (43-11 `:991-1030`, machinery inventory `:1320-1393`), every link of the rotation, tenant-move
   and tenant-delete chains is MACHINERY — never dial-gated — so "the chain's gated target keys"
   is the EMPTY SET for those three today. The fixture records the empty sets with justification;
   the minter seam is wired at all five entries (rotation:
   `RotationTriggerService.cs:53` region; move/delete: the admin endpoints' task-enqueue points)
   so a future reclassification is a fixture edit, not new plumbing. AC6 is satisfied per its
   letter ("minted … with the chain's gated target keys"); if the reviewer intended non-empty
   grants for machinery chains, that contradicts Amendment 4 and needs a story-level ruling.
6. **AC7's monotonicity test is vacuous until 43-11/43-12 land levels.** Today 193 of 197 rows
   default to `AutonomyDial.Min` and no chain link exceeds any entry. The checker + its synthetic
   self-test ship red-capable now; the fixture goes load-bearing when the zone levels land. Not
   blocking; stated so nobody mistakes a green run for evidence.

## Implementation Steps

1. **MODIFY `src/Tamma.Data/Entities/ActionAuthorization.cs`** — add
   `public string Scope { get; set; } = "single-use";` with a doc comment defining both values
   and D1's no-write consume rule. **MODIFY `src/Tamma.Data/TammaModelConfiguration.cs:1300-1338`**
   — `Scope` property (max length 32, default `single-use`) + `ck_action_authorizations_scope`.
   **CREATE `src/Tamma.Data/Migrations/ControlPlane/20260802xxxxxx_AddAuthorizationScope.cs`**
   (+Designer, snapshot) — D2's idempotent SQL; Down drops column + constraint.
   *(0.5 day)*

2. **MODIFY `src/Tamma.Data/Repositories/IActionAuthorizationLedger.cs` +
   `ActionAuthorizationLedger.cs`** — (a) `TryConsumeAsync`: widen the candidate predicate and
   ordering per D1; branch: standing → return row with no UPDATE; single-use → existing CAS
   verbatim. (b) new `MintStandingGrantAsync(tenantId, userId, correlationId, targetKind,
   targetKey, decidedByUserId, reason, ttl?, ct)` per D4 (upgrade-pending CAS, return-granted
   idempotency, insert + unique-violation retry). Existing tests in
   `ActionAssignmentStorageTests.cs:263-521` stay byte-unmodified (AC1). *(1 day incl. tests)*

3. **CREATE `src/Tamma.Activities/Core/RunCorrelation.cs`** — the `AsyncLocal<string?>` holder +
   `RunCorrelationWorkflowMiddleware` + a `UseTammaRunCorrelation` registration extension (the
   `EventPersistencePipelineExtensions` pattern, same file's `:405-421` trap respected).
   **CREATE `src/Tamma.Activities/Core/CorrelationPropagatingWorkflowDispatcher.cs`** —
   `IWorkflowDispatcher` decorator, `request.CorrelationId ??= RunCorrelation.Current` on the
   definition-request overload. **MODIFY `src/Tamma.ElsaServer/Program.cs`** — register both
   inside the `AddElsa` block / DI (decoration after Elsa's registration).
   **MODIFY `src/Tamma.Activities/ADL/DispatchCycleActivity.cs:128-132`** — set
   `CorrelationId = instanceId` on the request. *(0.5 day)*

4. **MODIFY `src/Tamma.Activities/LlmCall/TammaApiClient.cs`** — `AddCorrelationHeader(request)`
   next to `AddTenantHeader` (`:1185`); call it in all 12 send paths: `GetAsync :996`,
   `PostAsync :1043`, `SendJsonAsync :1099`, `PostVoidAsync :1141`, `DeleteBranchAsync :391`,
   `DisposeProviderAsync :710`, `AppendEventsAsync :756`, `PersistDocumentAsync :806`,
   `SetDocumentStatusAsync :844`, channel-outbox `:902`, `AppendPlatformEventsAsync :957`.
   Value: `RunCorrelation.Current`, bounded to 200 chars the same way Seam C bounds
   (`GovernanceEnforcement.cs:453-461` — reuse the digest rule client-side so both ends agree).
   No new public methods (no ratchet moves). *(0.5 day)*

5. **MODIFY `src/Tamma.Activities/LlmCall/CallLlmActivity.cs:135`** — correlation becomes
   `context.WorkflowExecutionContext.CorrelationId ?? context.WorkflowExecutionContext.Id`; same
   correction in `CallLlmInlineActivity` and `MediatedLlmText` where they derive a correlation,
   and in `MergeAndCompleteReviewActivity`'s `?correlationId=` pass to `DeleteBranchAsync`
   (currently the workflow instance id — the SeamEMediationTests comment at `:381-386` names it).
   *(0.5 day)*

6. **ADD the locate half of the resume seam.** **MODIFY
   `src/Tamma.ElsaServer/Endpoints/MergeApprovalResumeEndpoint.cs` and
   `DeploymentApprovalResumeEndpoint.cs`** — a `Locate` handler each: same bookmark lookup
   (`:79-113` logic extracted/shared), no run; loads the owning instance and returns
   `{workflowInstanceId, correlationId}`. **MODIFY `src/Tamma.ElsaServer/Program.cs`** — map
   `POST /elsa/api/adl/merge-approval/locate` + `/deploy-approval/locate` beside `:448`/`:458`,
   same auth. **MODIFY `src/Tamma.Api/Services/IElsaWorkflowService.cs` + `ElsaWorkflowService.cs`**
   — `LocateMergeApprovalGateAsync` / `LocateDeploymentApprovalGateAsync`; extend
   `MergeApprovalResumeResult` (`IElsaWorkflowService.cs:141-145`) with `CorrelationId`.
   *(0.5 day)*

7. **CREATE `src/Tamma.Api/Services/Actions/ApprovalChains.cs`** — D7's fixture: five chains
   (merge-composite, deploy-tail, rotation, tenant-move, tenant-delete), entry, links, minted
   targets (D8 sets; empty + justification for the three machinery chains), own-wait flags, and
   the pure monotonicity checker. **CREATE
   `src/Tamma.Api/Services/Actions/ApprovalGrantMinter.cs`** — resolves the principal via
   `IGovernancePrincipalResolver.ResolveAsync(caller: null)` (the gate's own rule,
   `AutonomyGateService.cs:149`), calls `MintStandingGrantAsync` per fixture target, emits D9's
   audit event. **MODIFY `src/Tamma.Api/Services/Actions/ActionGateEventsService.cs`** — add
   `GrantMintedType = "ACTION.GATE.GRANT_MINTED"` (`:35-52` block) + `EmitGrantMintedAsync`.
   DI: **MODIFY `src/Tamma.Api/Extensions/ActionCatalogGovernanceServiceCollectionExtensions.cs`**.
   *(1 day)*

8. **MODIFY `src/Tamma.Api/Endpoints/AdlEndpoints.cs`** — `ResumeMergeApproval` (`:67-133`):
   when decision is `merge`, locate → `ApprovalGrantMinter.MintForChain("merge-composite", …)` →
   resume; any other decision resumes as today (rejection mints nothing — AC3's second half).
   `ResumeDeploymentApproval` (`:158-221`): same with decision `approve` → deploy-tail chain.
   *(0.5 day)*

9. **Wire the remaining saga entries through the fixture** (no-ops today per D7):
   **MODIFY `src/Tamma.Api/Services/Secrets/Rotation/RotationTriggerService.cs`** (after `:53`,
   post-`TryBeginRotationAsync`) and the tenant move/delete admin entry points
   (`src/Tamma.Api/Endpoints/…` where `POST /api/admin/tenants/{id}/move` / cleanup-request
   enqueue) — one `MintForChain` call each, so the seam exists and the fixture test can assert
   minter↔fixture parity across all five chains. *(0.5 day)*

10. **CREATE `src/Tamma.Api/Services/Agents/ToolLoopAuthorizationBroker.cs`** (D6) and **MODIFY
    `src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs:346-391`** — on `gateDecision.IsDenied`:
    broker.TryCoverAsync(toolAction, correlationId) → covered ⇒ do NOT reject (log + proceed to
    execution; the existing `rejectedToolCalls` machinery untouched otherwise); not covered ⇒
    broker.EnsurePendingAsync (idempotent) and the denial message gains the authorization id.
    Broker optional-nullable in the runner ctor (registration decides; the gate itself stays
    REQUIRED — 43-9's pin `The_gate_is_a_required_constructor_dependency` untouched). DI in the
    same extensions file as step 7. *(1 day incl. tests)*

11. **Tests** (next section) **and the two gates**: `dotnet test`;
    `dotnet ef migrations has-pending-model-changes` clean after step 1. *(1 day)*

Revised sequencing for disjoint lanes: steps 1-2 (Data lane) ∥ steps 3-5 (correlation lane) →
step 6 → steps 7-9 (mint lane) → step 10 → step 11.

## Test Plan — fail-first, with each red state named

NUnit + FluentAssertions + Moq; Testcontainers fixture from `ActionAssignmentStorageTests`;
`WebApplicationFactory` for endpoint flows; the `WorkflowTestHelper` harness for workflow edges.

- **`ActionAssignmentStorageTests` additions** (same file, appended; existing members untouched —
  AC1): `StandingGrant_CoversRepeatedAsksWithoutConsumption` (mint standing, TryConsume 3× —
  each returns the row, `ConsumedAtUtc` stays null; RED today: column/scope absent — insert
  fails at compile; against a tree with only the column, the second TryConsume returns null
  because the CAS consumed it), `StandingGrant_DiesWithExpiry`, 
  `SingleUseGrant_ConsumeSemanticsUnchanged` (a pin re-asserting `:296`'s behaviour on a
  scope-default row), `MintStandingGrant_UpgradesAPendingRowInPlace` (Seam C-minted pending →
  mint → same row id, granted, standing; RED: method absent),
  `MintStandingGrant_IsIdempotentUnderTheOpenRowIndex`,
  `StandingGrant_IsPreferredOverSingleUse_SoTheSingleUseSurvives` (both present; 2 consumes; the
  single-use row still has `ConsumedAtUtc` null; RED: ordering absent → single-use burned).
- **`AutonomyGateLedgerConsultTests` additions**: `AStandingGrant_CoversASecondAskInTheSameRun`
  (two `EvaluateAsync` calls, same correlation → both `Automated`,
  `ReasonCoveredByAuthorization`; RED today: second call is `RequiresHuman` — the exact defect
  shape of Amendment 2-B), `AStandingGrant_DoesNotLeakAcrossCorrelations`.
- **`SeamEMediationTests.WhichEnforcedRoutes_sendACorrelationIdTheGateFilterCanSee` REWRITTEN**
  (`:313-391`) — its own comment invites this: under an ambient `RunCorrelation` all 16 probed
  routes are header-visible with the AMBIENT value (RED today: `visible` is exactly the branch
  delete, `invisible` 15 — the current assert `:381-390`); a second leg with no ambient pins
  header-absent (the `route:` fallback contract).
- **`RunCorrelationTests`** (Activities.Tests) — middleware sets/clears the ambient
  (`CorrelationId ?? Id`); dispatcher decorator stamps a null request correlation and never
  overwrites an explicit one; `DispatchCycleActivity` sets `CorrelationId == InstanceId` on the
  request (RED: property never set — assert on the captured request fails).
- **`CallLlmActivityCorrelationTests`** — with a workflow correlation set, the request BODY and
  header both carry the CYCLE correlation, not `WorkflowExecutionContext.Id` (AC4's exact pin;
  RED today at `CallLlmActivity.cs:135`).
- **`MergeApprovalWorkflowTests` addition** (existing fixture,
  `tests/Tamma.Activities.Tests/Workflows/MergeApprovalWorkflowTests.cs`) — the approve edge
  (`Merge` outcome) reaches `DispatchMerge` and nothing else does (edge pin backing the mint
  site).
- **`ApprovalGrantMintingTests`** (NEW, Api.Tests, WebApplicationFactory + Testcontainers) —
  `MergeDecision_MintsTheCompositeGrants_BeforeResume` (stub `IElsaWorkflowService`: recorded
  call order locate < mint < resume; grants = fixture's merge-composite set on the located
  correlation; RED: no mint happens at all), `RejectDecision_MintsNothing`,
  `DeployApprove_MintsDeployTail` / `DeployReject_MintsNothing` (AC3),
  `MintedGrant_LetsTheMediatedCallPass_WithA409Control` — the AC2 composition: seed the mint,
  call the enforced merge route with the correlation header → 200-shape via
  `ReasonCoveredByAuthorization`; without the mint → 409 `ACTION.GATE.REQUIRES_HUMAN` (control).
  RED today: both legs 409. Requires the target action above the dial (per-target key post-43-12,
  coarse pre).
- **`ToolLoopAuthorizationTests`** (NEW, Api.Tests) — AC5's pin: shell above the dial, loop of 3
  shell calls, exactly ONE pending row exists (RED today: broker absent — zero rows, three
  denials); grant the row standing → subsequent shell calls execute with no further rows;
  `Gate_remains_required_and_sync` (reflection: runner ctor gate param unchanged).
- **`ApprovalChainsTests`** (NEW, Api.Tests) — AC6: minter targets ≡ fixture for all five chains
  (RED: minter or fixture absent); the three machinery chains' sets are empty AND every fixture
  link resolves in the catalog (staleness both ways). AC7: monotonicity over (fixture ×
  catalog levels), violations named with the chain; `Checker_SelfTest_FlagsASyntheticViolation`
  (the red-capability proof — a synthetic chain with a 95 link under a 65 entry and no own wait
  MUST be flagged; this member fails if the checker is a stub).
- **`ActionGateEventsServiceTests` additions** — `GrantMinted_CarriesActorInstanceScopeAndTargets`
  (AC8's audit fields), `GrantMintedEmissionFailure_IsSwallowed` (D9).
- **Schema gate** — `dotnet ef migrations has-pending-model-changes` clean; the residency test
  (`ActionGovernanceResidencyTests`) stays green (the table was already excluded from the DROP
  list; the column changes nothing there).

## Count pins moved (values read from the tree, 2026-08-02)

| Pin | Before | After | Where |
|---|---|---|---|
| `SeamEMediationTests.WhichEnforcedRoutes_sendACorrelationIdTheGateFilterCanSee` — `visible` set | exactly `["DELETE /api/v1/git/{o}/{r}/branches"]` (`:381`) | all 16 probed routes under an ambient correlation; `[]` without one | `tests/Tamma.Activities.Tests/Actions/SeamEMediationTests.cs:381-390` |
| same test — `invisible.Should().HaveCount(…)` | `15` (`:388`) | `0` (ambient leg) / `16` (no-ambient leg) | same |
| `ActionGateEventsService` event-type constants | 9 (`:35-52`) | 10 (`GrantMintedType`) — no count pin exists on this block; named-constant asserts in `ActionGateEventsServiceTests.cs:63-68` gain one line | `src/Tamma.Api/Services/Actions/ActionGateEventsService.cs` |
| ControlPlane migrations | 20260729070256 is the latest | +1 (`AddAuthorizationScope`) — no count pin exists on the migrations list | `src/Tamma.Data/Migrations/ControlPlane/` |

Deliberately NOT moved (and asserted so): `KnownNonEffectClientMethods` stays 19 with history
`[19]` and `The_sweep_actually_sees_the_client_surface` unchanged — this story adds ZERO
`TammaApiClient` methods (header only). `KnownUngovernedEndpoints.PinnedCount` 216 /
`PinnedInScopeCount` 239 unchanged — the locate routes are ENGINE routes (`/elsa/api/…`), outside
that sweep, and no Tamma.Api route is added. `ActionEnforcementSitesTests`' 21 bound rows
unchanged — no new bindings. `ToolLoopAutonomyGateSeamTests` pins unchanged — the sync gate and
its required-ctor pin are untouched.

## Dependencies on the other stories in this batch

- **43-12 (per-target merge/deploy keys) — land FIRST or together.** The grant target sets (D8)
  and the AC2/AC3 tests name `effect:git.merge.*` / `effect:deploy.prod`; pre-43-12 they name the
  coarse keys and must be re-pointed in `ApprovalChains.cs` when 43-12 retires them (43-12 AC2
  greps for zero references to the coarse wires — the fixture would be a hit). File overlap:
  43-12 step 6 edits `DeploymentPipelineWorkflow.cs`; this plan does not touch that file — clean.
- **43-13 (caller-kind predicate) — coordinate, either order.** 43-13 steps 2/5 change
  `AutonomyQuery` and `AutonomyGateService` — the same `ConsultLedgerAsync` region this story's
  tests lean on (this plan changes the LEDGER, not the service, so the code conflict is
  test-level). AC8's "a human caller never needs a grant" is only assertable once 43-13's
  predicate exists; until then that AC8 leg is deferred to 43-13's fixture.
- **43-11 — the ruling model; docs-blocking only.** The monotonicity test goes load-bearing when
  its level assignments land (contradiction #6).
- **43-15 (toggles/dial UI) — after this story.** Consumes the grant table for approve-rate
  telemetry; the toggle layer is explicitly out of scope here.
- **43-16 (acceptance unification) — disjoint.** No shared files.
- **42-10 (shell sandbox + secret.read) — REAL overlap on `InlineToolLoopRunner`.** Its
  best-effort secret-read grading edits the same Seam B region as step 10. Sequence one after
  the other (either order); do not run the lanes concurrently.
- **39-25 (ambiguity threading) — disjoint** (document lifecycle).
- **40-8 (create-issues workflow) — near-disjoint.** It adds a workflow + touches
  `SingleIssueCycleWorkflow` outcome edges; this plan touches neither (the dispatcher decorator
  is what spares the cycle file). Its new `CreateIssuesWorkflow` inherits the run correlation
  for free via D5. Shared file: `Tamma.ElsaServer/Program.cs` (route/DI registrations) — trivial
  merge.
- **31-13 (full PR operations) — coordinate on `TammaApiClient`.** It adds client methods (issue
  create/comment/labels), which moves the effect-sweep pins; this story edits every send helper
  in the same file. Land this story's client change first (mechanical, whole-file-touching), then
  31-13 rebases; its new methods get the header for free.

## Risks

- **AsyncLocal flow through Elsa's execution pipeline.** If a future Elsa invoker schedules
  activity work outside the middleware's async flow, the ambient is empty and calls fall back to
  `route:` — fail-safe (a 409 with a route correlation, today's behaviour), never wrong-correlation.
  The `RunCorrelationTests` middleware test plus the SeamE probe rewrite pin the happy path.
- **In-flight workflow instances predate the threading.** A cycle dispatched before deploy has no
  correlation; sub-workflows fall back to their own id ambient, and a grant minted at approval
  uses `located.CorrelationId ?? located.WorkflowInstanceId` — which matches ONLY the located
  instance's own subtree. For the merge chain the mediated calls are made by the merge
  sub-workflow (a child of the located merge-approval instance), so the fallback still covers.
  Stated here so a mixed-version window is not read as a defect.
- **Standing grants outliving intent.** A correlation-standing grant lives to its TTL (24 h
  default) within one correlation. A hijacked run could reuse it — bounded by correlation,
  principal, target and the LLM-only path; the toggle layer (43-15), not this story, is where
  standing-beyond-one-run lives. Recorded, not closed.
- **Locate→mint→resume is not atomic.** A resume that fails after the mint leaves a benign
  correlation-scoped grant (D3). The reverse order is the defect this story exists to fix, so
  the non-atomicity is on the safe side by construction.
- **Two stories in one file.** `InlineToolLoopRunner` (42-10) and `TammaApiClient` (31-13) —
  sequencing named in Dependencies; the wave planner must not run those lanes in parallel.

## Effort

4.5 days: 0.5 migration + 1 ledger semantics + 0.5 correlation plumbing + 0.5 client header +
0.5 activity correlation + 0.5 locate seam + 1 fixture/minter/endpoints + 1 tool-loop broker +
1 tests/pins/gates (steps overlap across the two lanes).

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-08-02 | 1.0.0   | Initial plan — Scope column + standing-consume semantics (D1/D2), locate→mint→resume grant minting (D3/D4), ambient run-correlation threading (D5), Seam B denial-path broker (D6), five-chain fixture + monotonicity (D7); 7 contradictions/corrections recorded incl. the nonexistent `{instanceId}` decide route and the empty machinery-chain grant sets | Claude |
