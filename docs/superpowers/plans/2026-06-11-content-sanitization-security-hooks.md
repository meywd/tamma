# Content Sanitization & Security Hooks — Refreshed Plan (2026-06-11)

> Refresh of the prior plan (`~/.claude/plans/hashed-purring-teapot.md`, file no longer
> exists). Original design intent preserved: defense-in-depth sanitization across 5
> integration paths, generic decorator over `IAgentProvider`, core modules in
> `packages/shared/src/security/` (no new package), pre/post hooks on MCP
> `invokeTool()`, and a tool-execution loop for the `IAIProvider` path with
> sanitization at each step.
>
> **Headline finding**: roughly half of the original plan already shipped under
> Epic 9 (stories 9-7 and 9-11, commits `bc0339a3..d036c608` and `b0737f51`).
> The primitives exist; what remains is (a) the `IAIProvider` tool loop, and
> (b) wiring the shipped-but-unconsumed primitives into the real composition
> roots (`execute-agent`, MCP client construction, orchestrator direct-agent
> path, action gating, secureFetch).

## Current-State Verification (checked against main @ 98cfb1c2, 2026-06-10)

| Original assumption | Status | Evidence |
|---|---|---|
| `packages/shared/src/security/` does not exist; plan creates it | **STALE — already exists** | `packages/shared/src/security/{content-sanitizer,url-validator,action-gating,secure-fetch}.ts` + colocated tests + `index.ts` barrel. `IContentSanitizer` + `ContentSanitizer` exported from `@tamma/shared`. |
| `SecureAgentProvider` decorator to be created | **STALE — already exists** | `packages/providers/src/secure-agent-provider.ts` (+ test). Classic decorator over `IAgentProvider`; sanitizes `config.prompt` (input), `taskResult.output` / `taskResult.error` (output). Wired via `RoleBasedAgentResolver` optional `sanitizer` option (`packages/providers/src/role-based-agent-resolver.ts:232`). |
| MCP client gets a `ToolHookRegistry` with pre/post hooks on `invokeTool()` | **STALE (renamed) — concept shipped as `ToolInterceptorChain`** | `packages/mcp-client/src/interceptors.ts`: `PreInterceptor`, `PostInterceptor`, `ToolInterceptorChain`, `createSanitizationInterceptor()`, `createUrlValidationInterceptor()`. Wired inside `MCPClient.invokeTool()` (`client.ts:341-396`, pre runs after schema validation/before execution; post runs after result construction/before events) plus `setInterceptorChain()` (`client.ts:672`). **BUT: zero non-test consumers — no production composition root ever constructs a chain or even an `MCPClient`.** |
| New `executeToolLoop()` for the `IAIProvider` path | **HELD — still missing** | No `executeToolLoop` anywhere. `MessageRequest.tools` (`packages/providers/src/types.ts:233`) and `MessageResponse.tool_calls` exist as types, but no code outside `packages/providers/src` calls `sendMessage` at all. The LLM-API tool path is genuinely unbuilt. |
| CLI integration path needed | **PARTIALLY STALE** | `tamma start` (`start.tsx:108`), `tamma server` (`server.ts:147`), `process-issue.ts:175` all build `ContentSanitizer` when `config.security.sanitizeContent !== false` and pass it to `RoleBasedAgentResolver`. **Gap**: `tamma execute-agent` (`packages/cli/src/commands/execute-agent.ts:384-410`) builds the agent via `AgentProviderFactory.create()` directly and calls `executeTask()` with **no** `SecureAgentProvider` wrap and no sanitizer — this is the worker entrypoint invoked by the C#/Elsa side, i.e. the highest-traffic unsanitized path. |
| Orchestrator integration path needed | **HELD (gap confirmed)** | `packages/orchestrator/src/engine.ts` has zero security imports. It accepts either `agentResolver` (sanitized transitively when the resolver carries a sanitizer) or a raw `agent?: IAgentProvider` (`engine.ts:38`) which **bypasses sanitization entirely** (`executeTask` call sites at `engine.ts:572`, `engine.ts:804`). No security events are emitted to the event store. |
| Two provider hierarchies: `IAIProvider` and `IAgentProvider`/`ICLIAgentProvider` | **HELD** | `IAIProvider` in `packages/providers/src/types.ts`; `IAgentProvider` in `packages/providers/src/agent-types.ts` (doc comment: "New code should prefer ICLIAgentProvider from './types.js'"). CLI agent providers live in `packages/providers/src/` (`claude-agent-provider.ts`, `opencode-provider.ts`, etc.) — there is no separate cli-agents package. |
| Action gating / secure fetch usable by hooks | **SHIPPED BUT UNWIRED** | `evaluateAction` + `DEFAULT_BLOCKED_COMMANDS` (`action-gating.ts`) and `secureFetch` (`secure-fetch.ts`) have **zero consumers** outside their own tests. Dead exports awaiting integration. |
| Tenancy merge (wave-b, PR #343) moved things the plan touches | **NO** | Wave-b was C#-side (`apps/tamma-elsa`) schema-per-tenant work. None of the TS files above moved or changed shape. However: `tamma api` now spawns the **C# ASP.NET Core binary** (`packages/cli/src/commands/api.ts`), and there is **no `packages/api`** anymore. The SaaS request path (incl. `LlmCallWorkflow` prompting) is C# and is **out of scope** for the TS `shared/src/security` modules — see Risks. |

Also present (shipped under 9-11, complements this plan): `packages/mcp-client/src/security/{rate-limiter,sandbox,validator}.ts`, and `packages/providers/src/sanitize-error.ts` (error redaction for diagnostics).

## Design Intent (unchanged)

1. **Generic decorator, no per-provider changes** — `SecureAgentProvider` wraps ANY
   `IAgentProvider`. (Done; this plan extends its *coverage*, not its shape.)
2. **Core primitives live in `packages/shared/src/security/`** — no new package. (Done.)
3. **Pre/post hooks on MCP tool invocation** — adopt the shipped
   `ToolInterceptorChain` name; the planned `ToolHookRegistry` is dropped.
4. **`executeToolLoop()` for the `IAIProvider` path** — sanitize at every step:
   gate each tool call, sanitize each tool result before it re-enters the
   conversation, bound iterations.
5. **Fail-open warnings for heuristics, fail-closed for gates** — sanitizer never
   throws (warnings only); action gating and URL validation block.

## Phases — the 5 integration paths

### Phase 1 — CLI agents path: close the `execute-agent` gap + action gating

The resolver-based entrypoints (`start`, `server`, `process-issue`) are covered.
`execute-agent` is not, and `allowedTools` are passed through unexamined.

- **Files**
  - `packages/cli/src/commands/execute-agent.ts` — after `factory.create(chainEntry)`
    (~line 397), wrap: `const agent = new SecureAgentProvider(rawAgent, new ContentSanitizer(), logger)`
    honoring the same `config.security.sanitizeContent !== false` toggle used by the
    other commands (read from the repo `.tamma/config.json` already loaded by the command;
    default ON).
  - `packages/shared/src/security/action-gating.ts` — extend `evaluateAction` (if
    needed) so it can evaluate an `allowedTools` entry of the form
    `Bash(<command>)` used by Claude Code allowlists; no breaking change to the
    existing signature.
  - `packages/providers/src/role-based-agent-resolver.ts` — optional
    `actionGate?: ActionGateOptions`; when set, the resolver filters/refuses
    `allowedTools` entries that fail `evaluateAction` before constructing the
    `AgentTaskConfig` (fail-closed: drop entry + `logger.warn`).
- **Tests** (Vitest, colocated, ESM `.js` imports, strict TS)
  - `execute-agent.test.ts`: prompt with injection markers reaches the (mock) provider
    sanitized; `sanitizeContent: false` disables; output/error fields sanitized in the
    result file.
  - `role-based-agent-resolver.test.ts`: blocked `allowedTools` entries dropped with warning.

### Phase 2 — LLM API providers (`IAIProvider`): build `executeToolLoop()`

The only fully-unbuilt piece of the original plan.

- **Files**
  - `packages/providers/src/execute-tool-loop.ts` (+ `.test.ts`) — new:
    ```ts
    export interface ToolExecutor {
      name: string;
      description: string;
      input_schema: Record<string, unknown>;
      execute(args: Record<string, unknown>): Promise<string>;
    }
    export interface ToolLoopOptions {
      maxIterations?: number;            // default 10
      sanitizer?: IContentSanitizer;     // sanitize tool results + final output
      actionGate?: ActionGateOptions;    // evaluateAction() on each tool call (fail-closed)
      validateUrl?: boolean;             // gate URL-bearing args via validateUrl()
      logger?: ILogger;
    }
    export async function executeToolLoop(
      provider: IAIProvider,
      request: MessageRequest,
      tools: ToolExecutor[],
      options?: ToolLoopOptions,
    ): Promise<MessageResponse>;
    ```
    Loop: `sendMessage` → if `finishReason === 'tool_calls'`, for each call:
    (1) `evaluateAction` gate — on block, return a refusal tool-result string,
    do NOT execute; (2) execute; (3) `sanitizer.sanitizeOutput()` the tool result
    before appending it to `messages`; repeat until `stop` or `maxIterations`
    (then throw `createProviderError('TOOL_LOOP_LIMIT', …, false, 'medium')`).
    Sanitize the final `content` once more on exit. Never mutate the caller's
    `request` (build new message arrays — state-management rule).
  - `packages/providers/src/index.ts` — export `executeToolLoop`, `ToolExecutor`,
    `ToolLoopOptions`.
  - Optional follow-up (only if a consumer materializes): a built-in `fetch` tool
    executor backed by `secureFetch` from `@tamma/shared` — first real consumer
    of `secure-fetch.ts`.
- **Tests**: mock `IAIProvider` scripted to return `tool_calls` then `stop`;
  assert gating blocks `rm -rf` style calls without executing; assert tool results
  are sanitized (zero-width chars stripped, injection warnings logged); assert
  iteration cap throws; assert no mutation of the input request.

### Phase 3 — MCP client: wire the existing `ToolInterceptorChain`

Mechanism shipped (9-11-T4) but no production code constructs a chain — or an
`MCPClient`. Make secure-by-default composition trivial and use it where the
client is composable.

- **Files**
  - `packages/mcp-client/src/interceptors.ts` — add
    `createDefaultSecurityChain(sanitizer: IContentSanitizer, logger?: ILogger): ToolInterceptorChain`
    combining `createSanitizationInterceptor` + `createUrlValidationInterceptor`
    (with `validateUrl` from `@tamma/shared`); export from `index.ts`.
  - `packages/intelligence-server/src/server.ts` / `types.ts` — when the caller
    supplies `bundle.mcpClient`, call
    `mcpClient.setInterceptorChain(createDefaultSecurityChain(...))` unless the
    bundle explicitly opts out (`bundle.mcpInterceptors: false | ToolInterceptorChain`).
    `McpManagementService.invokeTool` (`McpManagementService.ts:174`) then gets
    pre/post hooks for free.
  - `packages/intelligence/src/context/sources/mcp-source.ts` — no change
    (`IMCPClientLike` consumers inherit hooks from the concrete client).
- **Tests**: `interceptors.test.ts` for the default-chain factory;
  `intelligence-server` route test proving an injected fake client receives a chain
  and that `invokeTool` results pass through post-interceptors.

### Phase 4 — Orchestrator: no unsanitized agent path + audit events

- **Files**
  - `packages/orchestrator/src/engine.ts` — add optional
    `sanitizer?: IContentSanitizer` to `EngineContext`. In the constructor, if a
    raw `agent` is injected AND a sanitizer is present, wrap it:
    `this.agent = new SecureAgentProvider(agent, sanitizer, logger)`. The
    resolver path stays as-is (already wraps internally). This removes the
    bypass at `engine.ts:572` / `engine.ts:804` without breaking existing
    constructor signatures.
  - Security audit events (DCB): when sanitization produces warnings, emit
    `SECURITY.INJECTION_DETECTED` / `SECURITY.CONTENT_SANITIZED` via the engine's
    event store with tags `{ issueId, provider, phase }`. Implementation: a thin
    `IContentSanitizer` decorator in `packages/orchestrator/src/` (e.g.
    `auditing-sanitizer.ts`) that forwards to the inner sanitizer and appends
    events on non-empty warnings — keeps `@tamma/shared` free of event-store
    coupling.
  - `packages/cli/src/commands/{start.tsx,server.ts,process-issue.ts}` — pass the
    already-constructed sanitizer into `EngineContext` too (one-line each).
- **Tests**: `engine.test.ts` — raw-agent injection + sanitizer ⇒ prompt reaching
  the mock agent is sanitized; warnings ⇒ events appended with correct type/tags;
  no sanitizer ⇒ behavior unchanged.

### Phase 5 — CLI/config surface, docs, end-to-end

- **Files**
  - `packages/shared/src/types/security-config.ts` — extend alongside the existing
    `sanitizeContent?: boolean`:
    `actionGating?: boolean` (default true once Phase 1 lands),
    `extraBlockedCommands?: string[]`, `urlValidation?: boolean`.
  - `packages/shared/src/types/repo-config.ts` + `packages/shared/src/config/resolve-config.ts`
    (`resolve-config.ts:181` area) — plumb the new fields from `.tamma/config.json`.
  - `packages/cli` commands — thread the resolved security config into resolver
    options / engine context (Phases 1 & 4 consume it).
  - Docs: update `wiki`/README security section listing the five paths and the
    config toggles. No new `.md` beyond existing doc locations.
- **Tests**: `resolve-config.test.ts` precedence (repo config over defaults);
  one CLI-level test per command asserting the toggles reach the resolver/engine.

## Acceptance Criteria

1. Every `executeTask()` call site in the monorepo goes through
   `SecureAgentProvider` when `security.sanitizeContent !== false` — including
   `tamma execute-agent` (grep gate: no direct factory-created agent executes
   unwrapped outside tests).
2. `executeToolLoop()` exists, is exported from `@tamma/providers`, blocks gated
   tool calls without executing them, sanitizes every tool result before it
   re-enters the conversation, and enforces an iteration cap.
3. `ToolInterceptorChain` is constructible via a one-call default factory and is
   attached wherever an `MCPClient` is composed (currently the
   intelligence-server bundle path); `createSanitizationInterceptor` /
   `createUrlValidationInterceptor` gain at least one production consumer.
4. The orchestrator's raw-`agent` injection path can no longer bypass
   sanitization when a sanitizer is configured, and sanitization warnings are
   visible in the event stream as `SECURITY.*` events.
5. `evaluateAction` and `secureFetch` each have ≥1 production consumer (no more
   dead exports).
6. All new code: strict TS (`exactOptionalPropertyTypes` conditional assignment),
   ESM `.js` import suffixes, colocated `*.test.ts` Vitest suites; no mutation of
   caller-owned objects; no sanitizer ever throws on content (warnings only).
7. `pnpm build && pnpm test` green across the workspace.

## Risks

- **C#/SaaS path is out of scope**: `tamma api` spawns the ASP.NET Core binary;
  `LlmCallWorkflow` prompt assembly happens in `apps/tamma-elsa`. This plan
  secures the TS engine paths only. A mirrored C# sanitizer is a separate epic —
  call this out explicitly so "sanitization is on" is not over-claimed for SaaS.
- **Heuristic injection detection is warn-only by design** — do not convert to
  blocking without a false-positive review; code diffs legitimately contain
  strings like "ignore previous instructions" in test fixtures.
- **Action gating false positives** in `allowedTools` filtering could break agent
  runs mid-workflow; default-on should land only after running the gate in
  log-only mode against existing fixtures (Phase 1 test corpus).
- **Double sanitization**: resolver-wrapped providers + engine-level wrapping
  (Phase 4) could sanitize twice. Sanitization is idempotent for the
  strip/normalize steps, but warnings would be double-logged/double-evented —
  engine must skip wrapping when the agent came from a resolver (track via the
  existing `agentResolver` vs `agent` branch, which already distinguishes them).
- **Interceptor chain ordering**: sanitization before URL validation (sanitizer
  may unmask zero-width-obfuscated URLs); encode the order in
  `createDefaultSecurityChain` and test it.
- **`execute-agent` runs headless under Elsa** — a thrown gate error must still
  produce a well-formed result file (the command already guards execution; keep
  failures inside `taskResult.error`).

## Delta from previous plan

| # | Previous-plan assumption | What changed | Adaptation |
|---|---|---|---|
| 1 | Create core modules in `packages/shared/src/security/` | Already created by Epic 9 story 9-7 (commits `bc0339a3`, `b144d7ad`, `4bd837a0`, `507acc7b`): `ContentSanitizer`/`IContentSanitizer`, `validateUrl`/`isPrivateHost`, `evaluateAction`/`DEFAULT_BLOCKED_COMMANDS`, `secureFetch` | Phase dropped. Plan now *consumes* these; only minor additive extensions (Phase 1 gating helper, Phase 5 config types). |
| 2 | Create `SecureAgentProvider` decorator | Already shipped (`d036c608`) and wired into `RoleBasedAgentResolver` + 3 CLI commands | Phase reframed as coverage-closing: wrap the missed `execute-agent` path (Phase 1) and the engine raw-`agent` path (Phase 4). |
| 3 | MCP client gets a **`ToolHookRegistry`** with pre/post hooks on `invokeTool()` | Shipped under a different name in 9-11-T4 (`b0737f51`): `ToolInterceptorChain` already integrated inside `MCPClient.invokeTool()`; but nothing in production constructs a chain (or an `MCPClient`) | Drop the `ToolHookRegistry` name; adopt `ToolInterceptorChain`. Phase 3 = default-chain factory + wiring at the only current composition seam (intelligence-server bundle). |
| 4 | New `executeToolLoop()` for `IAIProvider` with per-step sanitization | Still absent — assumption held; additionally verified there are **zero** `sendMessage` consumers outside `packages/providers`, so the loop currently has no caller | Phase 2 builds it as an exported utility (with `ToolExecutor` seam) rather than threading it into a nonexistent call site; first consumers arrive with future IAIProvider-based features. |
| 5 | 5 integration paths incl. "orchestrator" and "CLI" as greenfield | CLI resolver paths already sanitized; orchestrator confirmed untouched; **`tamma api` is now a C# binary** (no `packages/api` exists) so the SaaS/API path moved out of TS reach | Orchestrator phase narrowed to the raw-agent bypass + DCB `SECURITY.*` audit events; SaaS/C# explicitly declared out of scope (risk #1). |
| 6 | (implicit) sanitization config surface to be invented | `security.sanitizeContent` toggle already exists in `security-config.ts` / `repo-config.ts` / `resolve-config.ts:181` | Phase 5 extends the existing surface (`actionGating`, `extraBlockedCommands`, `urlValidation`) instead of creating one. |
| 7 | (implicit) gating/secure-fetch wired by the same work that created them | They shipped consumer-less: `evaluateAction` and `secureFetch` have zero non-test references | New explicit acceptance criterion (#5): each gains a production consumer (resolver/loop gating; optional `fetch` tool executor). |
| 8 | Recent tenancy merge might have moved touched files | Verified: wave-b (PR #343) was C#-side only; all TS files in scope unmoved | No adaptation needed; baseline is main @ `98cfb1c2`. |
