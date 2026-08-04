# Epic 42: Agent Capability & Tool Layer

**Status:** Planned / docs — briefs authored, no code yet. Makes the tool layer first-class: extensible (native + dynamic + MCP), secured (secret-bound, redacted), and autonomy-gated (per-role, per-autonomy, per-mode).
**Stories:** 9 (42-1 through 42-9), all drafted
**Layer:** Layer 4 (integration/orchestration)
**Depends on:** Epic 29 (`ISecretStore` exists; a *reveal-to-runtime-consumer* extension is hard-blocked), Epic 39 (`AcceptanceRequest` channel + autonomy dial + DCB emitter), Epic 41 / 41-29 (the consumers)

> This epic is **backlog** — scoped and specified, not built. It *extends* the real `IToolExecutor` / `IToolExecutorRegistry` surface; it does not design a parallel framework.
>
> **Correction (2026-07-29):** earlier text named `ResolveToolsActivity` as part of that surface. **Story 43-0 DELETED `ResolveToolsActivity`** (`apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs`) — it was dead code with zero references outside its own file and shipped a third, wrong tool-name vocabulary. Nothing replaced it: tool selection is not a workflow activity. Where this page said 42-3 "extends `ResolveToolsActivity`", the work now has to land wherever the tool set is actually assembled for the model — `IToolExecutorRegistry` and the API-side tool loop (`InlineToolLoopRunner` / `ParallelToolExecutor`). Epic 42 is backlog, so this is a re-siting of unstarted work, not a lost implementation.

## 1. Overview

Tamma's workflows produce typed **documents** (Epic 39) and dispatch them by kind (Epic 41), and an agent can *act* through the tool-execution framework in `Tamma.Activities/LlmCall/Tools/`. But that framework is **coding-only**. `Program.cs` registers exactly **six** `IToolExecutor`s — `FileReadTool`, `FileWriteTool`, `GitOperationsTool`, `RunTestsTool`, `SearchCodeTool`, `ShellExecuteTool`. So an agent can edit the repo, run git, run tests, and run a shell — but has **no first-class tool** to reach a cloud/VPS resource, flip a feature flag, drive a deploy, or call an external API. Worse, the catalog is closed:

- **The registry is compile-time static.** `ToolExecutorRegistry` is populated once from `IEnumerable<IToolExecutor>` injected by DI. Adding a tool means shipping a class and a DI line. There is **no dynamic / per-deployment / per-tenant registration** and **no MCP path**.
- **A tool declares no governance.** `IToolExecutor` exposes `ToolName`, `Description`, `InputSchema`, `ExecuteAsync` — and nothing about **who may call it**, **at what autonomy level**, **what credential it needs**, or **how destructive it is**. `ToolCallValidator` + `ActionGate` block dangerous *shell strings*, but that is a per-command denylist, not a per-tool permission model.
- **No credential binding.** No tool binds to a stored secret. `ShellExecuteTool` has been silently standing in for a real capability layer.
- **No durable tool-use audit.** `ToolLoopEventEmitter` emits ephemeral `TOOL_LOOP.*` SSE progress events; nothing writes a **durable DCB `TOOL.*` row** the way documents get a `DOCUMENT.*` trail.

**This epic makes the tool layer first-class — the same governance documents already get.** It composes, not reinvents: the Epic 39 accept gate and the Epic 29 secret store.

## 2. How it underpins Epic 41

Epic 41's **41-29** (Task-Level Flow Router) dispatches each `PlanTask` to the workflow matching its `TaskKind`. But the dispatched workflow's agent **has no tool to do non-code work**: a `docs` task needs a **publish** capability, an `infra` task needs **deploy-control** and **cloud/VPS** tools, `41-22`/`41-23` need cloud ops and a **feature-flag kill-switch**, `41-5`/`41-7` need an **authenticated HTTP** tool. So Epic 42 is the **missing foundation under Epic 41**: 41-29 can *route* to a `docs`/`infra`/`design` workflow, but until this epic lands that workflow's agent falls back to `ShellExecute` or the human-assigned path (41 rule 4). This epic lights up the *agent* path of the non-code kinds — with **no change to the router** (41-29's `kind→workflow` map is untouched; this epic populates the tools those workflows resolve).

## 3. The tool contract (Story 42-1)

Today `IToolExecutor` is `{ ToolName, Description, InputSchema, ExecuteAsync }` and *must never throw*. Story 42-1 keeps all four members and the never-throw contract, and adds a governance descriptor surfaced via a **`ToolDescriptor`** with a **fail-safe default** — most-restrictive, never permissive — so an un-annotated tool is *denied by default*.

| Field (new) | Type | Meaning | Fail-safe default |
|---|---|---|---|
| `Category` | `Native` \| `Mcp` \| `ProviderAbstracted` | Where the tool comes from; drives which registration path owns it | `Native` |
| `PermissionClass` | `ReadOnly` \| `Mutating` \| `Command` \| `Destructive` | Governance tier | `Destructive` (deny-by-default) |
| `AutonomyFloor` | `int` (70–100) | Minimum autonomy dial at which an `AgentRole` may invoke the tool directly; below it, routed to the orchestrator/human | `100` |
| `RequiredSecret` | `SecretRequirement?` | Declares the credential the tool needs (Epic 29 `SecretPurpose`) | `null` |
| `Suspends` | `bool` | Tool may start a long external op and suspend the workflow (resumable-by-design) | `false` |

The six built-ins each declare a real descriptor (e.g. `SearchCodeTool` → `ReadOnly`, floor 70, no secret; `ShellExecuteTool` → `Command`, floor 85, still `ActionGate`-gated). `IToolExecutorRegistry` gains a **dynamic registration** seam (`Register`/`Unregister`) so tools can be added per deployment/tenant and by MCP — *vocabulary of governance is static; the catalog is dynamic*, mirroring Epic 39.

## 4. Permission + autonomy + secret model (single-user and SaaS)

Per CLAUDE.md's universal rule, every tenant-aware surface answers ownership **twice**, mirroring the **Prompt Store** two-scoping pattern (Story 42-2 owns a `tool_bindings` table analogous to `prompt_overrides`, `CHECK (user_id XOR tenant_id)`):

- **single-user mode** — the sole user owns tool enablement + per-role/per-autonomy grants; keyed by `user_id`. Resolution: user binding → system default descriptor.
- **SaaS mode** — `tenant_owner` / `tenant_admin` owns the tenant's grants; `member` users get the resolved grant with **no edit access** (403 on write); keyed by `tenant_id`. **No per-user override layer.** Resolution: tenant binding → system default.

**Enforcement is in the tool-resolution seam (Story 42-3), not a new gate.** *(Was "`ResolveToolsActivity`" — that activity was deleted by Story 43-0 on 2026-07-29 and never replaced; 42-3 must site this in `IToolExecutorRegistry` / the API-side tool loop instead.)* The resolver returns only the subset that is (a) enabled for the principal, (b) permitted for the agent's `AgentRole`, and (c) autonomy-eligible (`AutonomyFloor ≤ current dial`). A needed tool that is `Destructive` or above the floor is **not handed to the agent as callable**; instead the step routes that action to the orchestrator/human over the **existing** `AcceptanceRequest` channel — the same shape as the accept gate. The autonomy dial is read live, never cached.

**Credentials (Story 42-4)** bind through Epic 29's `ISecretStore` — `SecretScope.Tenant` in SaaS, `SecretScope.Platform`/user-owned in single-user. Secrets are **never** in tool args logged, in `ToolExecutionResult.Output`, in DCB `TOOL.*` events, or error messages (reuse `ErrorRedactor`). Reconciliation with Epic 29: `ISecretStore` **now exists** (Story 29-1 landed), but by design it *never returns plaintext through a public signature*. A tool that authenticates to Hetzner/Slack needs the live secret at execution time — so 42-4 **files a hard dependency** on an authorized, audited *reveal-to-runtime-consumer* path (an Epic 29 extension); until it lands, external-touching tools run only in the human-assigned path.

## 5. Tool families

| Family (story) | What it does | Permission class | Secret (`SecretPurpose`) | Epic 41 consumers |
|---|---|---|---|---|
| **Cloud / VPS resource ops** (42-7) — provider-abstracted (Hetzner / generic) | list / create / resize / delete VPS & cloud resources | `ReadOnly` (list) · `Mutating` (create/resize) · `Destructive` (delete) | `ApiKey` | `deployment-pipeline` · 41-22 · 41-23 |
| **Feature-flag / config toggle** (42-8) | read & flip flags / runtime config | `Mutating` (non-prod) · `Destructive` (prod flag / kill-switch) | `ApiKey` | `deployment-pipeline` · 41-22 · 41-29 `infra` |
| **Deploy control** (42-8) | trigger / promote / rollback / gate a release | `Destructive` (prod) · `Mutating` (staging) | `ApiKey` / `SigningKey` | `deployment-pipeline` · 41-22 · 41-24 |
| **Authenticated HTTP / external API** (42-9) — host+method allowlisted | one authenticated REST call to a bound endpoint | `Mutating` (default; `ReadOnly` for GET-only) | `ApiKey` (per-endpoint) | 41-5 · 41-7 · 41-24/25/26 · any integration |
| **MCP-exposed tools** (42-6) | whatever an external MCP server exposes | inherited/declared per tool (deny-by-default until classified) | per bound server | open-ended |

Each family declares its `PermissionClass`, `AutonomyFloor`, and `RequiredSecret`, so it is governed by 42-1–42-5 with **zero bespoke security code** — a new tool is a descriptor + an executor, not a new gate.

## 6. Stories

| Story | Title | Purpose |
|-------|-------|---------|
| 42-1 | Tool Contract & Registry Evolution | Add `ToolDescriptor` to the `IToolExecutor` surface + a dynamic `Register`/`Unregister` seam; annotate the six built-ins. |
| 42-2 | Tool Binding & Config Store (two-scoping) | Persist per-principal tool enablement as `tool_bindings` (user_id XOR tenant_id), mirroring `prompt_overrides`. |
| 42-3 | Per-Tool Permission & Autonomy Gating | Return only the permitted + autonomy-eligible subset at the tool-resolution seam (**not** `ResolveToolsActivity` — deleted by Story 43-0, 2026-07-29; site it in `IToolExecutorRegistry` / the API-side tool loop); route `Destructive`/above-floor tools to the orchestrator via `AcceptanceRequest`. |
| 42-4 | Tool Credential / Secret Binding | Bind external-touching tools to `ISecretStore`; file the reveal-to-runtime-consumer dependency on Epic 29; guarantee no-secret-in-logs/events. |
| 42-5 | Tool-Use DCB Audit | Emit durable `TOOL.INVOKED` / `TOOL.SUCCEEDED` / `TOOL.FAILED` / `TOOL.DENIED` / `TOOL.ESCALATED` events (secret-redacted) at the `ParallelToolExecutor` hook. |
| 42-6 | MCP Integration | Let external MCP servers expose tools into the registry via the 42-1 dynamic path, with the same permission/autonomy/secret/audit treatment as native tools. |
| 42-7 | Cloud / VPS Resource Operations Tool | Provider-abstracted (Hetzner + generic) cloud/VPS list/create/resize/delete family. |
| 42-8 | Feature-Flag & Deploy-Control Tools | Feature-flag / config-toggle and deploy trigger/promote/rollback — the release-control family. |
| 42-9 | Authenticated HTTP / External-API Tool | Generic host+method-allowlisted authenticated REST tool for publish/notify/integration. |

## 7. Sequencing

- **Wave 0 — the contract.** 42-1 (descriptor + dynamic registry). Everything depends on it.
- **Wave 1 — governance rails (parallel after 42-1).** 42-2 (binding store) → 42-3 (gating); 42-4 (secret binding); 42-5 (DCB audit). The security envelope every family inherits — no family ships before them.
- **Wave 2 — the open catalog.** 42-6 (MCP) — proving a non-native tool obeys 42-1–42-5.
- **Wave 3 — the families (parallel).** 42-9 (unblocks the most Epic 41 workflows) first, then 42-8, then 42-7.

## 8. Out of scope

- **A general plugin/marketplace runtime for arbitrary in-process code.** MCP (42-6) is the extensibility path with a process boundary and the same governance.
- **Rewriting the accept gate or the secret store.** This epic composes the Epic 39 `AcceptanceRequest` channel and the Epic 29 `ISecretStore`.
- **Building the flag / deploy / cloud *providers themselves*.** 42-7/42-8/42-9 define the abstraction + one reference driver each; exhaustive per-vendor coverage is follow-on.
- **The reveal-to-runtime-consumer secret path.** Named and depended-on (42-4), but it is an Epic 29 extension.

## 9. Open design questions

1. **Secret reveal for runtime tool execution (biggest).** Extend Epic 29 with an audited reveal-to-consumer API, or an injection seam that hands the tool a short-lived credential? 42-4 assumes the former is filed as an Epic 29 story.
2. **Descriptor default: deny vs. read-only.** Un-annotated tools default to `Destructive`/floor 100 — safe, but every MCP tool is inert until classified. Accept the friction, or default MCP tools to `ReadOnly`?
3. **Where does destructive-tool routing suspend?** Reuse the accept-gate machinery verbatim, or a distinct `ToolAuthorizationRequest` family on the same channel?
4. **MCP server trust & tenancy.** In SaaS, may a `tenant_admin` register an arbitrary MCP server, or is the allowlist platform-owned?

## 10. Dependencies

- **Epic 29 (secrets):** `ISecretStore` / `SecretRef` / `SecretScope` / `SecretPurpose` exist (29-1). **Hard-blocked capability:** an authorized reveal-to-runtime-consumer path — 42-4/42-7/42-8/42-9's agent path waits on it; the human-assigned path does not.
- **Epic 39:** the `AcceptanceRequest` channel + autonomy dial (42-3 routing), the DCB emitter drain (used by 42-5), resumable-by-design (42-1 `Suspends`).
- **Epic 41 / 41-29:** the consumers. 41-29's `TaskKind`→workflow map is unchanged; this epic supplies the tools those dispatched workflows resolve.
- **Existing surface (extended, not replaced):** `IToolExecutor`, `IToolExecutorRegistry` / `ToolExecutorRegistry`, `ParallelToolExecutor`, `ToolCallValidator` + `ActionGate`, `IToolLoopEventSink` / `ToolLoopEventEmitter`. *(`ResolveToolsActivity` was on this list until 2026-07-29; Story 43-0 deleted it as dead code and nothing replaced it.)*

## 11. See also

- [Epic 41: Full-Team Workflows](Epics/Epic-41-Full-Team-Workflows) — the consumers whose non-code agent paths this epic lights up
- [Epic 39: Document Lifecycle](Epics/Epic-39-Document-Lifecycle) — the accept gate + autonomy dial reused for destructive-tool routing
- [Epic 29: Secret Management](Epics/Epic-29-Secret-Management) — the `ISecretStore` credentials bind through
- [Document Lifecycle](Document-Lifecycle) — the "vocabulary static, catalog dynamic" pattern this epic mirrors
- [Epic 12: Agentic Tool Loop](Epics/Epic-12-Tool-Loop) — the existing tool-execution framework this epic extends
- Story files: [Epic 42 on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-42)

---

_Last updated: 2026-07-24_
