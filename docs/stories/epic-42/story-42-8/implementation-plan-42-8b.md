# Implementation Plan — Story 42-8B: Deploy-Control Tool

## Reconciled scope — differs from the story file

**Epic 42 was reconciled against Epic 43 on 2026-07-25.** 42-8B's verdict is **"Gating sections stripped"**
plus one story-specific deletion called out by name in both READMEs: **"42-8B's Scope 4 / AC7 DELETED — it
required the always-escalate class to bind *independently* of the dial and of 42-3's grant, so a satisfied
tool authorization would not satisfy the class. That is two gates, two audit ids and two human decisions for
one production deploy."** The deltas:

| Story file says | Reconciled |
|---|---|
| **§4 — "the always-escalate class binds *independently of* the autonomy dial and of 42-3's grant; a satisfied 42-3 authorization does not satisfy the Epic 39 class"** and **AC7** — "a prod deploy whose acceptance class is always-escalate routes to a human even when autonomy is 100 **and** a valid 42-3 grant is present" | **DELETED — both.** This is the single most consequential reconciliation delta in the epic. Epic 43 **absorbs** `AlwaysEscalate` rather than running it in parallel: the gate evaluator calls `AcceptanceGuardrails.TryPreGate` (**giving it its first production call site**) and, if it escalates for a class mapping to this `ActionKey`, contributes `AlwaysHuman` as a **floor composed with `max()`**. Because the encoding is monotone, a legacy always-escalate entry becomes a floor the catalog **cannot lower** — the safety property AC7 wanted — while producing **one** gate, **one** audit id and **one** human decision. See **D6**. |
| §2's per-call `PermissionClass` table and `ToolInvocationFacts Describe(string argumentsJson)` | **STRIPPED.** 42-3 is deleted; `ToolPermissionClass` no longer exists (42-1 rewritten). |
| §2's **`deploy_status` / `deploy_control` split** | **KEPT — a capability boundary, not a gate.** `deploy_status` physically cannot reach a mutating provider method. Post-reconciliation this is the primary structural safety property here. |
| **§3 — "the prod/staging discriminator comes from the 42-2 binding (`ConfigJson`: the declared target map)"** | **42-2 is DELETED and nothing replaces `ConfigJson`** — Epic 43's `action_assignments` stores policy only. The target map moves to deployment configuration (**D3**); the containment rule (the model picks a `targetKey` from a closed declared set; undeclared is refused) survives verbatim. **Per-tenant target maps become a recorded gap — G1.** |
| AC1's `ReadOnly`/floor 70 and `Destructive`/floor 100 | **STRIPPED.** AC1 becomes: two executors, both declaring `SecretRequirement(ApiKey\|SigningKey, "deploy/<platform>", Required)`; `deploy_status.Suspends == false`, `deploy_control.Suspends == true`. |
| AC3 (`Describe` table), AC5 (`ToolAuthorizationRequired`), AC6 (target-bound single-use), **AC7**, AC12's "42-3 decision id" and "Epic 39 acceptance decision id" | **STRIPPED / DELETED.** AC4's containment half survives (D3); AC12 reduces to 42-5's family tags. |
| Risk: "Stage-1 filter vs. max-class descriptor" | **Gone** — Epic 43 records the same insight in its Seam B analysis, with credit. |

**Unchanged:** §1's provider abstraction, §5's secret binding, §6's handle-not-suspend design and the shared
`WaitForToolOperationActivity`, §7's audit tags, and the siting rule.

## Scope & Deliverable

Two `IToolExecutor`s in `Tamma.Api` — `deploy_status` (`status`) and `deploy_control`
(`trigger`/`promote`/`rollback`) — over an `IDeployControlProvider` abstraction with one reference driver
(the platform's own Docker-Compose-on-Hetzner path) and a generic seam. `deploy_status` publishes only
`status` and cannot reach a mutating method. `deploy_control` accepts a `targetKey` from a **closed,
configuration-declared set**, refuses anything else before touching the provider, returns promptly with an
`operationHandle`, and declares `Suspends = true`. Completion is owned by the engine-side
`WaitForToolOperationActivity` — **shared with 42-7; whichever story lands first ships it, its bookmark
builder, its `CanonicalSuspendActivities` registration and its authenticated callback endpoint.** Credentials
bind through 42-4; every operation emits 42-5's `TOOL.*` trio with deploy tags.

## Pre-Reading

- `docs/stories/epic-42/story-42-8/42-8b-deploy-control-tool.md` — the story (**read the Reconciled scope table first**; §4 and AC7 are deleted, not merely restated)
- `docs/stories/epic-42/story-42-8/42-8-feature-flag-deploy-control-tools.md` + `implementation-plan.md` — the split index and its plan
- `docs/stories/epic-42/README.md` — the verdict table, which names this deletion explicitly; `docs/stories/epic-43/README.md` — **§5 "Absorbing the existing always-escalate list"** (the mechanism that replaces AC7), Enforcement Seam B and Seam E, and **Storage** (`action_assignments`' columns — the evidence for the `ConfigJson` gap); `.dev/decisions/epic-43-action-catalog-design.md` — "`AlwaysEscalate` absorbed, not deleted"
- **`docs/stories/epic-42/story-42-7/implementation-plan.md`** — **D5/D6/D7 and W1/W2 are the shared wait machinery, argued once there.** This plan does not re-derive them; it states which story pays
- `docs/stories/epic-42/story-42-1/implementation-plan.md` (D2 `ToolDescriptor(RequiredSecret, Suspends)`, **D3 the `Suspends` wording**), `story-42-4/implementation-plan.md` (D2/D3/D8, G1), `story-42-5/implementation-plan.md` (D2/D3)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs` — `AlwaysEscalate`, `EscalationClass` `:200-202`, `EscalationClassKind` `:206-210` (`document-type` / `agent-action`), `AcceptorRequirement` `:254-258`; `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:128-133`
- **`apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs`** — `Compose` `:38-48`, `ForStageGate` `:55`, `ForDecisionSession` `:66`, `ForDocumentInput` `:82`, **`CanonicalSuspendActivities` `:98-105` (exactly two entries; `ForToolOperation` does NOT exist)**
- **`apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs:158-198`** (clause b) and `:202-236` (clause b-inverse) — why the `CanonicalSuspendActivities` registration must land in the same commit as the activity
- `apps/tamma-elsa/src/Tamma.Activities/Testing/WaitForCIResultsActivity.cs` — the landed two-armed-resume precedent
- `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/DocumentDecisionResumeEndpoint.cs` — the authenticated, tenant-folded resume posture the callback must match
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:753-766`; `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:286-292`; `apps/tamma-elsa/src/Tamma.Activities.Guardrails/Allowlist.cs:16-17`, `:45-64`, `:57-58`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:500` — `EnableParallelTools` defaults **false**

## Corrections to the story

- **U1 — AC7's premise was already contradicted by the code it relied on, independently of the
  reconciliation.** AC7 says a prod deploy's always-escalate class routes to a human "even when autonomy is
  100". But `AcceptanceRules.AutonomyLevel` is stored, validated `[70,100]` and **never branched on
  anywhere** — Epic 43's opening line is that there are 11 production references and every one is a
  declaration, a default, a DTO field or an audit tag, and that `AcceptanceGuardrails.TryPreGate` has **zero
  production call sites**. So "binds independently of the dial" was a statement about a comparison that no
  code performs. Epic 43 gives `TryPreGate` its first production call site and composes its contribution with
  `max()`. **The deletion does not weaken the guarantee; it makes it real for the first time.**
- **U2 — `LifecycleBookmarks.ForToolOperation` does not exist.** Verified: the builders are `Compose`,
  `ForStageGate`, `ForDecisionSession`, `ForDocumentInput` (`:38-82`). Whichever of 42-8B / 42-7 lands first
  **writes it**. The story treats it as available.
- **U3 — `CanonicalSuspendActivities` has exactly two entries and its registration is build-gated.**
  `:98-105`. Clause (b) of `ResumableStandardStructuralTests` (`:158-198`) fails any `BookmarkSuspend`/`Both`
  workflow whose declared suspend type is absent from that dictionary; clause (b-inverse) (`:202-236`) fails
  a graph containing an undeclared canonical suspend node. The registration must land **with** the activity.
- **U4 — the effort note's wave-order assumption may not hold.** The story says *"under the stated wave order
  (42-9 → 42-8A → 42-8B → 42-7) this story lands first and therefore carries the shared wait machinery."*
  Post-reconciliation 42-9 is materially harder than it looked (its entire configuration source was deleted
  with 42-2), so the order may change. Both figures are given below; **the pair pays for the wait machinery
  once, and the plan does not assume which story pays.**
- **U5 — `IDeployControlProvider` is not on `TAMMA001`'s injection denylist and would not trip it.** Verified
  (closed 13-entry list at `Allowlist.cs:45-64`; the HTTP check needs a statically-literal external host).
  The story says so. Siting is settled by rule 1 and by the engine not hosting the catalog — the analyzer is
  a backstop, not the enforcement.

## Design Decisions

- **D1 — the two executors, `IDeployControlProvider` and every driver live in `Tamma.Api`**, package
  `Tamma.Api.Services.Tools.Deploy`, registered at `Program.cs:753-766`. **The one engine-side artefact is
  `WaitForToolOperationActivity`** (D5), which holds no credential and makes no vendor call. Reasons and
  precedent: identical to 42-7 D1.
- **D2 — the status/control split is the primary safety property.** `deploy_status` publishes an
  `InputSchema` enumerating only `status` and has no code path to `TriggerAsync`/`PromoteAsync`/
  `RollbackAsync`; an argument object naming a mutating verb returns `Success = false` with **zero** calls on
  a spy `IDeployControlProvider` — asserted on the spy, not the message. With per-call classification
  stripped, this is the guarantee that holds regardless of catalog configuration.
- **D3 — the declared target map moves to deployment configuration; containment survives, classification
  does not.**

  ```
  Deploy:Platform                       — the driver key
  Deploy:Targets:<key>:IsProd           — the closed declared set
  Deploy:Targets:<key>:DisplayName
  ```

  Bound via `IOptions<DeployControlOptions>`, validated fail-loud at startup. The model selects a `targetKey`
  from this set; an **undeclared or missing** `targetKey` is a refusal — `Success = false`, zero provider
  calls — not a reclassification, since there is no class to assign. A free-text `environment` field supplied
  by the model is **never read** (the surviving half of AC4). `IsProd` still matters: it is the fact an Epic
  43 catalog row and its audit are authored against, and it is carried on every `TOOL.*` row.
- **G1 — per-tenant target maps are a recorded capability gap.** In SaaS, 42-2 would have let each
  `tenant_admin` declare their own deploy targets; D3's configuration is deployment-wide. Single-user is
  fully served; SaaS gets a platform-declared target set and cannot give tenants distinct prod definitions.
  **No replacement store is invented here** — that is the duplication the reconciliation deleted. The same
  gap appears in **42-4** (secret names), **42-8A** (environment maps) and **42-9** (endpoint bindings).
  **Four stories, one missing store; decide it once at epic/Epic 43 level.**
- **D4 — secret binding.** `SecretRequirement(SecretPurpose.ApiKey | SecretPurpose.SigningKey,
  "deploy/<platform>", Required)`, resolved by 42-4 to `SecretRef.ForTenant(runTenantId, name)` in SaaS and
  `SecretRef.ForPlatform(name)` in single-user. No user scope — `SecretScope` has exactly `Platform` and
  `Tenant`, `SecretRef`'s ctor throws on either mismatch, and the sole user's ownership is
  `SecretMetadata.OwnerUserId`. `runTenantId` comes from the run context only. Fetched immediately before the
  vendor call, used once, dropped, and scrubbed **by value** from anything `ExecuteAsync` returns (42-4 D8).
- **D5 — the executor returns a handle; it does not suspend, and it cannot.** The loop runs inside a
  **blocking** `POST /api/v1/llm/call` in `Tamma.Api`, where there is no `ActivityExecutionContext` and no
  bookmark to create. `deploy_control` returns promptly with `Success = true` and an `operationHandle`
  (`{ platform, deploymentId, targetKey, releaseRef }`), and `Suspends = true` is a **declaration that
  completion is owned by an engine-side wait** (42-1 D3, verbatim). `WaitForToolOperationActivity`
  (`Tamma.Activities/ToolExecution/`, generic over `{ kind, operationId }`, credential-free, no vendor call)
  suspends on `LifecycleBookmarks.ForToolOperation(tenantId, operationId)` — **which this story writes if it
  lands first (U2)** — with two armed resume paths exactly as `WaitForCIResultsActivity` does: the completion
  callback → `Completed`, a durable scheduled-delay bookmark → `TimedOut`. The type **must** be added to
  `CanonicalSuspendActivities` in the same commit (U3). Any polling of the deploy platform is a `Tamma.Api`
  concern.
- **D6 — prod deploy stays a human decision by POLICY, through ONE gate.** The story's §4 intent —
  *"this tool executes an authorized deploy; it never decides to skip the gate"* — is preserved and is
  unchanged. What is deleted is the *independence* requirement. The mechanism is now Epic 43's §5: the gate
  evaluator calls `AcceptanceGuardrails.TryPreGate` (its first production call site) and an always-escalate
  class mapping to this `ActionKey` contributes `AlwaysHuman` as a floor composed with `max()`. Because
  `max()` is monotone, **a legacy always-escalate entry is a floor the catalog cannot lower** — only deleting
  it in the acceptance-rules UI can. That delivers AC7's safety property with one gate, one audit id and one
  human decision, instead of two of each for a single production deploy. **This story therefore contains no
  escalation code at all** — a fact worth stating plainly, because §4 reads as though it did.
- **D7 — the handle is minted server-side; the callback endpoint is authenticated and tenant-folded.** The
  `operationHandle` crosses from a tool result into an engine-side bookmark name, so a model-supplied handle
  must never select a bookmark. The callback is new external surface and must be keyed tenant + operation,
  matching `DocumentDecisionResumeEndpoint` — **not deployment-id-only**, or anyone who learns a deployment
  id can resume a suspended workflow.
- **D8 — audit is 42-5's trio with deploy tags; no new family, no decision ids.** `platform` / `operation` /
  `targetKey` / `releaseRef` / `deploymentId` / terminal `status` / `isProd`. AC12's two decision ids are
  stripped: any gate decision is recorded in **Epic 43's** event family and correlated by
  `toolCallId`/`correlationId`. The wait activity emits its request/completion pair through
  `TammaEventEmitter` — it *is* an Elsa activity with a context, the one place here that emitter applies.
- **D9 — the provider abstraction names no vendor type in the executors.** `IDeployControlProvider`
  (`GetStatusAsync`/`TriggerAsync`/`PromoteAsync`/`RollbackAsync`) with one reference driver aligned to the
  platform's own deploy path (Docker Compose on the Hetzner VPS) plus a generic seam.

## Implementation Steps

1. **Precondition gate.** 42-1, 42-4, 42-5 landed. **Check whether 42-7 has already shipped
   `WaitForToolOperationActivity` + `ForToolOperation` + the `CanonicalSuspendActivities` entry + the callback
   endpoint** — if so, steps 5–6 collapse to adding this story's operation `kind`.
2. **CREATE `Tamma.Api/Services/Tools/Deploy/DeployControlOptions.cs`** — D3's target map with fail-loud
   startup validation.
3. **CREATE `.../Deploy/IDeployControlProvider.cs` + the reference driver + the generic seam** (D9).
4. **CREATE `.../Deploy/DeployStatusTool.cs` + `DeployControlTool.cs`** (D2/D3/D4/D5) — descriptors, schemas,
   target containment, fail-closed refusals, secret resolution immediately before the vendor call, by-value
   scrubbing at the `ExecuteAsync` boundary, `Success = false` on every driver throw.
5. **CREATE `Tamma.Activities/ToolExecution/WaitForToolOperationActivity.cs`; MODIFY
   `Tamma.Activities/Documents/LifecycleBookmarks.cs`** — `ForToolOperation` (U2) **and** the
   `CanonicalSuspendActivities` entry (U3), same commit. *Skip if 42-7 shipped them; add only the `kind`.*
6. **CREATE the authenticated, tenant-scoped completion callback endpoint** in `Tamma.ElsaServer/Endpoints/`
   (D7), plus the Api-side polling seam if the driver needs one.
7. **MODIFY `Tamma.Api/Program.cs:753-766`** — register both executors, the options binding and the driver.
8. **CREATE the test suites** (Test Plan). Author the Epic 43 catalog entries for `tool:deploy_status` and
   `tool:deploy_control` as **admin data**, not code.
9. **Finish:** full `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean; confirm the only
   engine-side additions are the wait activity and the bookmark builder.

## Data & Migrations

None. Secrets are Epic 29's `secrets` table; events ride `IEventRepository` → `domain_events`; the bookmark
is Elsa's existing persistence; D3's map is configuration.

## Events

Reuses 42-5's `TOOL.INVOKED`/`SUCCEEDED`/`FAILED` with `platform` / `operation` / `targetKey` /
`releaseRef` / `deploymentId` / `status` / `isProd` tags (D8). The wait activity emits its request/completion
pair through `TammaEventEmitter`. **No new family.**

## Test Plan

- **`DeployToolDescriptorTests`** — both executors registered; descriptors read through an
  **`IToolExecutor`-typed** reference declare `SecretRequirement(ApiKey|SigningKey, "deploy/<platform>",
  Required)`; `deploy_status.Suspends == false`, `deploy_control.Suspends == true`. **Covers reconciled AC1.**
- **`DeployStatusCannotMutateTests`** (D2) — `InputSchema` enumerates only `status`; an argument object
  naming `trigger`/`promote`/`rollback` returns `Success = false` with **zero** calls on a spy
  `IDeployControlProvider`. **Covers AC2.**
- **`DeployTargetContainmentTests`** (D3, replacing the stripped AC3/AC4) — `trigger`/`promote` against a
  declared target reaches the provider; an **undeclared** `targetKey`, a **missing** `targetKey` and
  malformed JSON each yield `Success = false` with zero provider calls; and a free-text
  `"environment": "staging"` alongside a declared prod `targetKey` does **not** change which target is
  touched (asserted on the spy's received arguments).
- **`DeployProviderAbstractionTests`** (D9) — a stub provider drives `trigger → status → rollback` with the
  reference driver **not registered**; a reflection assertion that the executors name no concrete platform
  type. **Covers AC8.**
- **`DeployCredentialScopingTests`** (D4) — SaaS resolves `SecretRef.ForTenant(runTenantId, name)`,
  single-user `SecretRef.ForPlatform(name)`; a `Tenant`-scoped ref with a null tenant id throws; a
  grep-for-value test with a **pattern-non-matching** token asserts it appears in no
  `ToolExecutionResult.Output`, no `TOOL.*` payload and no captured log line. **Covers AC9.**
- **`DeployNeverThrowTests`** — a driver that throws yields `Success = false` + `TOOL.FAILED`, and
  `ExecuteAsync` **returns** rather than propagating. Both branches (`EnableParallelTools` `false` — the
  default — and `true`). **Covers AC11.**
- **`ToolOperationWaitTests`** (D5/D7, Testcontainers) — **shared with 42-7; written once by whichever story
  lands first.** Against a stub slow provider, `trigger` returns inside the per-tool timeout with
  `Success = true` and a non-empty `operationHandle`, and **no** suspend occurs inside
  `POST /api/v1/llm/call`; then the wait activity (a) resumes to `Completed` on the callback, (b) resumes to
  `TimedOut` on its durable delay, (c) resumes from the **persisted** bookmark after a host restart
  mid-wait; a model-supplied handle selects no bookmark; a cross-tenant callback does not resolve; the
  activity type is present in `LifecycleBookmarks.CanonicalSuspendActivities` and
  `ResumableStandardStructuralTests` stays green. **Covers AC10.**

## Definition of Done

| AC (reconciled) | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — two executors, secret requirement, `Suspends` values | 4, 7 | `DeployToolDescriptorTests` |
| 2 — status executor cannot mutate | 4 (D2) | `DeployStatusCannotMutateTests` (spy at zero calls) |
| 4 — the model cannot select a target it was not given | 2, 4 (D3) | `DeployTargetContainmentTests` |
| 8 — provider abstraction holds | 3, 4 (D9) | `DeployProviderAbstractionTests` |
| 9 — credential scoping; nothing leaks | 4 (D4) | `DeployCredentialScopingTests` |
| 10 — long-op round trip, both resume arms, restart-durable, handle not forgeable | 5, 6 (D5/D7) | `ToolOperationWaitTests` |
| 11 — never-throw, both branches | 4 | `DeployNeverThrowTests` |
| ~~3, 5, 6, 12~~ | — | **STRIPPED — Epic 43 governs** |
| ~~**7 — the always-escalate class binds independently of the dial and of the tool grant**~~ | — | **DELETED. Epic 43 absorbs `AlwaysEscalate` as an `AlwaysHuman` floor composed with `max()`, delivering the same guarantee through one gate, one audit id and one human decision (D6/U1).** |

## Blocks / Blocked by

- **Blocked by — 42-1, 42-4, 42-5.** All hard, all Wave 1.
- **Blocked by — Epic 43 for governance, not for shipping — and here the gap matters most in the epic.**
  Without a catalog row and Seam B, `deploy_control` is auditable and secret-bound but **ungoverned**, and a
  prod promote is the highest-impact action in Epic 42. **Do not enable `deploy_control` in any deployment
  before Epic 43 Story 9 is live.** Specifically depends on Epic 43 §5's absorption of `AlwaysEscalate` (D6)
  for the prod-deploy human decision, and on Story 9's Seam B for the gate itself.
- **Shares with 42-7 — `WaitForToolOperationActivity`, `LifecycleBookmarks.ForToolOperation`, its
  `CanonicalSuspendActivities` entry, the authenticated callback endpoint, and `ToolOperationWaitTests`.
  Land them ONCE** (U2/U4). Whichever story goes first ships all five; the second adds only its operation
  `kind`. **Do not budget both stories' upper figures.**
- **Not blocked by — 42-8A**, which shares only the Wave-1 envelope (the split index records that the two
  halves share no implementation).
- **Open product question — G1**, shared with 42-4 / 42-8A / 42-9.
- **Blocks — Epic 41 consumers:** `deployment-pipeline` (promotion + rollback), **41-22** (incident
  rollback), **41-24** (release-notes trigger keyed off a promote), 41-29's `infra` `TaskKind` agent path.

## Risks & Mitigations

- **Prod blast radius, with no gate shipping in this story.** A wrongly-issued prod deploy is the
  highest-impact action in the epic, and the target-bound single-use authorization that used to guard it is
  stripped. Mitigation: the guarantee moves to Epic 43 — but **it is only as good as the catalog row and the
  always-escalate entry an admin authors**, and until Story 9 lands there is no gate at all. Hence the
  do-not-enable note above, and the status/control split, which makes the *read* half safe immediately and
  unconditionally.
- **Reading the AC7 deletion as a weakening.** It is not (U1): AC7's premise was a dial comparison that no
  code performs, and `AcceptanceGuardrails.TryPreGate` has zero production call sites today. Mitigation: D6
  states the replacement mechanism and its monotonicity argument, so a reviewer can check that the floor
  genuinely cannot be lowered rather than taking the deletion on trust.
- **Rollback is the operation most reached for under incident pressure**, and it is no longer graded by the
  tool at all. Mitigation: `isProd` and the operation ride every `TOOL.*` row (D8), so the catalog row for
  `tool:deploy_control` is the single place to decide rollback policy — and that recommendation should be
  written into the catalog-authoring note in step 8, not left implicit.
- **Handle forgery and the callback surface (D7).** Mitigation: server-side minting, tenant-folded bookmarks,
  authenticated tenant+operation-keyed callback, and the negative cases in `ToolOperationWaitTests`.
- **Double-building the shared wait machinery (U2/U4).** Mitigation: step 1's explicit check;
  `CanonicalSuspendActivities` is a build-gated single registration (U3), so a duplicate surfaces at once.
- **Siting drift.** Mitigation: D1's rule, with the honest note (U5) that `TAMMA001` would not mechanically
  catch a misplaced driver.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | Precondition gate + `DeployControlOptions` with fail-loud validation | 0.5 |
| 3 | `IDeployControlProvider` + reference driver + generic seam | 1.25 |
| 4 | Both executors (split, containment, secret binding, redaction, handle minting) | 1.25 |
| 5–6 | `WaitForToolOperationActivity` + `ForToolOperation` + `CanonicalSuspendActivities` + callback endpoint | 1.5 |
| 7–8 | DI wiring + seven test suites incl. the Testcontainers wait round trip | 1.25 |
| 9 | Full green + catalog-row authoring notes | 0.25 |
| **Total, carrying the shared wait machinery** | | **6.0** |
| **Total, if 42-7 lands first** (steps 5–6 collapse to a `kind`) | | **~4.0** |

Story estimate: ~5–6 d standalone, ~4 d if 42-7 is first. Stripping the gating sections and deleting AC7
removed `Describe`, the `ToolAuthorizationRequest` plumbing, the target-bound authorization matrix **and**
the Epic-39-class interaction test — a little over a day; D3's configuration shape adds part of it back.
**The pair (42-7 + 42-8B) pays for the wait machinery once.**
