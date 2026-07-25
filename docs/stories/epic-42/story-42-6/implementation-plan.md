# Implementation Plan — Story 42-6: MCP Integration

## Reconciled scope — differs from the story file

**Epic 42 was reconciled against Epic 43 on 2026-07-25.** 42-6's verdict is **"Part A unchanged. Part B
gains a catalog-binding prerequisite — an MCP tool entering the registry must resolve to a catalog entry."**
The deltas:

| Story file says | Reconciled |
|---|---|
| **Part A (§A1, §A2, AC A1–A3)** | **Unchanged.** Every line survives, including the retire-2/re-scope-6 decision and the "no tool-invoking route may reappear under `/api/kb/mcp/*`" invariant test. |
| **Part B §2** — an MCP tool "inherits 42-1's fail-safe default (`Destructive`, floor 100, deny-by-default) until an operator/`tenant_admin` classifies it via the **42-2 binding store**" | **42-1 no longer has a fail-safe governance default** (its descriptor is now `(RequiredSecret, Suspends)`), and **42-2 is DELETED.** Replaced by the new prerequisite: **registration is refused unless the composed tool name resolves to an Epic 43 catalog entry under `ActionNamespace.Tool`** (D5). Deny-by-default survives, relocated: an unclassified MCP tool cannot enter the registry at all, which is *stricter* than entering-but-inert. |
| **Part B §3 / B6 / B8** — 42-3 stage-2 authorization, "no `ExecuteAsync` before an `Authorize` decision" | **42-3 is DELETED.** Gating is Epic 43's **Seam B**, one call site in the shared tool-loop path. B8 is rewritten (D6) to assert that an MCP tool is subject to Seam B *identically to a native tool* — which follows from it being an ordinary `IToolExecutor` behind the same registry, and is exactly what this story must prove. |
| **Part B §1** — the 64-char budget "enforced twice: the validator regex and `tool_bindings.ToolName`'s `HasMaxLength(64)` (42-2 §1c)" | The second enforcement **no longer exists**. Epic 43's `ActionKey` is `(ActionNamespace Ns, string Key)` with no stated length constraint. So the budget is enforced by the validator regex **and by this story's registration-time check** — which therefore becomes load-bearing rather than belt-and-braces (D4). |
| **Part B Dependencies** — 42-1, 42-2, 42-3, 42-4, 42-5 | 42-2 and 42-3 are gone; **Epic 43's catalog (Stories 2/3/5) joins as a hard prerequisite** for D5. Surviving: 42-1 (`Register`/`Unregister`), 42-4 (secret binding), 42-5 (audit). |
| §0 / Open Question 3 — port `packages/mcp-client/` to C# **vs** adopt an MCP C# SDK | **Still open. This plan does not decide it** and deliberately does not estimate Part B's implementation. It is a product/architecture call with a large cost delta, and the reconciliation did not touch it. Framed for the decider in D1, with the evidence updated (the LOC figure in every existing document is wrong — see X1). |

## Scope & Deliverable

**Part A (Wave 0.5, independently landable, depends on nothing):** the two tool-facing `/api/kb/mcp/*`
routes and their entire call graph are deleted; the six server/config admin routes survive, explicitly
re-scoped as sidecar-KB administration, with a route-inventory test that fails if a tool-invoking route ever
reappears under that prefix. The dashboard's dead MCP tool panel is removed.

**Part B (Wave 2, blocked on the Wave-1 envelope *and* Epic 43's catalog):** an MCP client + one
`McpToolExecutor : IToolExecutor` per discovered tool, registered through 42-1's `Register` under
`mcp__<server>__<tool>`, refused at registration unless the name fits 64 chars, does not collide
case-insensitively, and **resolves to an Epic 43 catalog entry**. Secrets bind through 42-4, invocations
audit through 42-5, and gating happens at Epic 43's Seam B with no MCP-specific code. Part B's
implementation is **not estimated** pending §0.

## Pre-Reading

- `docs/stories/epic-42/story-42-6/42-6-mcp-integration.md` — the story (**read the Reconciled scope table first**; Part A survives verbatim)
- `docs/stories/epic-42/README.md` — the verdicts; "Existing MCP prior art"; Open Questions 1–3
- `docs/stories/epic-43/README.md` — §1 `ActionNamespace.Tool` / `ActionKey.ToWire()`; §4 resolution; Enforcement **Seam B**; and — read this before writing D5 — the honest hole *"MCP is one coarse member with no drift signal. Adding a server, or a tool on an existing server, changes nothing in the catalog."*
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:3159` — `var kb = app.MapGroup("/api/kb").RequireAuthorization("SettingsView");`; **`:3179-3187`** — the `// MCP (8)` block; the dev-without-JWT branch that replaces every named policy with `AllowAnonymousRequirement`
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:115` (`// ── MCP (8) ──`) and the eight handlers: `ListMcpServers` `:117`, `GetMcpServer` `:122`, `StartMcpServer` `:128`, `StopMcpServer` `:134`, `GetMcpConfig` `:140`, `UpdateMcpConfig` `:145`, **`ListMcpTools` `:151`**, **`InvokeMcpTool` `:157`**
- `apps/tamma-elsa/src/Tamma.Api/Services/KnowledgeBase/IIntelligenceHttpClient.cs` + `IntelligenceHttpClient.cs:91-120` — the forwarders; `Dtos/KnowledgeBase/KnowledgeBaseDtos.cs:52-65` — the proxy DTOs
- `packages/intelligence-server/src/server.ts:47` (`new McpManagementService(bundle?.mcpClient)`), `:120-146` (the eight Fastify routes), `:211` (`buildIntelligenceBundleFromEnv()`)
- `packages/intelligence-server/src/services/McpManagementService.ts:48` (class), `:52-54` (ctor, `client ?? null`), **`:163-171`** (`invokeTool` → `{ success:false, content:null, error:'MCP client not configured', durationMs:0 }`, returned with **HTTP 200**), `:84`/`:89-97`/`:113`/`:122`/`:129`/`:153` (the other null-client degradations)
- **`packages/intelligence-server/src/env-composition.ts:429-447`** — `buildIntelligenceBundleFromEnv`; sets `vectorStore` `:437` and optionally `ragPipeline` `:440`. **A search for `mcp`/`Mcp`/`MCP` across this entire file returns zero matches** — the production composition root has no MCP code at all. `types.ts:173` declares the optional field; the only assignment anywhere is a test mock at `src/__tests__/routes.test.ts:105`
- `packages/mcp-client/` — `package.json` (`@tamma/mcp-client` 0.1.0), `src/` (32 files), `__tests__/`; `client.ts`, `registry.ts`, `security/`, `audit.ts`, `connections/pool.ts`, `servers/index.ts`
- `packages/intelligence/src/context/sources/mcp-source.ts:8-12` (`IMCPClientLike` — `listServers`/`listResources`/`readResource`, **no `invokeTool`**), `:14` (`MCPSource`), `:60` (`createMCPSource`), `:6` (the "avoids a hard build dependency" comment)
- `packages/dashboard/src/services/knowledge-base/api-client.ts:153-158` — `mcpApi.invokeTool` targets `/kb/mcp/servers/{server}/tools/{tool}/invoke` and `mcpApi.listTools` targets `/kb/mcp/servers/{server}/tools` — **neither pattern is mapped by the API**, so both 404 today
- `apps/tamma-elsa/src/Tamma.Activities/Security/ToolCallValidator.cs:28-29` — `^[a-zA-Z0-9_\-]{1,64}$`, `RegexOptions.Compiled`; the check order in `Validate` `:65-179`: **(1) allowlist `:79`, (2) name format `:92`**
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolExecutorRegistry.cs:19` — `StringComparer.OrdinalIgnoreCase`; `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs:260-282` (the validator block), `:431` (`GetExecutor`), `:335` (the fork)
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs:57-66` — `NonHttpProviders`, **six** entries; the guard `:86-92` throwing `ProviderNotSupportedException` with *"requires a non-HTTP transport (CLI subprocess or MCP) that is not yet ported to C#. See audit finding 003."*; surfaced as `PROVIDER_NOT_SUPPORTED` / **501** at `Endpoints/ProviderEndpoints.cs:744-746`
- `apps/tamma-elsa/src/Tamma.Api/Tests/KnowledgeBase/IntelligenceHttpClientTests.cs` — the two pinning tests the story cites at `:251` and `:259` (**verify; unconfirmed**)
- `docs/stories/epic-42/story-42-1/implementation-plan.md` (D4/D5 — the registry seam and the platform-only constraint), `story-42-4/implementation-plan.md`, `story-42-5/implementation-plan.md`

## Corrections to the story

- **X1 — `packages/mcp-client/` is 9,662 LOC of non-test source, not 7,865.** Measured over `src/**/*.ts`;
  including `__tests__` it is 15,121. The figure 7,865 appears in the story (§(a)), in
  `epic-42/README.md` (twice) and in its Open Question 3. **Every port-vs-adopt cost estimate keyed on 7,865
  is understated by ~23%.** Since §0's decision is explicitly cost-driven, correct the number before
  deciding.
- **X2 — "never built" is imprecise, and the imprecision matters.** Verified: no `package.json` anywhere
  declares `@tamma/mcp-client` as a dependency (the only two repo-wide mentions are *comments* at
  `packages/intelligence/src/context/sources/mcp-source.ts:6` and
  `packages/intelligence-server/src/server.ts:4`); no `tsconfig.json` project-references it; and the root
  `typecheck` script (`package.json:24`) lists only `shared`, `platforms`, `providers`, `orchestrator`, `cli`
  — so **`tsc` never validates it**, and `.github/workflows/ci.yml` never runs a root `pnpm build`. But it
  **is** in `pnpm-workspace.yaml` (`packages/*`) and `pnpm-lock.yaml:495`, and **its tests DO execute** under
  the root vitest run (`vitest.config.ts:54` includes `packages/**/*.{test,spec}.{ts,tsx}`; aliases at
  `:40-42` point `@tamma/mcp-client` at source). So the accurate statement is: **9,662 LOC that is installed,
  test-executed, never type-checked and never built, with zero dependents.** That is worse than "dead code"
  for a decider — the passing tests create an illusion of maintenance.
- **X3 — there are THREE parallel MCP client abstractions, and the story names only two.**
  `IMCPClientLike` (`packages/intelligence/src/context/sources/mcp-source.ts:8-12`, resources only),
  `IMcpClient` (`packages/intelligence-server/src/types.ts:101`, the one `McpManagementService` consumes),
  and the real `IMCPClient` inside `packages/mcp-client/` (`types.ts:251`). **None of the three is the
  same interface**, and none is `@tamma/mcp-client`'s from the sidecar's perspective. §0's decision should
  name what happens to all three, not two.
- **X4 — `NonHttpProviders` has six entries, not two.** `claude-code`, `claude-code-cli`, `opencode`,
  `opencode-cli`, `zen-mcp`, `zen` (`HttpProviderClient.cs:57-66`). The story's point — that a C# MCP
  transport has a second, independent customer — stands, but the surrounding failure surface is broader than
  the two `zen*` entries suggest, and four of the six want a **CLI subprocess**, not MCP. Do not let §0 be
  argued as "porting MCP also fixes the provider gap"; it fixes one third of it.
- **X5 — the invoke route returns HTTP 200.** `McpManagementService.invokeTool` `:163-171` returns
  `{ success:false, … }` as a **200 OK** body, not an error status. So a caller checking status codes sees
  success today. This strengthens Part A's "retire while it dead-ends" argument and is worth stating in the
  deletion rationale.
- **X6 — the two `IntelligenceHttpClientTests` line cites are unverified.** `:251` and `:259`. Re-derive
  before deleting.

## Design Decisions

### Part A

- **D1 — retire, do not re-gate; and delete the whole call graph in one commit.** Re-gating would mean
  threading the registry, `ToolCallValidator`, Epic 43's Seam B and 42-5's audit through a KB-admin proxy
  route to reach a sidecar that holds no client (X and `env-composition.ts` has **zero** MCP code). Part B
  provides the governed path; a second entry point earns nothing and would have to be deleted later at
  higher cost. The complete graph: the two `Program.cs` route mappings (`:3186-3187`), `KbEndpoints
  .ListMcpTools` (`:151`) + `.InvokeMcpTool` (`:157`), the `IIntelligenceHttpClient` members and their
  `IntelligenceHttpClient` implementations (`:111-120`), the `McpInvokeRequest` DTO, and the two pinning
  tests (X6). Plus the dashboard's `mcpApi.invokeTool` / `mcpApi.listTools` and their `useMCPServers` call
  sites — which **404 today** (`api-client.ts:153-158` targets patterns the API never maps), so removing them
  is cleanup, not a behaviour change.
- **D2 — the surviving six are re-scoped in code, and the invariant is a test, not a comment.** `servers`,
  `servers/{id}`, `servers/{id}/start`, `servers/{id}/stop`, `config` GET/PUT stay, with an explicit
  "sidecar KB administration — NOT the governed tool catalog" note at the mapping site. The invariant test
  walks `EndpointDataSource` and fails if any mapped pattern contains both `mcp` and a tool-invoking segment.
  Record in the same note that this surface becomes `AllowAnonymous` in the dev-without-JWT branch, so it
  must never grow a capability that matters — the reason the invariant is a build gate rather than a
  convention.

### Part B

- **D3 — §0 (port vs adopt) is an OPEN PRODUCT QUESTION and this plan does not answer it.** The two live
  options — port the (X1-corrected) **9,662 LOC** TS client to C#, or adopt an MCP C# SDK — have materially
  different costs and different long-run maintenance stories. Proxying through the TS sidecar is already
  ruled out on evidence (it would put tool execution behind an HTTP hop outside the tool envelope,
  re-creating in Part B exactly the bypass Part A deletes). Inputs a decider needs, assembled here rather
  than decided: (i) which transports are actually required — stdio vs SSE/streamable HTTP; (ii) protocol
  version maintenance burden, which is the whole argument for an SDK; (iii) how much of the TS client's
  extras (rate limiter, path validator, resource monitor, audit log, connection pool) is **already** provided
  by the 42-1/42-4/42-5 + Epic 43 envelope and would be deleted on port — several duplicate it outright;
  (iv) X3's three abstractions and what happens to each; (v) X4's honest scope for the provider-transport
  side benefit. **Time-boxed spike, decision recorded in `.dev/decisions/` before Part B opens.** Whatever is
  chosen, `packages/mcp-client/` must not stay orphaned: it becomes the port's source of truth and is then
  deleted, or it is deleted outright. X2 makes leaving it strictly worse than either.
- **D4 — the composed name is `mcp__<server>__<tool>`, and registration-time validation is now the primary
  enforcement.** `ToolCallValidator` applies `^[a-zA-Z0-9_\-]{1,64}$` (`:28-29`) as check **#2** (`:92`),
  *after* the allowlist check (`:79`) — so a colon-namespaced name would pass the allowlist and then be
  rejected on **every** call: a catalog that registers cleanly and fails 100% of invocations. Double
  underscore is legal and collision-safe. The 64-char budget means `server + tool ≤ 57`. Per the Reconciled
  scope, 42-2's second enforcement is gone, so **reject at registration** — never truncate (truncation
  collides). Registry lookup is `OrdinalIgnoreCase` (`ToolExecutorRegistry.cs:19`), so normalize to
  lower-case and reject case-insensitive collisions at registration too.
- **D5 — THE NEW PREREQUISITE: no catalog entry, no registration.** An MCP tool entering the registry must
  resolve to an Epic 43 catalog entry under `ActionNamespace.Tool` — i.e. `ActionKey(Tool,
  "mcp__<server>__<tool>")` must be present in the catalog index. If it is not, `Register` is **refused**
  with a typed error naming the catalog. This is where deny-by-default now lives, and it is *stricter* than
  what 42-2 offered: previously an unclassified MCP tool entered the registry and sat inert; now it does not
  enter. Two things must be faced honestly rather than assumed:
  - **Epic 43's README records that MCP is "one coarse member with no drift signal — adding a server, or a
    tool on an existing server, changes nothing in the catalog."** So the catalog as designed may hold a
    single coarse `tool:mcp` member rather than a per-tool entry. If so, D5 degrades to "the coarse MCP
    member must exist and be enabled", and per-tool classification has no home. **That is a design question
    for Epic 43, surfaced here as a blocking coordination item** (see Blocks / Blocked by), not something
    this story may resolve by minting its own store — which is precisely the duplication the reconciliation
    deleted.
  - Epic 43 D2 says an unclassified action is *"allowed at runtime, unmergeable in CI"*. D5 is deliberately
    **stricter at the registration boundary** than that runtime rule, because registration is not a hot path
    and an unclassified *external* tool is a different risk class from an unclassified in-repo call site.
    Flag the divergence to Epic 43 rather than silently adopting either posture.
- **D6 — B8 is rewritten: prove sameness, not a bespoke path.** Original: *"routed to 42-3's stage-2
  invocation-time authorization exactly like a native destructive tool — no `ExecuteAsync` before an
  `Authorize` decision."* Rewritten: **an MCP tool is subject to Epic 43's Seam B identically to a native
  tool, with zero MCP-specific gating code** — asserted by driving the same denial scenario against a native
  tool and an MCP tool and diffing the outcomes. That is the real acceptance criterion for "the catalog is
  open-ended *and* governed", and it follows structurally from `McpToolExecutor` being an ordinary
  `IToolExecutor` behind the same registry (`InlineToolLoopRunner.cs:431`) and the same fork (`:335`).
- **D7 — everything Part B adds is Api-side.** Package `Tamma.Api.Services.Tools.Mcp`, wired next to the six
  built-ins at `Program.cs:753-766`. Reasons in force order: rule 1 (a workflow step never calls an external
  system or holds an external credential, and an MCP call is exactly a credentialed external process/socket
  call); runtime (`Tamma.ElsaServer/Program.cs:286-292` — the catalog was removed from the engine, and the
  singleton the `Register` seam mutates is the Api-side one). *Honest scope:* `TAMMA001`'s injection check is
  a closed 13-entry denylist naming no MCP type, and its HTTP check fires only on a statically-literal
  external host — an MCP endpoint is always config-supplied — so the analyzer would **not** mechanically
  trip. Siting is settled by the first two reasons; the analyzer is a backstop. **This story adds nothing
  engine-side.**
- **D8 — refresh is `Unregister` + `Register`, and a vanished server must not leave dangling executors.**
  42-1 D4's replace path defaults to reject-on-duplicate, so rediscovery unregisters first. A server going
  away unregisters its tools loudly — a dangling executor that fails every call is worse than an absent one,
  because the model keeps choosing it.
- **D9 — tenancy: platform-owned first, because 42-1 forces it.** 42-1 D5 ships `Register`/`Unregister`
  platform/deployment-scoped only and rejects principal-bound registration outright, so the platform-owned
  path is not a preference — it is the only reachable one. Whether tenant-scoped MCP registration is ever
  permitted is Epic 42 Open Question 2 / Epic 43 Open Question 1 (**the same question, filed twice**), and
  it is *not* decided here. If it is permitted, Part B also owns building the per-principal registry **view**
  — which is separately what would finally give 39-5 D6's principal-bound-tool pattern a delivery path into
  `InlineToolLoopRunner` (whose ctor `:45-67` accepts no ad-hoc executor collection today).

## Implementation Steps

### Part A — Wave 0.5, no dependencies

1. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs`** — delete the two mappings at `:3186-3187`; add
   D2's re-scoping note to the surviving block.
2. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs`** — delete `ListMcpTools` (`:151`) and
   `InvokeMcpTool` (`:157`); update the `// ── MCP (8) ──` header to `(6)`.
3. **MODIFY `IIntelligenceHttpClient.cs` + `IntelligenceHttpClient.cs`** — delete the two MCP tool members
   and their implementations (`:111-120`); **DELETE** the `McpInvokeRequest` DTO from
   `Dtos/KnowledgeBase/KnowledgeBaseDtos.cs`.
4. **DELETE the two pinning tests** in `Tamma.Api.Tests/KnowledgeBase/IntelligenceHttpClientTests.cs`
   (re-derive the lines, X6).
5. **MODIFY `packages/dashboard/`** — remove `mcpApi.invokeTool` / `mcpApi.listTools`
   (`src/services/knowledge-base/api-client.ts:153-158`) and their `useMCPServers` call sites.
6. **CREATE the Part A tests** (Test Plan).

*Optional, coordinate with the sidecar owner:* the Fastify side (`server.ts:120-146`) still exposes eight
routes. Retiring its two tool routes is not required for Part A's guarantee (nothing in the C# API can reach
them once step 1 lands) but leaving them is a loaded gun for anyone who later sets `mcpClient`. Recommend
retiring them in the same change; if the sidecar is owned elsewhere, file it.

### Part B — Wave 2, blocked on §0, the Wave-1 envelope, and Epic 43

7. **§0 spike + decision record in `.dev/decisions/`** (D3). Nothing below starts first.
8. **CREATE `Tamma.Api/Services/Tools/Mcp/`** — the client (per §0), `McpToolExecutor : IToolExecutor`
   (never throws — transport/protocol errors are `Success = false`), the discovery/refresh service, and the
   config-driven server registration (`Mcp:Servers:<name>:{ Endpoint, Transport, AuthSecretName }`) plus a
   management endpoint.
9. **Registration validation** (D4/D5): compose → lower-case normalize → 64-char check → case-insensitive
   collision check → **Epic 43 catalog resolution** → `Register`. Every refusal typed and loud.
10. **Wire 42-4 and 42-5** — the server's auth secret is a `SecretRequirement` like any other; invocations
    emit the `TOOL.*` trio with MCP tags.
11. **CREATE the Part B tests** (Test Plan).

## Data & Migrations

None in either part. Part B's server registration is configuration plus (optionally) an Epic 43 catalog row
authored by an admin — this story mints no table.

## Events

- **Part A: none.** It deletes a surface.
- **Part B:** reuses 42-5's `TOOL.INVOKED`/`SUCCEEDED`/`FAILED` with MCP tags (`server`, `mcpTool`). Adds
  `TOOL.MCP_SERVER_REGISTERED` / `TOOL.MCP_SERVER_REMOVED` (server name + tool count, **never the auth
  secret**) for the catalog-change audit.

## Test Plan

**Part A**

- **`McpRouteRetirementTests`** — `POST /api/kb/mcp/tools/invoke` and `GET /api/kb/mcp/tools` return **404**;
  a route-inventory test over `EndpointDataSource` fails if any mapped pattern contains both `mcp` and a
  tool-invoking segment (`tools`, `invoke`, `execute`, `call`). **Covers A1.**
- **`IntelligenceHttpClientSurfaceTests`** — a reflection assertion that no `InvokeMcpTool*` / `ListMcpTools*`
  member exists on `IIntelligenceHttpClient`, so no surviving admin route can reach a tool call. **Covers A2.**
- **`McpAdminRouteRegressionTests`** — the six survivors still respond as before; a dashboard-side check
  asserts no remaining `mcpApi.invokeTool` / `mcpApi.listTools` call site. **Covers A3.**

**Part B**

- **`McpNameCompositionTests`** — `mcp__<server>__<tool>` for representative inputs; a raw
  `ToolCallValidator.Validate` on the composed name returns `IsValid = true` (the regression that a colon
  scheme would have failed on every call); a >64-char composition and a case-insensitive collision are each
  **rejected at registration** and never appear in `GetAll`. **Covers B1, B3.**
- **`McpCatalogBindingTests`** (D5, the new prerequisite) — a tool whose `ActionKey(Tool, name)` has no
  catalog entry is **refused** at `Register` with a typed error naming the catalog and is absent from
  `GetAll`; with the entry present, registration succeeds. Plus a test pinning the *coarse-member* fallback
  if Epic 43 lands one MCP member rather than per-tool entries, so the divergence is visible rather than
  silently satisfied. **Covers the reconciled B5.**
- **`McpToolExecutorContractTests`** — success, protocol error and timeout each map to a
  `ToolExecutionResult` **without throwing** (the `IToolExecutor` contract at `IToolExecutor.cs:8`); driven
  through both `InlineToolLoopRunner` branches (`EnableParallelTools` `false` — the default — and `true`).
  **Covers B2, B4.**
- **`McpGovernanceSamenessTests`** (D6) — the same Epic 43 Seam B denial scenario is run against a native
  tool and an MCP tool and the outcomes are asserted **identical**; a grep-style structural assertion that
  no MCP type appears in the gating path. **Covers the reconciled B8.**
- **`McpSecretAndAuditTests`** — an MCP tool with a bound secret authenticates via 42-4 and emits redacted
  42-5 `TOOL.*`; the secret value appears in no event, no `ToolExecutionResult.Output`, no log line.
  **Covers B6.**
- **`McpRefreshTests`** — removing a server `Unregister`s its tools; `GetExecutor` returns null, `GetAll`
  omits them, no dangling executor is resolvable; rediscovery via `Unregister`+`Register` succeeds where a
  bare duplicate `Register` is rejected. **Covers B7.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| A1 — the two tool routes 404; no tool-invoking `mcp` route can reappear | 1, 2 (D1/D2) | `McpRouteRetirementTests` |
| A2 — no MCP tool member on `IIntelligenceHttpClient` | 3 | `IntelligenceHttpClientSurfaceTests` |
| A3 — six admin routes unchanged; dashboard cleaned | 1, 5 | `McpAdminRouteRegressionTests` |
| B1 — tools discovered and registered under `mcp__<server>__<tool>` | 8, 9 (D4) | `McpNameCompositionTests` |
| B2 — the namespaced name survives the live path end-to-end | 8 (D4) | `McpNameCompositionTests`, `McpToolExecutorContractTests` |
| B3 — overflow / collision rejected at registration | 9 (D4) | `McpNameCompositionTests` |
| B4 — success / protocol error / timeout map without throwing, both branches | 8 (D7) | `McpToolExecutorContractTests` |
| B5 — **reconciled**: no Epic 43 catalog entry ⇒ no registration | 9 (D5) | `McpCatalogBindingTests` |
| B6 — bound secret authenticates; nothing leaks | 10 | `McpSecretAndAuditTests` |
| B7 — removal unregisters cleanly | 8 (D8) | `McpRefreshTests` |
| B8 — **reconciled**: governed identically to a native tool at Seam B, with no MCP-specific gating code | 8 (D6) | `McpGovernanceSamenessTests` |

## Blocks / Blocked by

- **Part A — blocked by NOTHING.** It touches only route mapping, `KbEndpoints`, `IIntelligenceHttpClient`,
  one DTO, two tests and the dashboard. It can land before 42-1. **It should land first in the whole epic**:
  it is a deletion while the route dead-ends (X5: it even returns 200 today) and becomes a migration the
  moment anyone sets `mcpClient` on the sidecar bundle. Shipping the epic with it open would make the epic's
  own governance claim false.
- **Part B — blocked by §0 (D3), an open product question.** No estimate, no start.
- **Part B — blocked by 42-1** (`Register`/`Unregister`, and its platform-only constraint which forces D9),
  **42-4** (the server's auth secret), **42-5** (the audit trail).
- **Part B — blocked by Epic 43 Stories 2 / 3 / 5** (catalog core, groups, storage + resolver) for D5, and
  **needs a coordination decision from Epic 43** on whether the `Tool` namespace holds a per-MCP-tool entry
  or one coarse `mcp` member. Epic 43's own README records the coarse-member design and names the resulting
  blind spot; D5 is not implementable against the coarse form as written. **This is the single most
  important cross-epic item in 42-6 and it is unresolved.**
- **Open, filed twice, decided nowhere:** may a `tenant_admin` register a tenant-scoped MCP server? Epic 42
  Open Question 2 and Epic 43 Open Question 1 are the same question. If yes, Part B additionally owns the
  per-principal registry view.
- **Blocks — open-ended catalog growth for Epic 41 and beyond without per-tool code**, and (per the epic
  README) 41-28's shipped-UI audit, whose required browser/render capability **no Epic 42 family provides** —
  an external MCP server is the only plausible path to it.
- **Adjacent, not blocking:** `zen-mcp-provider` (MCP as transport *to an LLM*, not a tool bridge) and
  `MCPSource` (MCP *resources*). Neither blocks; both should converge on §0's client. X4 bounds how much of
  the provider gap §0 actually closes.

## Risks & Mitigations

- **Shipping the epic with Part A still open.** The epic's governance claim would be false while a live
  invoke route exists. Mitigation: Part A depends on nothing and is sequenced Wave 0.5, ahead of 42-1.
- **§0 dragging (D3).** A real fork with a large cost delta; leaving it open stalls Part B and keeps 9,662
  LOC alive and misleading (X1/X2). Mitigation: time-boxed spike, decision in `.dev/decisions/`, and X1/X2/X3
  give the decider corrected evidence rather than the numbers currently in circulation.
- **D5 may be unimplementable as written.** If Epic 43 ships one coarse MCP catalog member, per-tool
  classification has no home and "deny-by-default per MCP tool" cannot be enforced. Mitigation: raised as a
  blocking coordination item above; `McpCatalogBindingTests` includes the coarse-member fallback so the
  degradation is *visible* rather than silently satisfied. **Do not resolve it by minting an MCP-local
  binding store** — that recreates 42-2.
- **Tool-name format (high probability, cheap to get wrong).** A colon-namespaced name passes the allowlist
  check (`:79`) and is rejected by the format regex (`:92`) on every call — a catalog that registers cleanly
  and fails 100% of invocations. Mitigation: `mcp__…__…` plus B2/B3.
- **Trust surface.** An MCP server is external code exposing tools; auto-trusting it reopens the attack
  surface the epic closes. Mitigation: D5's refuse-at-registration, D9's platform-owned allowlist, and the
  same secret/audit envelope as native tools.
- **Global-singleton mutation for a per-tenant server.** Registering a tenant's tools into the shared
  singleton leaks them cross-tenant. Mitigation: 42-1 D5 rejects principal-scoped registration outright;
  per-tenant MCP stays behind the registry view.
- **Input-schema mismatch.** MCP schemas may not satisfy `ToolCallValidator`'s argument constraints (the
  100 KB cap at `:25`, the recursive string sanitization at `:158-160`). Mitigation: validate and normalize
  at registration; reject a tool whose schema cannot be represented, loudly.

## Est. Effort

| Part | Work | Days |
|---|---|---|
| **A** | Route + handler + forwarder + DTO + test deletions, the two invariant tests, dashboard cleanup | **0.5–1** |
| **A (optional)** | Retiring the sidecar's two Fastify tool routes | +0.25 |
| **§0** | Time-boxed port-vs-adopt spike + `.dev/decisions/` record | **1–2** |
| **B** | **Not estimable until §0.** Adopting an SDK: ~4–6 d (adapter + lifecycle/refresh + catalog-binding flow + tenancy). Porting 9,662 LOC of transports/registries/pool and testing it against real servers: **materially more** — and X1 means every previously circulated figure was keyed on a 23%-low LOC count | **withheld** |

The estimate is deliberately withheld rather than averaged. Part A is small, independent, and should be
scheduled immediately; Part B should not be scheduled at all until §0 is recorded **and** Epic 43's
`Tool`-namespace granularity is settled.
