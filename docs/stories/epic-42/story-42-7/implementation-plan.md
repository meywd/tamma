# Implementation Plan — Story 42-7: Cloud / VPS Resource Operations Tool

## Reconciled scope — differs from the story file

**Epic 42 was reconciled against Epic 43 on 2026-07-25.** The verdict for 42-7 (with 42-8A / 42-8B / 42-9)
is **"Gating sections stripped. They declare capability and secrets; the catalog governs them."** The
deltas:

| Story file says | Reconciled |
|---|---|
| §2's per-call `PermissionClass` column and the `ToolInvocationFacts Describe(string argumentsJson)` seam | **STRIPPED.** `Describe` was 42-3's seam; 42-3 is deleted and nothing consumes `ToolInvocationFacts`. Epic 43's **Seam B** gates one call site in the shared tool-loop path, keyed on an `ActionKey` derived from the tool name — not on per-call facts. `ToolPermissionClass` no longer exists (42-1 is rewritten; `ToolDescriptor` is now `(RequiredSecret, Suspends)`). |
| §2's **read/write executor split** (`cloud_resource_read` cannot reach a mutating provider method) | **KEPT — it is a capability boundary, not a gate.** Two registered executors, with the read half physically unable to mutate, is the part of §2 that survives and is arguably the more valuable half: it holds no matter how the catalog is configured. |
| AC1's `ReadOnly`/floor 70 and `Destructive`/floor 100 descriptor values | **STRIPPED** — no class, no floor. AC1 becomes: two executors registered, each declaring `SecretRequirement(ApiKey, "cloud/<provider>-token", Required)`; `cloud_resource_read` `Suspends = false`, `cloud_resource_write` `Suspends = true`. |
| AC3 (`Describe` table-driven test), AC4 (`ToolAuthorizationRequired` + `ToolAuthorizationRequest`), AC5 (target-bound single-use authorization), AC9 (the 42-3 decision id in the `TOOL.*` row) | **STRIPPED.** All four are 42-3 machinery. AC9's *lineage* intent survives in reduced form: the `TOOL.*` row carries `provider`/`operation`/`resourceId`/`operationHandle` (42-5's tags), and any authorizing-actor id is Epic 43's to add in its own event family. |
| The "Stage-1 filter vs. max-class descriptor" risk | **Gone.** It was a risk about 42-3's stage-1 filter. Epic 43 records the same insight (transplanted into its Seam B analysis, with credit) and resolves it there. |
| §3's secret binding, §4's handle-not-suspend design, §5's audit, §1's provider abstraction | **All unchanged.** These are the capability, and they are the story. |

**Net:** 42-7 becomes a *capability* story — provider abstraction, one reference driver, the read/write
split, secret binding, the engine-side wait, and audit. Governance is declared by an Epic 43 catalog entry
and enforced at Seam B, with **zero bespoke code here**.

## Scope & Deliverable

Two `IToolExecutor`s in `Tamma.Api` — `cloud_resource_read` (`list`/`describe`) and `cloud_resource_write`
(`create`/`resize`/`delete`) — dispatching to an `ICloudResourceProvider` abstraction with one reference
driver (Hetzner) and a generic seam. The read executor's `InputSchema` enumerates only the read operations
and it cannot reach a mutating provider method. Both bind their provider token through 42-4
(`SecretRef.ForTenant(runTenantId, …)` in SaaS, `SecretRef.ForPlatform(…)` in single-user) and never leak
it. `cloud_resource_write` returns promptly with an `operationHandle` and declares `Suspends = true`;
completion is owned by a new engine-side `WaitForToolOperationActivity` with two armed resume paths.
Every operation emits 42-5's `TOOL.*` trio with cloud tags.

## Pre-Reading

- `docs/stories/epic-42/story-42-7/42-7-cloud-vps-resource-tool.md` — the story (**read the Reconciled scope table first**; §1, §3, §4, §5 and the "Where this code lives" section survive verbatim)
- `docs/stories/epic-42/README.md` — the verdicts; "Where the code lives"; the tool-families table
- `docs/stories/epic-43/README.md` — Seam B, and §1's `ActionNamespace.Tool`
- `docs/stories/epic-42/story-42-1/implementation-plan.md` (D2 `ToolDescriptor(RequiredSecret, Suspends)`, D3 the `Suspends` wording, D8 siting), `story-42-4/implementation-plan.md` (D2/D3/D8 — the provider, fail-closed resolution, redaction split), `story-42-5/implementation-plan.md` (D2/D3 — hook points and tags)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutor.cs` — the never-throw contract `:8`, `:33`; `ToolExecutionResult` at `LlmCallModels.cs:464-474`
- **`apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs`** — `Compose` `:38-48`, `ForStageGate` `:55`, `ForDecisionSession` `:66`, `ForDocumentInput` `:82`, and **`CanonicalSuspendActivities` `:98-105` — exactly TWO entries today** (`WaitForDocumentDecisionActivity` → `document-decision`, `WaitForDocumentInputActivity` → `document-input`)
- **`apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs:158-198`** (clause b) and `:202-236` (clause b-inverse) — the tests that make the `CanonicalSuspendActivities` registration mandatory rather than optional
- **`apps/tamma-elsa/src/Tamma.Activities/Testing/WaitForCIResultsActivity.cs`** — the landed two-armed-resume precedent this story copies (verify its exact bookmark/timeout shape at implementation)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/WaitForDocumentDecisionActivity.cs` + `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/DocumentDecisionResumeEndpoint.cs` — the tenant-folded, authenticated resume-endpoint posture the callback must match
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/` — `ITenantInfrastructureProvider` + `TenantProviderRegistry`, the in-repo template for a pluggable external-infrastructure driver seam sited in `Tamma.Api` (it already names Hetzner Cloud as a prospective backend)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:753-766` — where the executors register; `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:286-292` — the catalog was removed from the engine
- `apps/tamma-elsa/src/Tamma.Activities.Guardrails/Allowlist.cs:16-17`, `:45-64` (13 entries; `IProviderCredentialResolver` at `:59`), `:57-58`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:500` — `EnableParallelTools` defaults **false**

## Corrections to the story

- **W1 — `LifecycleBookmarks.ForToolOperation` does not exist; this story creates it.** The story references
  it as though it were available. Verified: `LifecycleBookmarks` exposes `Compose` (`:38-48`), `ForStageGate`
  (`:55`), `ForDecisionSession` (`:66`) and `ForDocumentInput` (`:82`) — no tool-operation builder. Adding one
  is a small, well-precedented change (compose through the same tenant-folding core), but it is **work in
  this story**, not a given.
- **W2 — `CanonicalSuspendActivities` has exactly two entries, and registration is enforced by a build
  gate.** `:98-105`. `ResumableStandardStructuralTests` clause (b) (`:158-198`) fails any
  `BookmarkSuspend`/`Both` workflow whose declared suspend activity is not in that dictionary, and clause
  (b-inverse) (`:202-236`) fails any workflow whose graph contains a canonical suspend node it did not
  declare. So the story's *"must be registered … or `ResumableStandardStructuralTests` rejects any
  `BookmarkSuspend` workflow that uses it"* is exactly right — and it also means the registration must land
  **with** the activity, in the same commit, or the first consuming workflow fails the build.
- **W3 — the effort note's wave-order arithmetic is now the reverse of what the story says.** 42-7 says
  *"under the stated wave order (42-9 → 42-8A → 42-8B → 42-7) 42-8B ships the shared wait machinery, so this
  story's realistic figure is the lower one."* That remains true **only if the wave order holds**. Post-
  reconciliation 42-9 is materially harder than it looked (its entire configuration source was deleted with
  42-2 — see that plan), so the order may change. **Both figures are stated below and the plan does not
  assume which story pays.**
- **W4 — `ICloudResourceProvider` is not on `TAMMA001`'s injection denylist, and would not trip it.** The
  story says this ("*Honest scope*") and it is verified: the denylist is a closed 13-entry list
  (`Allowlist.cs:45-64`) naming no cloud type, and the HTTP check fires only on a statically-resolvable
  literal external host — a cloud API endpoint is config-supplied. Siting is settled by rule 1 and by the
  runtime fact that the engine hosts no tool catalog; **the analyzer is a backstop, not the enforcement.**
  Stated so no one treats a green build as proof of correct siting.

## Design Decisions

- **D1 — everything except the wait activity lives in `Tamma.Api`,** package
  `Tamma.Api.Services.Tools.Cloud`, registered next to the six built-ins at `Program.cs:753-766`. Reasons in
  force order: rule 1 (a workflow step never calls an external API directly or holds an external credential —
  these executors do both); runtime (`Tamma.ElsaServer/Program.cs:286-292` records the catalog was *removed*
  from the engine, so an engine-side executor would never be resolved); `TAMMA001` as backstop (W4).
  Precedent: `GetAcceptanceRulesTool` is an `IToolExecutor` in `Tamma.Api.Services.AcceptanceRules`, and
  `Allowlist.cs:57-58` notes `InlineToolLoopRunner` *"now lives in the `Tamma.Api` assembly, outside the
  analyzed engine surface."*
- **D2 — the read/write split survives the reconciliation and is now the story's primary safety property.**
  With per-call classification gone, the *only* structural guarantee that a read capability cannot mutate is
  that the read executor has no code path to a mutating provider method. `cloud_resource_read` publishes an
  `InputSchema` enumerating only `list`/`describe` and rejects any other `operation` with `Success = false`
  — asserted against a **spy provider at zero calls**, not against the message text. This is worth more
  post-reconciliation than before, because it does not depend on any catalog row being configured correctly.
- **D3 — the operation key is drawn from a closed, config-declared set; an undeclared key is refused.** What
  survives of §2's "resolved, never asserted by the model" principle, restated as capability containment
  rather than classification: the provider and its resource types come from configuration; an unparseable
  argument object, a missing `operation`, or an unknown `operation` is a **fail-closed
  `ToolExecutionResult { Success = false }`** with zero provider calls — never a best-effort guess. No
  `PermissionClass` is computed, because nothing consumes one.
- **D4 — secret binding is descriptor-declared and provider-resolved.**
  `SecretRequirement(SecretPurpose.ApiKey, "cloud/<provider>-token", Required)` — `SecretPurpose` being the
  `Tamma.Core`-sited enum 42-1 §0 relocates. Resolved by 42-4's `IToolSecretProvider`, which **constructs**
  the ref from the run's tenant identity: `SecretRef.ForTenant(runTenantId, name)` in SaaS,
  `SecretRef.ForPlatform(name)` in single-user. There is no user scope — `SecretScope` has exactly `Platform`
  and `Tenant` and `SecretRef`'s ctor throws on either mismatch; the sole user's ownership is
  `SecretMetadata.OwnerUserId`, metadata not scope. `runTenantId` comes from the run context, never from tool
  config, tool arguments, or the model. **Note the post-reconciliation gap (42-4 G1):** with 42-2 deleted
  there is no per-principal override of the logical secret *name*, so `"cloud/<provider>-token"` is whatever
  the descriptor hardcodes. Workable here — one provider, one token per scope — and recorded rather than
  papered over.
- **D5 — the executor returns a handle; it does not suspend, and it cannot.** The tool loop runs server-side
  inside a **blocking** `POST /api/v1/llm/call` in `Tamma.Api` (`CallLlmInlineActivity` is a thin client over
  `TammaApiClient`), where there is no `ActivityExecutionContext` and no bookmark to create — and
  `TammaEventEmitter`, the only in-engine emit path, structurally requires both an `ActivityExecutionContext`
  and an `IActivity` (`Tamma.Activities/Core/TammaActivity.cs:82-147`). So `cloud_resource_write` returns
  promptly with `Success = true` and an `operationHandle` (`{ provider, operationId, resourceId?,
  pollUrlKey? }`), holding no thread or socket past the per-tool timeout, and `Suspends = true` on the
  descriptor is a **declaration that completion is owned by an engine-side wait** (42-1 D3's wording,
  verbatim).
- **D6 — `WaitForToolOperationActivity` is the epic's ONLY engine-side artefact, is generic, and is shared
  with 42-8B.** In `Tamma.Activities/ToolExecution/`, generic over `{ kind, operationId }`, **credential-free
  and making no vendor call** — it resumes on a callback, it does not poll. Two armed resume paths exactly as
  `WaitForCIResultsActivity` does: the completion callback → `Completed`, and a durable scheduled-delay
  bookmark → `TimedOut`, so a workflow can never hang. It suspends on a new
  `LifecycleBookmarks.ForToolOperation(tenantId, operationId)` (W1: **this story writes it**, composing
  through the same tenant-folding core as the other three builders), and the activity type **must** be added
  to `LifecycleBookmarks.CanonicalSuspendActivities` in the same commit (W2), or the first workflow declaring
  it fails clause (b) of the 39-10 build gate. Any *polling* of the provider is a `Tamma.Api` concern — a
  platform task or the callback endpoint — for the same rule-1 reason as the executor.
- **D7 — the handle is minted server-side and the callback endpoint is authenticated and tenant-folded.**
  The `operationHandle` crosses from a tool result into an engine-side bookmark name, so a model-supplied
  handle must never select a bookmark: the executor mints the `operationId` itself and the bookmark is
  tenant-folded. The completion callback is a **new external surface** and must be authenticated and keyed
  tenant + operation, matching `DocumentDecisionResumeEndpoint`'s posture — not operation-id-only, or anyone
  who learns an id can resume a suspended workflow.
- **D8 — audit is 42-5's trio with cloud tags; no new family, and no decision id.** `provider`, `operation`,
  `resourceId`, `operationHandle` — **never the token**. The story's AC9 ("carries the 42-3 decision id") is
  stripped; if Epic 43's gate denies or escalates a cloud operation, that record belongs to **Epic 43's**
  event family, correlated to these rows by `toolCallId`/`correlationId`. The wait activity emits its
  request/completion pair through `TammaEventEmitter` — it *is* an Elsa activity with a context, the one
  place in this story where that emitter applies.
- **D9 — the provider abstraction names no vendor type in the executors.** `ICloudResourceProvider`
  (list/describe/create/resize/delete) plus a Hetzner reference driver and a generic seam, mirroring the Git
  and AI provider abstractions and following `ITenantInfrastructureProvider`/`TenantProviderRegistry` as the
  in-repo template for an external-infrastructure driver seam in `Tamma.Api`. The LLM sees an
  `operation` + `resource` schema, never per-provider tools.

## Implementation Steps

1. **Precondition gate.** 42-1 (descriptor + `Suspends` + the relocated `SecretPurpose`), 42-4
   (`IToolSecretProvider`), 42-5 (`TOOL.*`) landed. Check whether 42-8B has already shipped
   `WaitForToolOperationActivity` + `ForToolOperation` + the `CanonicalSuspendActivities` entry + the callback
   endpoint — if so, steps 4–5 collapse to adding this story's operation `kind`.
2. **CREATE `Tamma.Api/Services/Tools/Cloud/ICloudResourceProvider.cs` + `HetznerCloudResourceProvider.cs`**
   (D9) and a generic driver seam.
3. **CREATE `.../Cloud/CloudResourceReadTool.cs` + `CloudResourceWriteTool.cs`** (D2/D3/D4/D5) — descriptors,
   schemas, fail-closed argument handling, secret resolution immediately before the vendor call, `Success =
   false` on every driver throw (never-throw contract), by-value credential scrubbing at the `ExecuteAsync`
   boundary (42-4 D8).
4. **CREATE `Tamma.Activities/ToolExecution/WaitForToolOperationActivity.cs`** and **MODIFY
   `Tamma.Activities/Documents/LifecycleBookmarks.cs`** — `ForToolOperation` (W1) **and** the
   `CanonicalSuspendActivities` entry (W2), same commit.
5. **CREATE the authenticated, tenant-scoped completion callback endpoint** in `Tamma.ElsaServer/Endpoints/`
   (the `DocumentDecisionResumeEndpoint` shape), plus the Api-side polling seam if the driver needs one (D6).
6. **MODIFY `Tamma.Api/Program.cs:753-766`** — register both executors and the provider registry.
7. **CREATE the test suites** (Test Plan). Author the Epic 43 catalog entries for `tool:cloud_resource_read`
   and `tool:cloud_resource_write` as **admin data**, not code.
8. **Finish:** full `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean; confirm the only
   new engine-side files are the wait activity and the bookmark builder.

## Data & Migrations

None. Secrets are Epic 29's `secrets` table; events ride `IEventRepository` → `domain_events`; the bookmark
is Elsa's existing persistence.

## Events

Reuses 42-5's `TOOL.INVOKED`/`SUCCEEDED`/`FAILED` with `provider` / `operation` / `resourceId` /
`operationHandle` tags (never the token). The wait activity emits its request/completion pair through
`TammaEventEmitter`. **No new family.** Epic 43 owns any gate-decision event.

## Test Plan

- **`CloudToolDescriptorTests`** — both executors registered; descriptors read through an
  **`IToolExecutor`-typed** reference declare `SecretRequirement(ApiKey, "cloud/<provider>-token", Required)`;
  `cloud_resource_read.Suspends == false`, `cloud_resource_write.Suspends == true`. **Covers reconciled AC1.**
- **`CloudReadCannotMutateTests`** (D2) — the published `InputSchema` enumerates only `list`/`describe`; an
  argument object naming `create`/`resize`/`delete` returns `Success = false` with **zero** calls on a spy
  `ICloudResourceProvider` — asserted on the spy, not the message. **Covers AC2.**
- **`CloudArgumentFailClosedTests`** (D3) — malformed JSON, a missing `operation`, and an unknown `operation`
  each yield `Success = false` with zero provider calls. *(This replaces the stripped AC3 `Describe` table
  with the containment property that survives.)*
- **`CloudProviderAbstractionTests`** (D9) — a stub `ICloudResourceProvider` drives `create → describe →
  delete` with the Hetzner driver **not registered**, and the executors name no Hetzner type (a reflection
  assertion over their referenced types). **Covers AC6.**
- **`CloudCredentialScopingTests`** (D4) — SaaS resolves `SecretRef.ForTenant(runTenantId, name)`,
  single-user `SecretRef.ForPlatform(name)`; constructing a `Tenant`-scoped ref with a null tenant id throws;
  a grep-for-value test with a pattern-non-matching token asserts it appears in no
  `ToolExecutionResult.Output`, no `TOOL.*` payload and no captured log line. **Covers AC7.**
- **`CloudNeverThrowTests`** — a driver that throws on transport, 4xx and 5xx yields `Success = false` +
  `TOOL.FAILED`; the test asserts `ExecuteAsync` **returns** rather than propagating. Run on **both**
  execution branches (`EnableParallelTools` `false` — the default — and `true`). **Covers AC10.**
- **`ToolOperationWaitTests`** (D6/D7, Testcontainers) — against a stub async provider, `create` returns
  inside the per-tool timeout with `Success = true` and a non-empty `operationHandle`, and **no** suspend
  occurs inside `POST /api/v1/llm/call`; then `WaitForToolOperationActivity` (a) resumes to `Completed` on
  the callback, (b) resumes to `TimedOut` on its durable delay with no callback, (c) resumes from the
  **persisted** bookmark after the host is restarted mid-wait; a model-supplied handle selects no bookmark;
  a cross-tenant callback does not resolve. Plus a test asserting the activity type is present in
  `LifecycleBookmarks.CanonicalSuspendActivities` and that `ResumableStandardStructuralTests` stays green.
  **Covers AC8.**

## Definition of Done

| AC (reconciled) | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — two executors, secret requirement, `Suspends` values | 3, 6 | `CloudToolDescriptorTests` |
| 2 — read executor physically cannot mutate | 3 (D2) | `CloudReadCannotMutateTests` (spy at zero calls) |
| 6 — provider abstraction holds; no vendor type in the executors | 2, 3 (D9) | `CloudProviderAbstractionTests` |
| 7 — credential scoping; nothing leaks | 3 (D4) | `CloudCredentialScopingTests` |
| 8 — long-op round trip, both resume arms, restart-durable, handle not forgeable | 4, 5 (D6/D7) | `ToolOperationWaitTests` |
| 10 — never-throw on driver failure, both branches | 3 | `CloudNeverThrowTests` |
| — argument fail-closed | 3 (D3) | `CloudArgumentFailClosedTests` |
| ~~3 (`Describe`), 4 (`ToolAuthorizationRequired`), 5 (target-bound single-use), 9 (decision id)~~ | — | **STRIPPED — Epic 43 governs; see Reconciled scope** |

## Blocks / Blocked by

- **Blocked by — 42-1** (`ToolDescriptor` + `Suspends` + the `Tamma.Core`-sited `SecretPurpose`), **42-4**
  (token binding), **42-5** (`TOOL.*`). All hard.
- **Blocked by — Epic 43** for governance, but **not for shipping.** The capability is complete and safe
  without it: the read/write split holds unconditionally, and the write half's operations are auditable from
  day one. What is missing without a catalog entry is the *policy* that decides whether an agent may call the
  write half at a given autonomy — which is exactly the division of labour the reconciliation intends. Author
  the two catalog rows as admin data when Epic 43 Stories 3/5 land.
- **Shares with 42-8B — `WaitForToolOperationActivity`, `LifecycleBookmarks.ForToolOperation`, its
  `CanonicalSuspendActivities` entry, and the authenticated callback endpoint. Land them ONCE.** Whichever
  story goes first ships all four; the second adds only its operation `kind`. Do not budget both stories'
  upper figures (W3).
- **Blocked by — nothing engine-side beyond 39-10**, which is landed (`ResumeBehaviorAttribute`,
  `LifecycleBookmarks`, `ResumableStandardStructuralTests` all verified in tree).
- **Blocks — Epic 41 consumers:** `deployment-pipeline` (its own post-merge stages), **41-22** (incident
  response / rollback — recreate or replace a node), **41-23** (capacity & health review, `cloud_resource_read`
  only), and 41-29's `infra` `TaskKind` agent path. *(Note the epic README's standing correction: 41-29 routes
  `infra` to the **coding** path, not to `deployment-pipeline` — the tool need is unchanged, the routing
  sentence in 42-7's own "Epic 41 consumers" section is stale.)*

## Risks & Mitigations

- **Irreversible operations with no bespoke gate in this story.** A `delete` is unrecoverable, and the
  target-bound single-use authorization that used to guard it (AC5) is stripped. Mitigation: the guarantee
  moves to Epic 43's catalog — but **it is only as good as the catalog row an admin authors**, and until
  Epic 43 Story 9 lands there is no gate at all. This must be stated when the family ships: `cloud_resource_write`
  is auditable and secret-bound from day one, and *governed* only once a catalog entry exists. Consider not
  enabling the write half in any deployment before Epic 43's Seam B is live.
- **The engine-side wait is shared, and sharing invites double-building (W3).** Mitigation: step 1's explicit
  check for 42-8B's artefacts; the `CanonicalSuspendActivities` entry is a build-gated singleton registration
  (W2), so a duplicate type would surface immediately.
- **Handle forgery (D7).** Mitigation: server-side minting, tenant-folded bookmarks, and the AC8 round trip's
  negative cases.
- **The callback endpoint is new external surface.** Mitigation: authenticated and tenant+operation-keyed,
  matching `DocumentDecisionResumeEndpoint`; a cross-tenant callback test.
- **Siting drift.** A contributor adds a driver to `Tamma.Activities` because the six built-ins live there.
  Mitigation: D1's rule — and the honest note (W4) that `TAMMA001` would **not** catch it, so review, not the
  analyzer, is the control.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | Precondition gate + `ICloudResourceProvider` + Hetzner driver + generic seam | 1.5 |
| 3 | Both executors (split, schemas, fail-closed, secret binding, redaction) | 1.5 |
| 4–5 | `WaitForToolOperationActivity` + `ForToolOperation` + `CanonicalSuspendActivities` + callback endpoint | 1.5 |
| 6–7 | DI wiring + seven test suites incl. the Testcontainers wait round trip | 1.5 |
| 8 | Full green | 0.25 |
| **Total, standalone** | | **6.25** |
| **Total, if 42-8B lands first** (steps 4–5 collapse to adding a `kind`) | | **~4.25** |

Story estimate: ~6–8 d standalone, ~4–5 d if 42-8B is first. Stripping the gating sections removed the
`Describe` seam, the `ToolAuthorizationRequest` plumbing and the target-bound authorization test matrix —
roughly a day — offset by W1's `ForToolOperation` builder, which the story assumed existed. **Budget the
shared wait machinery once across this story and 42-8B, not twice.**
