# Story 42-7: Cloud / VPS Resource Operations Tool (provider-abstracted)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As an **agent running an infra, incident, or capacity workflow**, I want a **provider-abstracted
cloud/VPS capability** to list, create, resize, and delete compute resources — with the mutating
operations authorized against their **concrete target** and bound to the provider credential — so that
an `infra` task or a `41-22` rollback can actually touch the VPS instead of shelling out unbound.

## Priority

P2 / Wave 3 — a tool family on the Wave-1 rails. Sequenced after 42-9/42-8B by Epic 41 demand (fewer 41
workflows need raw cloud ops than need HTTP/flags), but it is the family that finally replaces
`ShellExecute`-as-a-deploy-substitute for real infrastructure.

## Where this code lives (binding)

**Both executors, `ICloudResourceProvider`, and every driver live in `Tamma.Api`** — package
`Tamma.Api.Services.Tools.Cloud`, registered next to the six built-ins at `Tamma.Api/Program.cs`
L753–766. Nothing in this story is added to `Tamma.Activities`.

Three independent reasons, in force order:

1. **Rule 1** — a workflow step never calls an external API directly or holds an external credential.
   These executors do both.
2. **Runtime** — the engine no longer hosts the tool catalog at all. `Tamma.ElsaServer/Program.cs`
   L286–292 records that `IToolExecutor*` + `IToolExecutorRegistry` were *removed* from the engine and
   "the tool executors are registered there [`Tamma.Api`], not here." Api-side is where the DI wiring
   already is; an engine-side executor would never be resolved.
3. **Guardrail backstop** — `TAMMA001` (`Tamma.Activities.Guardrails`, `DiagnosticSeverity.Error`,
   wired into `Tamma.Activities`/`Tamma.ElsaServer` as an analyzer project reference) denies
   credential-resolver injection on the engine surface; `Allowlist.IsEngineSurface` deliberately
   excludes `Tamma.Api`. *Honest scope:* `TAMMA001`'s injection check is a closed denylist that does
   not currently name `ICloudResourceProvider`, and its HTTP check only fires on a statically-literal
   external host — so it is the **backstop**, not the thing that would mechanically fail this build.
   Siting is settled by (1) and (2).

Precedent for exactly this split: `GetAcceptanceRulesTool` is an `IToolExecutor` living in
`Tamma.Api.Services.AcceptanceRules`, and `Allowlist.cs` L57–58 notes `InlineToolLoopRunner` "now lives
in the `Tamma.Api` assembly, outside the analyzed engine surface, so no engine exemption is needed."
`ITenantInfrastructureProvider` + `TenantProviderRegistry` (`Tamma.Api/Services/Provisioning/V2/`) is
the in-repo template for a **pluggable external-infrastructure driver seam sited in `Tamma.Api`** — it
already names Hetzner Cloud as a prospective backend.

Only the 42-1 contract types the executors implement (`IToolExecutor`, `ToolDescriptor`,
`SecretRequirement`) stay in `Tamma.Activities.LlmCall.Tools`. A `ToolDescriptor` never carries a
`SecretRef` — only the logical requirement (42-4).

## Scope

1. **A provider abstraction, mirroring the Git/AI provider pattern.** Define `ICloudResourceProvider`
   (list / describe / create / resize / delete) and one reference driver — **Hetzner** (the platform's
   own VPS host, per CLAUDE.md) — plus a **generic** driver seam so other providers grow the way the 7
   Git platforms / 8 AI providers did. The executors dispatch to the configured provider; the LLM sees
   an `operation` + `resource` schema, never per-provider tools.

2. **Operation granularity — DECIDED: split the family AND report per call.**
   *Corrected: an earlier draft offered "operation-scoped descriptors **or** a read/write split", with
   an acceptance criterion that accepted whichever got built. That AC could not fail; both halves are
   now mandatory and separately testable.*

   | Operation | Executor | Per-call `PermissionClass` | Notes |
   |---|---|---|---|
   | `list` / `describe` | `cloud_resource_read` | `ReadOnly` | safe at autonomy 70 |
   | `create` | `cloud_resource_write` | `Mutating` | reversible (delete undoes it) |
   | `resize` | `cloud_resource_write` | `Destructive` | downtime / data movement |
   | `delete` | `cloud_resource_write` | `Destructive` | irreversible |

   - **Two registered executors, not one.** `cloud_resource_read` declares `ReadOnly` / floor 70 and
     **physically cannot** reach a mutating provider method — it exposes only `list`/`describe` in its
     `InputSchema` and rejects any other `operation`. This is what makes a read capability genuinely
     free at autonomy 70 no matter how the write half is gated.
   - **The split is not sufficient on its own**, which is why it is not the whole decision: one grant of
     `cloud_resource_write` still covers *delete any resource*. `cloud_resource_write` therefore
     implements 42-3 Scope 2's per-call seam — `ToolInvocationFacts Describe(string argumentsJson)`
     returning `{ PermissionClass, Operation, Target }` — with `Target` = `<provider>:<resourceId>`
     (for `create`, the requested `<provider>:<resourceType>/<name>`). 42-3 stage 2 authorizes **that
     action against that target**, single-use.
   - Unparseable / unknown-operation arguments return the fail-safe facts
     (`Destructive`, `Operation = ToolName`, `Target = null`) — deny-by-default, never permissive.

3. **Secret binding.** `SecretRequirement(SecretPurpose.ApiKey, "cloud/<provider>-token", Required)` —
   `SecretPurpose` being the `Tamma.Core`-sited enum 42-1 §0 relocates (Epic 29's own enum is in
   `Tamma.Api.Services.Secrets` and is unreachable from the contract assembly). Resolved by 42-4 to
   **`SecretRef.ForTenant(runTenantId, name)` in SaaS** and **`SecretRef.ForPlatform(name)` in
   single-user**. *Corrected: an earlier draft said "user-scoped in single-user" — there is no user
   scope. `SecretScope` has exactly `Platform` and `Tenant`, and `SecretRef`'s constructor throws on
   either mismatch; the sole user's ownership is carried by `SecretMetadata.OwnerUserId`, metadata not
   scope.* The token never reaches logs/events/output (42-4/42-5), and `runTenantId` comes from the run
   context — never from tool config, tool arguments, or the model.

4. **Long ops — the executor does NOT suspend; it returns a handle.**
   *Corrected: an earlier draft said "the tool starts the op and the workflow suspends". An
   `IToolExecutor` cannot suspend a workflow: the tool loop runs server-side inside a **blocking**
   `POST /api/v1/llm/call` in `Tamma.Api` (`CallLlmInlineActivity` is a thin client over
   `TammaApiClient`), where there is no `ActivityExecutionContext` and no bookmark to create. The old
   AC ("no blocked worker thread, resumable across a crash") was unverifiable against the executor.*

   The shape, mirroring 42-3's cross-process gate and the landed `WaitForCIResultsActivity`:
   - `cloud_resource_write` **returns promptly** with `Success = true` and an `operationHandle`
     (`{ provider, operationId, resourceId?, pollUrlKey? }`) — the executor holds no thread and no
     socket past the per-tool timeout.
   - `Suspends = true` on the descriptor is a **declaration that completion is owned by an engine-side
     wait**, not a capability the executor exercises. 42-1's wording must be read that way.
   - A new engine-side `WaitForToolOperationActivity` (`Tamma.Activities/ToolExecution/`, no credential
     — it resumes on a callback, it does not poll the vendor) suspends on
     `LifecycleBookmarks.ForToolOperation(tenantId, operationId)` with **two armed resume paths**,
     exactly as `WaitForCIResultsActivity` does: the completion callback → `Completed`, and a durable
     scheduled-delay bookmark → `TimedOut`, so the workflow can never hang. It must be registered in
     `LifecycleBookmarks.CanonicalSuspendActivities` or `ResumableStandardStructuralTests` rejects any
     `BookmarkSuspend` workflow that uses it. The activity is **generic over the operation kind**
     (`{ kind, operationId }`) and is **shared with 42-8B** (long deploys) — whichever story lands
     first ships it; the second reuses it and adds only its `kind`.
   - Any *polling* of the provider is a `Tamma.Api` concern (a platform task or the callback endpoint),
     never engine-side — same rule-1 reason as the executor itself.

5. **Audit.** Every op emits 42-5 `TOOL.*` with `provider` / `operation` / `resourceId` /
   `operationHandle` tags (never the token). A `resize`/`delete` carries the authorizing actor and the
   42-3 decision id in its lineage.

## Acceptance Criteria

1. Two executors are registered with the descriptors read through an **`IToolExecutor`-typed**
   reference (42-1's DIM caveat): `cloud_resource_read` = `ReadOnly` / floor 70 / `Suspends=false`;
   `cloud_resource_write` = `Destructive` (family max) / floor 100 / `Suspends=true`. Both declare
   `SecretRequirement(ApiKey, "cloud/<provider>-token", Required)`.
2. `cloud_resource_read` cannot mutate: its published `InputSchema` enumerates only `list`/`describe`,
   and an argument object naming `create`/`resize`/`delete` returns `Success = false` with **zero**
   calls on a spy `ICloudResourceProvider` (asserted on the spy, not on the message).
3. `cloud_resource_write.Describe(argumentsJson)` is table-driven-tested: `create`→`Mutating`,
   `resize`→`Destructive`, `delete`→`Destructive`; `Target` equals `<provider>:<resourceId>` for
   resize/delete and `<provider>:<resourceType>/<name>` for create; malformed JSON, a missing
   `operation`, and an unknown `operation` each return `(Destructive, Operation = "cloud_resource_write",
   Target = null)`.
4. A `delete` produces **no** `ICloudResourceProvider.DeleteAsync` call and terminates the run with
   `AgentRunFailureCodes.ToolAuthorizationRequired`; the emitted `ToolAuthorizationRequest` carries
   `Operation = "delete"` and the concrete `Target`. Asserted on **both** execution branches — once
   with `EnableParallelTools = false` (the default) and once `true`.
5. Authorization is target-bound: after an `Authorize` for `(session, cloud_resource_write, delete,
   <provider>:srv-A)`, exactly one delete of `srv-A` executes; a second delete of `srv-A` re-gates, and
   a delete of `srv-B` re-gates.
6. Provider abstraction holds: a stub `ICloudResourceProvider` drives `create → describe → delete` with
   the Hetzner driver **not registered**, and the test passes — the executors name no Hetzner type.
7. Credential scoping: SaaS resolves `SecretRef.ForTenant(runTenantId, name)` and single-user resolves
   `SecretRef.ForPlatform(name)`; a test asserts constructing a `Tenant`-scoped ref with a null tenant
   id throws, and a grep-for-value test asserts the token string appears in no
   `ToolExecutionResult.Output`, no `TOOL.*` event payload, and no captured log line.
8. Long-op round trip: against a stub async provider, `create` returns inside the per-tool timeout with
   `Success = true` and a non-empty `operationHandle`, and **no** suspend occurs inside
   `POST /api/v1/llm/call`. `WaitForToolOperationActivity` then (a) resumes to `Completed` on the
   callback, (b) resumes to `TimedOut` on its durable delay with no callback, and (c) resumes from the
   **persisted** bookmark after the workflow host is restarted mid-wait. A test asserts the activity
   type is present in `LifecycleBookmarks.CanonicalSuspendActivities`.
9. A `resize`/`delete` `TOOL.*` row carries the authorizing actor id and the 42-3 decision id.
10. A driver that throws (transport, 4xx, 5xx) yields `Success = false` + `TOOL.FAILED`; the test
    asserts `ExecuteAsync` **returns** rather than propagating — the never-throw contract holds.

## Events

Reuses 42-5 `TOOL.INVOKED/SUCCEEDED/FAILED` with cloud tags. The engine-side wait activity emits its
request/completion pair through `TammaEventEmitter` (it *is* an Elsa activity with a context — the one
place in this story where that emitter applies). No new family.

## Single-user vs SaaS

- **single-user:** the sole user's cloud token, stored as a **platform-scoped** secret
  (`SecretRef.ForPlatform`) owned via `SecretMetadata.OwnerUserId`; authorization of a gated op routes
  to the single orchestrator/user.
- **SaaS:** tenant-scoped token (`SecretRef.ForTenant(runTenantId, …)`); gated ops route to the tenant
  orchestrator/role. The authorization bookmark is tenant-folded, so a tenant's cloud operations and
  credentials never cross the boundary.

## Epic 41 consumers

`deployment-pipeline` (infra tasks dispatched by 41-29), **41-22** (incident response / rollback —
recreate/replace a node), **41-23** (capacity & health review — `cloud_resource_read` only).

## Dependencies

- **42-1** — `ToolDescriptor` + `Suspends`; the `Tamma.Core`-sited `SecretPurpose` (§0). Note the DIM
  caveats: descriptors must be read through an `IToolExecutor`-typed reference, and a mocked executor
  returns a **null** descriptor.
- **42-3** — the per-call `ToolInvocationFacts Describe(...)` seam and stage-2 argument-bound
  authorization (both execution branches), plus the `ToolAuthorizationRequired` failure code and the
  engine-side gate. This family is inert-but-safe until 42-3 lands.
- **42-4** — token binding. *Corrected: an earlier draft called this "hard-blocked on the Epic 29
  reveal path". It is not. Four runtime plaintext readers already ship (`SecretStorePlatformCredentialReader`
  is audited and scope-generic); 42-4 generalizes them. The residual dependency is a non-null
  `ISecretAccessAuditor` — only `NullSecretAccessAuditor` is registered today, so audited-read
  assertions land nowhere until Epic 29 swaps it.*
- **42-5** — `TOOL.*` audit (direct `IEventRepository` append in `Tamma.Api`).
- **`Tamma.Activities` holds no external credential** and carries the `TAMMA001` analyzer; no
  credential-holding code from this story may be added to it. The only new engine-side artefact is
  `WaitForToolOperationActivity`, which holds no credential and makes no vendor call — and it is
  **shared with 42-8B**; land it once.
- **Epic 41 / 41-29** (`infra` `TaskKind` dispatch → `deployment-pipeline`) as the consumer.

## Risks

- **Stage-1 filter vs. max-class descriptor — settled in 42-3, but this family depends on it.**
  This family's write half advertises `Destructive` as its **maximum**, so a stage-1 filter reading
  the raw descriptor max would never hand `cloud_resource_write` to the agent: the model emits no
  call, stage 2 never fires, and "route the action to an actor" degrades to "the capability is
  unreachable". 42-3 Scope 1 now decides this — stage 1 keys on the **binding-resolved effective
  ceiling**, `Destructive` is a stage-2 discriminator, and a max-class tool with a non-empty ceiling
  **is** offered (42-3 AC1b). If that decision is ever reverted, this family is dead on arrival;
  treat 42-3 AC1b as a hard prerequisite of this story.
- **Irreversible ops.** A wrongly-authorized `delete` is unrecoverable. Mitigation: target-bound,
  single-use authorization (AC5) + full lineage (AC9). Consider an always-escalate acceptance-rules
  class for `delete` regardless of autonomy — Epic 39 policy, not tool code.
- **Handle forgery.** The `operationHandle` crosses from the tool result into an engine-side bookmark
  name. It must be minted server-side and tenant-folded; a model-supplied handle must never select a
  bookmark. Pin in the AC8 round trip.
- **Callback endpoint is external surface.** The completion callback that resumes the wait is a new
  unauthenticated-by-default seam. It must be authenticated and tenant-scoped like
  `DocumentDecisionResumeEndpoint` (keyed tenant + operation), not session-id-only.

## Estimated Effort

Large. ~6–8 days **standalone**; **~4–5 days if 42-8B lands first** and `WaitForToolOperationActivity`
+ its bookmark prefix + `CanonicalSuspendActivities` registration + the authenticated callback endpoint
are already in place — provider abstraction + Hetzner driver + the read/write split + `Describe` remain
either way. *Corrected upward from ~5–6 days: the earlier estimate assumed the executor could suspend,
which it cannot; the wait activity and its resume path are new work.* **Note the wave order** (42-9 →
42-8A → 42-8B → 42-7): under it 42-8B ships the shared wait machinery, so this story's realistic figure
is the lower one and 42-8B's is its standalone ~5–6 days. The pair costs the wait activity **once** —
do not budget both stories' upper figures.
