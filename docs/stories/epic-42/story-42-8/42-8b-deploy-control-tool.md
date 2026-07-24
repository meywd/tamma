# Story 42-8B: Deploy-Control Tool

Status: drafted

*Split from the former combined story 42-8 — see [the split index](./42-8-feature-flag-deploy-control-tools.md).*

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As an **agent running a deploy, promotion, or incident workflow**, I want to **trigger, promote, roll
back, and check a release** through a governed tool — with prod-affecting operations authorized against
the concrete target by an actor and the deploy credential bound — so that a `deployment-pipeline`
promotion or a `41-22` rollback executes an *authorized* deploy instead of a shell script.

## Priority

P2 / Wave 3, after 42-9 and 42-8A. Shares its engine-side wait machinery with **42-7**; whichever lands
first ships it.

## Where this code lives (binding)

**Both executors, `IDeployControlProvider`, and every driver live in `Tamma.Api`** — package
`Tamma.Api.Services.Tools.Deploy`, registered next to the six built-ins at `Tamma.Api/Program.cs`
L753–766.

Reasons, in force order: (1) **rule 1** — a workflow step never calls an external API or holds an
external credential, and these do both; (2) **runtime** — `Tamma.ElsaServer/Program.cs` L286–292
records the tool catalog was *removed* from the engine and "the tool executors are registered there
[`Tamma.Api`], not here", so an engine-side executor is never resolved; (3) **guardrail backstop** —
`TAMMA001` (`DiagnosticSeverity.Error`, analyzer-referenced by `Tamma.Activities`/`Tamma.ElsaServer`)
denies credential-resolver injection on the engine surface and `Allowlist.IsEngineSurface` excludes
`Tamma.Api`. *Honest scope:* `TAMMA001`'s injection check is a closed denylist not naming
`IDeployControlProvider`, and its HTTP check fires only on a statically-literal external host — it is
the backstop, not the mechanical failure. Precedent: `GetAcceptanceRulesTool` in
`Tamma.Api.Services.AcceptanceRules`; `Allowlist.cs` L57–58 on `InlineToolLoopRunner`.

The **one** engine-side artefact is `WaitForToolOperationActivity` (below), which holds no credential
and makes no vendor call. Only the 42-1 contract types stay in `Tamma.Activities.LlmCall.Tools`.

## Scope

1. **Provider abstraction.** `IDeployControlProvider` — `GetStatusAsync` / `TriggerAsync` /
   `PromoteAsync` / `RollbackAsync` — with one reference driver aligned to the platform's own deploy
   path (Docker Compose on the Hetzner VPS, per CLAUDE.md) plus a generic seam, mirroring the Git/AI
   provider pattern.

2. **Operation granularity — DECIDED: split the family AND report per call.**
   *Corrected: an earlier draft graded classes in prose and left the split to an AC that accepted either
   design. Both halves are now mandatory and separately testable.*

   | Operation | Executor | Per-call `PermissionClass` |
   |---|---|---|
   | `status` | `deploy_status` | `ReadOnly` |
   | `trigger` / `promote` to a **staging** target | `deploy_control` | `Mutating` |
   | `trigger` / `promote` to a **prod** target, and **any** `rollback` | `deploy_control` | `Destructive` |

   - `deploy_status` declares `ReadOnly` / floor 70 and exposes only `status`; it cannot reach a
     mutating provider method.
   - `deploy_control` implements 42-3's per-call seam
     `ToolInvocationFacts Describe(string argumentsJson)` → `{ PermissionClass, Operation, Target }`
     with `Target` = `<targetKey>:<releaseRef>`, so an actor authorizes *this release to this target*,
     not "may deploy".

3. **Target is resolved, never asserted by the model.** The prod/staging discriminator comes from the
   **42-2 binding** (`ConfigJson`: the declared target map), keyed by a `targetKey` the model picks from
   a closed, binding-declared set. An undeclared or missing `targetKey` is **treated as prod** →
   `Destructive`, never as staging.

4. **Prod deploy stays a human decision by *policy*, and this tool never bypasses it.** Epic 39 already
   models "final production-deploy authorization for regulated/breaking changes" as an always-escalate
   acceptance-rules class. This tool *executes* an authorized deploy; it never decides to skip the gate.
   The always-escalate class binds **independently of** the autonomy dial and of 42-3's grant — a
   satisfied 42-3 authorization does not satisfy the Epic 39 class.

5. **Secret binding.** `SecretRequirement(SecretPurpose.ApiKey | SecretPurpose.SigningKey,
   "deploy/<platform>", Required)` — `SecretPurpose` being the `Tamma.Core`-sited enum 42-1 §0
   relocates. Resolved by 42-4 to **`SecretRef.ForTenant(runTenantId, name)` in SaaS** and
   **`SecretRef.ForPlatform(name)` in single-user**. *Corrected: an earlier draft said "user-scoped in
   single-user" — there is no user scope; `SecretScope` has exactly `Platform` and `Tenant` and
   `SecretRef`'s constructor throws on either mismatch. The sole user's ownership is
   `SecretMetadata.OwnerUserId` metadata.* `runTenantId` comes from the run context only.

6. **Long deploys — the executor does NOT suspend; it returns a handle.**
   *Corrected: an earlier draft said "trigger the deploy, suspend the workflow". An `IToolExecutor`
   cannot suspend a workflow: the tool loop runs server-side inside a **blocking**
   `POST /api/v1/llm/call` in `Tamma.Api` (`CallLlmInlineActivity` is a thin client over
   `TammaApiClient`), where there is no `ActivityExecutionContext` and no bookmark to create. The old
   AC — "a long deploy suspends and resumes on completion" — was unverifiable against the executor.*

   The shape, mirroring 42-3's cross-process gate and the landed `WaitForCIResultsActivity`:
   - `deploy_control` **returns promptly** with `Success = true` and an `operationHandle`
     (`{ platform, deploymentId, targetKey, releaseRef }`); it holds no thread past the per-tool timeout.
   - `Suspends = true` on the descriptor is a **declaration that completion is owned by an engine-side
     wait**, not a capability the executor exercises. Read 42-1's `Suspends` that way.
   - The engine-side `WaitForToolOperationActivity` (`Tamma.Activities/ToolExecution/`, generic over
     `{ kind, operationId }`, **shared with 42-7**) suspends on
     `LifecycleBookmarks.ForToolOperation(tenantId, operationId)` with two armed resume paths exactly as
     `WaitForCIResultsActivity` does — the completion callback → `Completed`, and a durable
     scheduled-delay bookmark → `TimedOut` — so a deploy that never reports cannot hang the workflow. It
     must be registered in `LifecycleBookmarks.CanonicalSuspendActivities` or
     `ResumableStandardStructuralTests` rejects any `BookmarkSuspend` workflow that uses it.
   - Any polling of the deploy platform is a `Tamma.Api` concern (platform task / callback endpoint),
     never engine-side.

7. **Audit.** Every op emits 42-5 `TOOL.*` tagged `platform` / `operation` / `targetKey` / `releaseRef`
   / `deploymentId` / terminal `status`. A `promote`-to-prod or `rollback` carries the authorizing actor
   and the 42-3 decision id in its lineage, plus the Epic 39 acceptance decision id when the
   always-escalate class fired.

## Acceptance Criteria

1. Two executors are registered; descriptors read through an **`IToolExecutor`-typed** reference
   (42-1's DIM caveat): `deploy_status` = `ReadOnly` / floor 70 / `Suspends=false`; `deploy_control` =
   `Destructive` (family max) / floor 100 / `Suspends=true`. Both declare
   `SecretRequirement(ApiKey|SigningKey, "deploy/<platform>", Required)`.
2. `deploy_status` cannot mutate: its `InputSchema` enumerates only `status`, and an argument object
   naming `trigger`/`promote`/`rollback` returns `Success = false` with **zero** calls on a spy
   `IDeployControlProvider`.
3. `deploy_control.Describe(argumentsJson)` is table-driven-tested: `trigger`/`promote` to a
   binding-declared staging target → `Mutating`; the same to a binding-declared prod target →
   `Destructive`; `rollback` against **any** target (including staging) → `Destructive`; and each of
   {undeclared `targetKey`, missing `targetKey`, malformed JSON} → `Destructive`. `Target` equals
   `<targetKey>:<releaseRef>`.
4. The model cannot downgrade the class: a test supplies a free-text `"environment": "staging"` field
   alongside a `targetKey` the binding maps to prod, and asserts the class derives from the binding map
   alone.
5. A prod `promote` and a `rollback` each produce **no** provider call and terminate the run with
   `AgentRunFailureCodes.ToolAuthorizationRequired`, the `ToolAuthorizationRequest` carrying the
   operation and the concrete `Target`. Asserted on **both** execution branches —
   `EnableParallelTools = false` (the default) and `true`.
6. Authorization is target-bound and single-use: after an `Authorize` for
   `(session, deploy_control, promote, prod:v1.4.2)`, exactly one matching promote executes; a second
   identical promote re-gates and a promote of `prod:v1.4.3` re-gates.
7. The Epic 39 always-escalate class is **independent** of the 42-3 grant: a prod deploy whose
   acceptance class is always-escalate routes to a human even when autonomy is 100 **and** a valid 42-3
   grant is present — the test asserts the provider is not called until both are satisfied.
8. Provider abstraction holds: a stub `IDeployControlProvider` drives `trigger → status → rollback`
   with the reference driver **not registered**; the executors name no concrete platform type.
9. Credential scoping: SaaS resolves `SecretRef.ForTenant(runTenantId, name)`, single-user resolves
   `SecretRef.ForPlatform(name)`; constructing a `Tenant`-scoped ref with a null tenant id throws; a
   grep-for-value test asserts the credential appears in no `ToolExecutionResult.Output`, no `TOOL.*`
   payload, and no captured log line.
10. Long-op round trip: against a stub slow provider, `trigger` returns inside the per-tool timeout with
    `Success = true` and a non-empty `operationHandle`, and **no** suspend occurs inside
    `POST /api/v1/llm/call`. `WaitForToolOperationActivity` then (a) resumes to `Completed` on the
    callback, (b) resumes to `TimedOut` on its durable delay with no callback, and (c) resumes from the
    **persisted** bookmark after the workflow host is restarted mid-wait. A test asserts the activity
    type is present in `LifecycleBookmarks.CanonicalSuspendActivities`.
11. A driver that throws yields `Success = false` + `TOOL.FAILED`; the test asserts `ExecuteAsync`
    **returns** rather than propagating (never-throw contract).
12. A gated `promote`/`rollback` `TOOL.*` row carries the authorizing actor id, the 42-3 decision id,
    and — when the Epic 39 class fired — the acceptance decision id.

## Events

Reuses 42-5 `TOOL.INVOKED/SUCCEEDED/FAILED` with `platform` / `targetKey` / `releaseRef` /
`deploymentId` / `status` tags. The engine-side wait activity emits its request/completion pair through
`TammaEventEmitter` — it *is* an Elsa activity with a context, the one place in this story where that
emitter applies. No new family.

## Single-user vs SaaS

- **single-user:** the sole user's deploy credential as a **platform-scoped** secret
  (`SecretRef.ForPlatform`) owned via `SecretMetadata.OwnerUserId`; prod authorizations route to the
  single orchestrator/user.
- **SaaS:** tenant-scoped credential and tenant-scoped target map (a tenant drives only its own
  deploys); prod authorizations route to the tenant orchestrator/role, and both the authorization
  bookmark and the operation bookmark are tenant-folded, so a cross-tenant resume cannot resolve
  another tenant's deploy.

## Epic 41 consumers

`deployment-pipeline` (promotion + rollback for infra tasks via 41-29), **41-22** (incident rollback),
**41-24** (release-notes trigger keyed off a promote), 41-29 `infra` `TaskKind`.

## Dependencies

- **42-1** — `ToolDescriptor` + `Suspends`; the `Tamma.Core`-sited `SecretPurpose` (§0). DIM caveats
  apply: read descriptors through an `IToolExecutor`-typed reference; a mocked executor returns **null**.
- **42-2** — the binding's `ConfigJson` carries the declared target map; without it §3's fail-safe
  degrades to "every target is prod".
- **42-3** — `Describe` + stage-2 argument-bound authorization on both branches, the
  `ToolAuthorizationRequired` code, and the engine-side gate.
- **42-4** — credential binding. *Corrected: an earlier draft called this "hard-blocked on the Epic 29
  reveal path". It is not — four runtime plaintext readers already ship and 42-4 generalizes them. The
  residual dependency is a non-null `ISecretAccessAuditor`; only `NullSecretAccessAuditor` is registered
  today.*
- **42-5** — `TOOL.*` audit.
- **42-7 (shared)** — `WaitForToolOperationActivity`, its bookmark prefix, its
  `CanonicalSuspendActivities` registration, and its authenticated callback endpoint. Land once,
  whichever story is first.
- **Epic 39** — the always-escalate acceptance-rules class for prod deploy (policy, reused not rebuilt).
- **`Tamma.Activities` holds no external credential** and carries the `TAMMA001` analyzer; no
  credential-holding code from this story may be added to it.
- **Epic 41 / 41-29** consumers.

## Risks

- **Stage-1 filter vs. max-class descriptor — settled in 42-3, and a hard prerequisite here.**
  `deploy_control`'s max **is** `Destructive`, so a stage-1 filter reading the raw descriptor max
  would never hand it to the agent and stage 2 would never fire. 42-3 Scope 1 now keys stage 1 on the
  **binding-resolved effective ceiling**, with `Destructive` as a stage-2 discriminator and a
  max-class tool with a non-empty ceiling still offered (42-3 AC1b). Treat that as a prerequisite:
  if it is reverted, this family is unreachable.
- **Prod blast radius.** A wrongly-authorized prod deploy is the highest-impact action in the epic.
  Mitigation: target-bound single-use authorization (AC6), the *independent* Epic 39 always-escalate
  class (AC7), and full lineage (AC12).
- **Handle forgery.** The `operationHandle` crosses from the tool result into an engine-side bookmark
  name. It must be minted server-side and tenant-folded; a model-supplied handle must never select a
  bookmark. Pinned in the AC10 round trip.
- **Callback endpoint is external surface.** The completion callback that resumes the wait is a new
  seam and must be authenticated and tenant-scoped like `DocumentDecisionResumeEndpoint` (keyed
  tenant + operation), not deployment-id-only — otherwise anyone who learns a deployment id can resume
  a suspended workflow.
- **Rollback is graded `Destructive` even on staging.** Deliberate: a rollback discards a deployed
  state and is the operation most often reached for under incident pressure. If this proves too coarse,
  the change belongs in `Describe`'s table (AC3), not in a per-call model assertion.

## Estimated Effort

Large. ~5–6 days standalone; **~4 days if 42-7 lands first** and
`WaitForToolOperationActivity` + bookmark + callback endpoint are already in place. Provider
abstraction + reference driver + read/write split + `Describe` + the Epic-39-class interaction test.
**Under the stated wave order** (42-9 → 42-8A → 42-8B → 42-7) this story lands first and therefore
carries the shared wait machinery — budget the standalone ~5–6 days here and the reduced figure for
42-7. The pair pays for that machinery **once**.
