# Story 32-21: MCP & Plugin Tool Sourcing (C#)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **tenant building agent-driven workflows on the managed `call-LLM` path**,
I want the managed run's tool catalog to include **MCP-server tools and plugin tools** — sourced per-tenant from servers/plugins the tenant has enabled, with their credentials read from the Epic 29 cabinet — unioned with the built-in tool executors through the single `IToolExecutorRegistry.GetAllowed(allowlist)` seam, intersected with the agent's allowed-tool set, and every invocation routed through the same `ToolHookRegistry` pre/post sanitization hooks,
So that **the managed run can use my MCP integrations** (filesystem, search, internal APIs, third-party MCP servers) and plugin tools **without the engine ever holding a key, running a tool, or opening an MCP socket** — all tool execution happens inside `Tamma.Api`'s `InlineToolLoopRunner`, server-side, key-free by contract, with the same enablement gate and sanitization that govern the rest of the agent model.

## Priority

P1 — This is the **tool-catalog build-out** for the managed execution layer (deep-dive §4 MCP+plugins; §6 item 2; §7 item 3). 32-5 ships the `InlineToolLoopRunner` with the **built-in catalog only** and explicitly scopes MCP/plugins out to this follow-on. Net-new for C#: MCP/plugins exist today only in the TypeScript `packages/mcp-client`; the C# `ProviderSession.cs:87` records *"MCP transport not yet ported."* This story closes that gap by making MCP-server tools and plugin tools first-class members of the API tool catalog the managed run consumes. It depends on 32-5 (the runner/endpoint that consumes the catalog) and ties to Epic 6 (intelligence/RAG tooling) and Epic 9 (the unified agent API surface).

## Context

### What exists today

- **TypeScript only.** `packages/mcp-client` is a complete MCP client: a `ConnectionPool` + `HealthChecker`, transports for `stdio`/`sse`/`websocket` (`transports/{stdio,sse,websocket}.ts`), a `ToolRegistry`/`ResourceRegistry`/`PromptRegistry` (`registry.ts`), a `RateLimiterRegistry` + config/command/url validators (`security/`), capability + resource caches (`cache/`), an `audit.ts` trail, and request/response `interceptors.ts`. **None of it is reachable from the C# engine or API.**
- **C# has no MCP.** `ProviderSession.cs:87` says *"MCP transport not yet ported."* The C# tool catalog is built-in-only: `IToolExecutorRegistry` (`Tamma.Activities/LlmCall/Tools/IToolExecutorRegistry.cs`) registers six local executors (`file_read`/`file_write`/`shell_execute`/`git_operations`/`run_tests`/`search_code`) and exposes `GetAllowed(string[]? allowlist)` — the single seam through which the tool loop selects tools.
- **The managed run already routes every tool through sanitization.** 32-5's `InlineToolLoopRunner` (extracted verbatim from `CallLlmInlineActivity.AgenticToolLoop`) runs sanitize → multi-turn call → tool-call validation → tool execution → tool-output sanitization + `RedactSecrets` → compaction. Sanitization is via `IContentSanitizer` (`Tamma.Activities/Security/IContentSanitizer.cs`). **This story does not change that pipeline — it makes MCP/plugin tools flow through it identically.**
- **The agent model already has the enablement + cabinet primitives.** 32-16 ships `ITenantAgentEnablementReader.IsEnabledForPrincipalAsync(agentId, principal, ct)` (the per-tenant catalog-membership gate, keyed by `AgentId`). Epic 29's cabinet exposes `IRuntimeSecretResolver` (`Tamma.Api/Services/Secrets/Stopgap/IRuntimeSecretResolver.cs`) for runtime secret reads. This story **mirrors the 32-16 PATTERN** for **MCP servers** as a per-tenant resource — but with its **own separate entity** (`McpServerEnablement`, keyed by `McpServerId`, in the tenant schema), NOT the agent enablement reader (different schema residency + a different target id). It reuses the cabinet's `IRuntimeSecretResolver` shape directly.

### What this story does (deep-dive §4 MCP+plugins)

This story sources **MCP server tools** and **plugin tools** into the API tool catalog used by the managed run, **inside `Tamma.Api`**, where the key lives and the loop runs:

> **The runner's tool catalog = built-in executors ∪ MCP server tools ∪ plugin tools**, unioned through **one** `IToolExecutorRegistry.GetAllowed(allowlist)`, **intersected** with the agent's allowed-tool set (32-2/32-18), with **every** invocation routed through the `ToolHookRegistry` pre/post sanitization hooks + `IContentSanitizer`/`RedactSecrets`.

Concretely:

- A **composite tool source** is assembled per managed run: built-in executors (today's registry), plus MCP-server tools discovered from the tenant's enabled MCP servers, plus plugin tools from the tenant's enabled plugins. They are exposed to the runner as `IToolExecutor` instances behind the **unchanged** `IToolExecutorRegistry` interface — the runner sees one catalog, calls `GetAllowed(allowlist)` once, and is **oblivious** to a tool's origin.
- **MCP servers are a per-tenant resource.** Per-tenant MCP-server config (`name`, transport, command/url, declared tool prefixes) lives in a tenant-scoped store; **credentials live in the Epic 29 cabinet** (read via `IRuntimeSecretResolver`, never in config), and a server is usable in a run only if it is **enabled** for the principal — mirroring the 32-16 enablement **pattern** but with its **own separate `McpServerEnablement` entity keyed by `McpServerId`** (not the agent enablement reader): the tenant enables which MCP servers exist for it; member users just use what's enabled.
- **Plugin tools** are sourced from the tenant's enabled plugins through the same composite source and the same hook/sanitization path.
- **Every invocation** — built-in, MCP, or plugin — is routed through the `ToolHookRegistry` pre/post hooks and `IContentSanitizer`/`RedactSecrets`, so MCP/plugin tool outputs are sanitized and secret-redacted exactly like built-in tool outputs. No tool bypasses the hooks.
- **The allowlist + agent-allowed intersection is preserved.** The union catalog is filtered by `GetAllowed(allowlist)` (the engine's per-call allowlist) **and** intersected with the resolved agent's allowed-tool set (32-2/32-18) — an MCP/plugin tool the agent is not allowed to use is never offered to the model, even if the server is enabled.

### The strategy decision (presented as a first-class design choice — deep-dive §7.3)

Deep-dive §7.3 leaves the MCP transport strategy **open**: *port `mcp-client` to C# vs host the TS client as an API-managed sidecar vs a .NET MCP SDK.* This spec resolves it (see **Technical Design → Strategy decision**) with a recommendation and the trade-offs, because the choice drives the deployment surface, the dependency footprint, and the SaaS isolation story.

### Server-side only (the locked rule 1)

The engine holds no key, runs no tool, and **opens no MCP/provider socket**. MCP connections, plugin loading, and all tool execution happen in `Tamma.Api`, inside the managed run. **Local** tools (`file_read`/`shell_execute`/`git_operations`) execute against the **tenant's sandbox** from inside the managed run — exactly as the built-in executors do today (this story does not change their sandboxing; it only widens the catalog around them).

### Explicitly out of scope (referenced, not implemented here)

- **The endpoint / runner / resilience relocation** — 32-5. This story plugs a wider catalog into the runner 32-5 ships; it does not touch the endpoint, the credential resolver, metering, or the thin-client cutover.
- **Per-tenant MCP-server credentials at rest** beyond reading them via Epic 29's `IRuntimeSecretResolver` — the cabinet's storage/rotation is Epic 29; this story is a **consumer**.
- **MCP resources / prompts (the non-tool MCP capabilities).** The TS client exposes `ResourceRegistry`/`PromptRegistry`; this story sources **tools** only. MCP resources (as a RAG source) are an Epic 6 follow-on; MCP prompts are out of scope (prompts come from Epic 27).
- **Streaming MCP tool progress to the dashboard** — that rides the "Streaming run tap" follow-on (deep-dive §6.4); this story emits buffered tool results into the buffered run.
- **Prompt/response cache** — the capability/resource caches here are MCP-discovery caches, not the LLM prompt/response cache ("Prompt + response cache" follow-on, deep-dive §6.3).

## Acceptance Criteria

1. **One catalog, three sources, one seam.** The managed run's tool catalog is **built-in executors ∪ enabled-MCP-server tools ∪ enabled-plugin tools**, assembled into a single set of `IToolExecutor` instances exposed behind the **unchanged** `IToolExecutorRegistry` interface. The runner calls `GetAllowed(allowlist)` exactly **once** and is agnostic to a tool's origin. No second registry interface, no second `GetAllowed` path.

2. **MCP-server tools are sourced per-tenant, enabled-gated.** An `IMcpToolSource` resolves the tenant's **enabled** MCP servers (via the per-tenant MCP-server store + this story's own `IMcpServerEnablementReader.IsEnabledForPrincipalAsync(mcpServerId, principal, ct)` gate — keyed by `McpServerId`, mirroring the 32-16 PATTERN as a SEPARATE entity, NOT the agent reader), connects over the chosen transport, discovers each server's tools, and wraps each as an `IToolExecutor` (`mcp__<server>__<tool>` naming, mirroring the TS client convention). A server that is **not enabled** for the principal contributes **zero** tools to the catalog — even if configured. Member users cannot enable/disable MCP servers (tenant_owner/tenant_admin only; member → 403 on writes, read allowed).

3. **MCP credentials come from the Epic 29 cabinet, never from config.** Each MCP-server config carries a **secret reference**, not a secret. At connect time the source reads the credential via `IRuntimeSecretResolver` (Epic 29) and injects it into the transport (header/env/arg per transport). The credential is **request-scoped, never logged, never returned, never persisted** in a tool result. Config rows store references only; a config with a literal secret is rejected at write time.

4. **Plugin tools are sourced through the same composite source.** An `IPluginToolSource` resolves the tenant's enabled plugins and wraps each plugin tool as an `IToolExecutor`, joining the same union and the same enablement/hook/sanitization path as MCP tools. Plugin enablement follows the per-tenant model (32-16 shape).

5. **Every invocation routes through the hooks + sanitizer (no bypass).** Built-in, MCP, and plugin tool invocations all pass through the `ToolHookRegistry` pre-hook (argument sanitization/validation) and post-hook (output sanitization), and tool output is run through `IContentSanitizer` + `RedactSecrets` before re-entering the model context — **identical** to the built-in path. A test proves an MCP tool's output is sanitized and secret-redacted exactly like a built-in tool's output. No code path executes a tool outside the hook pipeline.

6. **Allowlist ∩ agent-allowed is enforced.** The union catalog is filtered by the engine's per-call `allowlist` via `GetAllowed(allowlist)` **and** intersected with the resolved agent's allowed-tool set (32-2/32-18). An MCP/plugin tool the agent is not allowed to use is **never** advertised to the model nor executable — even if its server/plugin is enabled and the allowlist permits it. Fail-closed: an unknown/un-enabled tool name resolves to "no executor," not to a silent built-in.

7. **The engine holds no MCP socket or key (rule 1).** All MCP connections, plugin loading, credential reads, and tool execution happen in `Tamma.Api` inside the managed run. `ElsaServer` opens **no** MCP socket and reads **no** MCP credential. A grep over `Tamma.ElsaServer` finds zero MCP transport/connection types and zero `IRuntimeSecretResolver` MCP reads. Local tools (`file_read`/`shell_execute`/`git_operations`) still execute against the **tenant's sandbox** from inside the managed run.

8. **Connection lifecycle is bounded and fail-soft per server.** MCP server connections are pooled/health-checked (mirroring the TS `ConnectionPool`/`HealthChecker`), rate-limited per server, and **connect failures are isolated**: an unreachable or failing MCP server contributes **zero** tools and is logged at WARN with `{ server, tenantId, correlationId }` — it does **not** fail the managed run (the run proceeds with the remaining catalog). A per-server connect/discovery timeout is enforced. Tool-discovery results are cached per `(tenantId, server, configHash)` with a short TTL (the capability cache), invalidated on config/enablement change.

9. **Per-tenant config CRUD + events, enablement-gated.** MCP-server config is created/updated/deleted via tenant-scoped admin endpoints (tenant_owner/tenant_admin; member → 403), and enable/disable goes through this story's **own `McpServerEnablement` entity** (keyed by `McpServerId`; same XOR/unique-index discipline as 32-16's `TenantAgentEnablement`, but a SEPARATE entity — different schema residency + target id). DCB events `MCP.SERVER.REGISTERED` / `MCP.SERVER.ENABLED` / `MCP.SERVER.DISABLED` / `MCP.SERVER.CONNECT.FAILED` and `AGENT.TOOL.SOURCED` (per managed run: counts of built-in/MCP/plugin tools offered) are emitted from `Tamma.Api` via the tenant `IEventRepository`, tagged `{ tenantId, server?, correlationId }`. Tool **invocations** continue to be audited by 32-5/32-6's existing `toolCalls` trail — this story does not duplicate per-invocation events.

10. **Strategy decision is recorded and implemented.** The spec records the chosen MCP transport strategy (port to C# vs TS sidecar vs .NET MCP SDK) with justification and trade-offs (Technical Design → Strategy decision), and the implementation follows the recommended path. The chosen transports cover at least `stdio` and `http`/`sse` (the two the managed API process can host); `websocket` is optional/deferred if the SDK/path doesn't cover it.

11. **No new control-plane DROP-list / model-test churn unless a CP table is added.** If the per-tenant MCP-server config + enablement are **tenant-schema** resident (the default — performance/config data is tenant-scoped), they do **not** go in the `Program.cs` startup-reset DROP list and require **no** `ControlPlaneDbContextModelTests` edit. If any new table is **control-plane** resident, it MUST be appended to both (called out in Dev Notes; the default is tenant-schema, mirroring the agent config/data ownership rule).

12. **Tests cover sourcing, gating, sanitization, isolation, and fail-soft.** Union catalog (built-in ∪ MCP ∪ plugin) behind one `GetAllowed`; enablement gate (un-enabled server → zero tools); cabinet credential read (reference-only config; secret never in config/log/result); hook+sanitizer applied to MCP output identically to built-in; allowlist ∩ agent-allowed intersection; member-403 on MCP-config writes; connect-failure isolation (run proceeds, WARN logged, other tools intact); discovery-cache hit/invalidation; cross-tenant isolation (tenant A's MCP server never visible to tenant B); the strategy-path's transport adapters; and a grep proving zero MCP socket/credential reads in `ElsaServer`.

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Services/Agents/Tools/
  ICompositeToolCatalog.cs          # NEW — assembles built-in ∪ MCP ∪ plugin into one IToolExecutorRegistry
  CompositeToolCatalog.cs           # NEW — impl; calls the three sources, dedupes, exposes GetAllowed
  IMcpToolSource.cs                 # NEW — resolves enabled MCP servers -> IToolExecutor[] (tenant-scoped)
  McpToolSource.cs                  # NEW — connect/discover/wrap; cabinet creds; pool/health/rate-limit; cache
  IPluginToolSource.cs              # NEW — resolves enabled plugins -> IToolExecutor[]
  PluginToolSource.cs               # NEW — wrap plugin tools as IToolExecutor
  McpToolExecutor.cs                # NEW — IToolExecutor wrapping one MCP tool (invoke over transport)
  PluginToolExecutor.cs            # NEW — IToolExecutor wrapping one plugin tool

apps/tamma-elsa/src/Tamma.Api/Services/Mcp/
  IMcpConnectionPool.cs / McpConnectionPool.cs   # NEW — pooled connections + HealthChecker (mirrors TS pool)
  Transports/IMcpTransport.cs                    # NEW — transport abstraction
  Transports/StdioMcpTransport.cs                # NEW — stdio (local server process)
  Transports/HttpSseMcpTransport.cs              # NEW — http/sse (remote server)
  McpServerConfig.cs                             # NEW — per-tenant config record (secret REFERENCE, not secret)
  IMcpToolDiscoveryCache.cs / McpToolDiscoveryCache.cs  # NEW — (tenantId, server, configHash) -> tools, short TTL
  McpServerRateLimiter.cs                        # NEW — per-server rate limiter (mirrors TS RateLimiterRegistry)

apps/tamma-elsa/src/Tamma.Api/Services/Mcp/
  IMcpServerEnablementReader.cs                  # NEW — IsEnabledForPrincipalAsync(mcpServerId, principal, ct); mirrors the 32-16 PATTERN, SEPARATE entity
  McpServerEnablementService.cs                  # NEW — impl + enable/disable over McpServerEnablement (keyed by McpServerId)

apps/tamma-elsa/src/Tamma.Data/Entities/
  McpServer.cs                                   # NEW — per-tenant MCP-server config entity (tenant-schema)
  McpServerEnablement.cs                         # NEW — per-tenant enablement of a server, keyed by McpServerId (OWN entity, NOT TenantAgentEnablement)

apps/tamma-elsa/src/Tamma.Api/Endpoints/
  McpServerEndpoints.cs                          # NEW — tenant CRUD + enable/disable (owner/admin; member 403)

apps/tamma-elsa/src/Tamma.Api/Program.cs
  Program.cs                                     # MODIFY — register ICompositeToolCatalog as the runner's
                                                 #          IToolExecutorRegistry in the API; map MCP endpoints

apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutorRegistry.cs
  IToolExecutorRegistry.cs                       # UNCHANGED — the single seam the runner already uses
```

> **No second seam.** `ICompositeToolCatalog` **is an `IToolExecutorRegistry`** — it implements the existing interface so the runner's `GetAllowed(allowlist)` call site (`InlineToolLoopRunner`, 32-5) is unchanged. The composite is registered **in the API** as the `IToolExecutorRegistry` the managed run resolves; the engine keeps no registry (it runs no tools — 32-5 deleted the engine-side tool registrations).

### The composite catalog (AC1, AC6)

```csharp
// Tamma.Api/Services/Agents/Tools/ICompositeToolCatalog.cs
public interface ICompositeToolCatalog : IToolExecutorRegistry { }

// CompositeToolCatalog.cs — assembled per managed run (scoped), then handed to the runner as IToolExecutorRegistry.
public sealed class CompositeToolCatalog : ICompositeToolCatalog
{
    // built-in registry (the existing six executors) + the two dynamic sources
    public CompositeToolCatalog(
        IToolExecutorRegistry builtIn,                 // today's ToolExecutorRegistry
        IMcpToolSource mcp,
        IPluginToolSource plugins,
        ToolCatalogScope scope);                       // { TenantId, Principal, AgentAllowedTools, CorrelationId }

    // GetAllowed = (builtIn ∪ enabled-MCP ∪ enabled-plugin) filtered by allowlist
    //              AND intersected with scope.AgentAllowedTools (32-2/32-18).
    public IReadOnlyList<IToolExecutor> GetAllowed(string[]? allowlist);
    public IToolExecutor? GetExecutor(string toolName);     // null when not enabled / not allowed (fail-closed)
    // ...IsAllowed / GetAll mirror the union
}
```

The union is built once per run; MCP/plugin tools are discovered through their sources (with the discovery cache). `GetAllowed` applies **both** the engine allowlist **and** the agent-allowed intersection, so the model is only ever offered tools that are (enabled) ∧ (allowlisted) ∧ (agent-allowed). Unknown/un-enabled names → `GetExecutor` returns `null` (no silent fallback — `feedback_resolution_no_empty_fallback`).

### MCP tool source (AC2, AC3, AC8)

```csharp
public interface IMcpToolSource
{
    // Enabled servers for this principal -> their discovered tools as IToolExecutor[].
    Task<IReadOnlyList<IToolExecutor>> ResolveAsync(ToolCatalogScope scope, CancellationToken ct);
}

// McpToolSource flow (per scope):
//  1. servers = store.ListForTenant(scope.TenantId)
//  2. enabled = servers.WhereAsync(s => mcpEnablement.IsEnabledForPrincipalAsync(s.Id, scope.Principal, ct))  // OWN reader, keyed by McpServerId
//  3. for each enabled server (fail-soft, isolated):
//       cred  = await secrets.GetAsync(server.CredentialRef, ct)        // Epic 29 cabinet — reference -> secret
//       conn  = await pool.ConnectAsync(server, cred, timeout, ct)      // stdio/http-sse; rate-limited
//       tools = cache.GetOrAdd((tenantId, server, configHash),
//                   () => conn.ListToolsAsync(ct))                      // capability cache, short TTL
//       yield tools.Select(t => new McpToolExecutor(server, t, conn))   // name: mcp__<server>__<tool>
//     catch connect/discovery error => emit MCP.SERVER.CONNECT.FAILED; log WARN; contribute 0 tools (run continues)
```

`McpToolExecutor.ExecuteAsync(args, ct)` invokes the MCP tool over the pooled transport and returns the result; the **runner** (not this executor) then applies the `ToolHookRegistry` post-hook + `IContentSanitizer` + `RedactSecrets` — identical to the built-in path (AC5). The credential is held only for the connect and never travels into the tool result.

### MCP server config — secret reference, not secret (AC3)

```csharp
// Tamma.Data/Entities/McpServer.cs — tenant-schema (per-tenant); performance/config data is tenant-scoped.
public sealed class McpServer
{
    public Guid Id { get; init; }
    public Guid? TenantId { get; init; }          // set in SaaS; NULL in single-user
    public Guid? UserId { get; init; }            // set in single-user; NULL in SaaS  (principal XOR)
    public required string Name { get; init; }    // catalog name; tool prefix mcp__<Name>__*
    public required McpTransportKind Transport { get; init; }   // Stdio | HttpSse
    public string? Command { get; init; }         // stdio: command + args (no secret)
    public string? Url { get; init; }             // http/sse: endpoint (no secret)
    public required string CredentialRef { get; init; }   // Epic 29 cabinet REFERENCE — NEVER a literal secret
    public string[]? ToolAllowPrefixes { get; init; }     // optional server-side narrowing
    public DateTimeOffset CreatedAt { get; init; }
    // EF: principal-XOR CHECK + UNIQUE NULLS NOT DISTINCT (TenantId, UserId, Name), mirroring AgentRoleSelection.
}
```

Write-time validation rejects any config whose `Command`/`Url`/args embed a literal secret pattern; only `CredentialRef` carries auth, resolved at connect via `IRuntimeSecretResolver`.

### MCP-server enablement — OWN entity keyed by `McpServerId` (NOT the agent reader)

MCP-server enablement **mirrors the 32-16 PATTERN** (per-tenant catalog membership, principal-XOR, `UNIQUE NULLS NOT DISTINCT`, member→403 on writes) but is a **SEPARATE entity** — different schema residency (tenant-schema vs 32-16's CP) and a different target id (`McpServerId` vs `AgentId`). It does **not** reuse `TenantAgentEnablement` or `ITenantAgentEnablementReader`.

```csharp
// Tamma.Data/Entities/McpServerEnablement.cs — tenant-schema; OWN entity (not TenantAgentEnablement).
public sealed class McpServerEnablement
{
    public Guid Id { get; init; }
    public Guid? TenantId { get; init; }          // set in SaaS; NULL in single-user  (principal XOR)
    public Guid? UserId { get; init; }            // set in single-user; NULL in SaaS
    public required Guid McpServerId { get; init; }   // the enabled target — an McpServer.Id (NOT an AgentId)
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    // EF: principal-XOR CHECK (ck_mcp_server_enablements_principal_xor)
    //     + UNIQUE NULLS NOT DISTINCT (TenantId, UserId, McpServerId), mirroring 32-16's TenantAgentEnablement discipline.
}

// Tamma.Api/Services/Mcp/IMcpServerEnablementReader.cs — OWN reader (NOT ITenantAgentEnablementReader).
public interface IMcpServerEnablementReader
{
    /// <summary>True iff the MCP server is enabled for the principal. Keyed by McpServerId.
    /// Same XOR/index discipline as 32-16's TenantAgentEnablement, SEPARATE entity.</summary>
    Task<bool> IsEnabledForPrincipalAsync(Guid mcpServerId, Principal principal, CancellationToken ct);
}
```

`McpToolSource` injects `IMcpServerEnablementReader` (its own reader), NOT `ITenantAgentEnablementReader`; there is no `AgentId`/`AgentSurfaceId` on an MCP server.

### Strategy decision (deep-dive §7.3) — **RECOMMENDED: .NET MCP SDK (`ModelContextProtocol`), TS `mcp-client` as the behavioural reference**

Three options were weighed; the recommendation is **option (c) with a thin in-house adapter layer** that preserves the TS client's hardening:

| Option | What it is | Pros | Cons | Verdict |
|---|---|---|---|---|
| **(a) Port `mcp-client` to C#** | Re-implement the TS client (pool, transports, registry, cache, rate-limiter, validators) in C#. | Full control; verbatim parity with the TS hardening (validators, rate-limiter, audit); no new external dep; runs fully in-process in `Tamma.Api` (no extra socket/process). | High effort (largest LOC); we'd re-own protocol-level transport correctness and track MCP spec churn ourselves. | Fallback if SDK is immature. |
| **(b) Host the TS client as an API-managed sidecar** | Run `mcp-client` as a child process/sidecar the API drives over IPC. | Reuse the *exact* shipped TS client unchanged. | Adds a process + IPC hop **inside** the trust boundary; two runtimes to deploy/observe; key would cross the IPC boundary (more places a secret lives); fights SaaS isolation + the "engine holds no socket, API owns one process" model. | **Rejected** — adds surface for no security benefit. |
| **(c) .NET MCP SDK** | Use the official `ModelContextProtocol` .NET SDK for protocol/transport; wrap its client behind `IMcpTransport`/`IMcpConnectionPool`; **layer our own** validators, rate-limiter, capability cache, and audit (the TS client's value-adds) on top. | Lowest protocol-maintenance burden; idiomatic C# in-process in `Tamma.Api`; no extra runtime/process; we keep ownership of the security layer (validators/rate-limit/sanitization-hooks) where it must live. | New external dependency; SDK surface may lag the spec — mitigated by the adapter seam (`IMcpTransport`) so we can swap to (a) per-transport without touching the catalog. | **RECOMMENDED.** |

> **Recommendation rationale:** the protocol transport is commodity and churny — owning it (option a) is cost with no differentiation. The differentiated security work (per-tenant enablement, cabinet creds, allowlist ∩ agent-allowed, the sanitization hooks, rate-limiting, audit) is **ours regardless of transport** and we keep it in `Tamma.Api`. The sidecar (option b) is rejected because it multiplies where a tenant secret lives and breaks the one-process API model. The `IMcpTransport` seam keeps the strategy reversible: if the .NET SDK proves insufficient for a transport, that single transport can fall back to a hand-rolled port (option a) without disturbing the catalog or the security layer. The TS `mcp-client` (`security/validator.ts`, `security/rate-limiter.ts`, `audit.ts`, `cache/`) is the **behavioural reference** for the layers we re-implement in C#.

> **Always research latest docs before pinning the SDK** — confirm the current `ModelContextProtocol` .NET package name, version, and transport coverage (stdio/http-sse/websocket) via WebSearch/Context7 before adding the dependency; do not assume API shape.

### Wiring (Program.cs, API)

```csharp
// Tamma.Api/Program.cs — the managed run resolves the composite as its IToolExecutorRegistry.
builder.Services.AddSingleton<IToolExecutorRegistry, ToolExecutorRegistry>();   // built-in (unchanged)
builder.Services.AddScoped<IMcpToolSource, McpToolSource>();
builder.Services.AddScoped<IPluginToolSource, PluginToolSource>();
builder.Services.AddScoped<ICompositeToolCatalog, CompositeToolCatalog>();      // wraps built-in ∪ MCP ∪ plugin
// InlineToolLoopRunner (32-5) is constructed with ICompositeToolCatalog as its IToolExecutorRegistry.
builder.Services.AddSingleton<IMcpConnectionPool, McpConnectionPool>();
builder.Services.AddSingleton<IMcpToolDiscoveryCache, McpToolDiscoveryCache>();
app.MapMcpServerEndpoints();   // tenant CRUD + enable/disable (owner/admin; member 403)
```

The engine registers **no** tool registry and **no** MCP type (32-5 already removed the engine-side tool/sanitizer registrations).

## Dependencies

**Internal (hard prerequisites):**

- **32-5** (Call-LLM endpoint + managed execution) — ships the `InlineToolLoopRunner` and the `IToolExecutorRegistry` seam this story plugs the wider catalog into; the runner/endpoint **consumes** this catalog. (Sequence F.) **This story does not exist without 32-5.**
- **32-16** (Per-tenant agent/persona enablement) — the per-tenant catalog-membership **PATTERN** (XOR/unique-index discipline, member→403 RBAC) this story **mirrors** for MCP servers via its **own separate `McpServerEnablement` entity + `IMcpServerEnablementReader`** keyed by `McpServerId`. It does NOT consume `ITenantAgentEnablementReader` (different schema residency + target id).
- **32-2 / 32-18** (agent registry + enablement-aware resolution) — supplies the resolved agent's **allowed-tool set** that the union catalog is intersected with (AC6).
- **Epic 29** (cabinet) — `IRuntimeSecretResolver` reads MCP-server credentials from the cabinet by reference (AC3). This story is a **consumer**; storage/rotation is Epic 29.
- **Epic 27** (prompt store) — unchanged; referenced only to note MCP **prompts** are out of scope (prompts come from Epic 27, not MCP).

**Ties / collaborators (not hard blockers, confirm per Epic 9):**

- **Epic 6** (intelligence/RAG) — MCP **resources** as a future RAG source, and Epic 6 tools join the same catalog via the plugin/built-in path; the boundary is "tools here, resources/RAG in Epic 6."
- **Epic 9** (unified agent API) — the engine↔API callback + endpoint conventions (`TammaApiClient`, `TammaEngineAuthHandler`) the MCP-config endpoints follow; confirm the C# surface per story.

**Consumers (downstream, not blockers):**

- **32-6** (action trail) — consumes the per-run `toolCalls` (now spanning MCP/plugin tools) + `AGENT.TOOL.SOURCED`.
- **Streaming run tap** follow-on (deep-dive §6.4) — streams MCP/plugin tool progress once the live sink lands.

**External:** the chosen **.NET MCP SDK** (`ModelContextProtocol`, per the strategy decision) — version/transport coverage confirmed via WebSearch/Context7 before pinning.

## Testing Strategy

1. **Union catalog, one seam (AC1).** A `CompositeToolCatalog` over fakes of built-in/MCP/plugin sources returns built-in ∪ MCP ∪ plugin through a single `GetAllowed(allowlist)`; the runner's call site is unchanged (it sees only `IToolExecutorRegistry`).
2. **Enablement gate (AC2).** An MCP server **not enabled** for the principal (per the OWN `IMcpServerEnablementReader.IsEnabledForPrincipalAsync(mcpServerId, principal, ct)`, keyed by `McpServerId`) contributes **zero** tools even when configured; enabling it (an `McpServerEnablement` row) makes its tools appear. Member-role write → 403; member read allowed. Assert the gate uses the OWN reader, not `ITenantAgentEnablementReader`.
3. **Cabinet credential read (AC3).** Config carries a `CredentialRef`; the source reads the secret via a fake `IRuntimeSecretResolver` at connect; a config with a literal secret in `Command`/`Url`/args is rejected at write. Assert the secret never appears in config rows, logs, the tool result, or any event payload.
4. **Hook + sanitizer parity (AC5).** An MCP tool returning content with a secret and an injection pattern is sanitized + `RedactSecrets`'d **identically** to a built-in tool returning the same content (golden-equality test); no tool path bypasses `ToolHookRegistry`.
5. **Allowlist ∩ agent-allowed (AC6).** A tool that is enabled + allowlisted but **not** in the agent's allowed set is neither advertised nor executable; an un-enabled/unknown name → `GetExecutor` returns `null` (no silent built-in).
6. **Engine holds nothing (AC7).** Grep `Tamma.ElsaServer` → zero MCP transport/connection types and zero MCP `IRuntimeSecretResolver` reads; the composite + MCP types are registered only in `Tamma.Api`.
7. **Fail-soft isolation (AC8).** One MCP server times out / refuses connection → it contributes zero tools, `MCP.SERVER.CONNECT.FAILED` is emitted, WARN is logged, **the run proceeds** with the remaining catalog; the other servers' tools are intact.
8. **Discovery cache (AC8).** Repeated resolution within TTL hits the cache (no re-`ListTools`); a config/enablement change invalidates `(tenantId, server, configHash)`.
9. **Cross-tenant isolation.** Tenant A's MCP server is never visible/usable in tenant B's run (config + enablement keyed by principal; tenant-schema resident).
10. **Transports (AC10).** `StdioMcpTransport` and `HttpSseMcpTransport` adapters connect + list + invoke against a fake MCP server; the `IMcpTransport` seam is honoured (strategy-reversible).
11. **Plugin source (AC4).** Enabled-plugin tools join the union and the same hook/sanitization/intersection path.
12. **Events (AC9).** `MCP.SERVER.REGISTERED/ENABLED/DISABLED/CONNECT.FAILED` + `AGENT.TOOL.SOURCED` emitted from `Tamma.Api` via the tenant `IEventRepository`, key-free, correctly tagged.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

8-10 days (the MCP transport layer + connection pool/health/rate-limit/cache + the per-tenant config/enablement/CRUD + the cabinet-credential wiring + the composite catalog + the plugin source + the .NET MCP SDK adapter + tests — net-new for C#).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Tools/ICompositeToolCatalog.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Tools/CompositeToolCatalog.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Tools/IMcpToolSource.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Tools/McpToolSource.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Tools/IPluginToolSource.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Tools/PluginToolSource.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Tools/McpToolExecutor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Tools/PluginToolExecutor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/IMcpConnectionPool.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/McpConnectionPool.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/Transports/IMcpTransport.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/Transports/StdioMcpTransport.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/Transports/HttpSseMcpTransport.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/McpServerConfig.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/IMcpToolDiscoveryCache.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/McpToolDiscoveryCache.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/McpServerRateLimiter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/McpServer.cs` | Create (tenant-schema) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/McpServerEnablement.cs` | Create (OWN entity keyed by `McpServerId`; mirrors the 32-16 PATTERN, NOT `TenantAgentEnablement`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/IMcpServerEnablementReader.cs` | Create (`IsEnabledForPrincipalAsync(mcpServerId, principal, ct)`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Mcp/McpServerEnablementService.cs` | Create (enable/disable + reader over `McpServerEnablement`) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/McpServerEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register composite + sources + pool + cache; map MCP endpoints) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutorRegistry.cs` | Unchanged (the single seam) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/Tools/CompositeToolCatalogTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/Tools/McpToolSourceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Mcp/McpTransportTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/McpServerEndpointsTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback`).
3. Read 32-5 IN FULL (the runner + `IToolExecutorRegistry` seam you plug into), the deep-dive §4 (tools/MCP/plugins) and §7.3 (the strategy decision), and 32-16 (the enablement PATTERN you mirror — XOR/index/RBAC — in your OWN `McpServerEnablement` entity keyed by `McpServerId`, NOT the agent reader).
4. Reviewed `Tamma.Activities/LlmCall/Tools/IToolExecutorRegistry.cs` (the seam — DON'T change it), `Tamma.Activities/LlmCall/Tools/ToolExecutorRegistry.cs` (the built-in source you union with), `IContentSanitizer` + `RedactSecrets` (the post-hook path every tool must traverse), `IRuntimeSecretResolver` (Epic 29 cabinet read), and the TS `packages/mcp-client` (`client.ts`, `transports/*`, `security/{validator,rate-limiter}.ts`, `cache/*`, `audit.ts`) as the behavioural reference for the layers you re-implement in C#.
5. **Researched the current `ModelContextProtocol` .NET SDK** (name, version, transport coverage) via WebSearch/Context7 before pinning the dependency — do not assume the API shape.
6. Confirmed 32-5 / 32-16 / 32-2-18 / Epic-29 contracts are landed before wiring them; use fakes in tests until then.

### Key Design Decisions

- **One catalog, one seam (AC1).** `ICompositeToolCatalog : IToolExecutorRegistry`. The runner is oblivious to a tool's origin; there is no second `GetAllowed` path. This is the whole point — MCP/plugin tools are first-class members of the existing catalog, not a parallel system.
- **MCP servers are a per-tenant resource, enablement-gated by an OWN entity (mirrors the 32-16 PATTERN).** Enablement is this story's own `McpServerEnablement` entity + `IMcpServerEnablementReader` keyed by `McpServerId` — the SAME XOR/index/RBAC discipline as 32-16's `TenantAgentEnablement`, but a SEPARATE entity (tenant-schema, not CP; targets `McpServerId`, not `AgentId`). The tenant enables which MCP servers exist for it; members use what's enabled (member → 403 on writes). An un-enabled server contributes zero tools, fail-closed.
- **Credentials by reference, from the cabinet (AC3).** Config rows carry a `CredentialRef`, never a secret. The secret is read at connect via `IRuntimeSecretResolver` (Epic 29), held request-scoped, and never logged/returned/persisted in a tool result.
- **Every tool goes through the hooks (AC5).** MCP/plugin tool outputs are sanitized + secret-redacted **identically** to built-in tool outputs — the runner's existing `ToolHookRegistry`/`IContentSanitizer`/`RedactSecrets` path is the single chokepoint; no executor bypasses it.
- **Fail-soft per server (AC8).** A broken MCP server degrades the catalog, never the run. Connect failures are isolated, logged WARN, and emit `MCP.SERVER.CONNECT.FAILED`; the run proceeds with the rest of the catalog.
- **Strategy = .NET MCP SDK behind `IMcpTransport`, security layer kept in-house (AC10).** Recommended over a hand port (cost, no differentiation) and over a TS sidecar (multiplies where secrets live, breaks the one-process API model). The transport seam keeps the choice reversible per-transport.
- **No empty fallback (`feedback_resolution_no_empty_fallback`).** An unknown/un-enabled tool name resolves to "no executor," never to a silent built-in. Credential reads are cabinet→error, never empty.
- **Tenant-schema by default → no DROP-list / model-test churn (AC11).** MCP-server config + enablement are tenant-scoped (performance/config data ownership rule), so they live in the `t_<hex>` schema owned by `EfTenantDbMigrator`, NOT in `Program.cs`'s control-plane startup-reset DROP list, and require **no** `ControlPlaneDbContextModelTests` edit. If a reviewer decides the MCP catalog must be CP-resident, it MUST then be appended to **both** the DROP list and the strict `Model_Has_ExpectedControlPlaneEntities` `BeEquivalentTo` list (called out here so it isn't missed). The default keeps the surface tenant-isolated.
- **EF: this story extends the single migration snapshot, not a branch.** Stories are implemented sequentially against one `TammaModelConfiguration` / migration snapshot; the new tenant-schema tables amend it.
- **Admin route policy.** Tenant-scoped MCP enable/disable + CRUD = `tenant_owner`/`tenant_admin` (member → 403). There is no platform-global `/api/admin/*` route here, so `PlatformOwnerAccess` does not apply; if a platform-global MCP registry is ever added it would use `PlatformOwnerAccess` (NEVER `OwnerAccess`).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns an MCP-server config / enablement? | The sole user (keyed by `UserId`; `TenantId` NULL). | The tenant (keyed by `TenantId`). No per-user layer; members can't add/enable. |
| Who can enable/disable an MCP server? | The sole user (owns everything). | `tenant_owner` / `tenant_admin` only; member → 403 (read allowed). |
| Whose credential does an MCP connection use? | The sole user's cabinet secret (Epic 29), read by reference at connect. | The tenant's cabinet secret (Epic 29), read by reference at connect. Never cross-tenant. |
| Which MCP servers / plugin tools are in a run's catalog? | The sole user's enabled set. | The tenant's enabled set (∩ the agent's allowed tools, ∩ allowlist). |
| Where do MCP config / discovery / tools live? | The user's (sole) tenant schema; tenant-isolated. | The tenant's `t_<hex>` schema; never visible to another tenant or to platform admin. |
| Where do `MCP.SERVER.*` / `AGENT.TOOL.SOURCED` events land? | The user's (sole) tenant event store. | The tenant's `t_<hex>` event store via the tenant-scoped `IEventRepository`. Never cross-tenant. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| A tool bypasses the sanitization hooks → unsanitized MCP output re-enters the model (AC5) | Critical | Single chokepoint: the runner applies `ToolHookRegistry`/`IContentSanitizer`/`RedactSecrets` to **every** `IToolExecutor` output; MCP/plugin executors return raw content only — they never apply (or skip) sanitization themselves. Golden-equality test vs a built-in tool. |
| MCP secret leaks into config / logs / tool result (AC3) | Critical | Config carries `CredentialRef` only; literal-secret configs rejected at write; secret read request-scoped via `IRuntimeSecretResolver`, never logged/returned/persisted; credential-safety test asserts absence everywhere. |
| The engine opens an MCP socket / reads a key (rule 1) | High | All MCP types/registrations live in `Tamma.Api`; grep `Tamma.ElsaServer` proves zero MCP transport/credential references; the engine runs no tools (32-5 removed its tool registrations). |
| A broken MCP server fails the whole run (AC8) | High | Fail-soft isolation: per-server connect/discovery timeout, contribute-zero-tools-on-error, `MCP.SERVER.CONNECT.FAILED` + WARN, run proceeds. |
| An un-enabled/disallowed MCP tool reaches the model (AC2/AC6) | High | Union filtered by enablement gate **and** `GetAllowed(allowlist)` **and** agent-allowed intersection; unknown name → `null` executor (no silent fallback). |
| .NET MCP SDK is immature for a transport (AC10) | Medium | `IMcpTransport` seam isolates the SDK; a single transport can fall back to a hand-rolled port without touching the catalog/security layer. Research the SDK before pinning. |
| Cross-tenant tool leakage | Medium | Config + enablement keyed by principal (XOR), tenant-schema resident; discovery cache keyed by `(tenantId, server, configHash)`; isolation test. |
| Discovery latency on every run | Medium | Capability cache with short TTL keyed `(tenantId, server, configHash)`, invalidated on config/enablement change; pooled connections + health checks. |
| Mis-scoping MCP config as control-plane → DROP-list/model-test churn missed (AC11) | Medium | Default is tenant-schema (no churn); if changed to CP, append to both the `Program.cs` DROP list and the strict `Model_Has_ExpectedControlPlaneEntities` list (Dev Notes flag). |

### Success Metrics

- [ ] The managed run's catalog provably equals built-in ∪ enabled-MCP ∪ enabled-plugin, behind one `GetAllowed`.
- [ ] `grep` over `Tamma.ElsaServer` finds **zero** MCP transport/connection/credential references (engine holds no socket/key).
- [ ] 100% of MCP/plugin tool invocations traverse the `ToolHookRegistry`/`IContentSanitizer`/`RedactSecrets` path (parity with built-in).
- [ ] An un-enabled or disallowed MCP/plugin tool is **never** advertised or executable.
- [ ] A failing MCP server never fails a managed run (fail-soft); it degrades the catalog and logs WARN.
- [ ] No MCP secret appears in any config row, log line, tool result, or DCB event payload.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§1 steps-never-call-providers; §2.6 step 5 the provider call / tool loop home)
- Managed-LLM deep dive: `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§4 tools/MCP/plugins/cache/RAG; §6 item 2 the NEW MCP & plugin sourcing story; §7.3 the MCP strategy open decision)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (sequence; follow-ons after step F)
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-21-mcp-and-plugin-tool-sourcing-plan.md`
- Sibling stories: `story-32-5/` (the endpoint/runner that consumes this catalog), `story-32-16/` (the per-tenant enablement model reused for MCP servers), `story-32-2/` + `story-32-18/` (the agent allowed-tool set the catalog is intersected with), `story-32-6/` (action trail consuming the per-run tool calls); Epic 29 (cabinet creds), Epic 6 (intelligence/RAG tools + MCP resources), Epic 9 (unified agent API surface)
- Reference code: `packages/mcp-client/` (TS client — behavioural reference: `client.ts`, `transports/{stdio,sse,websocket}.ts`, `registry.ts`, `security/{validator,rate-limiter}.ts`, `cache/*`, `audit.ts`); `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutorRegistry.cs` (the seam); `apps/tamma-elsa/src/Tamma.Activities/Security/IContentSanitizer.cs`; `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Stopgap/IRuntimeSecretResolver.cs`

## Logging Requirements

- **INFO**: MCP server registered/enabled/disabled (server name, tenantId — **never the credential**); managed run tool catalog assembled (`AGENT.TOOL.SOURCED` counts: builtIn/mcp/plugin offered, correlationId); MCP connection established (server, transport — no auth material).
- **DEBUG**: per-server discovery (tool names listed), cache hit/miss for `(tenantId, server, configHash)`, allowlist ∩ agent-allowed narrowing result, rate-limit decisions.
- **WARN**: MCP server connect/discovery failure (server, tenantId, correlationId, error class — key-free) with the run proceeding fail-soft; a configured-but-un-enabled server skipped; a write rejected for embedding a literal secret.
- **ERROR**: composite-catalog assembly failure that cannot degrade fail-soft (the run's catalog cannot be built), and DCB append failure (logged, not swallowed).
- **Structured context**: `{ tenantId, server, transport, correlationId }` where applicable; tool counts on `AGENT.TOOL.SOURCED`.
- **Credential safety (LOAD-BEARING)**: NEVER log, return, or persist an MCP-server credential, transport auth header/env/arg, or any secret read from the cabinet. Only the **reference** (`CredentialRef`) and non-secret config (name, transport, url/command sans secrets) are safe. Tool results are sanitized + secret-redacted before re-entering the model context; the `MCP.SERVER.*` and `AGENT.TOOL.SOURCED` event payloads are key-free by contract.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation — MCP-server + plugin tool sourcing into the C# managed-run tool catalog (deep-dive §4 MCP+plugins / §6 item 2 / §7.3). Composite catalog `ICompositeToolCatalog : IToolExecutorRegistry` (built-in ∪ MCP ∪ plugin behind the single `GetAllowed` seam, ∩ agent-allowed); per-tenant MCP-server config (cabinet creds by reference, enablement-gated like agents 32-16); every invocation through `ToolHookRegistry`/`IContentSanitizer`/`RedactSecrets`; engine holds no MCP socket/key (rule 1); fail-soft per-server isolation + discovery cache; **strategy decision resolved — .NET MCP SDK behind `IMcpTransport`, security layer kept in-house** (sidecar rejected, hand-port as fallback). Consumed by 32-5's `InlineToolLoopRunner`; ties to Epic 6/9. | Claude |
| 2026-06-21 | 1.0.1   | Cross-spec reconciliation (I4): MCP enablement is now its **OWN** tenant-schema `McpServerEnablement` entity keyed by `McpServerId`, gated by an OWN `IMcpServerEnablementReader.IsEnabledForPrincipalAsync(mcpServerId, principal, ct)` — it **mirrors the 32-16 PATTERN** (XOR/unique-index/RBAC) as a SEPARATE entity (different schema residency + target id), and no longer keys on `AgentId`/`AgentSurfaceId` or consumes `ITenantAgentEnablementReader`. Removed the "reuse 32-16 shape" wording. | Claude |
