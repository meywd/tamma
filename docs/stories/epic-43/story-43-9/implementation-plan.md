# Implementation Plan — Story 43-9: The Five Seams, Enforcement Live, and the Authorization Ledger

> ## AMENDMENT 2026-08-01 — THIS PLAN IS HALF-OBSOLETE. READ BEFORE STEP 1.
>
> This plan was written 2026-07-25 against a tree in which none of Story 43-9 existed. Stories
> 43-4, 43-5, 43-6 and 43-8 have since shipped, and **steps 1, 2, 3, 4, 6 and 12 are already
> done in the tree.** Following the plan literally rebuilds `IAutonomyGate`,
> `AutonomyGateEvaluator`, `AutonomyGateService`, `ActionGateEventsService`, the entire Seam B
> block, and the `action_authorizations` ledger.
>
> The full per-AC audit with file:line evidence is in the story document's **AMENDMENT 2026-08-01
> §A**. The four decisions (§B–§E there) bind this plan too. What follows is the plan-specific
> correction set; nothing below has been deleted, only annotated.
>
> ### Step-by-step status, verified 2026-08-01
>
> | Step | Status | Evidence / what to do instead |
> |---|---|---|
> | 1 — `CREATE Tamma.Core/Actions/IAutonomyGate.cs` | **DONE** — and the file is named differently | The four types live in `Tamma.Core/Actions/AutonomyGovernance.cs`: `IAutonomyGate` `:314`, `AutonomyQuery` `:279`, `AutonomyDecision` `:292`, `AutonomyOutcome` `:143`. **Remaining:** `AutonomyDecision` has no `CoveredBy`/`AuthorizationId` (it has `Enabled`, `AllowedRoles`); add both for step 12's consult. |
> | 2 — `CREATE AutonomyGateEvaluator.cs` | **DONE** | `Tamma.Core/Actions/AutonomyGateEvaluator.cs`, 636 lines, pure static; `TryPreGate` bridge `:595`, floor semantics `:549-560`. Tests in `Tamma.Core.Tests/Actions/`. |
> | 3 — `CREATE AutonomyGateService.cs` | **DONE** | `Tamma.Api/Services/Actions/AutonomyGateService.cs:49`. The resolver widening it depended on is also done (`IAcceptanceRulesResolver.cs:40,48`). **Remaining:** the ledger consult (step 12). |
> | 4 — `CREATE ActionGateEventsService.cs` | **DONE** | `Tamma.Api/Services/Actions/ActionGateEventsService.cs`; nine type constants `:35-52`. Two deviations from the AC text, both deliberate — see story AC14's amendment. |
> | 5 — `MODIFY LlmCallEndpoints.cs` (Seam A) | **TO DO** — and its enforcement posture changed | No gate call exists. Decision 1 (story §B): this route **never opts into enforcement**, which is what makes D2 structural. |
> | 6 — `MODIFY InlineToolLoopRunner.cs` (Seam B) | **DONE** | Required gate ctor param `:73`, null-throw `:94`, seam `:332-390`, denial `:381`. Note it takes `IToolLoopAutonomyGate` (sync, non-DB), **not** `IAutonomyGate`. |
> | 7 — filter + `MODIFY GovernsExtensions.cs` | **TO DO, but NOT as written** | Decision 1: `Governs()` stays metadata-only. Add a separate per-route opt-in covering **both** authoring planes (builder extension `GovernsExtensions.cs:28` and controller attribute `ActionGateMetadata.cs:47` — a filter inside `Governs()` would have missed the 4 `MentorshipController` actions entirely). |
> | 8 — `CREATE BackgroundActionGate.cs` (Seam D) | **PARTIAL** | The admin-API rejection half is DONE (`ActionPolicyEndpoints.cs:614-623`, test `ActionPolicyEndpointsTests.cs:295`). The helper and call sites are not. **29** actors, not 25. `IAutonomyGate` is **scoped** (`ActionCatalogGovernanceServiceCollectionExtensions.cs:84`) — a singleton hosted service must create a scope per tick. |
> | 9 — evaluate route + baseline entry | **TO DO** | The pin bump is impossible as written — Decision 3 (story §D). Also delete `GovernedEndpointCoverageSweepTests.PreProvisionedJustificationKeyword_isStillUnused` (43-8 §A3 step 3). |
> | 10 — `CheckActionGateActivity` + client method | **TO DO** | `KnownReadOnlyClientMethods` **does not exist**; the real baseline is `MediationClientEffectSweepTests.KnownNonEffectClientMethods` (`Tamma.Activities.Tests/Actions/MediationClientEffectSweepTests.cs:231`) and it **cannot be bumped** — Decision 3. |
> | 11 — `MODIFY DeploymentPipelineWorkflow.cs` | **TO DO** | `:242-245` still has exactly two terms. All three line references in this plan (`:242`, `:248`, `:588`) are **still correct**. |
> | 12 — `CREATE ActionAuthorizationLedger.cs` | **DONE** — by 43-5, in `Tamma.Data`, not `Tamma.Api` | `Tamma.Data/Repositories/IActionAuthorizationLedger.cs` + `ActionAuthorizationLedger.cs:132`; migration `20260729070256_AddActionGovernance.cs:75-104`; Testcontainers tests `Tamma.Api.Tests/Actions/ActionAssignmentStorageTests.cs:296-521`. **Remaining:** no production caller of `TryConsumeAsync`; nothing reads `Tamma:Governance:AuthorizationTtlHours`. |
> | 13 — decide + pending endpoints | **TO DO** | `DecideAsync` (the state machine) already exists on the ledger — this step adds the **routes** only. |
> | 14 — test suites + `dotnet test` | **TO DO**, scoped to what is actually new | Many named tests already exist under different names; the story's per-AC amendments name them. |
>
> **Sequencing, revised:** 5 → 7 → 9–11 → 8 → 12(remaining)–13 → 14. Steps 1–4, 6 and the bulk of
> 12 are dropped. Step 9's baseline entry is 43-8's one open handover and should go first inside
> that group.

## Scope & Deliverable

When this story is done the autonomy dial is a control, not a label.

`IAutonomyGate` + a pure `AutonomyGateEvaluator` in `Tamma.Core`, `AutonomyGateService` in `Tamma.Api`. Five seams wired, **enforcing in v1**, with shipped defaults that reproduce today's behaviour exactly: Seam A observing at the llm-call endpoint and never blocking; Seam B at one site in `InlineToolLoopRunner` between sanitization and the parallel fork, with the gate as a **required** constructor parameter and denials riding the existing `rejectedToolCalls` path; Seam C as an endpoint filter returning **409**; Seam D as a deny-only per-tick helper that cannot take down the host; Seam E as a `[FlowNode]` activity reaching the gate over a new `EngineServiceOnly` mediation route, adopted in exactly one place — the deployment pipeline's prod-approval decision — **by OR**, on `effect:deploy.promote-prod`.

Plus the `action_authorizations` ledger semantics (`TryConsumeAsync`, group-covers-member, TTL, consumption), `POST /api/actions/authorizations/{id}/decide`, `GET /api/actions/authorizations?state=pending`, and one audit family in which **denials under enforcement are not swallowed**.

## Pre-Reading

> **AMENDED 2026-08-01 — EVERY LINE NUMBER BELOW IS STALE EXCEPT THE DEPLOYMENT-PIPELINE ONES.**
> The list is kept verbatim so a reader can see what changed. Use this table instead; it was
> verified against the working tree on 2026-08-01 at commit `6429691`.
>
> | Reference as written below | Verified 2026-08-01 |
> |---|---|
> | `InlineToolLoopRunner.cs:45-55` (ctor) | `:63-107` — and it now has **13** parameters, one of which (`IToolLoopAutonomyGate autonomyGate`, `:73`) is **required**. The "all-optional ctor" premise is already fixed. |
> | `InlineToolLoopRunner.cs:259-281` (validator block) | `:308-330`; `ArgumentsJson` rewrite `:319` |
> | `InlineToolLoopRunner.cs:284` (assistant message) | `:393` |
> | `InlineToolLoopRunner.cs:299-325` (rejected→tool-result) | `:411-433` |
> | `InlineToolLoopRunner.cs:330` / `:335` (`executableToolCalls` / fork) | `:439` / `:444` |
> | *(not in the original list)* **Seam B itself** | `:332-390` — **already implemented.** Read this before writing anything for Seam B. |
> | `LlmCallModels.cs:500` (`EnableParallelTools = false`) | **still correct** |
> | `ToolExecutorRegistry.cs:56-62` (fail-open allowlist) | **still correct** |
> | `Program.cs:1698-1730` (Dev re-registers "all 22 policies") | `:1791-1808`, and it is **26** policies (`:1795-1806`) |
> | `Program.cs:1788-1803` (middleware order) | `:1874-1889` |
> | `Program.cs:2919-2957` ("the 11 landed resume endpoints") | **6** endpoints: `:3211, 3215, 3222, 3228, 3235, 3248` |
> | `Program.cs:3026` (`POST /api/v1/llm/call`) | `:3318`, with `.Governs(effect:llm.call)` at `:3321` |
> | `Program.cs:3136` (`POST /api/v1/notifications/slack`) | `:3438` |
> | `NotificationEndpoints.cs:116` (`Results.Accepted`) | **still correct** |
> | `TammaApiClient.cs` `IsSuccessStatusCode` at `:228,502,…` | **not re-verified in this pass** — treat as unconfirmed. The *claim* (the client discriminates on nothing else) is what `Client_treats_202_as_success` must encode; it does not need this line list. |
> | `PermissionHandler.cs:26,41,106` | `:33`, `:48`, `:113`, `:160` |
> | `SelfOrPermissionRequirement.cs:65` | `:69` |
> | `DeploymentPipelineWorkflow.cs:242` / `:248` / `:588` | **all three still correct** |
> | `WaitForDeploymentApprovalActivity.cs:52` | **still correct** |
> | `ActionGate.cs:17`; `Program.cs:750` registers it | `ActionGate.cs:18`; registered `Program.cs:731` |
> | `AcceptanceRulesEventsService.cs:16-18,54-93` (template) | not re-verified; the template has already been **consumed** — `ActionGateEventsService.cs` exists |
> | `AcceptanceRulesService.cs:91-108` (the widening) | `:114-130`, and the widening is **already done** (`IAcceptanceRulesResolver.cs:40,48`) |
> | `Tamma.ElsaServer/Program.cs:2849-2851`-equivalent | not re-located; treat as unverified |
> | "**NOT FOUND (authored by prerequisite stories)**: `Tamma.Core/Actions/*`, `ActionGroup` + shipped defaults, `action_assignments` / `action_authorizations` entities, `IGovernancePrincipalResolver`, `IGovernancePolicySnapshotProvider`, `.Governs`, `/api/actions`" | **ALL OF IT NOW EXISTS.** `Tamma.Core/Actions/` holds 16 files; `ActionGroup.cs`; `Tamma.Data/Entities/ActionAuthorization.cs`; `Tamma.Api/Services/Actions/GovernancePrincipalResolver.cs`, `GovernancePolicySnapshotStore.cs`; `Tamma.Api/Infrastructure/GovernsExtensions.cs`; `Tamma.Api/Endpoints/ActionPolicyEndpoints.cs`. This line is the single most misleading sentence in the plan. |
>
> **Add to the pre-reading, because they did not exist on 2026-07-25 and now constrain the work:**
> - `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IToolLoopAutonomyGate.cs` — the **sync, non-DB** Seam B gate. Its `:15-19` doc-comment is why Seam B has no `RequiresHuman` and cannot consult the ledger.
> - `apps/tamma-elsa/src/Tamma.Api/Extensions/ActionCatalogGovernanceServiceCollectionExtensions.cs:43-97` — the whole governance DI graph. **Lifetimes matter:** snapshot provider **singleton** `:52`, `IAutonomyGate` **scoped** `:84`, `IToolLoopAutonomyGate` **scoped** `:92`, ledger **singleton** `:46`.
> - `apps/tamma-elsa/src/Tamma.Api/Services/Actions/GovernancePolicySnapshotStore.cs:17-32,59-63` — the 60 s TTL and its honestly-stated propagation delay.
> - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Actions/MediationClientEffectSweepTests.cs:231,442,543,546-576` and `tests/…/RatchetDisciplineTests.cs:59-93,200-235` — the ratchet Decision 3 must not break.
> - `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:128,142,157` — the second ratchet, same problem.
> - `docs/stories/epic-43/story-43-8/43-8-drift-harnesses.md` §A3 step 3 — 43-8's one open handover to this story.

- `docs/stories/epic-43/story-43-9/43-9-seams-enforcement-and-authorization-ledger.md` — this story (ACs are source of truth)
- `docs/stories/epic-43/README.md` — "Enforcement" (the five-seam table and every per-seam constraint), Decision D1 (enforce in v1), §"Absorbing the existing always-escalate list"
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs` — **read `:45-55` (all-optional ctor), `:259-281` (validator block, `ArgumentsJson` rewrite at `:271`), `:284` (assistant message), `:299-325` (rejected→tool-result machinery), `:330` (`executableToolCalls`), `:335` (`EnableParallelTools` fork)**. Every one of those numbers constrains where the gate goes.
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:500` — `EnableParallelTools = false`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolExecutorRegistry.cs:56-62` — the fail-open allowlist the gate must survive
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` — `:1698-1730` (Development re-registers all 22 policies as `AllowAnonymousRequirement`), `:1788-1803` (middleware order → tenant context after authorization), `:2919-2957` (the 11 landed resume endpoints), `:3026` (`POST /api/v1/llm/call`), `:3136` (`POST /api/v1/notifications/slack`)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/NotificationEndpoints.cs:116` — `Results.Accepted` — **the 202 that makes 202-as-escalation unusable**
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` — `IsSuccessStatusCode` at `:228,502,551,593,626,680,729,761,804,854,890` (eleven sites, no status-code discrimination anywhere)
- `apps/tamma-elsa/src/Tamma.Api/Auth/PermissionHandler.cs:26,41,106` + `SelfOrPermissionRequirement.cs:65` + `Tamma.Api/Hubs/…/OrchestratorChannelHandler.cs:46-50` — the two unconditional bypasses the filter deliberately does not inherit
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs:242` (`prodApprovalNeeded`), `:248` (`waitProdApproval`), `:588` (`StageDeployDispatch`, shared across stages)
- `apps/tamma-elsa/src/Tamma.Activities/ADL/WaitForDeploymentApprovalActivity.cs:52`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs:17` — **the name collision**; `Program.cs:750` registers it
- `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/AcceptanceRulesEventsService.cs:16-18,54-93` — the events-service template
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/IAcceptanceRulesResolver.cs` + `AcceptanceRulesService.cs:91-108` — the one interface widening
- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:2849-2851`-equivalent comment in `Tamma.Api/Program.cs` — "the engine registers no repository and mediates everything through the API client"
- `docs/stories/epic-43/story-43-8/implementation-plan.md` — `.Governs` / `ActionGateMetadata` shape; the `KnownUngovernedEndpoints` ratchet this story bumps
- **NOT FOUND (authored by prerequisite stories):** `Tamma.Core/Actions/*` (43-2), `ActionGroup` + shipped defaults (43-3), `action_assignments` / `action_authorizations` entities + `IGovernancePrincipalResolver` + `IGovernancePolicySnapshotProvider` (43-5), `.Governs` (43-8), `/api/actions` group (43-6).

## Design Decisions

- **D1 — Enforcement is LIVE in v1. Binding; there is no flip story.** Defaults reproduce today's behaviour exactly (43-3), so shipping enforcing changes nothing on day one; the admin opts in and it bites immediately. The rejected alternative — ship switched off, flip after ≥30 days of `WOULD_BLOCK` telemetry — means an admin who sets deploy to human-only **gets nothing**, in an epic whose product requirement is precisely that setting. `WOULD_BLOCK` survives as a shadow signal for *untightened* actions, not as a precondition. Consequence for this plan: there is no "enforcement flip" step, and the ledger + decide endpoint + pending surface (which design.md parked in that story) ship **here**, because a live enforcing gate that can escalate without a way to grant is unusable.

- **D2 — Seam A is observe-only in EVERY version, and that is a safety decision, not a phasing one.** Two independent reasons, either sufficient: (i) a `RequiresHuman` at `Program.cs:3026` reaches a `DispatchWorkflow` whose calling workflow has **no human route in 44 of 45 cases** — the workflow would suspend with nobody able to resume it, which is worse than proceeding; (ii) blocking at A *and* at E double-gates deploy, because the deployment pipeline both dispatches `llm-call` and has a prod-approval decision. Agent-action enforcement therefore lives **only at Seam E**. Pinned by `LlmCallSeam_NeverBlocks_EvenUnderEnforce`, run with the action at `AlwaysHuman` at every scope, so a future author who "completes" Seam A goes red.

  > **AMENDED 2026-08-01 — D2 is upheld, but the mechanism that was going to break it is
  > replaced. See new D15.** As written, D2 was unsatisfiable next to step 7: the Seam A route
  > already carries `.Governs(effect:llm.call)` (`Program.cs:3321`) on an `Enforceable = true`
  > member, so attaching the filter inside `Governs()` would have made this route blockable on
  > day one. The pin `LlmCallSeam_NeverBlocks_EvenUnderEnforce` would have gone red *against the
  > implementation the same plan prescribes*. D15 resolves it structurally.

- **D3 — Seam B's position is fixed by three separate facts, and all three must hold simultaneously.**
  1. **After** the validator block closes (`:281`), because `ToolCallValidator` **rewrites `tc.ArgumentsJson` at `:271`** — a gate before it would evaluate pre-sanitization arguments and, worse, would report a `Target` that is not what executes.
  2. **Before** `executableToolCalls` (`:330`) and the `EnableParallelTools` fork (`:335`), because `EnableParallelTools` defaults **`false`** (`LlmCallModels.cs:500`) — a gate on the parallel branch alone would govern **nothing** in the default configuration.
  3. **Not nested inside `if (_toolCallValidator != null)`** (`:260`), because **every** ctor dependency on that path is optional-nullable (`:45-55`). Nesting makes the gate absent exactly when the validator is absent — a governance component whose presence is contingent on an unrelated optional dependency. `IAutonomyGate` is a **required** ctor parameter; pinned by `Constructor_RequiresTheGate` (a compile-time property, asserted by reflection over the ctor signature so it survives a future overload).

  > **AMENDED 2026-08-01 — D3 SHIPPED, in Story 43-4/43-5. All three facts hold in the tree.**
  > After the validator block (`InlineToolLoopRunner.cs:308-330` closes, rewrite at `:319`; seam
  > at `:332`); before `executableToolCalls` (`:439`) and the fork (`:444`); not nested inside
  > `if (_toolCallValidator != null)`; gate required at `:73` with a null-throw at `:94`. The
  > `Constructor_RequiresTheGate` pin exists as
  > `ToolLoopAutonomyGateSeamTests.The_gate_is_a_required_constructor_dependency` (`:235`).
  > **One correction to D3's own premise:** it says "*every* ctor dependency on that path is
  > optional-nullable (`:45-55`)". That is no longer true — the gate itself is required, which is
  > the point. Cite `:63-107`.

- **D4 — Seam B's outcome is `Denied`, and the denial reuses machinery that already exists.** A denial writes `rejectedToolCalls[tc.Id] = reason`; `:299-325` already converts that into a tool-result message fed back to the model (sanitized and secret-redacted on the way). **No exception, no new failure code, no new plumbing.** Naming it `RequiresHuman` would be a lie — there is no human on this path, no bookmark, nobody to resume it. Calling it escalation would produce an audit trail claiming a person was asked when none was.

- **D5 — The gate is additive over the two fail-open allowlists, not a replacement for them.** `ToolExecutorRegistry.IsAllowed` returns `true` on a null/empty allowlist (`:56-62`) and `ToolCallValidator` fails open when absent. Both **stay**. The gate is evaluated independently, so a null allowlist cannot defeat it (`Gate_denies_even_when_registry_allowlist_is_null`). Deriving either allowlist from the catalog is explicitly out — it would delete 12 defensive shell aliases and add false-positive blocks.

- **D6 — Seam C is an endpoint filter, forced by middleware order, and two security properties fall out for free.** `ITenantContext.TenantId` is unset during authorization (`Program.cs:1788-1803`: tenant context runs *after*), and there is no `IAuthorizationPolicyProvider`, so a per-action `IAuthorizationHandler` is not implementable. The filter runs after all of it. Consequences worth keeping and testing: (i) it does **not** inherit `platformRole == "platform_admin"` (`PermissionHandler.cs:41`, `SelfOrPermissionRequirement.cs:65`) nor the `"*"` permission claim (`PermissionHandler.cs:26,106`) — **a platform admin can edit assignments but cannot bypass a governed effect**; (ii) it is unaffected by `Program.cs:1698-1730`, which re-registers all 22 named policies with `AllowAnonymousRequirement` in Development-without-JWT.

  > **AMENDED 2026-08-01 — D6's *conclusion* stands; its coordinates and one count are wrong, and
  > its attachment mechanism is superseded by D15.** The middleware-order argument is confirmed
  > (`Program.cs:1874-1889`: `UseAuthentication` `:1874` → `ProxyHeaderAuthMiddleware` `:1880` →
  > `UseAuthorization` `:1881` → `UseRateLimiter` `:1882` → `ImpersonationContextMiddleware`
  > `:1887` → `TenantContextMiddleware` `:1888`), so a per-action `IAuthorizationHandler` is
  > genuinely not implementable and an endpoint filter is genuinely forced. Corrections:
  > the Development blanket is `Program.cs:1791-1808` and covers **26** named policies
  > (`:1795-1806`), not 22 — assert against the array length, not a literal. The bypass sites are
  > `PermissionHandler.cs:48` (`platform_admin`) and `:33`/`:113`/`:160` (`"*"`), plus
  > `SelfOrPermissionRequirement.cs:69`. **Attachment:** the filter is no longer attached by
  > `Governs()` — see D15 — and must cover the controller-attribute plane too.

- **D7 — Denial is `409`, and `202` is disqualified by evidence, not by taste.** `TammaApiClient` branches **solely** on `IsSuccessStatusCode` at all eleven mutating sites, and **202 is already a success code on that client**: `QueueSlackNotificationAsync:386` → `Program.cs:3136` → `NotificationEndpoints.cs:116` `Results.Accepted`. A 202 "escalated" response would be indistinguishable from success and the engine would proceed as if the effect had happened — the exact failure the gate exists to prevent, introduced by the gate. `409` rather than `403` because the semantic is *not* "you may not": the caller is authorized; the **system** is not yet permitted to act autonomously. A characterization test (`Client_treats_202_as_success`) encodes the reason so a future author cannot "improve" it back to 202.

- **D8 — Seam D can only deny, and the impossibility is structural.** A hosted service has no `ActivityExecutionContext`, no bookmark, and no one watching — a sweeper cannot suspend for a person. So `automation:*` descriptors are `EscalatableToHuman = false`, the admin API **rejects** a mid-range `MinAutonomy` on them (`ACTION_POLICY.INVALID`) rather than silently treating it as Deny, and the UI is two-state. Exceptions are caught **inside** the helper: `BackgroundServiceExceptionBehavior` defaults to `StopHost`, so an unhandled governance failure would kill the process. Failures emit `ACTION.GATE.EVALUATION_FAILED` and the tick proceeds ungated — fail-open is correct here, because fail-closed on an evaluation *error* would stop every sweeper on a CP blip.

- **D9 — Seam E reaches the gate over HTTP, and the route is honestly ungoverned.** `Tamma.ElsaServer` registers no repository and mediates everything through `TammaApiClient`; `ElsaServer.csproj` references only `Tamma.Activities` + the analyzer. So DI injection of `IAutonomyGate` into an activity is not possible. New route `POST /api/v1/governance/evaluate` (`EngineServiceOnly`). It mints **no `ExternalEffect` member** — it is a read — and lands in `KnownUngovernedEndpoints` with `gate-evaluation-endpoint-cannot-gate-itself`, bumping the 43-8 count pin in the same commit. Anything else is circular.

- **D10 — Seam E's adoption is by OR and on the EFFECT. Both halves are load-bearing.** *By OR*: `prodApprovalNeeded` (`DeploymentPipelineWorkflow.cs:242-246`) already fires unconditionally for business mode; replacing that predicate with a threshold check would be **strictly weaker** for business-mode tenants — a governance epic that removes an existing gate. The new term is additive only. *On the effect*: `StageDeployDispatch` (`:588`) is **shared across staging/uat/prod**, so a single `agent-action:deploy` member cannot distinguish stage; gating `effect:deploy.promote-prod` at the prod-approval decision does. Pinned by `EnforceMode_NeverWeakensBusinessModeGate` and `Gate_is_on_the_effect_not_the_shared_dispatch`.

- **D11 — No new suspend activity, no new bookmark prefix.** `LifecycleBookmarks.CanonicalSuspendActivities` is `Dictionary<Type, string>` — a prefix without an activity is **not representable**. So the ledger is written by the surfaces that already exist: the 11 landed resume endpoints (`Program.cs:2919-2957`) and the new decide endpoint. This is what kills the superseded `ToolAuthorizationDecision` / `WaitForToolAuthorizationActivity` / `ForToolAuthorization` design wholesale.

- **D12 — One event family; `.DENIED` and `.REQUIRES_HUMAN` under enforcement are NOT swallowed.** The `<Feature>EventsService` template wraps emission in a swallowing try/catch, which is right for telemetry and **wrong for a block**: a denial with no audit row is a compliance hole and is indistinguishable, after the fact, from the action having been allowed. Those two emissions propagate. Volume control instead comes from `.ALLOWED` firing only when `Source != system-default` or `Enforced`. Appended **directly via `IEventRepository`** from `Tamma.Api` — `TammaEventEmitter` structurally requires an `ActivityExecutionContext` and the tool loop runs inside a blocking HTTP request (42-3's finding, preserved and credited).

- **D13 — The live-read fix is scoped to the gate path only.** `DocumentLifecycleWorkflow.cs:184` caches `ResolvedAcceptanceRules` at Init. The gate re-reads at each decision point; the existing `state.Rules` reads at `:433,589,678,1208-1209` are **untouched**, so workflow instances already in flight remain valid. Widening the resolver (`ResolveBaseAsync` / `ResolveBaseForTenantAsync`, lifted from `AcceptanceRulesService.cs:91-108`) is done **once** — here or in 43-5, agreed in one place, never both.

- **D14 — `TryPreGate`'s first production call site takes only the always-escalate contribution.** It also implements an unrelated rounds-exhausted short-circuit; the gate **ignores** that outcome. The document lifecycle keeps owning rounds. The always-escalate contribution enters as `AlwaysHuman` composed by `max()`, so a legacy entry is a floor a catalog row cannot lower — only deleting it in the acceptance-rules UI can.

  > **AMENDED 2026-08-01 — D13 and D14 both SHIPPED in Story 43-5.** D14: call site
  > `AutonomyGateEvaluator.cs:595`, escalation-only rule documented `:549-560`, provenance
  > `ActionAssignmentSource.AlwaysEscalateLegacy` (`AutonomyGovernance.cs:167`). D13: the
  > widening is `IAcceptanceRulesResolver.cs:40,48` → `AcceptanceRulesService.cs:114,123`, and
  > "done once, never twice" is satisfied — **do not widen it again**. D13's line numbers are
  > stale (`DocumentLifecycleWorkflow.cs:184` → `:195`; `state.Rules` reads
  > `:433,589,678,1208-1209` → `:445,601,690,1223-1224`; `AcceptanceRulesService.cs:91-108` →
  > `:114-130`). **D13's substantive claim is also half-wrong:** "the gate re-reads at each
  > decision point" is not the shipped design — the policy source is a **singleton 60-second-TTL
  > whole-snapshot cache** (`GovernancePolicySnapshotStore.cs:59-63`, registered
  > `TryAddSingleton` at `ActionCatalogGovernanceServiceCollectionExtensions.cs:52`), whose own
  > doc-comment (`:28-32`) states that a policy change may take up to 60 s to be observed. "Live"
  > means "not captured at workflow Init", not "immediate". See the story's AC15 amendment for
  > the replacement tests.

### Decisions added 2026-08-01

- **D15 — `.Governs(action)` stays METADATA-ONLY; enforcement is an explicit PER-ROUTE OPT-IN.
  Supersedes step 7's "`Governs()` now also does `.AddEndpointFilter<…>()`".**

  **The conflict.** `POST /api/v1/llm/call` already carries `.Governs(effect:llm.call)`
  (`Program.cs:3321`) and `effect:llm.call` is `Enforceable = true` (the `Effect(...)` helper
  defaults `enforceable: true`, `ActionCatalog.Descriptors.cs:55-60`; the sole `false` is
  `effect:secret.reveal` at `:395`). Attaching the filter inside `Governs()` therefore makes Seam
  A blockable the instant the filter lands — contradicting D2 and AC3 — and double-gates deploy,
  since the pipeline reaches the model through that same route
  (`DeploymentPipelineWorkflow.cs:588`) while Seam E gates the prod-approval decision.

  **The decision.** `Governs()` attaches metadata and nothing else. A **separate, visible call at
  each route that should be gated** turns enforcement on. Spelling is the implementer's choice;
  the binding semantic is that *binding* and *enforcing* are two different lines in the diff.

  **Reasoning, recorded.**
  1. *Blast radius must not be a helper's side effect.* 21 routes are bound today —
     17 minimal-API `.Governs(...)` (`Program.cs:3126,3132,3144,3147,3156,3321,3344,3348,3352,3359,3375,3383,3390,3404,3417,3441,3451`)
     plus 4 `[Governs]` controller actions (`MentorshipController.cs:62,151,175,199`). One line
     inside `Governs()` converts 17 of them into live 409 gates at once, with no per-route review.
  2. *Structural beats keyed.* Seam A's route never opts in, so "never blocks" is a fact about
     the wiring rather than a carve-out keyed on an action name that a future edit deletes.
  3. *The rejected alternative:* special-case `effect:llm.call` inside the filter. It lost
     because it hides the safety property in the one place a "remove the special cases" commit
     deletes it, it still flips the other 20 routes wholesale, and it silently fails to protect a
     second never-block route added later.
  4. *A fourth fact the one-line design never accounted for:* the two authoring shapes do not
     share a mechanism. `GovernsExtensions.Governs` is a `RouteHandlerBuilder` extension
     (`GovernsExtensions.cs:28`); the 4 controller actions are governed by an **attribute**
     (`ActionGateMetadata.cs:47-52`) that never passes through it. A filter inside `Governs()`
     would have enforced 17 and skipped 4 while reading as "all bindings enforced". **The opt-in
     must cover both planes and say which mechanism it uses for each.**

  **D15 makes "which routes are enforced" a thing that must be WRITTEN DOWN, and this plan does
  not write it down.** Step 7 says `.Governs(...)` on "the 17 mutating `EngineServiceOnly`
  routes" — all 17 are already bound, so the real deliverable is an explicit opt-in list. The
  plan must enumerate it, and a test must assert the enforcement-opted-in set equals that list
  exactly, so both an accidental addition and an accidental omission go red. See also the story's
  amendment §C-bis: `KnownUngovernedEndpoints` names 43-9 as binding owner for five *further*
  routes (`POST /api/kb/mcp/tools/invoke` at `KnownUngovernedEndpoints.cs:393`, plus four
  `/api/admin/scheduled-triggers/*` entries) that this plan never mentions and that must be
  decided explicitly.

  **This overturns 43-8's stated design, and that is recorded rather than glossed.** 43-8 says
  four times that 43-9 attaches the filter inside `Governs()` "so annotating and enforcing stay
  one call" — `GovernsExtensions.cs:11-13`, `ActionGateMetadata.cs:8`,
  `docs/stories/epic-43/story-43-8/43-8-drift-harnesses.md:89` and `:296`. Update those notes when
  the opt-in lands. 43-8's factual claim (a binding is metadata today) is unaffected.

- **D16 — AC2's "byte-identical to today" carries exactly one deliberate exception, and AC2 is
  scoped to routes that opt in.**

  `effect:mcp.tool.invoke` ships `min: AutonomyDial.AlwaysHuman`
  (`ActionCatalog.Descriptors.cs:386-388`) with `Enforceable = true`, reversed from `Min` on
  **2026-07-30**. Reasoning on the descriptor `:342-372` and in the epic README D2
  (`docs/stories/epic-43/README.md:548`): epic D2 tolerates an unclassified action at runtime only
  because the drift harnesses make it unmergeable in CI, and **no CI harness can enumerate a
  remote MCP server's tools**, so for MCP that half of the bargain does not exist. The runtime
  tolerance has nothing backing it.

  Under D15 the MCP route does not opt into enforcement in this story — it carries no binding at
  all (`Program.cs:3491` is `.RequireAuthorization("SettingsManage")` only) and sits in
  `KnownUngovernedEndpoints.cs:393`. So the precise claim AC2 must make is: **day-one control flow
  is byte-identical to today at every seam site that opts into enforcement in this story;
  `effect:mcp.tool.invoke` is the single catalogued exception to "shipped defaults change nothing"
  and has no enforcing seam here.** A future opt-in of that route is a behaviour change and must
  be argued as one. (Blast radius is independently empty today: no MCP tool executor is
  registered, so an `mcp__*` name already terminates as an unknown tool — `:363-367`.)

  **D16 overrides a written commitment and must amend it in the same change.**
  `KnownUngovernedEndpoints.cs:393-394` currently justifies the MCP entry as *"binding-owned-by
  Story 43-9: … 43-9 attaches the `.Governs` binding plus the enforcement filter"*. Rewrite that
  justification to record the opposite decision and why. The entry stays (the route stays
  unbound), so no count pin moves.

- **D17 — the two ratchets this story must widen get NAMED, DATED, REVIEWED exceptions, not
  edited assertions and not a second client.**

  **The problem, twice.**
  (a) `TammaApiClient.EvaluateGovernanceAsync` (step 10) is a read, so it belongs in
  `MediationClientEffectSweepTests.KnownNonEffectClientMethods` (`:231`) — taking it 19 → 20. The
  pin is the last element of `NonEffectPinHistory = [19]` (`:543`) under
  `TheRatchetPin_IsMechanicallyShrinkOnly` (`:546-562`), re-asserted from the registry by
  `RatchetDisciplineTests.EveryRatchet_hasACountPin_thatIsMechanicallyShrinkOnly` (`:200-235`).
  Appending `20` is red by design; editing `19` in place is the undeclared re-widening the ratchet
  exists to catch.
  (b) `POST /api/v1/governance/evaluate` (step 9) is a new baseline entry in
  `KnownUngovernedEndpoints`, whose `PinnedCount = 216` is the last element of
  `PinHistory = [237, 216]` (`Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:128,142`) under
  the identical rule. 43-8 §A3 step 3 already derived this and already said the resolution must be
  "a reviewed decision recorded at the history … not a quiet edit of the assertion".

  **Why an exception and not the alternatives.** A pin that forbids *any* new non-effect method
  eventually forces one of two dishonest moves: classify a genuinely read-only method as an
  effect, or split `TammaApiClient` so the new method lands where the sweep cannot see it —
  `MediationClientEffectSweepTests.cs:66-73` already names "a method inherited from a base class
  the client might grow" as exactly that hole. Both are worse than an exception a reviewer sees in
  the diff.

  **Required shape, tight enough that it cannot become a blanket escape hatch:**
  1. A **separate, per-item collection**, not a count bump — keyed by the exact method name /
     exact `(method, pattern)` route, so a *different* addition still goes red. The count-level
     precedent (`TemplateExampleConformanceTests.cs:208-224,614-632`, "name the index that may
     rise", cited by 43-8) is **rejected here because it is anonymous**: any future item could
     occupy the widened slot.
  2. Each entry carries **item key + ISO date + reviewing story id + a justification that passes
     the owning fixture's existing classifier** (`MediationClientEffectSweepTests.RatchetClassifies`,
     `:626-628`, keywords `read-only` / `internal-session-lifecycle-no-external-effect`; the
     endpoint fixture's own classifier for the route case, justification
     `gate-evaluation-endpoint-cannot-gate-itself`).
  3. Each exception set is **itself count-pinned at 1** and **itself shrink-only**, and is
     **registered in the ratchet-discipline registry** so all three AC8 properties are asserted
     against it. `Tamma.Activities.Tests/…/RatchetDisciplineTests.Ratchets()` (`:59-93`) goes 3 → 4
     and `Tamma.Api.Tests/…/RatchetDisciplineTests` 1 → 2; both registry pins are review-gated
     bumps whose own failure messages invite the change (`:123-128`), unlike the ratchets
     themselves.
  4. The underlying baselines **do not move**: `KnownNonEffectClientMethods` stays 19 with history
     `[19]`; `KnownUngovernedEndpoints.PinnedCount` stays 216 with history `[237, 216]`. The
     exception sets are unioned into each fixture's "is this item accounted for" check and
     **excluded from the count pins**, so unreviewed growth is still impossible.
  5. **Staleness applies both ways:** an exception entry whose method/route no longer exists, or
     which becomes mapped to an `ExternalEffect` / becomes bound, fails until deleted.

  **Two pins that legitimately DO move, and must be bumped in the same commit:**
  `MediationClientEffectSweepTests.The_sweep_actually_sees_the_client_surface` 36 → 37 (`:442`;
  its own message says "move this number in the same commit") and
  `KnownUngovernedEndpoints.PinnedInScopeCount` 237 → 238 (`:157`; a plain literal with no
  direction rule).

## Corrections to the design

1. **`InlineToolLoopRunner` line numbers.** Verified 2026-07-25: validator block `:260`–`:281`; assistant message `:284`; rejected-call machinery `:299`–`:325`; `executableToolCalls` at **`:330`** (design said `:329-332`); `EnableParallelTools` fork at **`:335`** (design said `:334`). The gate goes between `:281` and `:284` — i.e. immediately after the validator block closes, before the assistant message is appended. Placing it after `:284` would gate a call already recorded in conversation history.

2. **The design's ctor-optionality claim understates it.** *All ten* `InlineToolLoopRunner` ctor parameters are optional or nullable (`:45-55`), including `logger`, `httpClientFactory`, `configuration` and `sanitizer`. Adding `IAutonomyGate` as the first **required** parameter is therefore a genuine break in an established (bad) pattern and will touch every construction site — expect churn in `Tamma.Api.Tests`. That churn is the point: a test that constructs the runner without a gate must not compile.

3. **`202` is reachable on more than the Slack path.** `PostVoidAsync` (`TammaApiClient` `:888-890`) treats any 2xx as success and is used by `QueueSlackNotificationAsync`. So the "202 is already success" argument is not a one-route coincidence — it is the client's general contract. Strengthens D7.

4. **`EngineServiceOnly` covers 26 route registrations, not 11.** `grep -c EngineServiceOnly Program.cs` → 37 occurrences, of which the `.RequireAuthorization("EngineServiceOnly")` route registrations run from `:2838` to `:3146`; **17 are mutating**. The brief's earlier "11 mediation routes" is incomplete; it omits `POST /api/engine/events` (`:2838`), `POST /api/engine/platform-events` (`:2843`), `POST /api/engine/documents` (`:2854`), `POST /api/engine/documents/{documentId:guid}/status` (`:2856`), `POST /api/engine/channel/outbox` (`:2864`), and lists `POST /api/v1/llm/call` (`:3026`) separately. Seam C attaches to the **17 mutating** ones.

5. **The design placed the ledger, decide endpoint and pending surface in a separate "enforcement flip" story.** That story does not exist under D1 (enforcement is live in v1), so they ship here. A live enforcing gate that can return `RequiresHuman` with no way to grant would be a hang, not a gate.

> **AMENDED 2026-08-01 — corrections 1, 2, 4 and 5 are themselves now out of date.**
>
> - **Correction 1** (the `InlineToolLoopRunner` line numbers) has been overtaken twice: the
>   numbers moved again *and* the seam it describes is already built. Current: validator block
>   `:308-330`, rewrite `:319`, **Seam B `:332-390`**, assistant message `:393`, rejected-call
>   machinery `:411-433`, `executableToolCalls` `:439`, fork `:444`. The gate does sit "between
>   the validator block closing and the assistant message", exactly as Correction 1 required.
> - **Correction 2** ("*all ten* ctor parameters are optional or nullable … adding `IAutonomyGate`
>   as the first **required** parameter … will touch every construction site") **describes work
>   that is done.** The ctor has 13 parameters (`:63-107`), `IToolLoopAutonomyGate autonomyGate`
>   at `:73` is required with a null-throw at `:94`, and the construction-site churn already
>   happened. Note the *type* is `IToolLoopAutonomyGate`, not `IAutonomyGate`.
> - **Correction 4** ("`EngineServiceOnly` covers 26 route registrations, not 11"): the raw count
>   `grep -c EngineServiceOnly Program.cs` → **37** is still right, but the range moved — the
>   `.RequireAuthorization("EngineServiceOnly")` registrations now run `:3125`–`:3449`, not
>   `:2838`–`:3146`. The "17 mutating" conclusion still matches the 17 `.Governs(...)` bindings
>   in the tree. **But under D15 the relevant number is no longer "17 to bind" — they are already
>   bound. The open question is which of them OPT IN to enforcement, which is a per-route
>   decision this plan must now enumerate explicitly.**
> - **Correction 5** is still correct and is now also settled by the tree: the ledger shipped in
>   43-5 (`Tamma.Data/Repositories/`), and its decide/pending *routes* remain this story's.
>
> **A correction the plan never made and needs:** `KnownReadOnlyClientMethods` (step 10) does not
> exist under that name. The baseline is
> `Tamma.Activities.Tests/Actions/MediationClientEffectSweepTests.KnownNonEffectClientMethods`
> (`:231`), and it cannot be "bumped by one" — see D17.

## Implementation Steps

> **AMENDED 2026-08-01 — steps 1, 2, 3, 4, 6 and 12 are DONE. Do not execute them.** The status
> table at the top of this file gives the evidence for each. The steps are left in place, with
> their original wording, so a reader can see what was planned and what actually happened. Read
> the table first; then execute only steps 5, 7, 8 (the helper half), 9, 10, 11, 13 and 14, in the
> revised order given in the table.

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Actions/IAutonomyGate.cs`** — `IAutonomyGate`, `AutonomyQuery(ActionKey, GovernancePrincipal, AgentRole?, Operation, Target, CorrelationId)`, `AutonomyDecision(Outcome, Action, Group, Risk, AutonomyLevel, EffectiveMinAutonomy, Source, Enforced, CoveredBy, AuthorizationId, Reason)`, `enum AutonomyOutcome { Automated, RequiresHuman, Denied }`.

2. **CREATE `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs`** (AC1, D14) — pure static `Evaluate(query, snapshot, baseRules)`: the `max()` ladder (platform ceiling, legacy always-escalate floor, principal ladder with action-beats-group `??`), the `TryPreGate` always-escalate bridge taking only the escalation contribution, `EscalatableToHuman` collapsing `RequiresHuman`→`Denied` for `automation:*`. **Zero I/O.** All ladder tests hang off this.

3. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/AutonomyGateService.cs`** (AC1) — `IAutonomyGate` impl: principal via `IGovernancePrincipalResolver`, snapshot via `IGovernancePolicySnapshotProvider` (scoped), base rules via the widened `IAcceptanceRulesResolver`, ledger consult via `IActionAuthorizationLedger.TryConsumeAsync`, audit via `ActionGateEventsService`. **MODIFY** `IAcceptanceRulesResolver` + `AcceptanceRulesService` per D13 if 43-5 has not already.

4. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionGateEventsService.cs`** (AC14, D12) — `AcceptanceRulesEventsService.cs:16-18,54-93` template, direct `IEventRepository` append, the eight event types and the union tag set, swallowing try/catch **except** `.DENIED`/`.REQUIRES_HUMAN` under enforcement, `.ALLOWED` volume rule.

5. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Endpoints/LlmCallEndpoints.cs`** (AC3, Seam A, D2) — evaluate `ActionKey(AgentAction, request.Action)` when non-null, emit, **always proceed**. One `// OBSERVE-ONLY IN EVERY VERSION —` comment citing the 44-of-45 reason.

6. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs`** (AC4/5/6, Seam B, D3/D4/D5, Correction 1) — add `IAutonomyGate gate` as a **required** first ctor parameter; insert the gate loop **between `:281` and `:284`**: for each `tc` not already rejected, resolve the tool name through `ToolNameAliases`, split `git_operations` into `.read`/`.write` from the parsed subcommand, evaluate, and on non-`Automated` write `rejectedToolCalls[tc.Id]`. Fix every construction site.

7. **CREATE `apps/tamma-elsa/src/Tamma.Api/Infrastructure/AutonomyGateEndpointFilter.cs`; MODIFY `GovernsExtensions.cs`** (AC7/8, Seam C, D6/D7) — the filter reads `ActionGateMetadata` off `context.HttpContext.GetEndpoint()`, evaluates, and on non-`Automated` short-circuits with `Results.Conflict(new { code = "ACTION.GATE.REQUIRES_HUMAN", … })`. `Governs()` now also does `.AddEndpointFilter<AutonomyGateEndpointFilter>()`. **MODIFY `Program.cs`** — `.Governs(...)` on the **17 mutating `EngineServiceOnly` routes** (Correction 4).

8. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/BackgroundActionGate.cs`** (AC9, Seam D, D8) — `Task<bool> MayRunAsync(BackgroundActor actor, Guid? tenantId, ct)`; principal from `IGovernancePrincipalResolver`; try/catch → `ACTION.GATE.EVALUATION_FAILED` → return `true` (fail-open on *error*, deny only on *decision*). **MODIFY** each of the 25 hosted services to call it once per tick. **MODIFY** the Story 43-6 admin endpoints to reject a non-sentinel threshold on `automation:*` with `ACTION_POLICY.INVALID`.

9. **CREATE `apps/tamma-elsa/src/Tamma.Api/Endpoints/GovernanceEvaluateEndpoint.cs`; MODIFY `Program.cs`** (AC10, D9) — `POST /api/v1/governance/evaluate`, `EngineServiceOnly`, returns the `AutonomyDecision` projection. **MODIFY `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs`** — add the entry with `gate-evaluation-endpoint-cannot-gate-itself` and bump the count pin.

10. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Policy/CheckActionGateActivity.cs`** (AC10) — `[Activity]`, inputs `(ActionKeyWire, Role?, Operation?, Target?, CorrelationId)`, outputs `GateOutcome`, `[FlowNode]` outcomes `Automated` / `RequiresHuman`; calls a new `TammaApiClient.EvaluateGovernanceAsync` (read-only ⇒ goes in `KnownReadOnlyClientMethods`, bumping 43-8's pin by one). Fail-open on transport failure with an emitted `EVALUATION_FAILED` — the engine must not stall on a CP blip.

11. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs`** (AC11, D10) — insert a `CheckActionGateActivity` node on `effect:deploy.promote-prod` before `:242`, bind its outcome to a `Variable<string> gateOutcome`, and add the third **OR** term to `prodApprovalNeeded`. `waitProdApproval` (`:248`) is unchanged. `StageDeployDispatch` (`:588`) is **not** touched.

12. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionAuthorizationLedger.cs`** (AC12, D11) — `IActionAuthorizationLedger` impl over 43-5's `action_authorizations`: `TryConsumeAsync(principal, correlationId, actionKey)` matching an action-scoped row **or** a group-scoped row whose group contains the action; state machine `{pending → granted|denied|expired}`; TTL from `Tamma:Governance:AuthorizationTtlHours` (default 24); sets `consumed_at_utc`; returns the `AuthorizationId` so the second seam's `.ALLOWED` records `CoveredBy`.

13. **CREATE `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionAuthorizationEndpoints.cs`; MODIFY `Program.cs`** (AC13) — `POST /api/actions/authorizations/{id}/decide` (`ActionsManage`) and `GET /api/actions/authorizations?state=pending` (`AuthenticatedAny` reads, matching the acceptance-rules posture). Register **after** the literal `/api/actions/policy/...` routes so literals beat parameterized segments.

14. **CREATE the test suites** (see Test Plan), then run `dotnet test` and `dotnet ef migrations has-pending-model-changes` (must stay clean — the entities are 43-5's).

## Test Plan

> **AMENDED 2026-08-01 — five of the eleven fixtures below already exist, several under different
> names. Do not create duplicates.** Verified:
>
> | Planned fixture | Status | What is actually in the tree |
> |---|---|---|
> | `AutonomyGateEvaluatorTests` | **EXISTS** | `Tamma.Core.Tests/Actions/AutonomyGateEvaluatorTests.cs` + `AutonomyGateEvaluatorBreakGlassTests.cs` |
> | `AutonomyGateDefaultsTests` | **PARTIAL** | catalog-wide: `Tamma.Core.Tests/Actions/ActionCatalogDefaultsTests.ShippedDefaults_ReproduceTodaysGatingBehaviour`. The anti-no-op **pair** exists for Seam B only: `ToolLoopAutonomyGateSeamTests.cs:96` + `:116`. **Remaining:** the pair for Seams A, C, D, E. |
> | `AutonomyGateSeamTests` (Seam A) | **MISSING** | — |
> | `InlineToolLoopRunnerGateTests` | **EXISTS**, renamed | `Tamma.Api.Tests/Agents/ToolLoopAutonomyGateSeamTests.cs` — `The_gate_is_a_required_constructor_dependency` `:235`, `A_denied_tool_call_is_excluded_from_the_parallel_execution_path_too` `:142`, `The_gate_runs_with_no_validator_wired` `:73`, `A_denied_tool_call_is_not_executed_and_feeds_back_as_a_tool_result` `:36`. Gate-decision unit tests: `Tamma.Activities.Tests/Actions/ToolLoopAutonomyGateTests.cs`. **Remaining:** `Gate_runs_after_sanitization` (nothing asserts the gate sees the *rewritten* `ArgumentsJson`) and a test named for the null-allowlist property. |
> | `AutonomyGateEndpointFilterTests` | **MISSING** | — |
> | `BackgroundActionGateTests` | **PARTIAL** | `MidRangeThreshold_OnAutomation_Is400` exists as `ActionPolicyEndpointsTests.AutomationTarget_RejectsMidRangeThreshold` (`:295`). The rest is missing. `EveryHostedService_CallsTheHelperOncePerTick` must drive **29** actors (`Enum.GetValues<BackgroundActor>()`), not 25. |
> | `DeploymentPipelineGateTests` | **MISSING** | — |
> | `ActionAuthorizationLedgerTests` | **EXISTS**, renamed and richer | `Tamma.Api.Tests/Actions/ActionAssignmentStorageTests.cs:263-521` covers `GroupGrant_CoversEveryMemberWithinOneCorrelation`, `ActionGrant_CoversOnlyItself`, `Grant_does_not_leak_across_correlations` (as `GroupGrant_CannotBeConsumedForAnActionOutsideTheGroup`, `:345`), `ExpiredGrant_IsNotConsumable` (`:474`), `ConsumedGrant_IsNotReusable` (`:296`), `UniqueIndex_PreventsTwoPendingGrantsForOneScope` (`:263`, `:444`), plus two concurrency races the plan did not ask for (`:370`, `:405`). **Only `SecondSeam_RecordsCoveredBy` remains — and it cannot be written until `AutonomyDecision` gains `CoveredBy`/`AuthorizationId`.** |
> | `ActionAuthorizationEndpointsTests` | **MISSING** | `NoNewBookmarkPrefix_IsRegistered` needs a concrete reference value: `LifecycleBookmarks.CanonicalSuspendActivities` (`Tamma.Activities/Documents/LifecycleBookmarks.cs:98-105`) holds exactly 2 entries — pin the count and both key/value pairs, or the test cannot fail. |
> | `ActionGateEventsServiceTests` | **EXISTS** | `Tamma.Activities.Tests/Actions/ActionGateEventsServiceTests.cs` — `:63,76,106,123,134,150,161`. Note `RequiresHumanEmissionFailure_Propagates_UnderEnforcement` ships as `AppendFailure_OnAnEnforcedDenial_Rethrows` (`:134`) with its complement at `:150`. |
> | `GateLiveReadTests` | **MISSING, and one member is unwritable as named** | `ResolverWidening_ExistsExactlyOnce` is satisfied by 43-5. `Gate_rereads_rules_at_each_decision_point` is **false against the shipped design** — the snapshot provider is a singleton 60 s cache (`GovernancePolicySnapshotStore.cs:59-63`). Replace with: the gate does not read from serialized workflow state, plus a pin on `RefreshTtl == 60s` so the staleness window is declared. |
> | `GateNamingTests.NoTypeNamedActionGate_IsAddedToTammaApi` | **UNWRITABLE as named** | Two `ActionGate*` types already live in `Tamma.Api`: `ActionGateEventsService.cs:33` (deliberate, justified at `:25-31`) and `ActionGateMetadata.cs:22,32,47` (43-8). Replace with an exact allowlist + count pin — see the story's AC1 amendment. |
>
> **Two new fixtures D17 requires:** one per exception set, asserting the three ratchet properties
> and the count-of-1 pin, plus the registry-pin bumps (3 → 4 and 1 → 2).

NUnit + FluentAssertions + Moq; `WebApplicationFactory` for Seam C; the existing Testcontainers fixture for the ledger; `WorkflowTestHelper.BuildWorkflow` + a real `IWorkflowRunner` harness for Seam E.

- **`AutonomyGateEvaluatorTests`** (pure, Core) — the `max()` ladder: platform ceiling beats a lower tenant row; legacy always-escalate floor cannot be lowered by an action row (`LegacyAlwaysEscalate_CannotBeLoweredByAnActionRow`); action override beats group override outright; `RoundsExhausted_DoesNotAffectActionThreshold`; `automation:*` collapses `RequiresHuman`→`Denied`; provenance is reported on every row. **Covers AC1, AC16, AC9 (evaluator half).**
- **`AutonomyGateDefaultsTests`** — `ShippedDefaults_DoNotAlterControlFlow` per seam **and** `TighteningOneAction_DoesAlterControlFlowAtItsSeam` (the anti-no-op pair — without the second, the first is satisfiable by a gate that never fires). **Covers AC2.**
- **`AutonomyGateSeamTests`** — `LlmCallSeam_NeverBlocks_EvenUnderEnforce` (action at `AlwaysHuman` at platform, tenant and user scope; asserts 200 and that the dispatch proceeded). **Covers AC3.**
- **`InlineToolLoopRunnerGateTests`** — `Constructor_RequiresTheGate` (reflection over the ctor: no parameterless-defaulted `IAutonomyGate`); `SequentialAndParallelBranchesBothGoverned` (run with `EnableParallelTools` false **and** true); `Gate_runs_after_sanitization` (validator rewrites `ArgumentsJson`; the gate's observed `Target` is the rewritten value); `Gate_evaluates_when_validator_is_null`; `Gate_denies_even_when_registry_allowlist_is_null`; `Denied_tool_call_becomes_a_tool_result_message_not_an_exception` (asserts a `role: tool` message reaches the model and no exception escapes); `Gate_runs_before_the_assistant_message_is_appended`. **Covers AC4, AC5, AC6.**
- **`AutonomyGateEndpointFilterTests`** (`WebApplicationFactory`) — `Denial_returns_409`; `Denial_body_carries_the_documented_code_and_fields`; `Gate_still_evaluates_when_all_policies_are_AllowAnonymous` (Development-without-JWT config); `PlatformAdmin_cannot_bypass_a_governed_effect`; `WildcardApiKey_cannot_bypass_a_governed_effect`; `Client_treats_202_as_success` (characterization test against `TammaApiClient` — encodes *why* 409). **Covers AC7, AC8.**
- **`BackgroundActionGateTests`** — `Denied_tick_is_skipped_and_audited`; `Evaluation_failure_does_not_propagate_out_of_the_helper` (throwing gate → helper returns true, `EVALUATION_FAILED` emitted, no exception); `MidRangeThreshold_OnAutomation_Is400`; `EveryHostedService_CallsTheHelperOncePerTick` (reflection-assisted, paired with 43-8's actor coverage). **Covers AC9.**
- **`DeploymentPipelineGateTests`** (graph + execution) — `EnforceMode_NeverWeakensBusinessModeGate` (business mode + gate `Automated` ⇒ still waits); `GateRequiresHuman_AddsAWaitWhereThereWasNone` (dev mode, `requireProdApproval` false, gate `RequiresHuman` ⇒ waits); `ObserveMode_MatchesPreviousRoutingExactly`; `Gate_is_on_the_effect_not_the_shared_dispatch` (graph walk: no gate node adjacent to `StageDeployDispatch`); `GovernanceEvaluateRoute_IsJustifiedUngoverned`; `EngineGateCall_FailsOpenOnTransportError`. **Covers AC10, AC11.**
- **`ActionAuthorizationLedgerTests`** (Testcontainers) — `GroupGrant_CoversEveryMemberWithinOneCorrelation`; `ActionGrant_CoversOnlyItself`; `Grant_does_not_leak_across_correlations`; `ExpiredGrant_IsNotConsumable`; `ConsumedGrant_IsNotReusable`; `SecondSeam_RecordsCoveredBy`; `UniqueIndex_PreventsTwoPendingGrantsForOneScope`. **Covers AC12.**
- **`ActionAuthorizationEndpointsTests`** — `Member_Gets403OnDecide`; `Decide_is_idempotent_on_an_already-decided_row`; `PendingList_IsScopedToThePrincipal`; `RouteOrder_LiteralsBeatParameterized`; `NoNewBookmarkPrefix_IsRegistered` (asserts `LifecycleBookmarks.CanonicalSuspendActivities` is unchanged in count and keys). **Covers AC13.**
- **`ActionGateEventsServiceTests`** — `DeniedEmissionFailure_Propagates`; `RequiresHumanEmissionFailure_Propagates_UnderEnforcement`; `AllowedEmissionFailure_IsSwallowed`; `Allowed_notEmitted_forSystemDefaultUnenforced`; `Tags_ContainTheDocumentedUnionSet`. **Covers AC14.**
- **`GateLiveReadTests`** — `Gate_rereads_rules_at_each_decision_point`; `ExistingStateRulesReads_AreUntouched` (source-shape assertion over `DocumentLifecycleWorkflow.cs:433,589,678,1208-1209`); `ResolverWidening_ExistsExactlyOnce`. **Covers AC15.**
- **Naming pin** `GateNamingTests.NoTypeNamedActionGate_IsAddedToTammaApi` — the `Tamma.Activities.Security.ActionGate` collision. **Covers AC1.**

## Definition of Done

> **AMENDED 2026-08-01 — rows 1, 4, 5, 14, 15 and 16 are already satisfied, and rows 2, 6, 9 and
> 12 are partly satisfied, by code that shipped in Stories 43-4 / 43-5 / 43-6. Rows 1, 10 and 15
> also name tests that cannot be written as stated.** Use the story document's AMENDMENT §A table
> as the authoritative status list; the table below is retained as the original plan of record.
> Specifically: row 1's `GateNamingTests` and row 15's `Gate_rereads_rules_at_each_decision_point`
> must be replaced (see the story's AC1 and AC15 amendments), and row 10's "count pin bumped"
> must go through D17's exception mechanism.

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — Core/Api split, `AutonomyGate*` naming | 1, 2, 3 | `AutonomyGateEvaluatorTests`, `GateNamingTests` |
| 2 — live enforcement, behaviour-preserving defaults | 3, 6, 7, 8, 11 | `AutonomyGateDefaultsTests` (both halves) |
| 3 — Seam A observe-only forever | 5 | `LlmCallSeam_NeverBlocks_EvenUnderEnforce` |
| 4 — Seam B siting + required dependency | 6 | `InlineToolLoopRunnerGateTests` (four tests) |
| 5 — `Denied` via existing machinery | 6 | `Denied_tool_call_becomes_a_tool_result_message_not_an_exception` |
| 6 — additive over fail-open allowlists | 6 | `Gate_denies_even_when_registry_allowlist_is_null` |
| 7 — filter, no bypass inheritance | 7 | `AutonomyGateEndpointFilterTests` (three tests) |
| 8 — 409 not 202 | 7 | `Denial_returns_409`, `Client_treats_202_as_success` |
| 9 — deny-only, host-safe | 8 | `BackgroundActionGateTests` |
| 10 — Seam E over HTTP, route justified | 9, 10 | `GovernanceEvaluateRoute_IsJustifiedUngoverned`, `EngineGateCall_FailsOpenOnTransportError` |
| 11 — OR, on the effect | 11 | `DeploymentPipelineGateTests` (four tests) |
| 12 — ledger semantics | 12 | `ActionAuthorizationLedgerTests` |
| 13 — decide + pending surfaces | 13 | `ActionAuthorizationEndpointsTests` |
| 14 — one family, denials not swallowed | 4 | `ActionGateEventsServiceTests` |
| 15 — live read, scoped fix | 3 | `GateLiveReadTests` |
| 16 — `TryPreGate` first call site, floor only | 2 | `LegacyAlwaysEscalate_CannotBeLoweredByAnActionRow`, `RoundsExhausted_DoesNotAffectActionThreshold` |

## Risks & Mitigations

- **Enforcement live means a wrong shipped default is a production outage, not a warning.** This is the direct cost of D1. Mitigation: 43-3's `ShippedDefaults_ReproduceTodaysGatingBehaviour` is an explicit table, not a loop; `AutonomyGateDefaultsTests` runs the anti-no-op pair; the gate fails **open** on evaluation *error* at every seam (deny only on *decision*), so a CP blip degrades to today's behaviour rather than stopping the platform.
- **Making `IAutonomyGate` a required ctor parameter breaks every `InlineToolLoopRunner` construction site.** Deliberate (D3). Mitigation: land it as one mechanical commit; the compile break is the enforcement.

  > **AMENDED 2026-08-01 — this risk is RETIRED. It already happened.** The required parameter is
  > `IToolLoopAutonomyGate autonomyGate` at `InlineToolLoopRunner.cs:73`, with the null-throw at
  > `:94`; the construction-site churn landed with Story 43-4/43-5.
- **CP read on the hottest path.** Every tool call consults the gate. Mitigation: `IGovernancePolicySnapshotProvider` is **scoped** — one CP read pair per request, so a loop gating 40 calls issues one read (pinned by `TwoGateCallsInOneRequest_IssueOneRepositoryRead`, 43-5). Load-test the tool loop before release; a cold cache during a burst is the residual risk.

  > **AMENDED 2026-08-01 — this mitigation is factually wrong about the shipped code, and the
  > test it cites does not exist.** `IGovernancePolicySnapshotProvider` is registered
  > **`TryAddSingleton`** (`ActionCatalogGovernanceServiceCollectionExtensions.cs:52`), not scoped,
  > and `TwoGateCallsInOneRequest_IssueOneRepositoryRead` appears nowhere in the tree. What
  > actually ships is stronger for throughput and weaker for freshness: a singleton
  > whole-snapshot cache with a **60-second lazy-refresh TTL** where readers never block
  > (`GovernancePolicySnapshotStore.cs:17-32,59-63`), primed at cold start by
  > `GovernancePolicySnapshotPrimingService` (`…Extensions.cs:58-59`).
  > **The residual risk is therefore a different one and must be re-stated:** not "a cold cache
  > during a burst", but **up to 60 s between an admin tightening a threshold and any seam
  > enforcing it** — stated honestly in the store's own doc-comment at `:28-32`. That is a
  > product-visible property of an enforcing gate and belongs in the story's Architectural
  > Context and in the 43-7 admin UI copy, not only here. Mitigation to carry forward: pin
  > `RefreshTtl` so the window is a declared number, and make the admin surface say it.
- **Seam D's fail-open-on-error could mask a permanently broken gate.** A CP outage means 25 sweepers run ungated and only emit `EVALUATION_FAILED`. Mitigation: alert on `EVALUATION_FAILED` volume; the alternative (fail-closed) stops every background actor on a transient blip, which is worse.
- **Seam E's HTTP hop adds a failure mode to the deployment pipeline.** Mitigation: `EngineGateCall_FailsOpenOnTransportError` — a gate failure leaves the pre-existing predicate untouched, which is exactly today's behaviour, because the term is OR'd (D10). This is the second payoff of "by OR, never by replacement".
- **Group-vs-action precedence is override-wins, not max-wins, inside the principal ladder.** An admin who sets `deploy-control` to `AlwaysHuman` and later sets one member to `Min` has lowered it without touching the group. Deliberate — it is what "individual actions override their group" means — and mitigated by provenance badges, the DCB event and a confirm dialog on lowering a `Destructive` action. Not closed.
- **`file_write` / `shell_execute` remain bypasses.** `effect:git.pull-request.create` at `AlwaysHuman` is defeated by `git push` under `tool:git_operations.write`; every governed route is reachable by `curl` under `tool:shell_execute`. Out of scope, recorded; needs a protected-path selector and a merged shell denylist.
- **Gating the prod deploy gates the stage transition, not the deploy.** `DeploymentPipelineWorkflow.cs:588` dispatches generic `llm-call` with `enableTools=true`; the actual deploy happens inside the tool loop. Until a typed deploy tool exists, the effective gate on a real prod deploy is `tool:shell_execute` plus `ActionGate`'s regexes. **Must be surfaced in the `deploy-control` group description in the UI**, not only in this plan.
- **A platform admin or a wildcard API key can rewrite every threshold unchallenged.** The *gate* does not inherit the bypasses (D6) but the *write path* does, and `api_keys.Permissions` is accepted free-form with no validation, so `actions:manage` is self-grantable. No two-person mechanism exists anywhere in the repo; this is a new capability, filed as an epic open question, not solved here.

## Blocks / Blocked by

- **Blocked by Story 43-5** — `action_assignments` / `action_authorizations` entities + migration, `IGovernancePrincipalResolver` + `ISoleUserProvider`, `IGovernancePolicySnapshotProvider`, `IActionAssignmentRepository`. Hard. This story owns the ledger's *semantics*; 43-5 owns its *table*. Agree `IActionAuthorizationLedger`'s signature once, in one plan.
- **Blocked by Story 43-8** — `.Governs` + `ActionGateMetadata` for Seam C, and the `KnownUngovernedEndpoints` ratchet this story bumps twice (the evaluate route; the new read-only client method). Hard.
- **Blocked by Story 43-3** — behaviour-preserving shipped defaults; AC2 is untestable without them. Hard.
- **Coordinates with Story 43-6** — the decide + pending endpoints join `/api/actions` and reuse `ActionsManage`; route ordering (literals before parameterized) is a shared constraint.
- **Coordinates with Story 43-7** — the pending-authorizations panel and the "gating the transition, not the deploy" group description are UI work that consumes this story's surfaces.
- **Blocks Story 43-10** — the Epic 42 reconciliation's Seam-B transplant (42-3's siting analysis and effective-ceiling insight) references the seam as built here.
- **Sequencing within the story:** 1–4 → 5 → 6 → 7 → 8 → 9–11 → 12–13 → 14.

> **AMENDED 2026-08-01.**
> - **43-5, 43-8 and 43-3 have all LANDED.** The three "Hard" blockers are cleared; see the
>   story's Dependencies amendment for the shipped artefacts.
> - **43-8 hands over exactly one open obligation**, and it should be this story's first
>   task: 43-8 §A3 step 3 (`docs/stories/epic-43/story-43-8/43-8-drift-harnesses.md:362-381`) —
>   baseline `POST /api/v1/governance/evaluate` and delete
>   `GovernedEndpointCoverageSweepTests.PreProvisionedJustificationKeyword_isStillUnused`.
> - **43-8 must be amended in return**, because D15 overturns its stated design in four places
>   (`GovernsExtensions.cs:11-13`, `ActionGateMetadata.cs:8`, `43-8-drift-harnesses.md:89`,
>   `:296`). That is outside this story directory and is left OPEN here, flagged rather than
>   done: whoever implements D15 must update those notes in the same change, or 43-8 will keep
>   telling the next reader to build the version that breaks AC3.
> - **Revised sequencing:** 5 → 7 → 9–11 → 8 → 12(remaining)–13 → 14, with 9's baseline entry
>   first inside that group.
> - **Blocks Story 43-10** is unchanged, but note 43-10's "Seam-B transplant" now references a
>   seam that **already exists** (`InlineToolLoopRunner.cs:332-390`) rather than one this story
>   builds.

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-07-25 | 1.0.0   | Initial plan | Claude |
| 2026-08-01 | 1.1.0   | **Conformance amendment.** Steps 1, 2, 3, 4, 6 and 12 marked DONE against the tree (43-4/43-5) with file:line evidence; step 8 marked PARTIAL (admin half done at `ActionPolicyEndpoints.cs:614-623`). Added **D15** (`.Governs()` stays metadata-only; enforcement is an explicit per-route opt-in — resolves the AC3/AC7 contradiction and reverses 43-8's stated design), **D16** (AC2's single deliberate exception, `effect:mcp.tool.invoke` at `AlwaysHuman` since 2026-07-30, and AC2 rescoped to routes that opt in), **D17** (named/dated/reviewed per-item exception sets for the two strictly-decreasing ratchets this story must widen, instead of edited assertions or a second client). Pre-Reading line numbers refreshed wholesale — every one was stale except `DeploymentPipelineWorkflow.cs:242/248/588`, `WaitForDeploymentApprovalActivity.cs:52`, `LlmCallModels.cs:500`, `ToolExecutorRegistry.cs:56-62` and `NotificationEndpoints.cs:116`. Corrected: `KnownReadOnlyClientMethods` does not exist; resume endpoints are 6 not 11; `BackgroundActor` has 29 members not 25; the Dev policy blanket is 26 policies not 22; the snapshot provider is a **singleton 60 s cache**, not scoped, and `TwoGateCallsInOneRequest_IssueOneRepositoryRead` does not exist. Test Plan annotated with which fixtures already exist and under what names. | Claude |
