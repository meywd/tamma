# Story 42-6: MCP Integration

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **platform operator (single-user) or tenant_admin (SaaS)**, I want to register an **external MCP
server** and have the tools it exposes appear in the tool registry — governed by the same permission,
autonomy, secret, and audit rules as native tools — so that the catalog becomes open-ended: a new
capability is a server registration, not a code change and redeploy.

## Priority

P1 / Wave 2 — the **dynamic-tool path** that makes the catalog extensible without shipping a class per
tool. Depends on the full Wave-1 governance envelope (42-1–42-5); proves a non-native tool obeys it.

## The gap (READ FIRST)

The registry is compile-time static (42-1's gap): the only way to add a tool today is a class + a
`Program.cs` DI line. There is **no MCP client, no external-tool ingestion, no dynamic catalog**. 42-1
adds the `Register`/`Unregister` seam; this story is the first consumer of it — bridging MCP-exposed
tools into `IToolExecutor` so they flow through the exact same resolve → gate → secret → execute → audit
pipeline as `SearchCodeTool`.

## Scope

1. **An MCP client + `McpToolExecutor` adapter.** For each configured MCP server, discover its exposed
   tools (name, description, JSON input schema — the MCP tool spec maps cleanly onto `IToolExecutor`'s
   `ToolName`/`Description`/`InputSchema`) and register one `McpToolExecutor : IToolExecutor` per tool
   via 42-1's dynamic `Register`. `ExecuteAsync` proxies the call to the MCP server over its transport
   and maps the MCP result → `ToolExecutionResult` (**never throws** — a transport/protocol error is
   `Success = false`, honoring the contract). Names are namespaced (`mcp:<server>:<tool>`) to avoid
   collision with native tools.

2. **Governance is mandatory, deny-by-default.** An MCP tool arrives with **no** permission class or
   autonomy floor. Its `ToolDescriptor.Category = Mcp` and it inherits 42-1's fail-safe default
   (`Destructive`, floor 100, deny-by-default) **until an operator/tenant_admin classifies it** via the
   42-2 binding store (`allowed_roles`, `autonomy_floor`, `enabled`, `secret_binding_name`). So a freshly
   registered MCP tool is **inert until explicitly granted** — never auto-trusted. (See epic Open
   Question 2 on whether MCP tools may default to `ReadOnly` instead.)

3. **Secret binding + audit, unchanged.** An MCP tool that needs a credential binds through 42-4
   (`secret_binding_name` → `SecretRef`); its calls emit the 42-5 `TOOL.*` DCB family with the same
   redaction. The MCP server's own auth (if any) is a bound secret like any other. Nothing about MCP
   bypasses the envelope.

4. **Registration surface + tenancy.** Config-driven server registration
   (`Mcp:Servers:<name>:{ Endpoint, Transport, AuthSecretName }`) plus a management endpoint. **Tenancy
   (epic Open Question 4):** in SaaS, decide whether a `tenant_admin` may register a tenant-scoped MCP
   server (its tools registered into the tenant's view only, never the global singleton — see 42-1 Risk)
   or whether the MCP allowlist is platform-owned. This story implements the **platform-owned** path
   first (simpler, safe) and leaves per-tenant MCP registration as a documented follow-on if chosen.

5. **Refresh / health.** MCP tool sets can change; a server going away must `Unregister` its tools
   (loud, not a dangling executor that fails every call). A periodic/rediscovery refresh uses
   `Unregister`+`Register` (42-1's replace path).

## Acceptance Criteria

1. A configured MCP server's tools are discovered and registered as `McpToolExecutor`s visible via
   `GetAll`/`GetExecutor` with namespaced names (integration test against a stub MCP server).
2. An MCP tool executes end-to-end through `ParallelToolExecutor` and maps success/error to
   `ToolExecutionResult` without throwing (test drives a success, a protocol error, and a timeout).
3. A newly registered MCP tool is **deny-by-default** — not resolvable to an agent until classified via
   a 42-2 binding (test: agent resolve returns empty for the unclassified tool; grant a binding and it
   becomes eligible).
4. An MCP tool with a bound secret authenticates via 42-4 and emits redacted 42-5 `TOOL.*` events (test).
5. Unregistering/removing a server removes its tools from the registry with no dangling executor (test).
6. A destructive-classified MCP tool routes through 42-3's authorization exactly like a native
   destructive tool (test).

## Events

Reuses 42-5's `TOOL.*` family for invocation. Adds `TOOL.MCP_SERVER_REGISTERED` /
`TOOL.MCP_SERVER_REMOVED` (server name + tool count, **no auth secret**) for the catalog-change audit.

## Single-user vs SaaS

- **single-user:** the sole user registers MCP servers; tools classified against the user's bindings.
- **SaaS:** platform-owned MCP allowlist by default (this story); per-tenant MCP registration is the
  documented follow-on (Open Question 4). Either way, an MCP tool's secret is tenant-scoped (42-4) and
  its calls are tenant-tagged in the audit — a tenant's MCP tool never reaches another tenant.

## Dependencies

- **42-1** (dynamic `Register`/`Unregister`, `ToolCategory.Mcp`, deny-by-default descriptor).
- **42-2** (classification bindings), **42-3** (gating/authorization), **42-4** (secret binding),
  **42-5** (audit) — the full envelope MCP tools inherit.
- External: an MCP client library / SDK for the chosen transport.
- **Unblocks:** open-ended catalog growth for Epic 41 and beyond without per-tool code.

## Risks

- **Trust surface.** An MCP server is external code exposing tools; auto-trusting it would reopen the
  attack surface the epic closes. Mitigation: deny-by-default classification (AC3) + platform-owned
  allowlist first + the same secret/audit envelope.
- **Global-singleton mutation for a per-tenant server.** Registering a tenant's MCP tools into the
  shared registry singleton would leak them cross-tenant. Mitigation: keep per-tenant MCP behind a
  tenant-scoped registry view (deferred with the per-tenant path), platform-owned only in this story.
- **Schema mismatch.** MCP input schemas may not satisfy `ToolCallValidator`'s constraints. Mitigation:
  validate/normalize at registration; reject a tool whose schema can't be represented, loudly.

## Estimated Effort

Large. ~5–6 days (MCP client + adapter + lifecycle/refresh + the deny-by-default classification flow +
tenancy decision).
</content>
