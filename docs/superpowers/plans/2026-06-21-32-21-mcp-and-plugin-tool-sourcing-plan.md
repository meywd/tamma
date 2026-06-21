# Story 32-21 — MCP & Plugin Tool Sourcing (C#) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21

**Goal:** Source **MCP-server tools** and **plugin tools** into the C# managed-run tool catalog so the
32-5 `InlineToolLoopRunner` can use them, with the catalog = **built-in executors ∪ enabled-MCP-server
tools ∪ enabled-plugin tools** behind the **single** `IToolExecutorRegistry.GetAllowed(allowlist)`
seam, intersected with the agent's allowed-tool set (32-2/32-18), every invocation routed through the
existing `ToolHookRegistry`/`IContentSanitizer`/`RedactSecrets` path. Per-tenant MCP-server config +
credentials (Epic 29 cabinet, by reference) are **enablement-gated** by this story's OWN
`McpServerEnablement` entity + `IMcpServerEnablementReader` keyed by `McpServerId` (mirrors the 32-16
PATTERN, SEPARATE entity — NOT the agent reader). The engine holds no key, runs no tool, and opens no
MCP socket — all of it lives in `Tamma.Api`.

**Story file:** `docs/stories/epic-32/story-32-21/32-21-mcp-and-plugin-tool-sourcing.md`
**Design specs:** `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§4, §6 item 2, §7.3),
`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§1, §2.6 step 5)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (`Tamma.Api` + `Tamma.Activities` + `Tamma.Data`).
Tests in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale; plain `dotnet build` needs no wrapper).
**All C# — there is no TypeScript execution path** (`packages/api` is deleted; `packages/mcp-client` is a
**behavioural reference** only, not a runtime dependency).

---

## Non-goals (YAGNI guard)

- **NO change to the runner / endpoint / resilience (32-5).** This plan plugs a wider catalog into the
  **unchanged** `IToolExecutorRegistry` seam the runner already calls. It does not touch the endpoint,
  the credential resolver, metering, retry, or the thin-client cutover.
- **NO second tool-loop / sanitizer / validator.** Reuse the runner's existing
  `ToolHookRegistry`/`IContentSanitizer`/`RedactSecrets` path; MCP/plugin executors return raw content
  and the runner sanitizes — exactly like built-in executors.
- **NO MCP resources or prompts.** This story sources **tools** only. MCP resources (RAG) are Epic 6;
  MCP prompts are out of scope (prompts come from Epic 27).
- **NO cabinet storage/rotation.** Epic 29 owns the cabinet; this story **reads** credentials by
  reference via `IRuntimeSecretResolver`.
- **NO streaming of MCP tool progress.** Buffered results only; live streaming is the "Streaming run
  tap" follow-on.
- **NO new control-plane table by default.** MCP config + enablement are tenant-schema. Only if a
  reviewer mandates CP-residency do the DROP-list + `ControlPlaneDbContextModelTests` edits apply.

---

## Current-state findings (verified 2026-06-21, worktree @ epic32-specs)

| Seam | Where it is today | How 32-21 uses it |
|---|---|---|
| **Tool registry seam** | `Tamma.Activities/LlmCall/Tools/IToolExecutorRegistry.cs` — `GetExecutor`/`IsAllowed`/`GetAll`/`GetAllowed(string[]?)`. Built-in impl `ToolExecutorRegistry.cs` (six executors). | `ICompositeToolCatalog : IToolExecutorRegistry` wraps built-in ∪ MCP ∪ plugin; runner's `GetAllowed` call site unchanged. |
| **Tool loop / sanitization** | 32-5 `InlineToolLoopRunner` (extracted from `CallLlmInlineActivity.AgenticToolLoop`) — `ToolHookRegistry` + `IContentSanitizer` (`Tamma.Activities/Security/IContentSanitizer.cs`) + `RedactSecrets`. | Unchanged chokepoint; MCP/plugin tool outputs traverse it identically. |
| **Per-tenant enablement** | 32-16 `ITenantAgentEnablementReader.IsEnabledForPrincipalAsync(agentId, principal, ct)` (catalog membership, principal-XOR, `UNIQUE NULLS NOT DISTINCT`; CP-resident, keyed by `AgentId`). | Mirror the **PATTERN** in an OWN tenant-schema `McpServerEnablement` entity + `IMcpServerEnablementReader` keyed by `McpServerId` — SEPARATE entity (different schema residency + target id), NOT the agent reader. |
| **Cabinet secret read** | Epic 29 `IRuntimeSecretResolver` (`Tamma.Api/Services/Secrets/Stopgap/IRuntimeSecretResolver.cs`) — `GetAsync(ref)`. | Read MCP credential by reference at connect; request-scoped, never logged/returned. |
| **Agent allowed-tool set** | 32-2/32-18 resolver → `ResolvedAgentConfig { … AllowedTools }`. | The union catalog is intersected with this. |
| **TS MCP client (reference)** | `packages/mcp-client/` — `client.ts`, `ConnectionPool`/`HealthChecker`, transports `stdio/sse/websocket`, `registry.ts`, `security/{validator,rate-limiter}.ts`, `cache/*`, `audit.ts`. | Behavioural reference for the C# pool/transports/validators/rate-limiter/cache we re-implement on top of the .NET MCP SDK. |
| **C# MCP gap** | `ProviderSession.cs:87` "MCP transport not yet ported." | Closed by this story. |
| **Mode** | `ITammaModeProvider` (`TammaMode.cs`), process-stable. | Drives principal keying (UserId vs TenantId) for config/enablement. |

**Pre-work (do before Phase 1):** research the current `ModelContextProtocol` .NET SDK (package name,
version, transport coverage stdio/http-sse/websocket) via WebSearch/Context7 — **do not assume the API
shape**. Confirm 32-5 / 32-16 / 32-2-18 / Epic-29 interfaces are landed (or stub against them with fakes).

---

## Phase 0 — Strategy spike + dependency confirmation

- [ ] Confirm the recommended strategy (story §Strategy decision): **.NET MCP SDK behind `IMcpTransport`,
      security layer kept in-house.** Validate the SDK covers `stdio` + `http/sse`; `websocket` optional.
- [ ] If the SDK is insufficient for a required transport, fall back to a hand-rolled port of that single
      transport (TS `transports/*` as reference) behind the same `IMcpTransport` seam — no catalog change.
- [ ] Confirm 32-5's `IToolExecutorRegistry` seam + runner sanitization path; review 32-16's
      enablement PATTERN (XOR/index/RBAC) as the shape to mirror in this story's OWN `McpServerEnablement`
      entity (keyed by `McpServerId`); confirm Epic 29 `IRuntimeSecretResolver`.

## Phase 1 — Transport + connection layer (`Tamma.Api/Services/Mcp/`)

- [ ] **Test first:** `McpTransportTests` — `StdioMcpTransport` + `HttpSseMcpTransport` connect, list tools,
      invoke a tool against a fake MCP server; `IMcpTransport` honoured.
- [ ] `IMcpTransport` (connect / list-tools / invoke / dispose); `StdioMcpTransport` (local process),
      `HttpSseMcpTransport` (remote endpoint). Inject credential at connect (header/env/arg per transport).
- [ ] `IMcpConnectionPool` / `McpConnectionPool` + health check (mirror TS `ConnectionPool`/`HealthChecker`):
      pooled, per-server connect/discovery **timeout**, fail-soft on connect error.
- [ ] `McpServerRateLimiter` (mirror TS `RateLimiterRegistry`) — per-server rate limiting.
- [ ] **Credential safety test:** no auth material logged/returned by transports or the pool.

## Phase 2 — Per-tenant MCP-server config + enablement (`Tamma.Data` + endpoints)

- [ ] **Test first:** `McpServerEndpointsTests` — CRUD (owner/admin create/update/delete; **member → 403**);
      enable/disable via this story's OWN `McpServerEnablement` (keyed by `McpServerId`, mirrors the 32-16
      PATTERN); literal-secret config **rejected** at write; cross-tenant isolation.
- [ ] `McpServer` entity (`Tamma.Data/Entities/`) — **tenant-schema** (performance/config data is
      tenant-scoped): `TenantId`/`UserId` principal-XOR, `Name`, `Transport`, `Command`/`Url` (no secret),
      **`CredentialRef`** (cabinet reference — NEVER a literal secret), `ToolAllowPrefixes?`. EF config:
      principal-XOR CHECK + `UNIQUE NULLS NOT DISTINCT (TenantId, UserId, Name)`, mirroring `AgentRoleSelection`.
- [ ] `McpServerEnablement` — this story's **OWN** tenant-schema entity keyed by `McpServerId` (mirrors
      the 32-16 `TenantAgentEnablement` PATTERN — principal-XOR CHECK + `UNIQUE NULLS NOT DISTINCT
      (TenantId, UserId, McpServerId)` — but a SEPARATE entity, NOT `TenantAgentEnablement`). Expose an
      OWN `IMcpServerEnablementReader.IsEnabledForPrincipalAsync(mcpServerId, principal, ct)` +
      `McpServerEnablementService` (enable/disable). Do NOT consume `ITenantAgentEnablementReader` or key
      on `AgentId`/`AgentSurfaceId`.
- [ ] `McpServerEndpoints` — tenant-scoped CRUD + enable/disable (`tenant_owner`/`tenant_admin`; member 403,
      read allowed). Write-time literal-secret rejection.
- [ ] **DROP-list note:** tables are **tenant-schema** → NOT in `Program.cs` startup-reset DROP list, NO
      `ControlPlaneDbContextModelTests` edit. (Only if reviewer mandates CP-residency: append to **both**.)
- [ ] Extend the **single** EF migration snapshot (do not branch it) with the new tenant-schema tables.

## Phase 3 — Tool sources + executors (`Tamma.Api/Services/Agents/Tools/`)

- [ ] **Test first:** `McpToolSourceTests` — enabled servers → `IToolExecutor[]` (`mcp__<server>__<tool>`);
      un-enabled server → **zero tools**; cabinet credential read via fake `IRuntimeSecretResolver`;
      connect failure → zero tools + `MCP.SERVER.CONNECT.FAILED` + WARN, **run proceeds**; discovery cache
      hit/invalidation.
- [ ] `IMcpToolSource` / `McpToolSource` — flow: list tenant servers → filter by `IMcpServerEnablementReader.IsEnabledForPrincipalAsync(server.Id, principal, ct)` (OWN reader, keyed by `McpServerId`) → per server
      (fail-soft, isolated): read cred by ref → pool-connect → list/cache tools → wrap as `McpToolExecutor`.
- [ ] `McpToolExecutor : IToolExecutor` — invokes one MCP tool over the pooled transport, returns **raw**
      content (the runner sanitizes). `IMcpToolDiscoveryCache` keyed `(tenantId, server, configHash)`, short
      TTL, invalidated on config/enablement change.
- [ ] `IPluginToolSource` / `PluginToolSource` + `PluginToolExecutor` — enabled plugins → `IToolExecutor[]`,
      same enablement/hook/intersection path. (Plugin loading mechanism per Epic 9; stub if not landed.)

## Phase 4 — Composite catalog + runner wiring

- [ ] **Test first:** `CompositeToolCatalogTests` — union behind one `GetAllowed`; allowlist ∩ agent-allowed
      intersection; unknown/un-enabled name → `null` executor (no silent built-in); `GetExecutor` resolves
      MCP/plugin tools by name.
- [ ] `ICompositeToolCatalog : IToolExecutorRegistry` / `CompositeToolCatalog` — assembled per run with
      `ToolCatalogScope { TenantId, Principal, AgentAllowedTools, CorrelationId }`; `GetAllowed` =
      (built-in ∪ enabled-MCP ∪ enabled-plugin) filtered by allowlist **and** intersected with
      `AgentAllowedTools`.
- [ ] `Program.cs` (API) — register `IMcpToolSource`/`IPluginToolSource`/`ICompositeToolCatalog` (scoped),
      `IMcpConnectionPool`/`IMcpToolDiscoveryCache` (singleton); construct the 32-5 `InlineToolLoopRunner`
      with `ICompositeToolCatalog` as its `IToolExecutorRegistry`; `app.MapMcpServerEndpoints()`. Engine
      registers **nothing** MCP.

## Phase 5 — Sanitization parity + events + isolation tests

- [ ] **Sanitization parity (AC5):** golden-equality test — an MCP tool output with a secret + injection
      pattern is sanitized + `RedactSecrets`'d **identically** to a built-in tool's same output; no path
      bypasses `ToolHookRegistry`.
- [ ] **Events (AC9):** `MCP.SERVER.REGISTERED/ENABLED/DISABLED/CONNECT.FAILED` + `AGENT.TOOL.SOURCED`
      (builtIn/mcp/plugin counts) emitted from `Tamma.Api` via tenant `IEventRepository`, key-free, tagged.
- [ ] **Engine-holds-nothing (AC7):** grep `Tamma.ElsaServer` → zero MCP transport/connection/credential
      refs; composite + MCP types registered only in `Tamma.Api`.
- [ ] **Cross-tenant isolation:** tenant A's MCP server never visible/usable in tenant B's run.
- [ ] **Credential safety (AC3):** secret never in config rows, logs, tool results, or event payloads.

## Phase 6 — Hardening + docs

- [ ] Per-server connect/discovery timeouts tuned; rate-limit defaults sane; cache TTL/invalidation verified.
- [ ] Mode-parameterized tests (single-user UserId-keyed vs SaaS TenantId-keyed) for config/enablement.
- [ ] Update MCP setup docs (how a tenant registers an MCP server, sets a `CredentialRef`, enables it).
- [ ] Final grep gates: one `GetAllowed` seam; zero engine MCP refs; zero secret in payloads.

---

## Test list (xUnit, `Tamma.Api.Tests`)

1. `CompositeToolCatalogTests` — union behind one `GetAllowed`; allowlist ∩ agent-allowed; unknown → null.
2. `McpToolSourceTests` — enabled→tools; un-enabled→zero; cabinet cred read; connect-fail fail-soft; cache.
3. `McpTransportTests` — stdio + http/sse connect/list/invoke against a fake server; `IMcpTransport` honoured.
4. `McpServerEndpointsTests` — CRUD owner/admin; member 403; literal-secret rejected; cross-tenant isolation.
5. Sanitization-parity test — MCP output sanitized + redacted identically to built-in (golden equality).
6. Engine-holds-nothing grep test — zero MCP transport/credential refs in `Tamma.ElsaServer`.
7. Credential-safety test — secret absent from config/logs/results/events.
8. Events test — `MCP.SERVER.*` + `AGENT.TOOL.SOURCED` emitted from API, key-free, tagged.
9. Plugin-source test — enabled-plugin tools join the union + same hook/intersection path.
10. Mode-parameterized config/enablement (single-user UserId vs SaaS TenantId).

---

## Risks (carried from the story)

- **Tool bypasses sanitization** → MCP/plugin executors return raw content only; the runner is the single
  sanitization chokepoint; golden-equality test vs built-in.
- **Secret leak** → config by `CredentialRef` only; literal-secret writes rejected; request-scoped cabinet
  read; safety test asserts absence everywhere.
- **Engine opens a socket/key** → all MCP types in `Tamma.Api`; grep gate on `Tamma.ElsaServer`.
- **Broken server fails the run** → fail-soft per-server isolation + timeout + WARN + `CONNECT.FAILED`.
- **Un-enabled/disallowed tool reaches the model** → enablement gate ∧ allowlist ∧ agent-allowed; unknown
  → null (no silent fallback, `feedback_resolution_no_empty_fallback`).
- **.NET MCP SDK immature** → `IMcpTransport` seam isolates it; per-transport hand-port fallback.
- **Mis-scoping config as CP** → default tenant-schema (no DROP-list/model-test churn); flagged in Dev Notes.
- **EF parallel-migration hazard** → extend the single migration snapshot, never branch it.

---

## Definition of done

- [ ] Managed-run catalog = built-in ∪ enabled-MCP ∪ enabled-plugin behind one `GetAllowed`, ∩ agent-allowed.
- [ ] Per-tenant MCP-server config (cabinet creds by reference) enablement-gated by the OWN `McpServerEnablement` entity + `IMcpServerEnablementReader` keyed by `McpServerId` (mirrors the 32-16 PATTERN, SEPARATE entity); member 403.
- [ ] Every MCP/plugin invocation traverses `ToolHookRegistry`/`IContentSanitizer`/`RedactSecrets`.
- [ ] Engine holds no MCP socket/key (grep clean); all MCP lives in `Tamma.Api`.
- [ ] Fail-soft per server; discovery cache; cross-tenant isolation; no secret in any payload.
- [ ] Strategy decision implemented (.NET MCP SDK behind `IMcpTransport`); stdio + http/sse covered.
- [ ] All Phase 1–6 tests green via `sg docker -c "dotnet test apps/tamma-elsa/..."`.
- [ ] No `sprint-status.yaml` / git / build changes in this story's commits (controller owns those).
