# Story 43-9: The Five Seams, Enforcement Live, and the Authorization Ledger

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

As a **tenant admin who has set `effect:deploy.promote-prod` to human-only**,
I want that setting to actually stop the system from promoting to production by itself — at the one place where a real human wait exists, with one human decision covering the whole correlation rather than one per retry,
So that the autonomy dial is a safety control and not a label, and so that a denial always leaves an audit row.

## Priority

P0 — Without this story the whole epic is a declaration. `AcceptanceRules.AutonomyLevel` has 11 production references and **not one of them branches on it**; `AcceptanceGuardrails.TryPreGate` has **zero production call sites**. This story is the consuming layer.

## Architectural Context (READ FIRST)

### BINDING: enforcement is live in v1

**There is no enforcement-flip story and no soak precondition.** Every action ships assigned so that day one reproduces today's behaviour exactly (Story 43-3's `ShippedDefaults_ReproduceTodaysGatingBehaviour`); the admin opts into gating and it bites immediately.

Shipping the mechanism switched off behind a "≥30 days of `WOULD_BLOCK` telemetry" gate was considered and **rejected**: under it, an admin who sets deploy to human-only gets *nothing*, and an epic whose entire product requirement is "the admin can set what the system may do by itself" would ship not doing that. `WOULD_BLOCK` remains as a **shadow signal for actions the admin has not yet tightened**, not as a precondition for the mechanism working.

### The gate: split Core/Api exactly like `IAcceptanceRulesResolver`

`Tamma.Core` has **zero** `ProjectReference`s and cannot touch a database. So: `IAutonomyGate` + the pure static `AutonomyGateEvaluator` live in `Tamma.Core/Actions/`; `AutonomyGateService : IAutonomyGate` (the DB-touching impl) lives in `Tamma.Api/Services/Actions/`.

**Named `AutonomyGate*`, never `ActionGate*`.** `Tamma.Activities.Security.ActionGate` (`ActionGate.cs:17`) is a shipped, DI-registered (`Program.cs:750`), constructor-injected type, and the name collides inside `Tamma.Api`.

### The five seams, and why each is shaped the way it is

| Seam | Site | Enforces | Denial shape |
|---|---|---|---|
| **A — llm-call** | `Program.cs:3026` → `LlmCallEndpoints.CallLlm` | **Never, in any version** | — (observe only) |
| **B — tool dispatch** | `InlineToolLoopRunner.cs`, one site | Yes | `rejectedToolCalls` entry → message back to the model |
| **C — mutating routes** | endpoint filter via `.Governs` | Yes | **409**, never 202 |
| **D — background actors** | one call per tick per actor | **Deny only** | tick skipped, audit row |
| **E — Elsa graphs** | `CheckActionGateActivity`, over HTTP | Yes | `RequiresHuman` `[FlowNode]` into an existing wait |

**Seam A never blocks, in every version.** A `RequiresHuman` returned at the llm-call endpoint reaches a `DispatchWorkflow` whose **calling workflow has no human route in 44 of 45 cases** — escalation into a void, a workflow that suspends with nobody able to resume it. And blocking there *and* at Seam E would double-gate deploy: the deployment pipeline dispatches `llm-call` for the deploy stage *and* has a prod-approval decision. Agent-action enforcement lives **only at Seam E**, where a real human wait exists. This is pinned by a test, not by a comment.

**Seam B: one call site, and its position is load-bearing three ways.** In `InlineToolLoopRunner.cs`:
- **After** the `if (_toolCallValidator != null)` block **closes** (`:260` opens, closes at `:281`) — the validator **rewrites `tc.ArgumentsJson` at `:271`**, so gating before it would gate un-sanitized arguments.
- **Before** `executableToolCalls` is computed (`:330`) and therefore before the `EnableParallelTools` fork (`:335`). `EnableParallelTools` defaults **`false`** (`LlmCallModels.cs:500`), so a gate on the parallel branch alone would govern **nothing** in the default configuration.
- **NOT nested inside `if (_toolCallValidator != null)`.** Every `InlineToolLoopRunner` constructor dependency is optional-nullable (`:45-55`: `logger`, `httpClientFactory`, `configuration`, `sanitizer`, `toolRegistry = null`, `toolCallValidator = null`, `contextCompactor = null`, `eventEmitter = null`, `parallelExecutor = null`, `credentialResolver = null`). Nesting the gate inside that block would make the gate **absent exactly whenever the validator is absent**. `IAutonomyGate` is therefore a **required** constructor parameter.

A denial becomes a `rejectedToolCalls[tc.Id]` entry, which the machinery at `:299-325` **already** turns into a tool-result message fed back to the LLM. **Zero new plumbing, no exception, no new failure code.** The outcome is named **`Denied`, not `RequiresHuman`** — there is no human on this path, and calling it escalation would be a lie.

The two existing **fail-open** allowlists stay: `ToolCallValidator.Validate` and `ToolExecutorRegistry.IsAllowed` (`:56-62` — `if (allowlist is null || allowlist.Length == 0) return true;`). The gate is **additive** and cannot be defeated by a null allowlist.

**Seam C is an endpoint filter, not an `IAuthorizationHandler`.** Middleware order is authentication → `ProxyHeaderAuthMiddleware` → authorization → rate limiter → impersonation → tenant context (`Program.cs:1788-1803`), so **`ITenantContext.TenantId` is unset during policy evaluation**, and there is no `IAuthorizationPolicyProvider` for dynamic per-action policies. Two security properties follow and are worth keeping:
- The gate does **not inherit the two unconditional superuser bypasses** — `platformRole == "platform_admin"` succeeds every `PermissionRequirement` (`PermissionHandler.cs:41`, duplicated `SelfOrPermissionRequirement.cs:65`) and an api-key `permission` claim of `"*"` (`PermissionHandler.cs:26,106`; `OrchestratorChannelHandler.cs:46-50`). **A platform admin can edit assignments but cannot bypass a governed effect.**
- It is unaffected by the Development-without-JWT blanket that re-registers **all 22 named policies** with `AllowAnonymousRequirement` (`Program.cs:1698-1730`).

**Denial returns `409 Conflict`, never `202`.** Verified: `TammaApiClient` branches **solely** on `IsSuccessStatusCode` — 11 sites, every mutating method (`:228,502,551,593,626,680,729,761,804,854,890`) — and **`202` is already a success code on that client**: `QueueSlackNotificationAsync:386` calls `POST /api/v1/notifications/slack` (`Program.cs:3136`) which returns `Results.Accepted` (`NotificationEndpoints.cs:116`). A 202 "escalated" response would be **indistinguishable from success**, and the engine would proceed as if the effect had happened. `409` not `403`: the caller **is** authorized; the *system* is not yet permitted to act autonomously.

**Seam D can only deny.** A sweeper cannot suspend for a person — there is no `ActivityExecutionContext`, no bookmark, and nobody watching. Every `automation:*` descriptor is `EscalatableToHuman = false`, the admin API rejects a mid-range `MinAutonomy` on such a target, and the UI renders a two-state control. Exceptions are **caught inside the helper**: `BackgroundServiceExceptionBehavior` defaults to `StopHost`, and a governance evaluation failure must never take down the host.

**Seam E reaches the gate over HTTP, not by DI.** `Tamma.ElsaServer` registers **no repository** and mediates everything through `TammaApiClient` (`Program.cs:2849-2851`; `ElsaServer.csproj` references only `Tamma.Activities` + the analyzer). So this story adds a mediation route `POST /api/v1/governance/evaluate` (`EngineServiceOnly`). It mints **no `ExternalEffect` member** (it is a read) and goes on `KnownUngovernedEndpoints` with the justification **`gate-evaluation-endpoint-cannot-gate-itself`**.

**v1 adopts Seam E in exactly one place, by OR, never by replacement** — `DeploymentPipelineWorkflow.cs:242-246`:

```csharp
var prodApprovalNeeded = new FlowDecision(ctx =>
       string.Equals(mode.Get(ctx)?.Trim(), "business", StringComparison.OrdinalIgnoreCase)
    || requireProdApproval.Get(ctx)
    || gateOutcome.Get(ctx) == "requires-human")   // NEW — additive only
```
routed into the **existing** `WaitForDeploymentApprovalActivity` (`Tamma.Activities/ADL/WaitForDeploymentApprovalActivity.cs:52`), reached at `DeploymentPipelineWorkflow.cs:248`. A threshold-only *replacement* would be **strictly weaker** for business-mode tenants, whose gate is currently unconditional.

The gate is evaluated on **`effect:deploy.promote-prod`, not `agent-action:deploy`**, because `StageDeployDispatch` (`DeploymentPipelineWorkflow.cs:588`) is **shared across staging / uat / prod** and one `agent-action:deploy` member cannot distinguish stage.

### The ledger

One human decision must cover one deploy, not one decision per retry and not one per seam. `action_authorizations` is scoped by `(principal, correlationId, target)` where the target may be an **action or a whole group**; `TryConsumeAsync` lets a group-scoped grant satisfy every member within the correlation, and the second seam's `ACTION.GATE.ALLOWED` records `CoveredBy`.

**The ledger is written by the EXISTING human surfaces.** No new suspend activity and no new bookmark prefix: `LifecycleBookmarks.CanonicalSuspendActivities` is keyed by activity `Type`, so a prefix without an activity is not representable. Grants arrive via the landed resume endpoints (`Program.cs:2919-2957`) and via `POST /api/actions/authorizations/{id}/decide`.

## Acceptance Criteria

1. **`IAutonomyGate` + pure evaluator in Core; DB-backed service in Api.**
   `Tamma.Core/Actions/IAutonomyGate.cs` (`AutonomyQuery`, `AutonomyDecision`, `AutonomyOutcome { Automated, RequiresHuman, Denied }`) and `Tamma.Core/Actions/AutonomyGateEvaluator.cs` — **pure, static, zero I/O**, taking the policy snapshot and the base acceptance rules as arguments. `Tamma.Api/Services/Actions/AutonomyGateService : IAutonomyGate` performs the reads. Named `AutonomyGate*` throughout; a test asserts no type named `ActionGate*` is added to `Tamma.Api`.

2. **Enforcement is live, and the shipped defaults are behaviour-preserving.**
   `Enforce` resolves per the Story 43-5 ladder; the **shipped default enforces**, and every descriptor's `DefaultMinAutonomy` is set so that day one control flow is byte-identical to today. A test per seam asserts `ShippedDefaults_DoNotAlterControlFlow`, and a second test asserts that **tightening one action does change control flow at its seam** — otherwise the first test is satisfiable by a gate that does nothing.

3. **Seam A is observe-only, permanently.**
   `LlmCallEndpoints.CallLlm` evaluates `ActionKey(AgentAction, request.Action)` when `LlmCallRequest.Action` is non-null, emits the audit event, and **always proceeds**. Pinned by `LlmCallSeam_NeverBlocks_EvenUnderEnforce` — including with the action set to `AlwaysHuman` at every scope. The test's doc-comment states the reason (44-of-45 no-human-route; double-gating deploy against Seam E).

4. **Seam B: one call site, correctly positioned, with the gate as a required dependency.**
   The gate call sits in `InlineToolLoopRunner` **after** the validator block closes (`:281`) and **before** `executableToolCalls` (`:330`) / the `EnableParallelTools` fork (`:335`), and is **not** inside `if (_toolCallValidator != null)`. `IAutonomyGate` is a **required** constructor parameter. Tests: `Constructor_RequiresTheGate`; `SequentialAndParallelBranchesBothGoverned`; `Gate_runs_after_sanitization` (asserts the gate sees the rewritten `ArgumentsJson`); `Gate_evaluates_when_validator_is_null`.

5. **Seam B denials are `Denied`, expressed through existing machinery.**
   A denial writes `rejectedToolCalls[tc.Id]` and nothing else — the code at `:299-325` turns it into a tool-result message back to the model. No exception, no new failure code, no new plumbing. The outcome enum member used is `Denied`. Test: `Denied_tool_call_becomes_a_tool_result_message_not_an_exception`.

6. **Seam B is additive over the fail-open allowlists.**
   `ToolExecutorRegistry.IsAllowed` returns `true` on a null/empty allowlist (`:56-62`); the gate still denies. Pinned by `Gate_denies_even_when_registry_allowlist_is_null`.

7. **Seam C is an endpoint filter, attached by `.Governs`, and does not inherit the bypasses.**
   `Tamma.Api/Infrastructure/AutonomyGateEndpointFilter.cs`, attached by the Story 43-8 `.Governs(ActionKey)` extension. Tests: `Gate_still_evaluates_when_all_policies_are_AllowAnonymous` (the `Program.cs:1698-1730` Development blanket); `PlatformAdmin_cannot_bypass_a_governed_effect`; `WildcardApiKey_cannot_bypass_a_governed_effect`.

8. **Seam C denial is `409`, and `202` is proven unusable.**
   Body: `{ code: "ACTION.GATE.REQUIRES_HUMAN", action, group, effectiveMinAutonomy, autonomyLevel, authorizationId }`. Tests: `Denial_returns_409`; `Client_treats_202_as_success` — a characterization test asserting `TammaApiClient` returns success for a 202 response, so the reason 409 was chosen is *encoded*, not just written down.

9. **Seam D denies only, never escalates, and never takes down the host.**
   `Tamma.Api/Services/Actions/BackgroundActionGate.cs` — one call per tick per actor, principal from `IGovernancePrincipalResolver` (per-tenant for tenant-scoped sweeps, platform scope for cross-tenant). Every `automation:*` descriptor is `EscalatableToHuman = false`; the admin API rejects a non-sentinel `MinAutonomy` on an `automation:*` target with `ACTION_POLICY.INVALID`. Exceptions inside the helper are caught and emitted as `ACTION.GATE.EVALUATION_FAILED`. Tests: `MidRangeThreshold_OnAutomation_Is400`; `Evaluation_failure_does_not_propagate_out_of_the_helper`; `Denied_tick_is_skipped_and_audited`.

10. **Seam E reaches the gate over HTTP, and the route cannot gate itself.**
    `[Activity] CheckActionGateActivity` in `Tamma.Activities/Policy/` with `Automated` / `RequiresHuman` `[FlowNode]` outcomes, calling a new `TammaApiClient` method against `POST /api/v1/governance/evaluate` (`EngineServiceOnly`). The route mints **no** `ExternalEffect` member and is added to `KnownUngovernedEndpoints` with the justification `gate-evaluation-endpoint-cannot-gate-itself` (and the Story 43-8 count pin is bumped in the same commit). Test: `GovernanceEvaluateRoute_IsJustifiedUngoverned`.

11. **Seam E's one v1 adoption is by OR and on the effect, not the agent-action.**
    `DeploymentPipelineWorkflow.cs:242-246`'s `prodApprovalNeeded` gains a third **OR** term; the existing business-mode and `requireProdApproval` terms are untouched; the true branch routes into the existing `WaitForDeploymentApprovalActivity` (`:248`). The gate is evaluated on `effect:deploy.promote-prod`. Tests: `EnforceMode_NeverWeakensBusinessModeGate` (business mode + gate `Automated` still waits); `GateRequiresHuman_AddsAWaitWhereThereWasNone`; `Gate_is_on_the_effect_not_the_shared_dispatch` (asserts `StageDeployDispatch` at `:588` is **not** individually gated, since it cannot distinguish stage).

12. **The `action_authorizations` ledger: one human decision per correlation.**
    `IActionAuthorizationLedger.TryConsumeAsync(principal, correlationId, actionKey)` — an action-scoped grant covers itself; a **group-scoped grant covers every member**. States `{pending, granted, denied, expired}`; `requested_at_utc` **NOT NULL from day one**; `expires_at_utc` default +24h from `Tamma:Governance:AuthorizationTtlHours`; `consumed_at_utc`; `autonomy_level_at_request`. Unique index on `(tenant_id, user_id, correlation_id, target_kind, target_key) NULLS NOT DISTINCT WHERE state IN ('pending','granted')`. Tests: `GroupGrant_CoversEveryMemberWithinOneCorrelation`; `Grant_does_not_leak_across_correlations`; `ExpiredGrant_IsNotConsumable`; `SecondSeam_RecordsCoveredBy`.

13. **The decide endpoint and the pending-authorizations surface.**
    `POST /api/actions/authorizations/{id}/decide` (`ActionsManage`) with `{ decision: granted|denied, reason? }`, and `GET /api/actions/authorizations?state=pending` for the surface. No new suspend activity and no new bookmark prefix are introduced — `LifecycleBookmarks.CanonicalSuspendActivities` is keyed by activity `Type`, so a prefix without an activity is not representable; grants also arrive through the 11 landed resume endpoints (`Program.cs:2919-2957`). Tests: `Member_Gets403OnDecide`; `Decide_is_idempotent_on_an_already-decided_row`; `NoNewBookmarkPrefix_IsRegistered`.

14. **One audit event family, and denials under enforcement are not swallowed.**
    `Tamma.Api/Services/Actions/ActionGateEventsService.cs` (built on the `AcceptanceRulesEventsService.cs:16-18,54-93` template), appending **directly via `IEventRepository`** from `Tamma.Api` — `TammaEventEmitter` structurally requires an `ActivityExecutionContext` and the tool loop runs inside a blocking HTTP request. Types: `ACTION.GATE.ALLOWED` / `.REQUIRES_HUMAN` / `.DENIED` / `.WOULD_BLOCK` / `.AUTHORIZED` / `.AUTHORIZATION_DENIED` / `.PRINCIPAL_UNRESOLVED` / `.EVALUATION_FAILED`. Tags: `{actionKey, actionGroup, risk, autonomyLevel, effectiveMinAutonomy, assignmentSource, outcome, enforced, role, correlationId, issueId, tenantId, userId}`. Emission rides the template's swallowing try/catch **with one deliberate exception: `.DENIED` and `.REQUIRES_HUMAN` under enforcement are NOT swallowed** — a block with no audit row is a compliance hole. Volume control: `.ALLOWED` fires only when `Source != system-default` or `Enforced`. Test: `DeniedEmissionFailure_Propagates`; `AllowedEmissionFailure_IsSwallowed`.

15. **Gate reads are live, and the Init-time rules cache is fixed for the gate path only.**
    `DocumentLifecycleWorkflow.cs:184` resolves `ResolvedAcceptanceRules` once at Init into serialized state. The **gate path** re-reads at each decision point; the existing `state.Rules` reads (`:433,589,678,1208-1209`) are **untouched**, so in-flight instances stay valid. The one interface widening — `IAcceptanceRulesResolver.ResolveBaseAsync` / `ResolveBaseForTenantAsync`, lifted from `AcceptanceRulesService.cs:91-108` — is done once, here or in Story 43-5, never twice.

16. **The legacy always-escalate list gets its first production call site.**
    The evaluator calls `AcceptanceGuardrails.TryPreGate` and, if it escalates for a class mapping to this `ActionKey`, contributes `AlwaysHuman` as a **floor** composed by `max()` — so a legacy entry cannot be lowered by a catalog row. `TryPreGate`'s unrelated rounds-exhausted short-circuit is **ignored**; the document lifecycle keeps owning rounds. Tests: `LegacyAlwaysEscalate_CannotBeLoweredByAnActionRow`; `RoundsExhausted_DoesNotAffectActionThreshold`.

## Dependencies

- **Story 43-5 (storage, principal resolution, resolver, audit)** — `action_assignments` + `action_authorizations` tables/entities/migration, `IGovernancePrincipalResolver` + `ISoleUserProvider`, `IGovernancePolicySnapshotProvider`, `IActionAssignmentRepository`. **Blocking.** This story owns the *ledger semantics* (`TryConsumeAsync`, group-covers-member, TTL/consumption) and its two endpoints; 43-5 owns the table.
- **✅ Story 43-5 follow-up F11 — the break-glass override for the FAIL-CLOSED posture. CLEARED 2026-07-30; NO LONGER BLOCKING.** It shipped as a **config-sourced** lever (`Tamma:Governance:BreakGlass:Enabled` / `:ExpiresAtUtc` / `:Reason`), read once at construction, with **no endpoint and no writer** — engaging requires a config change and a restart, deliberately. It **refuses to engage** without an explicit UTC expiry, with one already past, or with one **more than 24 hours away** (cap added by review MEDIUM-3, 2026-07-31), expires by itself, and logs at ERROR on engage / refusal / expiry / **every bypassed decision**; each bypassed decision also writes an `ACTION.GATE.BREAK_GLASS_BYPASS` row on the **non-swallowing** append path (an unrecordable bypass fails rather than happening quietly), and carries the distinct provenance `ActionAssignmentSource.BreakGlass` (wire `break-glass`). **Precision this entry used to blur (review MEDIUM-1, fixed 2026-07-31):** "every bypassed decision" means the decisions the override **permitted**. A decision blocked while the override was engaged — by a read row, a ceiling, a disable, a role rule or an `AlwaysHuman` shipped default — is not a bypass, gets no bypass row, and keeps the provenance of whatever blocked it; it is audited on the ordinary non-swallowing `.DENIED`/`.REQUIRES_HUMAN` path. **A requirement on every seam this story adds:** do not treat "the override is engaged" as "this decision was bypassed" — read `decision.Source`, and note that `IAutonomyGate` is the path that is 1:1 while Seam B's row is deliberately a superset (allowed *and* denied shapes). **Also relevant to this story:** the disengage direction is not symmetric — setting `Enabled=false` and reloading configuration does NOT turn the override off in a running process, because the state is captured in the constructor; only expiry or a restart ends it. Full write-up: 43-5 → "F11 — CLOSED".

  **The constraint this story MUST honour when it wires seams A/C/D/E.** The override bypasses **degradation only**: it suspends the substitution of `AlwaysHuman` for a policy input that could not be READ, and nothing else. A decision denied by a policy row that WAS read — including a platform ceiling, an `Enabled = false`, an `AllowedRoles` restriction, a read legacy always-escalate entry, or an `AlwaysHuman` **shipped default** — is still denied while it is engaged. That boundary is enforced by construction in `AutonomyGateEvaluator` (the `Enabled`/`AllowedRoles` guards deliberately sit ABOVE the degradation branch; the snapshot bypass is sited inside the `!IsAuthoritative` branch, which provably carries no rows) and pinned by `EngagedButARealPolicyRowDenies_IsStillDenied` and `BreakGlassEngaged_AgainstARealPolicyDenial_IsSTILLDenied`. **A seam added here must not re-derive its own degraded behaviour**: call the gate, honour the decision, and emit the bypass row with its own `seam` tag — the gate is the only place that knows the composition is monotone.

  Two properties of the shipped lever this story should note rather than rediscover: it is **per-process, not per-tenant** (the failure it relieves is itself per-process, so a SaaS operator engaging it engages it for every tenant on that host), and it is **inert on a healthy evaluation** (pinned over the whole catalog by `Engaged_ChangesNothing_WhenEveryInputIsReadable`), so leaving it configured after an outage does not quietly change behaviour before the expiry does its job.
- **Story 43-5 follow-up F12 — the degraded outcome is a DENIAL, not an escalation, until this story lands.** `ToolLoopGateOutcome` has no `RequiresHuman` case, so the one live consumer feeds a degraded decision back to the model as a tool rejection and the run burns its turns reaching nobody. Seam work here is the first opportunity to make `AutonomyOutcome.RequiresHuman` mean an actual human wait on a live path; until then, do not describe the posture as "escalates".
- **Story 43-8 (drift harnesses)** — `.Governs(ActionKey)` and `ActionGateMetadata`. **Blocking for Seam C**: the filter attaches to metadata that must already exist and be swept. This story bumps `KnownUngovernedEndpoints` by one (AC10).
- **Story 43-3 (groups + behaviour-preserving defaults)** — AC2 is meaningless without it. Blocking.
- **Story 43-6 (admin API)** — `POST /api/actions/authorizations/{id}/decide` and the pending list join the `/api/actions` group and reuse `ActionsManage`. Coordinate route ordering (literals before parameterized).
- **Existing, verified:** `InlineToolLoopRunner` (`:45-55` ctor, `:260-281` validator block, `:299-325` rejected-call handling, `:330`/`:335` fork), `ToolExecutorRegistry.IsAllowed:56-62`, `LlmCallModels.cs:500`, `Program.cs:1698-1730`/`1788-1803`/`3026`/`3136`, `PermissionHandler.cs:26,41,106`, `SelfOrPermissionRequirement.cs:65`, `NotificationEndpoints.cs:116`, `DeploymentPipelineWorkflow.cs:242,248,588`, `WaitForDeploymentApprovalActivity.cs:52`, `AcceptanceGuardrails.TryPreGate`, `AcceptanceRulesEventsService.cs:16-18,54-93`.

## Out of Scope

- **A sixth seam.** Elsa's `UseWorkflowsApi()` surface (`ElsaServer/Program.cs:103,403`) runs in another process and is not gated; the TypeScript sidecar is ungoverned past the proxy route. Both recorded, neither closed.
- **Argument-value gating.** The gate matches on **identity, not payload**. "Gate this action *when* the payload looks like X" is not expressible and is deliberately not attempted — where a payload predicate is genuinely needed the answer is one of three things that already work (make the state unrepresentable in the document type's validation, use the landed `BlockingReviewViolation` clamp, or route it as a typed side-effect edge). A payload-predicate policy layer is a 39-5 change.
- **`ManagedAgent.ToResolvedTools` filtering.** Filtering *advertisement* means the model never asks, so the denial never fires and the capability silently vanishes — and the method is `private static` returning `null` on empty, where `null` and empty diverge downstream. Deferred with the rationale recorded.
- **Closing the `file_write` / `shell_execute` bypasses.** `effect:git.pull-request.create` set to human-only is still defeated by `git push` under `tool:git_operations.write`, and every governed route is still reachable by `curl` under `tool:shell_execute`. Needs a protected-path selector and a merged shell denylist; neither exists.
- **Gating the deploy itself.** Production deploy is an **LLM tool loop** (`DeploymentPipelineWorkflow.cs:588` dispatches generic `llm-call` with `enableTools=true`), not a typed activity. Gating `effect:deploy.promote-prod` gates the **stage transition**; the deploy happens inside the loop under `tool:shell_execute`. This must appear in the `deploy-control` group description in the UI, not only here.

## Estimated Effort

7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
| 2026-07-30 | 1.0.1   | Dependencies: 43-5 F11 (no break-glass override for the fail-closed posture) recorded as a **blocker**, and 43-5 F12 (the live seam hard-denies rather than escalating) recorded as context. Neither is built — F11's shape is a product decision. | Claude |
| 2026-07-30 | 1.0.2   | **F11 CLEARED — this story is unblocked.** The break-glass override shipped (config-sourced, mandatory expiry, per-decision ERROR log + non-swallowing `ACTION.GATE.BREAK_GLASS_BYPASS` row, `break-glass` provenance). Its scoping constraint — bypasses DEGRADATION only, never a successfully-read denial — is now a requirement on every seam this story adds. F12 unchanged and still open. | Claude |
