# Story 42-6: MCP Integration — and reconciling the MCP surfaces that already ship

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **platform operator (single-user) or tenant_admin (SaaS)**, I want to register an **external MCP
server** and have the tools it exposes appear in the tool registry — governed by the same permission,
autonomy, secret, and audit rules as native tools — **and I want the MCP tool-invoke HTTP surface that
already ships to stop being a way around those rules**, so that the catalog becomes open-ended *and*
has exactly one governed entry point.

## Priority

Two halves, different urgency. *Corrected: an earlier draft scheduled all of 42-6 into Wave 2. That
would ship the epic with a documented hole from day one — a live, ungoverned MCP invoke route.*

- **Part A — govern-or-retire the live `/api/kb/mcp/*` tool surface. P0 / Wave 0.5.** Depends on
  **nothing** in this epic. `POST /api/kb/mcp/tools/invoke` is mapped today (`Program.cs` L3187),
  bypassing `IToolExecutor`, `ToolExecutorRegistry`, `ToolCallValidator`, `ParallelToolExecutor` and
  any audit. It currently dead-ends (see below), so reconciling it is a **deletion**; reconciling it
  after someone wires an MCP client into the sidecar bundle is a **migration**.
- **Part B — the governed MCP catalog. P1 / Wave 2.** The dynamic-tool path that makes the catalog
  extensible without shipping a class per tool. Depends on the full Wave-1 envelope (42-1–42-5) and
  proves a non-native tool obeys it.

## The gap (READ FIRST)

*Corrected — an earlier draft said "there is **no MCP client, no external-tool ingestion, no dynamic
catalog**." Scoped to the C# backend that is accurate; read repo-wide it is false.*

**True for the C# backend.** There is no MCP client class in `apps/tamma-elsa/src` — every `Mcp` hit
is a proxy DTO (`Dtos/KnowledgeBase/KnowledgeBaseDtos.cs` L52–65), an endpoint forwarder
(`Endpoints/KbEndpoints.cs` L115–161, `Services/KnowledgeBase/IntelligenceHttpClient.cs` L91–120), or
a provider note. And the registry has no ingestion path: `IToolExecutorRegistry` declares exactly
`GetExecutor` / `IsAllowed` / `GetAll` / `GetAllowed` — no `Register`/`Unregister`
(`ToolExecutorRegistry.cs` L11–39 builds one `Dictionary` in the ctor and never mutates it). 42-1 adds
the seam; **Part B** is its first consumer.

**False repo-wide.** Four MCP-shaped surfaces already ship. This story owns a decision on each.

### Existing MCP surfaces (prior art — a decision per item)

**(a) `packages/mcp-client/` — a hand-rolled TS MCP client, orphaned.** 7,865 LOC of non-test source:
`MCPClient implements IMCPClient` (`client.ts` L85 / `types.ts` L251), `StdioTransport` /
`SSETransport` / `WebSocketTransport`, `ToolRegistry` / `ResourceRegistry` / `PromptRegistry`
(`registry.ts` L45/200/349), `RateLimiter` + `PathValidator` + `ResourceMonitor` + `OutputCollector`
(`security/`), `AuditLogger` (`audit.ts` L93), `ConnectionPool` (`connections/pool.ts` L31),
`SERVER_PRESETS` (`servers/index.ts` L316). It depends on no MCP SDK (deps: `@tamma/shared`,
`@tamma/observability`, `zod`, `eventemitter3`). **Zero dependents** — no other `package.json` names
`@tamma/mcp-client`, and `dist/` has never been built. → Scope §0.

**(b) Eight live `/api/kb/mcp/*` routes — the ungoverned invoke surface.** Mapped unconditionally at
`Program.cs` L3180–3187 on a group carrying `RequireAuthorization("SettingsView")` (L3159), with
`SettingsManage` stacked per-route on the four mutating ones — **both** policies evaluate (an earlier
review said "only `SettingsManage`"). In the dev-without-JWT branch (`Program.cs` L1718–1729) every
named policy including both of these is replaced by `AllowAnonymousRequirement`. They forward to the
TS sidecar. They **dead-end today**: `server.ts` L47 builds `new McpManagementService(bundle?.mcpClient)`,
and the only production composition root — `buildIntelligenceBundleFromEnv` (`env-composition.ts`
L429–447) — sets `vectorStore` and optionally `ragPipeline` and **never** `mcpClient`;
`@tamma/mcp-client` is not even a dependency of `@tamma/intelligence-server`. So `invokeTool` returns
`{ success:false, error:'MCP client not configured' }` and `listTools` returns `[]`. The only place
`mcpClient` is ever supplied is a test mock.

Two facts make retirement cheap and make *waiting* expensive: (i) `POST /api/kb/mcp/tools/invoke` has
**no in-repo caller** — the dashboard's `mcpApi.invokeTool` targets
`/kb/mcp/servers/{server}/tools/{tool}/invoke` and `mcpApi.listTools` targets
`/kb/mcp/servers/{server}/tools` (`packages/dashboard/src/services/knowledge-base/api-client.ts`
L153–158), neither of which the API maps — they 404 today; (ii) the moment anyone sets `mcpClient` on
the bundle, the route becomes a working ungoverned tool-execution path. → Part A.

**(c) `packages/providers/src/zen-mcp-provider.ts` — MCP as transport *to an LLM*. NOT in scope.**
`ZenMCPProvider implements IAIProvider`: it spawns a Zen MCP server process and translates
`sendMessage` into MCP `chat` tool invocations. It is a **provider**, not a tool bridge — it consumes
MCP to *reach a model*, whereas this story consumes MCP to *expose tools*. Do not conflate them, and
do not let 42-6 absorb it. One coupling worth knowing: the C# side lists `"zen-mcp"` / `"zen"` in
`HttpProviderClient.NonHttpProviders` (L58–66) and fails them with `PROVIDER_NOT_SUPPORTED` because
"a CLI subprocess or MCP transport … is not yet ported to C#". So a C# MCP *transport* has a second,
independent customer — §0's choice should be made knowing that, without taking the provider port on.

**(d) `MCPSource` (`packages/intelligence/src/context/sources/mcp-source.ts` L14) — an MCP *resource*
consumer.** Its seam `IMCPClientLike` (L8–12) is only `listServers` / `listResources` / `readResource`
— **no `invokeTool`** — and it deliberately declines to depend on `@tamma/mcp-client` ("avoids a hard
build dependency"). It reads resources into `ContextChunk`s and its only caller is its own test. It is
not a tool consumer and does not need governing, but whatever client §0 lands should be the thing
`IMCPClientLike` eventually converges on.

## Scope

### §0 — Decide the client: **port or adopt** (epic Open Question 3; blocks Part B's estimate)

Three options with materially different costs. **Proxying through the TS sidecar is ruled out on
evidence**: it would put tool execution behind an HTTP hop on the far side of the process the tool
envelope does not cover, re-creating in Part B exactly the bypass Part A deletes, and it keeps a second
governance path alive permanently. The live choice is **port the 7,865-LOC TS client to C#** vs
**adopt an MCP C# SDK**. Decide on: transport coverage actually needed (stdio vs SSE/streamable HTTP),
protocol-version maintenance burden, and whether the TS client's extras (rate limiter, path validator,
audit log, connection pool) are still needed once tools run inside the 42-1–42-5 envelope — several of
them duplicate it. Record the decision (and the rejected options) in `.dev/decisions/` before Part B
starts; the effort estimate below is not meaningful until then. Whatever is chosen, **`packages/mcp-client/`
must not be left orphaned**: either it becomes the port's source of truth (then delete it once ported)
or it is deleted outright — a 7,865-LOC unbuilt package that looks live is a trap for the next reader.

### Part A — govern or retire the already-live invoke surface (no dependency on Waves 0–1)

**A1. Retire the two tool-facing routes.** Delete `GET /api/kb/mcp/tools` and
`POST /api/kb/mcp/tools/invoke` (`Program.cs` L3186–3187), their handlers (`KbEndpoints.ListMcpTools`,
`KbEndpoints.InvokeMcpTool`), their forwarders (`IIntelligenceHttpClient.ListMcpToolsAsync` /
`InvokeMcpToolAsync`, declared L54–55, implemented `IntelligenceHttpClient.cs` L111–120), the
`McpInvokeRequest` DTO, and the two tests that pin them
(`Tamma.Api.Tests/KnowledgeBase/IntelligenceHttpClientTests.cs` L251, L259) — that is the complete
call graph. *Decision — retire, not re-gate:* re-gating means
threading the registry, `ToolCallValidator`, the 42-3 gate and 42-5 audit through a KB-admin proxy
route to reach a sidecar that holds no client. Part B provides the governed path; a second entry point
earns nothing. **Also** remove or repoint the dashboard's MCP tool panel (`mcpApi.listTools` /
`mcpApi.invokeTool` and their `useMCPServers` call sites) — those calls 404 today, so this is cleanup,
not a behaviour change.

**A2. Keep the six server/config admin routes, and pin that they cannot execute a tool.** (The
epic-level "reconcile the 8 `/api/kb/mcp/*` routes" resolves here to **retire 2, re-scope 6**.) `servers`,
`servers/{id}`, `servers/{id}/start`, `servers/{id}/stop`, `config` (GET/PUT) are sidecar-KB
administration, not tool execution. They stay, explicitly scoped as *sidecar KB admin* and explicitly
**not** the governed catalog — with a test that fails if a tool-invoking route reappears under
`/api/kb/mcp/*`. Note in code and here that this surface is `AllowAnonymous` in the dev-without-JWT
branch, so it must never grow a capability that matters.

### Part B — the governed MCP catalog

**Where this code lives (binding).** **The MCP client, `McpToolExecutor`, the discovery/refresh
service and the server-registration surface all live in `Tamma.Api`** — package
`Tamma.Api.Services.Tools.Mcp`, wired next to the six built-ins at `Tamma.Api/Program.cs` L753–766.
Nothing in this story is added to `Tamma.Activities`.

Reasons, in force order: (1) **rule 1** — a workflow step never calls an external system directly or
holds an external credential, and an MCP tool call is exactly an external process/socket call carrying
the server's bound auth secret; (2) **runtime** — `Tamma.ElsaServer/Program.cs` L286–292 records the
tool catalog was *removed* from the engine and "the tool executors are registered there [`Tamma.Api`],
not here", so an engine-side `McpToolExecutor` would never be resolved, and the registry singleton the
`Register` seam mutates is the Api-side one; (3) **guardrail backstop** — `TAMMA001`
(`DiagnosticSeverity.Error`, analyzer-referenced by `Tamma.Activities` / `Tamma.ElsaServer`) exists to
keep credentialed external calls out of the engine, and `Allowlist.IsEngineSurface` deliberately
excludes `Tamma.Api`. *Honest scope:* `TAMMA001`'s injection check is a closed denylist that does not
name any MCP type, and its HTTP check fires only on a statically-literal external host — an MCP server
endpoint is always config-supplied, so it would not mechanically trip. Siting is settled by (1) and
(2); the analyzer is the backstop. Precedent: `GetAcceptanceRulesTool` (an `IToolExecutor` in
`Tamma.Api.Services.AcceptanceRules`) and `Allowlist.cs` L57–58 on `InlineToolLoopRunner`.

Only the 42-1 contract types the adapter implements (`IToolExecutor`, `ToolDescriptor`,
`SecretRequirement`, `IToolExecutorRegistry`) stay in `Tamma.Activities.LlmCall.Tools`; a
`ToolDescriptor` never carries a `SecretRef` (42-4). **This story adds nothing engine-side.**

1. **An MCP client + `McpToolExecutor` adapter — with a name the validator accepts.** For each
   configured MCP server, discover its exposed tools (name, description, JSON input schema — the MCP
   tool spec maps cleanly onto `IToolExecutor`'s `ToolName`/`Description`/`InputSchema`) and register
   one `McpToolExecutor : IToolExecutor` per tool via 42-1's `Register`. `ExecuteAsync` proxies to the
   MCP server over its transport and maps the result → `ToolExecutionResult` (**never throws** — a
   transport/protocol error is `Success = false`, honoring the contract).

   **Namespacing — `mcp__<server>__<tool>`, not `mcp:<server>:<tool>`.** *Corrected: an earlier draft
   specified colons. `ToolCallValidator` (`Tamma.Activities/Security/ToolCallValidator.cs` L28–29)
   applies `^[a-zA-Z0-9_\-]{1,64}$` unconditionally as check #2 (L92), after the allowlist check — so a
   colon-namespaced name would pass the allowlist and then be rejected at L100 on **every** call.
   `InlineToolLoopRunner` L267 runs every LLM-returned call through that validator before execution, and
   there is no colon-tolerant second path.* Double underscore is legal and collision-safe.

   **The 64-char budget is a hard registration-time constraint**, enforced twice: the validator regex
   and `tool_bindings.ToolName`'s `HasMaxLength(64)` (42-2 §1c) — an unbindable tool is an ungovernable
   tool. `"mcp__" + server + "__" + tool` must fit 64, i.e. server + tool ≤ 57 chars. Reject an
   overflowing name **at registration** with a typed, loud error; never truncate (truncation collides).
   Registry lookup is `OrdinalIgnoreCase` (`ToolExecutorRegistry.cs` L19), so normalize the composed
   name to lower-case and reject a case-insensitive collision at registration too.

2. **Governance is mandatory, deny-by-default.** An MCP tool arrives with **no** permission class or
   autonomy floor. Its `ToolDescriptor.Category = Mcp` and it inherits 42-1's fail-safe default
   (`Destructive`, floor 100, deny-by-default) **until an operator/tenant_admin classifies it** via the
   42-2 binding store (`Enabled`, `AllowedRoles`, `AutonomyFloor`, `SecretBindingName`). A freshly
   registered MCP tool is **inert until explicitly granted** — never auto-trusted. (Epic Open Question 1
   asks whether MCP tools may instead default to `ReadOnly`; this story assumes the deny default until
   that is answered.)

3. **Secret binding + audit, unchanged.** An MCP tool that needs a credential binds through 42-4
   (`SecretBindingName` → `SecretRef`); its calls emit the 42-5 `TOOL.*` DCB family with the same
   redaction. The MCP server's own auth (if any) is a bound secret like any other. Nothing about MCP
   bypasses the envelope — that is the whole point of Part A.

4. **Registration surface + tenancy.** Config-driven server registration
   (`Mcp:Servers:<name>:{ Endpoint, Transport, AuthSecretName }`) plus a management endpoint. **Tenancy
   (epic Open Question 2):** in SaaS, decide whether a `tenant_admin` may register a tenant-scoped MCP
   server or whether the MCP allowlist is platform-owned. This story implements the **platform-owned**
   path first — and it *must*, because 42-1 ships `Register`/`Unregister` as **platform/deployment scope
   only** and rejects a principal-bound registration outright (42-1 §3, AC7) until the per-principal
   registry *view* lands. That view is 42-6's to build if per-tenant MCP is chosen; it is also what
   finally gives the 39-5 D6 principal-bound-tool pattern a delivery path into `InlineToolLoopRunner`.

5. **Refresh / health.** MCP tool sets change; a server going away must `Unregister` its tools (loud,
   not a dangling executor that fails every call). Periodic rediscovery uses `Unregister`+`Register`
   (42-1's replace path, which defaults to reject-on-duplicate).

## Acceptance Criteria

**Part A**

A1. `POST /api/kb/mcp/tools/invoke` and `GET /api/kb/mcp/tools` return **404** (endpoint test), and a
    route-inventory test over `EndpointDataSource` fails if any mapped route pattern contains both
    `mcp` and a tool-invoking segment.
A2. `IIntelligenceHttpClient` exposes no MCP tool member: a reflection test asserts no
    `InvokeMcpTool*` / `ListMcpTools*` member exists, so no surviving admin route can reach a tool call.
A3. The six surviving `/api/kb/mcp/*` admin routes still respond as before (regression test), and the
    dashboard has no remaining `mcpApi.invokeTool` / `mcpApi.listTools` call site.

**Part B**

B1. A configured MCP server's tools are discovered and registered as `McpToolExecutor`s visible via
    `GetAll`/`GetExecutor` under `mcp__<server>__<tool>` (integration test against a stub MCP server).
B2. **The namespaced name survives the live path end-to-end.** With the tool in the resolved set, a
    call flows through `InlineToolLoopRunner` → `ToolCallValidator.Validate` → `ExecuteAsync`; the test
    asserts the call is **not** in `rejectedToolCalls` and that a raw `ToolCallValidator.Validate` on
    the composed name returns `IsValid = true`.
B3. A server+tool pair whose composed name exceeds 64 chars, or that collides case-insensitively with a
    registered name, is **rejected at registration** with a typed error and never appears in `GetAll`
    (test asserts both the rejection and the absence).
B4. An MCP tool executes end-to-end through both `InlineToolLoopRunner` branches and maps
    success/protocol-error/timeout to `ToolExecutionResult` **without throwing** (three cases).
B5. A newly registered MCP tool is **deny-by-default** — absent from the agent's resolved set until
    classified via a 42-2 binding; granting a binding makes it eligible (test drives both states).
B6. An MCP tool with a bound secret authenticates via 42-4 and emits redacted 42-5 `TOOL.*` events; the
    test asserts the secret value appears in no emitted event, no `ToolExecutionResult.Output`, and no
    log line.
B7. Removing a server `Unregister`s its tools: `GetExecutor` returns null and `GetAll` omits them, with
    no dangling executor left resolvable (test).
B8. A `Destructive`-classified MCP tool is routed to 42-3's stage-2 invocation-time authorization
    exactly like a native destructive tool — no `ExecuteAsync` before an `Authorize` decision (test).

## Events

Reuses 42-5's `TOOL.*` family for invocation. Adds `TOOL.MCP_SERVER_REGISTERED` /
`TOOL.MCP_SERVER_REMOVED` (server name + tool count, **no auth secret**) for the catalog-change audit.
Part A emits nothing — it deletes a surface.

## Single-user vs SaaS

- **single-user:** the sole user registers MCP servers; tools classified against the user's
  `tool_bindings` rows (`user_id`-keyed). A bound MCP credential is a **platform-scoped** `SecretRef`
  (42-4 — there is no user scope; ownership is `SecretMetadata.OwnerUserId`, metadata not scope).
- **SaaS:** platform-owned MCP allowlist in this story (forced by 42-1's platform-only `Register`);
  per-tenant MCP registration needs the per-principal registry view and is the documented follow-on
  (Open Question 2). Either way an MCP tool's secret is tenant-scoped and its calls are tenant-tagged
  in the audit — a tenant's MCP tool never reaches another tenant.

## Dependencies

- **Part A: none.** It touches only `Program.cs` route mapping, `KbEndpoints`, `IIntelligenceHttpClient`
  and the dashboard KB panel. It can land before 42-1.
- **Part B:** **42-1** (dynamic `Register`/`Unregister`, `ToolCategory.Mcp`, deny-by-default descriptor,
  and the platform-only registration constraint), **42-2** (bindings), **42-3** (two-stage gating),
  **42-4** (secret binding), **42-5** (audit) — the full envelope MCP tools inherit.
- **The MCP client is a §0 decision, not an external given.** *Corrected: an earlier draft listed
  "External: an MCP client library / SDK for the chosen transport" as if the choice were made.* The
  real options are port `packages/mcp-client/` to C# or adopt an MCP C# SDK; proxying via the sidecar is
  rejected (see §0). No MCP package is referenced anywhere in `apps/tamma-elsa` today.
- **Adjacent, not depended on:** `zen-mcp-provider` (MCP-as-LLM-transport) and `MCPSource` (MCP
  resources). Neither blocks this story; both should converge on §0's client later.
- **`Tamma.Activities` holds no external credential** and carries the `TAMMA001` analyzer; no
  credential-holding or transport code from this story may be added to it. Part B is entirely
  Api-side (see "Where this code lives"); Part A touches only route mapping, `KbEndpoints`,
  `IIntelligenceHttpClient` and the dashboard.
- **Unblocks:** open-ended catalog growth for Epic 41 and beyond without per-tool code.

## Risks

- **Shipping the epic with the hole still open.** If Part A slips behind Wave 2, the epic's own
  governance claim is false while a live invoke route exists. Mitigation: Part A is independently
  landable and depends on nothing — sequence it in Wave 0.5.
- **Tool-name format (high probability, previously unnamed).** A colon-namespaced name passes the
  allowlist check and is then rejected by `ToolCallValidator`'s format regex on every call — a catalog
  that registers cleanly and fails 100% of invocations. Mitigation: `mcp__…__…` plus B2/B3.
- **Trust surface.** An MCP server is external code exposing tools; auto-trusting it would reopen the
  attack surface the epic closes. Mitigation: deny-by-default classification (B5), platform-owned
  allowlist first, same secret/audit envelope.
- **Global-singleton mutation for a per-tenant server.** Registering a tenant's MCP tools into the
  shared registry singleton leaks them cross-tenant. Mitigation: 42-1 rejects principal-scoped
  registration outright; per-tenant MCP stays behind the registry view.
- **Input-schema mismatch.** MCP input schemas may not satisfy `ToolCallValidator`'s argument
  constraints (size cap, sanitization). Mitigation: validate/normalize at registration; reject a tool
  whose schema can't be represented, loudly.
- **§0 dragging.** Port-vs-adopt is a real fork with a large cost delta; leaving it open stalls Part B
  and keeps the orphaned package alive. Mitigation: time-boxed spike, decision recorded in
  `.dev/decisions/` before Part B opens.

## Estimated Effort

- **Part A: Small — ~0.5–1 day.** Deletions plus the invariant tests and the dashboard cleanup.
- **Part B: Large, and not estimable until §0.** Adopting an SDK: ~4–6 days (adapter + lifecycle/refresh
  + classification flow + tenancy). Porting `packages/mcp-client/`: materially more, since 7,865 LOC of
  transports/registries/pool would have to be re-implemented and then tested against real servers. The
  estimate is deliberately withheld pending §0 rather than averaged.
