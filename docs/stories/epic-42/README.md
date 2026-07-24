# Epic 42: Agent Capability & Tool Layer — a first-class, extensible, secured, autonomy-gated tool catalog

## Overview

Tamma's workflows produce typed **documents** (Epic 39) and dispatch them by kind (Epic 41), and an
agent can *act* through the tool-execution framework in `Tamma.Activities/LlmCall/Tools/`. But that
framework is **coding-only**. `Program.cs` registers exactly **six** `IToolExecutor`s —
`FileReadTool`, `FileWriteTool`, `GitOperationsTool`, `RunTestsTool`, `SearchCodeTool`,
`ShellExecuteTool`. So an agent can edit the repo, run git, run tests, and run a shell — but has **no
first-class tool** to reach a cloud/VPS resource, flip a feature flag, drive a deploy, or call an
external API. Worse, the catalog is closed:

- **The registry is compile-time static.** `ToolExecutorRegistry` is populated once from
  `IEnumerable<IToolExecutor>` injected by DI. Adding a tool means shipping a class and a DI line.
  There is **no dynamic / per-deployment / per-tenant registration** and **no MCP path** — the catalog
  cannot grow without a code change per tool.
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

**This epic makes the tool layer first-class: extensible (native + dynamic + MCP), secured
(secret-bound, redacted), and autonomy-gated (per-role, per-autonomy, per-mode) — the same governance
documents already get.** It *extends* the real `IToolExecutor` / `IToolExecutorRegistry` /
`ResolveToolsActivity` / `ParallelToolExecutor` / `ToolCallValidator` surface. It does **not** design a
parallel framework, and it does **not** reinvent the accept gate (Epic 39) or the secret store
(Epic 29) — it composes them.

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
| `PermissionClass` | `ToolPermissionClass` = `ReadOnly` \| `Mutating` \| `Command` \| `Destructive` | Governance tier. `ReadOnly` = no side effects (FileRead, SearchCode). `Mutating` = reversible write (FileWrite, GitOperations, RunTests). `Command` = arbitrary exec, already `ActionGate`-gated (ShellExecute). `Destructive` = irreversible / prod-impacting (delete a cloud resource, flip a prod flag, promote a deploy). | `Destructive` (deny-by-default) |
| `AutonomyFloor` | `int` (70–100) | Minimum autonomy dial at which an `AgentRole` may invoke the tool **directly**; below it, the action is routed to the orchestrator/human via the accept-gate channel — never silently skipped. | `100` |
| `RequiredSecret` | `SecretRequirement?` | Declares the credential this tool needs — `{ Purpose (Epic 29 `SecretPurpose`), logical Name, scope-shape }`. `null` for tools that touch nothing external. | `null` |
| `Suspends` | `bool` | Tool may start a long external op and **suspend** the workflow (resumable-by-design, Epic 39), resuming on completion callback. | `false` |

The four existing members are unchanged; the six built-ins each declare a real `ToolDescriptor` (e.g.
`SearchCodeTool` → `ReadOnly`, floor 70, no secret; `ShellExecuteTool` → `Command`, floor 85, no
secret — it stays `ActionGate`-gated). `IToolExecutorRegistry` gains a **dynamic registration** seam
(`Register(IToolExecutor)` / `Unregister(name)` alongside today's DI-seeded set) so tools can be added
per deployment/tenant and by MCP — the *vocabulary of governance is static; the catalog is dynamic*,
mirroring Epic 39's "vocabulary static, composition dynamic".

## Permission + autonomy + secret model (single-user **and** SaaS)

Per CLAUDE.md's universal rule, every tenant-aware surface here answers ownership **twice**. The model
mirrors the **Prompt Store** two-scoping pattern exactly (Story 42-2 owns the persistence, a
`tool_bindings` table analogous to `prompt_overrides`; a `CHECK (user_id XOR tenant_id)` constraint):

- **single-user mode** — the sole user owns tool enablement + the per-role/per-autonomy grant.
  Bindings keyed by `user_id`. Resolution: user binding → system default descriptor.
- **SaaS mode** — `tenant_owner` / `tenant_admin` owns the tenant's grants; `member` users get the
  resolved grant with **no edit access** (403 on write, exactly like the Prompt Store). Bindings keyed
  by `tenant_id`. **No per-user override layer.** Resolution: tenant binding → system default.

**Enforcement is in `ResolveToolsActivity` (Story 42-3), not a new gate.** A workflow step declares the
tools it needs (`ToolNamesInput`, already the activity's input); the resolver returns only the subset
that is (a) enabled for the principal, (b) permitted for the agent's `AgentRole`, and (c)
**autonomy-eligible** — `Descriptor.AutonomyFloor ≤ current dial`. A needed tool that is `Destructive`
or above the floor is **not handed to the agent as callable**; instead the step routes that action to
the orchestrator/human over the **existing** `AcceptanceRequest` channel (Epic 39: *"the acceptor is an
actor, not a branch"*) — the same shape as the accept gate, so a destructive tool call is a *decision
taken by someone*, never an unattended side effect. The autonomy dial is read live, never cached into a
running workflow (Epic 39 rule).

**Credentials (Story 42-4)** bind through Epic 29's `ISecretStore` — `SecretScope.Tenant` in SaaS
(keyed by the tenant `Guid`), `SecretScope.Platform`/user-owned in single-user. Secrets are **never**
in tool args logged, in `ToolExecutionResult.Output`, in DCB `TOOL.*` events, or in error messages
(reuse `ErrorRedactor`). ⚠️ **Reconciliation with Epic 29:** `ISecretStore` **now exists** (Story 29-1
landed — CLAUDE.md's "does not yet exist" note is stale), but by design it *never returns plaintext
through a public signature* (plaintext only reaches a registered rotation handler via callback). A tool
that authenticates to Hetzner/Slack needs the live secret at execution time — so 42-4 **files a hard
dependency** on an authorized, audited *reveal-to-runtime-consumer* path (an Epic 29 extension building
on the 29-3 reveal-once UX + `ISecretAccessAuditor`); until it lands, external-touching tools run only
in the human-assigned path. This is the epic's single biggest external dependency (see Open Questions).

## Tool families (the concrete capabilities the platform needs)

| Family (story) | What it does | Permission class | Secret (Epic 29 `SecretPurpose`) | Epic 41 consumers |
|---|---|---|---|---|
| **Cloud / VPS resource ops** (42-7) — provider-abstracted (Hetzner / generic), like the Git & AI provider abstractions | list / create / resize / delete VPS & cloud resources | `ReadOnly` (list) · `Mutating` (create/resize) · `Destructive` (delete) | `ApiKey` (cloud-provider token) | `deployment-pipeline` (infra tasks via 41-29) · **41-22** incident/rollback · **41-23** capacity & health |
| **Feature-flag / config toggle** (42-8) | read & flip feature flags / runtime config | `Mutating` (non-prod) · `Destructive` (prod flag / kill-switch) | `ApiKey` (flag provider) | `deployment-pipeline` promotion · **41-22** kill-switch · 41-29 `infra` kind |
| **Deploy control** (42-8) | trigger / promote / rollback / gate a release | `Destructive` (prod) · `Mutating` (staging) | `ApiKey` or `SigningKey` (deploy platform) | `deployment-pipeline` · **41-22** rollback · **41-24** release notes trigger |
| **Authenticated HTTP / external API** (42-9) — generic, host+method allowlisted, per-endpoint bound | one authenticated REST call to a bound endpoint | `Mutating` (default; `ReadOnly` for GET-only bindings) | `ApiKey` (per-endpoint) | **41-5** stakeholder update · **41-7** standup publish · **41-24/25/26** docs publish · any integration |
| **MCP-exposed tools** (42-6) | whatever an external MCP server exposes | inherited/declared per tool (deny-by-default until classified) | per bound server | open-ended — any future kind |

Each family declares its `PermissionClass`, `AutonomyFloor`, and `RequiredSecret`, so it is governed by
42-1–42-5 with **zero bespoke security code** — a new tool is a descriptor + an executor, not a new
gate.

## Stories

| Story | Title | Purpose (one line) |
|---|---|---|
| **42-1** | Tool Contract & Registry Evolution | Add `ToolDescriptor` (category / permission class / autonomy floor / secret requirement / suspends) to the `IToolExecutor` surface and a dynamic `Register`/`Unregister` seam to `IToolExecutorRegistry`; annotate the six built-ins. |
| **42-2** | Tool Binding & Config Store (two-scoping) | Persist per-principal tool enablement + config as `tool_bindings` (user_id XOR tenant_id), mirroring `prompt_overrides`; define the single-user & SaaS resolution order. |
| **42-3** | Per-Tool Permission & Autonomy Gating | Extend `ResolveToolsActivity` to return the permitted + autonomy-eligible subset for an agent+principal; route `Destructive`/above-floor tools to the orchestrator via the `AcceptanceRequest` channel. |
| **42-4** | Tool Credential / Secret Binding | Bind external-touching tools to `ISecretStore` (per-tenant SaaS / per-user single-user); file the reveal-to-runtime-consumer dependency on Epic 29; guarantee no-secret-in-logs/events. |
| **42-5** | Tool-Use DCB Audit | Emit durable `TOOL.INVOKED` / `TOOL.SUCCEEDED` / `TOOL.FAILED` / `TOOL.DENIED` / `TOOL.ESCALATED` DCB events (secret-redacted args, `issueId`/`tenantId` tags) at the `ParallelToolExecutor` hook, alongside the ephemeral `TOOL_LOOP.*` SSE stream. |
| **42-6** | MCP Integration | Let external MCP servers expose tools into the registry via the 42-1 dynamic path, wrapped as `IToolExecutor` with the same permission/autonomy/secret/audit treatment as native tools. |
| **42-7** | Cloud / VPS Resource Operations Tool | Provider-abstracted (Hetzner + generic) cloud/VPS list/create/resize/delete tool family. |
| **42-8** | Feature-Flag & Deploy-Control Tools | Feature-flag / config-toggle and deploy trigger/promote/rollback tools — the release-control family. |
| **42-9** | Authenticated HTTP / External-API Tool | Generic host+method-allowlisted authenticated REST tool covering publish/notify/integration needs. |

## Sequencing

**Wave 0 — the contract.** **42-1** (descriptor + dynamic registry). Everything depends on it.

**Wave 1 — governance rails (parallel after 42-1).** **42-2** (binding store) → **42-3** (gating in
`ResolveToolsActivity`); **42-4** (secret binding); **42-5** (DCB audit). Together these are the
security envelope every family and MCP tool inherits — no family ships before them.

**Wave 2 — the open catalog.** **42-6** (MCP) — the dynamic-tool path proving a non-native tool obeys
42-1–42-5.

**Wave 3 — the families (parallel).** **42-7** cloud/VPS, **42-8** flags & deploy, **42-9** HTTP —
each a descriptor + executor on the Wave-1 rails. Order by Epic 41 demand: 42-9 (unblocks the most
41 workflows: docs publish, stakeholder, standup) first, then 42-8, then 42-7.

## Out of scope (deliberately not this epic)

- **A general plugin/marketplace runtime for arbitrary code.** MCP (42-6) is the extensibility path;
  loading untrusted in-process assemblies is not. — *MCP gives the open catalog with a process boundary
  and the same governance; in-proc plugins reopen the trust surface this epic closes.*
- **Rewriting the accept gate or the secret store.** This epic *composes* the Epic 39 `AcceptanceRequest`
  channel and the Epic 29 `ISecretStore`; it adds neither a second acceptor nor a second cabinet.
- **Building the flag / deploy / cloud *providers themselves*.** 42-7/42-8/42-9 define the
  provider-abstracted tool + one reference driver each (Hetzner, a flag provider, the deploy platform);
  exhaustive driver coverage per vendor is follow-on, exactly as the Git/AI provider abstractions grew.
- **The reveal-to-runtime-consumer secret path.** Named and depended-on here (42-4), but it is an
  **Epic 29 extension**, not built in Epic 42.

## Dependencies

- **Epic 29 (secrets):** `ISecretStore` / `SecretRef` / `SecretScope` / `SecretPurpose` **exist**
  (Story 29-1). **Hard-blocked capability:** an authorized *reveal-to-runtime-consumer* path (29-3
  reveal-once UX + `ISecretAccessAuditor` extension) — 42-4/42-7/42-8/42-9's agent path waits on it;
  the human-assigned path (Epic 41 rule 4) does not.
- **Epic 39:** the `AcceptanceRequest` workflow↔orchestrator channel + autonomy dial (42-3 routing),
  the DCB emitter drain (`TammaEventEmitter` → `tamma:events` → `EventDrain`, used by 42-5),
  resumable-by-design (42-1 `Suspends`).
- **Epic 41 / 41-29:** the consumers. 41-29's `TaskKind`→workflow map is unchanged; this epic supplies
  the tools those dispatched workflows resolve. `docs`/`infra`/`design` agent paths light up as
  42-7/42-8/42-9 land.
- **Existing surface (extended, not replaced):** `IToolExecutor`, `IToolExecutorRegistry` /
  `ToolExecutorRegistry`, `ResolveToolsActivity`, `ParallelToolExecutor`, `ToolCallValidator` +
  `ActionGate`, `IToolLoopEventSink` / `ToolLoopEventEmitter`.

## Open design questions (worth a decision)

1. **Secret reveal for runtime tool execution (biggest).** `ISecretStore` never returns plaintext
   through its public signature. Do we (a) extend Epic 29 with an audited *reveal-to-consumer* API for
   tool execution, or (b) an injection seam that hands the tool a short-lived credential it never
   stores? 42-4 assumes (a) is filed as an Epic 29 story; confirm the direction.
2. **Descriptor default: deny vs. read-only.** An un-annotated tool defaults to `Destructive` / floor
   `100` (deny-by-default). Safe, but every dynamically-registered/MCP tool is inert until classified.
   Accept the friction, or default MCP tools to `ReadOnly` and require explicit elevation to write?
3. **Where does destructive-tool routing suspend?** 42-3 routes a destructive/above-floor tool call to
   the orchestrator via `AcceptanceRequest`. Is a **tool invocation** an acceptance decision (reuse the
   accept-gate machinery verbatim) or a distinct `ToolAuthorizationRequest` family on the same channel?
   Affects whether tool authorizations show up in the existing Task View unchanged.
4. **MCP server trust & tenancy.** In SaaS, may a `tenant_admin` register an arbitrary MCP server
   (tenant-scoped), or is the MCP server allowlist platform-owned? Determines whether 42-6 needs a
   per-tenant MCP registration surface or only a platform one.
</content>
