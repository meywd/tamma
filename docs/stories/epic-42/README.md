# Epic 42: Agent Capability & Tool Layer — a first-class, extensible, secured, autonomy-gated tool catalog

## Overview

Tamma's workflows produce typed **documents** (Epic 39) and dispatch them by kind (Epic 41), and an
agent can *act* through the tool-execution framework in `Tamma.Activities/LlmCall/Tools/`. But that
framework is **coding-only**. `Tamma.Api/Program.cs` (L753–764) registers exactly **six**
`IToolExecutor`s — `FileReadTool`, `FileWriteTool`, `GitOperationsTool`, `RunTestsTool`,
`SearchCodeTool`, `ShellExecuteTool`. So an agent can edit the repo, run git, run tests, and run a
shell — but has **no first-class tool** to reach a cloud/VPS resource, flip a feature flag, drive a
deploy, or call an external API. Worse, the catalog is closed:

- **The registry is compile-time static.** `ToolExecutorRegistry` is populated once from
  `IEnumerable<IToolExecutor>` injected by DI into a plain `Dictionary`. `IToolExecutorRegistry`
  declares exactly four members — `GetExecutor` / `IsAllowed` / `GetAll` / `GetAllowed`; there is **no
  `Register`/`Unregister`**, so no dynamic / per-deployment / per-tenant registration, and **no MCP
  ingestion path into the governed catalog**.
- **A tool declares no governance.** `IToolExecutor` exposes `ToolName`, `Description`, `InputSchema`,
  `ExecuteAsync` — and nothing about **who may call it**, **at what autonomy level**, **what credential
  it needs**, or **how destructive it is**. `ToolCallValidator` + `ActionGate` block dangerous *shell
  strings*, but that is a per-command denylist, not a per-tool permission model.
- **No credential binding.** No tool binds to a stored secret. `ShellExecuteTool` has been silently
  standing in for a real capability layer — an agent that needs to deploy shells out, unbound and
  ungoverned.
- **No durable tool-use audit.** `ToolLoopEventEmitter` emits ephemeral `TOOL_LOOP.*` SSE progress
  events to `IToolLoopEventSink`; nothing writes a **durable DCB `TOOL.*` row** the way documents get a
  `DOCUMENT.*` trail.

None of this is planned. There is no epic or story for a tool/capability layer.

### What already exists (corrections to earlier drafts of this section)

An earlier draft of this overview described the surrounding platform as more barren than it is. Four
things **do** ship today and change how the stories are scoped:

- **Corrected — MCP is not greenfield; it is *ungoverned*.** Eight live routes exist at
  `/api/kb/mcp/*` (`Tamma.Api/Program.cs` L3180–3187 → `KbEndpoints.cs` L115–161), including
  `POST /api/kb/mcp/tools/invoke`, proxying to the TS `intelligence-server` sidecar. They bypass
  `IToolExecutor`, `ToolExecutorRegistry`, `ToolCallValidator` and any audit entirely. They are gated
  by `SettingsView` (group) **+** `SettingsManage` (per-route), and in the dev-without-JWT branch
  every named policy is replaced by `AllowAnonymousRequirement`. They currently **dead-end**: the
  sidecar's `McpManagementService` is constructed with an undefined client on every production boot
  (`buildIntelligenceBundleFromEnv` never sets `mcpClient`; `@tamma/mcp-client` is not a dependency of
  `@tamma/intelligence-server`), so invoke returns `{success:false, error:'MCP client not
  configured'}`. Separately, `packages/mcp-client/` is a **7,865-LOC hand-rolled TS MCP client**
  (stdio/SSE/WebSocket transports, tool/resource/prompt registries, validator + rate-limiter +
  sandbox, audit, connection pool, `SERVER_PRESETS`) with **zero dependents**, never built. **This
  makes 42-6 urgent, not deferrable** — reconciling a route that dead-ends is cheap now and expensive
  the moment someone wires a client into the sidecar bundle. (`packages/providers/src/zen-mcp-provider.ts`
  is *not* in scope: it is an `IAIProvider` that uses MCP as transport to reach an LLM, not a tool bridge.)
- **Corrected — runtime secret reveal exists.** Four runtime plaintext readers already ship around
  `ISecretStore`'s no-plaintext boundary: `SecretStorePlatformCredentialReader.ReadActivePlaintextAsync(scope, tenantId?, name)`
  (both scopes, audited via `ISecretAccessAuditor` on every branch), `CabinetTenantProviderKeyReader.TryReadAsync(tenantId, name)`,
  `RuntimeSecretResolver.GetAsync(cabinetName)` (platform-only, 60s cache + `Invalidate` + fail-closed,
  **unaudited**), and `IAlertChannelSecretReader.GetPlaintextAsync(secretId)`. `IProviderCredentialResolver` /
  `DefaultProviderCredentialResolver` is a full working BYOK→platform precedence resolver. So 42-4 is a
  **generalization of a proven pattern**, not a blocked mega-dependency. See Dependencies for the two
  gaps that *are* real.
- **Corrected — `ResolveToolsActivity` is dead code.** It is referenced nowhere outside its own file
  across `src/`, `tests/` and `workflows/`. It injects only `ILogger` + `IConfiguration` (never the
  registry), and its built-in fallback switch has three arms — `search_code`, `read_file`, `run_tests` —
  where `read_file` does not match any registered tool (`FileReadTool.ToolName == "file_read"`). The
  live path is `ManagedAgent.ToResolvedTools` → `InlineToolLoopRunner`. Nothing in this epic may be
  built on `ResolveToolsActivity`.
- **A seventh `IToolExecutor` implementation exists.** `GetAcceptanceRulesTool` (`get_acceptance_rules`,
  Story 39-5 D6) is **principal-bound at construction and deliberately not DI-registered**, so
  "registers exactly six" above is precise as written. 42-1 **does** annotate it (`ReadOnly`, floor 70,
  no secret — leaving it to inherit the `Destructive`/100 default would arm a trap for the 39-17 host
  that eventually mounts it), but keeps it **out of the DI-registered startup drift test** (42-1 AC4),
  which an implementer must not widen to "all `IToolExecutor` types". It is also outside 42-3's
  registry-driven stage-1 set: 42-3 Scope 5 decides that such a tool is admitted only by a host
  **injecting** it into the run, never by a blanket exemption. See both stories' carve-outs.

**This epic makes the tool layer first-class: extensible (native + dynamic + MCP), secured
(secret-bound, redacted), and autonomy-gated (per-role, per-autonomy, per-mode) — the same governance
documents already get.** It *extends* the real `IToolExecutor` / `IToolExecutorRegistry` /
`ManagedAgent.ToResolvedTools` / `InlineToolLoopRunner` / `ParallelToolExecutor` / `ToolCallValidator`
surface. It does **not** design a parallel framework, and it does **not** reinvent the accept gate
(Epic 39) or the secret store (Epic 29) — it composes them.

## The gap this epic fills — and how it underpins Epic 41

Epic 41 turns every recurring SDLC activity into a lifecycle workflow, and **41-29** (the Task-Level
Flow Router) dispatches each `PlanTask` to the workflow matching its `TaskKind`
(`code`/`test`/`docs`/`infra`/`design`/`investigation`/`chore`). But the workflow it dispatches has an
agent, and **that agent has no tool to do non-code work**:

- A `docs` task routed to `41-24`/`41-25`/`41-26` needs a **publish** capability (push to the wiki /
  docs host / issue tracker) — there is none.
- An `infra` task routed to `deployment-pipeline` needs **deploy-control** and **cloud/VPS** tools —
  there are none; today it can only shell out.
- `41-22` (incident response & postmortem, incl. `rollback`) and `41-23` (capacity & health review)
  need **cloud/VPS** ops and a **feature-flag kill-switch** — there are none.
- `41-5` (stakeholder update) and `41-7` (standup digest) need an **authenticated HTTP** tool to post
  the artifact to Slack/Jira/etc. — there is none.

So Epic 42 is the **missing foundation under Epic 41**: 41-29 can *route* to a `docs`/`infra`/`design`
workflow, but until this epic lands, that workflow's agent falls back to `ShellExecute` or to the
human-assigned path (41 rule 4). This epic lights up the *agent* path of the non-code kinds by giving
each the governed tool it needs — with **no change to the router** (41-29's `kind→workflow` map is
untouched; this epic populates the tools those workflows resolve).

## The tool contract (what a tool declares) — extending the real `IToolExecutor`

Today `IToolExecutor` is `{ ToolName, Description, InputSchema, ExecuteAsync }` and *must never throw*
(errors are `ToolExecutionResult { Success = false }`). Story **42-1** keeps all four members and the
never-throw contract, and adds a governance descriptor. To avoid breaking the six built-ins and the
never-throw guarantee, the new metadata is exposed as a **`ToolDescriptor`** the executor surfaces
(via a `Descriptor` member with a **fail-safe default** — most-restrictive, never permissive — so an
un-annotated tool is *denied by default*, not silently granted):

| Field (new) | Type | Meaning | Fail-safe default |
|---|---|---|---|
| `Category` | `ToolCategory` = `Native` \| `Mcp` \| `ProviderAbstracted` | Where the tool comes from; drives which registration path owns it. | `Native` |
| `PermissionClass` | `ToolPermissionClass` = `ReadOnly` \| `Mutating` \| `Command` \| `Destructive` | Governance tier, and **the family MAXIMUM over the tool's operations** — the *per-call* class comes from 42-3's `Describe(argumentsJson)`. `ReadOnly` = no side effects (FileRead, SearchCode). `Mutating` = reversible write (FileWrite, GitOperations, RunTests). `Command` = arbitrary exec, already `ActionGate`-gated (ShellExecute). `Destructive` = irreversible / prod-impacting (delete a cloud resource, flip a prod flag, promote a deploy). | `Destructive` (deny-by-default) |
| `AutonomyFloor` | `int` (70–100) | Minimum autonomy dial at which an `AgentRole` may invoke the tool **directly**; below it, the action is routed to the orchestrator/human as a `ToolAuthorizationRequest` on the decision-gate plumbing — never silently skipped. **No code branches on the dial today** (see below): 42-3 builds this consumer. | `100` |
| `RequiredSecret` | `SecretRequirement?` | Declares the credential this tool needs — `{ Purpose, logical Name, scope-shape }`. `Purpose` is Epic 29's `SecretPurpose` **after 42-1 §0 relocates it to `Tamma.Core`** — it ships today in `Tamma.Api.Services.Secrets`, which the contract's assembly cannot reach. `null` for tools that touch nothing external. | `null` |
| `Suspends` | `bool` | **Declares that completion is owned by an engine-side wait** — not a capability the executor exercises. *Corrected: an `IToolExecutor` cannot suspend a workflow.* The tool loop runs inside a **blocking** `POST /api/v1/llm/call` in `Tamma.Api`, where there is no `ActivityExecutionContext` and no bookmark to create. So the executor returns promptly with an `operationHandle`, and `WaitForToolOperationActivity` (engine-side, credential-free) suspends on it and resumes on the callback or a durable timeout (42-7 §4 / 42-8B §6). | `false` |

The four existing members are unchanged; the six built-ins each declare a real `ToolDescriptor` (e.g.
`SearchCodeTool` → `ReadOnly`, floor 70, no secret; `ShellExecuteTool` → `Command`, floor 85, no
secret — it stays `ActionGate`-gated). `IToolExecutorRegistry` gains a **dynamic registration** seam
(`Register(IToolExecutor)` / `Unregister(name)` alongside today's DI-seeded set) so tools can be added
per deployment and by MCP — the *vocabulary of governance is static; the catalog is dynamic*,
mirroring Epic 39's "vocabulary static, composition dynamic". **Scope caveat:** the registry is a
**singleton**, so 42-1 ships this seam **platform/deployment-scoped only** and rejects a
principal-scoped registration outright (42-1 AC7); per-tenant tools wait for 42-6 Part B's
per-principal registry *view*, since registering one principal's tool into the singleton leaks it to
every other principal.

### Where the code lives (assembly-siting rule — binding on 42-4 / 42-6 / 42-7 / 42-8A / 42-8B / 42-9)

Dependency direction is one-way: **`Tamma.Core` ← `Tamma.Activities` ← `Tamma.Api`.** `Tamma.Core`
is a leaf (no `ProjectReference` at all); `Tamma.Activities` references only `Tamma.Core` +
`Tamma.Data`; `Tamma.Api` references `Tamma.Activities`. A `Tamma.Activities → Tamma.Api` reference
is **circular and will not compile**.

| Lives in | What |
|---|---|
| `Tamma.Core` | Shared value types the contract needs on both sides — including **Epic 29's `SecretPurpose`, which 42-1 §0 *moves* here** (it ships in `Tamma.Api.Services.Secrets`, which `Tamma.Activities` cannot reach). *Move, not mirror:* 42-1 §0 rejects a parallel Core-owned enum mapped onto Epic 29's, because two taxonomies drift and the mapping table is exactly what the descriptor exists to remove. The move is data-safe — `SecretRow.Purpose` is a `string` column, so no schema or data change. |
| `Tamma.Activities.LlmCall.Tools` | The **contract only**: `IToolExecutor`, `ToolDescriptor`, `SecretRequirement`, `IToolExecutorRegistry` / `ToolExecutorRegistry`. |
| `Tamma.Api` | **Every external-touching executor and its provider drivers**: `CloudResourceTool`/`ICloudResourceProvider` (42-7), `FeatureFlagTool`/`IFeatureFlagProvider` + `DeployControlTool`/`IDeployControlProvider` (42-8A/42-8B), `HttpRequestTool` (42-9), `McpToolExecutor` + the MCP client (42-6), and the `IToolSecretProvider` implementation (42-4). Also both gating stages (42-3) and the `TOOL.*` emitter (42-5) — `ManagedAgent` / `InlineToolLoopRunner` are Api-side. |
| `Tamma.Activities` (engine side) — **carve-out** | The **suspend/wait activities only**, because an `IToolExecutor` cannot suspend a workflow (no `ActivityExecutionContext` inside the blocking `POST /api/v1/llm/call`): `WaitForToolAuthorizationActivity` (42-3) and `WaitForToolOperationActivity` (42-7 / 42-8B, landed once and shared). Both are **credential-free and make no vendor call** — they resume on a callback; any polling of a vendor is an Api-side concern. This is the *only* engine-side code the epic adds. |

This follows **rule 1** ("a workflow step must never call an external API/provider directly or hold an
external credential"), whose permanent backstop is the **`TAMMA001`** analyzer
(`Tamma.Activities.Guardrails`, `DiagnosticSeverity.Error`, wired into `Tamma.Activities` and
`Tamma.ElsaServer` as an `OutputItemType="Analyzer"` project reference — and `Error` is the only
gating severity because the repo sets `TreatWarningsAsErrors=false`). `Allowlist.IsEngineSurface`
covers `Tamma.Activities` / `Tamma.ElsaServer`; `Tamma.Api` is deliberately excluded.

Two honest caveats so nobody over-reads the analyzer: (1) `TAMMA001` is narrower than "no external
HTTP" — its injection check is a **closed 12-entry denylist** (which today contains
`IProviderCredentialResolver` but none of the types above), and its HTTP check only fires on an
`HttpClient` send whose host is a **statically-resolvable literal external host**; ~20 files in
`Tamma.Activities` already use `HttpClient` under it and compile. So the rule here is architectural
intent with the analyzer as backstop, not a mechanical compile failure. (2) It is nonetheless
**forced** on runtime grounds: `Tamma.ElsaServer/Program.cs` L286–292 records that the tool catalog
was *removed* from the engine and "the tool executors are registered there [`Tamma.Api`], not here" —
impl-in-Api is where the DI wiring already lives. Existing precedent for interface-in-Activities /
impl-in-Api: `GetAcceptanceRulesTool` (`Tamma.Api.Services.AcceptanceRules`) and
`InlineToolLoopRunner`, which `Allowlist.cs` L57–58 explicitly notes "now lives in the `Tamma.Api`
assembly, outside the analyzed engine surface, so no engine exemption is needed."

## Permission + autonomy + secret model (single-user **and** SaaS)

Per CLAUDE.md's universal rule, every tenant-aware surface here answers ownership **twice**. The model
mirrors the **Prompt Store** two-scoping pattern exactly (Story 42-2 owns the persistence, a
`tool_bindings` table analogous to `prompt_overrides`; a `CHECK (user_id XOR tenant_id)` constraint):

- **single-user mode** — the sole user owns tool enablement + the per-role/per-autonomy grant.
  Bindings keyed by `user_id`. Resolution: user binding → system default descriptor.
- **SaaS mode** — `tenant_owner` / `tenant_admin` owns the tenant's grants; `member` users get the
  resolved grant with **no edit access** (403 on write, exactly like the Prompt Store). Bindings keyed
  by `tenant_id`. **No per-user override layer.** Resolution: tenant binding → system default.

**Enforcement is two-stage on the live tool path (Story 42-3), not a new gate.**
*Corrected: earlier drafts sited enforcement in `ResolveToolsActivity`, which is dead code (see
"What already exists"). It is also single-stage, which cannot work — see below.*

- **Stage 1 — resolve-time pre-screen**, where the eligible tool set is built: `ManagedAgent.ToResolvedTools`
  (today `names.Select(n => new ResolvedTool { Name = n })` — bare names, no descriptor). It returns only
  the subset that is (a) enabled for the principal, (b) permitted for the agent's `AgentRole`, and
  (c) **autonomy-eligible** — `Descriptor.AutonomyFloor ≤ current dial`.
- **Stage 2 — invocation-time authorization**, in `InlineToolLoopRunner`'s pre-execution filter, covering
  **both** the parallel and the sequential branch (`ToolLoopConfig.EnableParallelTools` defaults to
  `false`, so sequential is the *default* path — a gate on only one branch governs nothing). Stage 2 is
  where `LlmToolCall.ArgumentsJson` first exists, so it is the **only** place the *operation* and *target*
  can be authorized. Resolve-time can only authorize a *capability* (`cloud_resource_write`), which would
  silently cover delete-any-resource; the per-call class + target must be reported to the gate regardless
  of how the tool family splits its verbs.

**What stage 1 does *not* do — filter on the raw descriptor class.** The descriptor's `PermissionClass`
is the family's **maximum**, and every Wave-3 write executor advertises `Destructive` as that maximum.
A stage-1 filter of `PermissionClass != Destructive` would hand the agent none of them, so the model
would never emit a call and stage 2 would never fire — the whole catalog inert. 42-3 therefore keys
stage 1 on the **binding-resolved effective ceiling** for the principal + role + autonomy, dropping a
tool only when that ceiling is empty. `Destructive` is a **stage-2** discriminator, applied to the
concrete call.

A gated *action* — one whose per-call class is `Destructive`, or whose resolved floor exceeds the
current dial — is **not executed**;
instead the step routes that action to the orchestrator/human over the **existing decision-gate
plumbing** (`WaitForDocumentDecisionActivity` + `DocumentDecisionResumeEndpoint`, keyed on
tenant + session) carrying a **sibling `ToolAuthorizationRequest` payload** — Epic 39: *"the acceptor is
an actor, not a branch"* — so a destructive tool call is a *decision taken by someone*, never an
unattended side effect. *Corrected:* `AcceptanceRequest` itself is **not** reusable — all seven of its
properties are `required`, including a `review`-typed `DocumentEnvelope`, and `AcceptanceRequestFactory`
is its only constructor and rejects a non-`review` envelope. 42-3 therefore owns three concrete
adaptation costs: a tool-authorization decision vocabulary (the gate's `[FlowNode]` outcomes and
`ReadDecision` are pinned to the four `AcceptanceDecision` kinds), a `RequestedAtUtc` equivalent (the
gate throws `DOCUMENT.DECISION.MISSING_REQUESTED_AT` without it), and a new bookmark prefix registered
in `LifecycleBookmarks.CanonicalSuspendActivities`.

**The autonomy dial (42-3 is its first control-flow consumer).** *Corrected: an earlier draft stated
flatly that "the dial is read live, never cached into a running workflow (Epic 39 rule)".* Epic 39 does
**specify** that (`epic-39/README.md`; story 39-5 Technical Notes) — but the landed
`DocumentLifecycleWorkflow` resolves `ResolvedAcceptanceRules` **once at Init** (`ResolveRules`, L184)
into serialized lifecycle state, and every later stage reads `state.Rules`. Worse, `AcceptanceRules.AutonomyLevel`
is validated to `[70,100]` and then **never branched on anywhere** — no comparison, no switch (nor is
the per-type `AcceptorRequirement` floor). So `AutonomyFloor ≤ currentAutonomy` is a **new Epic 39
design change requiring sign-off**, not a reuse: 42-3 must *ship* the live-read resolver seam
(an `Input<T>` bound to a delegate that consults the resolver per activity execution — the re-read-on-resume
pattern `WaitForDocumentDecisionActivity` already proves), not merely call an existing one.

**Credentials (Story 42-4)** bind through Epic 29's `ISecretStore` — `SecretScope.Tenant` in SaaS
(keyed by the tenant `Guid`), `SecretScope.Platform` in single-user. *Corrected:* there is **no user
scope** — `SecretScope` has exactly `Platform` and `Tenant`, and `SecretRef`'s constructor throws on
either mismatch; single-user ownership is carried by `SecretMetadata.OwnerUserId`, metadata not scope.
Secrets are **never** in tool args logged, in `ToolExecutionResult.Output`, in DCB `TOOL.*` events, or
in error messages (reuse `ErrorRedactor`). *Corrected:* `ISecretStore` performs **no authorization** —
it injects no caller identity and audits with actor `Guid.Empty`, so it resolves whatever `SecretRef`
the caller hands it. Cross-tenant isolation is **42-4's** obligation: pin the run's `tenantId` into the
ref, and assert the resolver never *constructs* another tenant's ref (not that the store rejects one).

*Corrected — this is no longer the epic's biggest external dependency.* `ISecretStore` exists (29-1;
CLAUDE.md's "does not yet exist" note is stale) and by design never returns plaintext through a public
signature — but **four runtime plaintext readers already ship around that boundary** (see "What already
exists"), so 42-4's `IToolSecretProvider` is a **Medium generalization of an existing seam**, modelled
on `IProviderCredentialResolver`/`DefaultProviderCredentialResolver` (BYOK→platform precedence, 60s
cache, `ToTag()` log projection, fail-closed `TammaError`) and backed by
`SecretStorePlatformCredentialReader`'s audited read. Two *real* gaps replace the phantom one:
`ISecretAccessAuditor`'s only implementation is `NullSecretAccessAuditor` (registered by
`TryAddSingleton` under "Audit pipe — null until a future story wires the real one"), so every audit
emission is dropped today; and `IProviderCredentialResolver` is on the `TAMMA001` injection denylist,
so no engine-surface tool may inject it — which is exactly why the executor lives in `Tamma.Api`.

## Tool families (the concrete capabilities the platform needs)

All five families ship their executor and provider drivers in **`Tamma.Api`** (see "Where the code
lives"). Every one of them is **also** an invocation-time authorization subject: the permission class
column is per-*operation*, so the class the descriptor advertises is the family's **maximum**, and the
actual class + target must be reported per call to 42-3's stage-2 gate.

| Family (story) | What it does | Permission class (max = descriptor) | Secret purpose † | Epic 41 consumers |
|---|---|---|---|---|
| **Cloud / VPS resource ops** (42-7) — provider-abstracted (Hetzner / generic), like the Git & AI provider abstractions | list / create / resize / delete VPS & cloud resources | `ReadOnly` (list) · `Mutating` (create/resize) · **`Destructive`** (delete) | `ApiKey` (cloud-provider token) | `deployment-pipeline` (infra tasks via 41-29) · **41-22** incident/rollback · **41-23** capacity & health |
| **Feature-flag / config toggle** (42-8A) | read & flip feature flags / runtime config | `Mutating` (non-prod) · **`Destructive`** (prod flag / kill-switch) | `ApiKey` (flag provider) | `deployment-pipeline` promotion · **41-22** kill-switch · 41-29 `infra` kind |
| **Deploy control** (42-8B) | trigger / promote / rollback / gate a release | `Mutating` (staging) · **`Destructive`** (prod) | `ApiKey` or `SigningKey` (deploy platform) | `deployment-pipeline` · **41-22** rollback · **41-24** release notes trigger |
| **Authenticated HTTP / external API** (42-9) — generic, host+method allowlisted, per-endpoint bound | one authenticated REST call to a bound endpoint | `ReadOnly` (GET-only bindings) · **`Mutating`** (default) | `ApiKey` (per-endpoint) | **41-5** stakeholder update · **41-7** standup publish · **41-24/25/26** docs publish · any integration |
| **MCP-exposed tools** (42-6) | whatever an external MCP server exposes; **also** reconciles the 8 ungoverned `/api/kb/mcp/*` routes — **retire 2, re-scope 6** (Part A) | inherited/declared per tool — **`Destructive`** until classified | per bound server | open-ended — any future kind |

† *Corrected:* Epic 29's `SecretPurpose` enum ships in `Tamma.Api.Services.Secrets` and is **not
reachable from `Tamma.Activities`** (which references only `Tamma.Core` + `Tamma.Data`; the reverse
reference is circular). **42-1 §0 relocates the enum itself to `Tamma.Core.Enums`** — same seven
members, same order, no schema change — so the column above names the *actual* type
`SecretRequirement.Purpose` carries after Wave 0, not a mirrored one the API side maps.

Each family declares its `PermissionClass`, `AutonomyFloor`, and `RequiredSecret`, so it is governed by
42-1–42-5 with **zero bespoke security code** — a new tool is a descriptor + an executor, not a new
gate. One naming constraint binds MCP specifically: `ToolCallValidator` rejects any tool name not
matching `^[a-zA-Z0-9_\-]{1,64}$` — no colon, 64 chars total. **Decided (42-6 Part B §1):
`mcp__<server>__<tool>`**, so `server + tool ≤ 57` chars; an overflowing or case-insensitively
colliding name is rejected **at registration**, never truncated. The same 64-char budget is enforced a
second time by `tool_bindings.ToolName`'s `HasMaxLength(64)` (42-2) — an unbindable tool is an
ungovernable one.

## Stories

| Story | Title | Purpose (one line) |
|---|---|---|
| **42-1** | Tool Contract & Registry Evolution | Add `ToolDescriptor` (category / permission class / autonomy floor / secret requirement / suspends) to the `IToolExecutor` surface and a dynamic `Register`/`Unregister` seam to `IToolExecutorRegistry`; annotate the six built-ins. |
| **42-2** | Tool Binding & Config Store (two-scoping) | Persist per-principal tool enablement + config as `tool_bindings` (user_id XOR tenant_id), mirroring `prompt_overrides`; define the single-user & SaaS resolution order. |
| **42-3** | Per-Tool Permission & Autonomy Gating | Two-stage gate on the live path — resolve-time eligible-set build in `ManagedAgent.ToResolvedTools`, invocation-time argument-bound authorization in `InlineToolLoopRunner` (both branches); route `Destructive`/above-floor calls to the orchestrator over the decision-gate plumbing with a sibling `ToolAuthorizationRequest`; **ship** the live-read autonomy resolver. |
| **42-4** | Tool Credential / Secret Binding | Generalize the shipped runtime-reveal pattern into `IToolSecretProvider` (impl in `Tamma.Api`), bind external-touching tools to `ISecretStore` (`SecretScope.Tenant` SaaS / `SecretScope.Platform` single-user); guarantee no-secret-in-logs/events. |
| **42-5** | Tool-Use DCB Audit | Emit the durable **invocation trio** `TOOL.INVOKED` / `TOOL.SUCCEEDED` / `TOOL.FAILED` (secret-redacted args, `issueId`/`tenantId` tags) via a **direct `IEventRepository` append in `Tamma.Api`** at the shared `InlineToolLoopRunner` call site covering both execution branches, alongside the ephemeral `TOOL_LOOP.*` SSE stream; owns the shared emit path + redaction rule the other `TOOL.*` events reuse. *(The governance events `TOOL.RESOLVED`/`DENIED`/`ESCALATED`/`AUTHORIZED` are **42-3**'s; `TOOL.SECRET_ACCESSED` is **42-4**'s; `TOOL.BINDING_*` is **42-2**'s.)* |
| **42-6** | MCP Integration | **Part A (Wave 0.5):** retire the 2 tool-facing `/api/kb/mcp/*` routes and re-scope the other 6 as sidecar-KB admin. **Part B (Wave 2):** let external MCP servers expose tools into the registry via the 42-1 dynamic path, wrapped as `IToolExecutor` (`mcp__<server>__<tool>`) with the same permission/autonomy/secret/audit treatment as native tools; decide **port-vs-adopt** for the orphaned `packages/mcp-client/` (proxying via the sidecar is rejected). |
| **42-7** | Cloud / VPS Resource Operations Tool | Provider-abstracted (Hetzner + generic) cloud/VPS list/create/resize/delete tool family, split `cloud_resource_read` / `cloud_resource_write`. |
| **42-8** | *(split)* Feature-Flag & Deploy-Control Tools | **Superseded by 42-8A + 42-8B** — the two halves share no implementation (two provider abstractions, two drivers, two secret bindings, and a suspend path on only one side). The remaining `42-8-…md` is the split index. |
| **42-8A** | Feature-Flag / Config-Toggle Tool | `feature_flag_read` / `feature_flag_write`; prod-vs-non-prod class resolved from the binding, never asserted by the model. No engine-side work. Medium. |
| **42-8B** | Deploy-Control Tool | `deploy_status` / `deploy_control` (trigger / promote / rollback); shares `WaitForToolOperationActivity` with 42-7. Large. |
| **42-9** | Authenticated HTTP / External-API Tool | Generic host+method-allowlisted authenticated REST tool covering publish/notify/integration needs. |

## Sequencing

**Wave 0 — the contract.** **42-1** (descriptor + dynamic registry, plus §0's relocation of Epic 29's
`SecretPurpose` into `Tamma.Core`). Everything depends on it. Its `Register`/`Unregister` seam lands
**platform/deployment-scoped only**; principal-scoped registration into the singleton stays rejected
until 42-6's per-tenant view.

**Wave 0.5 — prerequisite cleanup + closing the live hole.** Two independent items, neither gated on
42-1:
- Delete `ResolveToolsActivity` (or fix its `read_file` built-in to `file_read`) *before* anything is
  built on it, and reconcile the two independently-sourced allowlists `InlineToolLoopRunner` already
  carries (the resolved-tool name list vs `loopConfig.AllowedTools`). Small, but 42-3 and 42-5 both
  land on this surface.
- **42-6 Part A** (P0) — retire the 2 tool-facing `/api/kb/mcp/*` routes, re-scope the other 6 as
  sidecar-KB admin. Depends on **nothing** in this epic. It is a deletion while the route dead-ends;
  it becomes a migration the moment anyone sets `mcpClient` on the sidecar bundle. Shipping the epic
  with this open would make its own governance claim false.

**Wave 1 — governance rails (parallel after 42-1).** **42-2** (binding store) → **42-3** (two-stage
gating + the live-read autonomy resolver — the largest item in the wave, and an Epic 39 design change
needing sign-off); **42-4** (secret binding — *Corrected: **Medium**, a generalization of a shipped
seam, no longer the critical-path blocker*); **42-5** (DCB audit). Together these are the security
envelope every family and MCP tool inherits — no family ships before them.

**Wave 2 — the open catalog.** **42-6 Part B** (MCP) — the dynamic-tool path proving a non-native tool
obeys 42-1–42-5. It needs the full Wave-1 envelope. Its route-reconciliation half is **not** here: it
is Part A, in Wave 0.5, and is **not optional** (see above). Part B's estimate is not meaningful until
its §0 port-vs-adopt decision is recorded in `.dev/decisions/`.

**Wave 3 — the families (parallel on the Wave-1 rails).** **42-9** HTTP, **42-8A** flags, **42-8B**
deploy, **42-7** cloud/VPS — each a descriptor + executor **in `Tamma.Api`**. Order by Epic 41 demand
and by engine-side cost: **42-9 → 42-8A → 42-8B → 42-7**. 42-9 unblocks the most 41 workflows (docs
publish, stakeholder, standup) and 42-8A needs nothing engine-side; 42-8B and 42-7 share
`WaitForToolOperationActivity`, its bookmark prefix, its `CanonicalSuspendActivities` registration and
its authenticated callback endpoint — **whichever lands first ships them, the second reuses them**, so
only one of the two carries that cost (the per-story estimates state both figures).

## Out of scope (deliberately not this epic)

- **A general plugin/marketplace runtime for arbitrary code.** MCP (42-6) is the extensibility path;
  loading untrusted in-process assemblies is not. — *MCP gives the open catalog with a process boundary
  and the same governance; in-proc plugins reopen the trust surface this epic closes.*
- **Rewriting the accept gate or the secret store.** This epic *composes* the Epic 39 decision-gate
  plumbing (adding a sibling `ToolAuthorizationRequest` payload, not a second acceptor) and the Epic 29
  `ISecretStore` (adding no second cabinet).
- **Building the flag / deploy / cloud *providers themselves*.** 42-7/42-8A/42-8B/42-9 define the
  provider-abstracted tool + one reference driver each (Hetzner, a flag provider, the deploy platform);
  exhaustive driver coverage per vendor is follow-on, exactly as the Git/AI provider abstractions grew.
- **A real `ISecretAccessAuditor` implementation.** Only `NullSecretAccessAuditor` is wired today, so
  42-4/42-5's secret-read audit rows land nowhere until an Epic 29 story swaps it. This epic depends on
  it and does not build it.
- *Removed: "The reveal-to-runtime-consumer secret path."* **Corrected** — four such readers already
  ship (see "What already exists"), so there is nothing here to defer to Epic 29. 42-4 generalizes them.

## Dependencies

- **Epic 29 (secrets):** `ISecretStore` / `SecretRef` / `SecretScope` / `SecretPurpose` **exist** (Story
  29-1) — all in `Tamma.Api.Services.Secrets`, i.e. **in the API assembly and unreachable from
  `Tamma.Activities`**, which is why 42-1 §0 **relocates `SecretPurpose` to `Tamma.Core.Enums`**
  (a move, not a mirror — see "Where the code lives"). `SecretRef` / `SecretScope` / `ISecretStore`
  stay Api-side; a `ToolDescriptor` never carries a `SecretRef`, only the logical requirement.
  *Corrected: the "hard-blocked reveal-to-runtime-consumer path" no longer applies* — four runtime
  plaintext readers ship (audited `SecretStorePlatformCredentialReader`, `CabinetTenantProviderKeyReader`,
  `RuntimeSecretResolver`, `IAlertChannelSecretReader`). The **real** dependency is a non-null
  `ISecretAccessAuditor`: today only `NullSecretAccessAuditor` is registered, so audited-read acceptance
  criteria cannot be satisfied until it is swapped. 42-7/42-8A/42-8B/42-9's agent paths **no longer wait** on a
  reveal path.
- **Epic 39:** the decision-gate plumbing (`WaitForDocumentDecisionActivity` +
  `DocumentDecisionResumeEndpoint`, keyed tenant+session) carrying a **sibling `ToolAuthorizationRequest`** —
  `AcceptanceRequest` itself is not reusable (seven `required` properties incl. a `review`-typed
  `DocumentEnvelope`; `AcceptanceRequestFactory` is the only constructor). **Autonomy dial — not an
  existing consumable behaviour:** `AutonomyLevel` is stored, validated `[70,100]` and emitted in audit,
  but **no code branches on it**, and `DocumentLifecycleWorkflow` caches the resolved rules at Init
  contrary to Epic 39's own written rule; 42-3 must ship the live-read path and get that design change
  signed off. Also: resumable-by-design (42-1 `Suspends`).
- **DCB audit transport (42-5):** *Corrected* — **not** `TammaEventEmitter` → `tamma:events` →
  `EventDrain`. That emitter structurally requires an `ActivityExecutionContext` **and** an `IActivity`,
  and the tool loop no longer runs in the engine; `Tamma.Api` holds `IEventRepository` directly (as
  `AlertEventEmitter`, `PromptEventsService`, `EscalationDispositionService` already do). 42-5 appends
  directly. Hook the **shared** `InlineToolLoopRunner` call site so both the parallel and the default
  sequential branch are covered.
- **Epic 41 / 41-29:** the consumers. 41-29's `TaskKind`→workflow map is unchanged; this epic supplies
  the tools those dispatched workflows resolve. `docs`/`infra`/`design` agent paths light up as
  42-7/42-8A/42-8B/42-9 land.
- **Existing surface (extended, not replaced):** `IToolExecutor`, `IToolExecutorRegistry` /
  `ToolExecutorRegistry`, `ManagedAgent.ToResolvedTools`, `InlineToolLoopRunner`, `ParallelToolExecutor`,
  `ToolCallValidator` + `ActionGate`, `IToolLoopEventSink` / `ToolLoopEventEmitter`. *(Corrected:
  `ResolveToolsActivity` removed from this list — it is dead code.)*
- **Existing MCP prior art (42-6 must decide on each):** `packages/mcp-client/` (7,865 LOC, orphaned,
  never built) — **port to C# or adopt an official MCP C# SDK**; *proxying via the sidecar is ruled out
  by 42-6 §0* (it would put tool execution behind an HTTP hop outside the tool envelope, re-creating in
  Part B exactly the bypass Part A deletes). Either way the package must not stay orphaned — it becomes
  the port's source of truth and is then deleted, or it is deleted outright. The 8 `/api/kb/mcp/*`
  routes — **retire 2, re-scope 6** (settled in 42-6 A1/A2). `MCPSource` (an MCP *resource* consumer,
  not a tool consumer) whose `IMCPClientLike` seam should converge on whatever client 42-6 lands.

## Decisions taken (previously open questions, now settled by the code)

*These were open questions in an earlier draft. Their old numbers are **not** the numbers in "Open
design questions" below — that list has been renumbered; cite these as **D1** / **D2**.*

- **D1 — Secret reveal for runtime tool execution: option (a), and it is not a new capability.**
  *(Was the draft's "biggest" open question.)* Four runtime plaintext readers already ship around
  `ISecretStore`'s no-plaintext boundary, one of them scope-generic and audited
  (`SecretStorePlatformCredentialReader.ReadActivePlaintextAsync(scope, tenantId?, name)`), plus a full
  BYOK→platform resolver (`DefaultProviderCredentialResolver`). 42-4 generalizes that pattern into
  `IToolSecretProvider` (impl in `Tamma.Api`); no Epic 29 story is filed or waited on. Residual
  dependency: a non-null `ISecretAccessAuditor`.
- **D2 — Destructive-tool routing rides the decision-gate plumbing with a *sibling*
  `ToolAuthorizationRequest`.** `AcceptanceRequest` cannot carry a tool call:
  all seven properties are `required`, including a `review`-typed `DocumentEnvelope`, and
  `AcceptanceRequestFactory` — its only constructor — rejects a non-`review` envelope. The reusable part
  is the bookmark/suspend/resume machinery (`WaitForDocumentDecisionActivity` +
  `DocumentDecisionResumeEndpoint`, keyed tenant+session), not the record. 42-3 owns the three
  adaptation costs named above (decision vocabulary, `RequestedAtUtc` equivalent, bookmark prefix
  registration). Task View integration follows from reusing the machinery, and is 42-3's to verify.

## Open design questions (genuinely unresolved — worth a decision)

1. **Descriptor default: deny vs. read-only.** An un-annotated tool defaults to `Destructive` / floor
   `100` (deny-by-default). Safe, but every dynamically-registered/MCP tool is inert until classified.
   Accept the friction, or default MCP tools to `ReadOnly` and require explicit elevation to write?
2. **MCP server trust & tenancy.** In SaaS, may a `tenant_admin` register an arbitrary MCP server
   (tenant-scoped), or is the MCP server allowlist platform-owned? Determines whether 42-6 needs a
   per-tenant MCP registration surface or only a platform one. Sharpened by 42-1's `Register`/`Unregister`
   landing on a **singleton** registry two waves earlier than 42-6 Part B's per-tenant view — hence the
   Wave-0 constraint that dynamic registration be platform/deployment-scoped only, and 42-6's decision
   to implement the platform-owned path first *because it must*. What is genuinely open is only whether
   tenant-scoped MCP registration is ever permitted; if it is, 42-6 Part B also owns building the
   per-principal registry view (which is separately what would give the 39-5 D6 principal-bound-tool
   pattern a delivery path).
3. **MCP client: port or adopt.** `packages/mcp-client/` is 7,865 LOC of working hand-rolled TS with
   zero dependents; the C# backend has no MCP client at all. The two live options — port it to C#, or
   adopt an official MCP C# SDK — have materially different costs, and 42-6 Part B's effort estimate is
   not meaningful until one is chosen (time-boxed spike, recorded in `.dev/decisions/`). *Proxying
   through the TS sidecar is no longer one of the options: 42-6 §0 rules it out on evidence.* Note a
   second, independent customer for whatever transport is chosen: `HttpProviderClient.NonHttpProviders`
   fails `zen-mcp`/`zen` with `PROVIDER_NOT_SUPPORTED` because the MCP transport "is not yet ported to
   C#" — worth knowing when choosing, without taking that provider port on.
4. **Autonomy-dial design change (needs Epic 39 sign-off).** `AutonomyFloor ≤ currentAutonomy` would be
   the **first** control-flow use of `AcceptanceRules.AutonomyLevel` anywhere in the codebase. Confirm
   with Epic 39 that the dial is intended to gate behaviour (not just annotate audit), and that the
   live-read seam 42-3 ships is the one Epic 39 wants — including whether
   `DocumentLifecycleWorkflow`'s cache-at-Init should be corrected in the same change.
