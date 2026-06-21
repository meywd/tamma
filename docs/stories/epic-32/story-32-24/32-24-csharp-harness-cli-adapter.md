# Story 32-24: C# Harness / CLI Agent Adapter (single-user local — DEFERRED)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **self-hosted / single-user Tamma operator who runs harness-style agents (`claude-code`, `opencode`, `zen-mcp`) on my own machine**,
I want a C# harness/CLI agent execution path that spawns the local agent process (or drives its local SDK session), captures its self-reported aggregate cost, and slots into the single-user managed-run surface **without** round-tripping through `POST /api/v1/llm/call`,
So that single-user mode regains the harness/CLI parity it already has in the TypeScript `packages/providers` path — where the agent owns its own tool loop, auth, streaming, and retries — while SaaS keeps exactly one execution path (the API-provider path) and harness providers stay structurally unreachable there.

## Priority

**P3 — DEFERRED.** This story is explicitly **not** needed for SaaS and is **not** a blocker for the call-LLM endpoint (32-5), the SaaS gate (32-4), or any Wave-F / billing / analytics story. It restores single-user harness parity that today exists only in the TypeScript path; the C# engine currently **rejects** harness providers outright (`HttpProviderClient.NonHttpProviders` → `ProviderNotSupportedException`). Sequence it **after** the LLM-API path (32-5) and the gate (32-4) are landed — they define the seams this adapter plugs into without traversing the endpoint. It can ship at any later point single-user harness execution is wanted; nothing downstream waits on it.

> **DEFERRED NOTE (read first).** Per the managed-LLM deep dive §1 + §6 item 6 and the revised-agent design §5.3, harness/CLI providers are a **single-user-only LOCAL affordance** and are **legitimately exempt** from `/llm/call` mediation: they spawn a local process, hold their own auth, and run their own loop, so routing them through the central endpoint adds a hop with **no security benefit** (the cross-cutting rule targets *external API/provider* calls — a local process is not one). In SaaS the 32-4 gate makes them unreachable (`400 SAAS_PROVIDER_NOT_ALLOWED`). This adapter therefore **never calls `POST /api/v1/llm/call`** and **never resolves a remote credential** — it is the local counterpart to 32-5's API path, not a second client of it.

## Context

Tamma has **two provider hierarchies** (deep dive §1):

| | API providers | Harness / SDK providers |
|---|---|---|
| Interface (TS) | `ILLMProvider`/`IAIProvider`, `type:'llm-api'` | `IAgentProvider`/`ICLIAgentProvider`, `type:'cli-agent'` |
| Execution | Tamma owns request / tool-loop / streaming / retries; HTTPS to provider | Provider owns its **own** loop / auth / streaming / retries — `ClaudeAgentProvider` `spawn('claude', -p --output-format stream-json)`; `OpenCodeProvider` SDK session |
| Cost | per-token via `IProviderPricingService` | **aggregate `costUsd` only** — no input/output token split |
| In the C# engine today | **the only path** (32-5) | **rejected** — `HttpProviderClient.NonHttpProviders = { claude-code, claude-code-cli, opencode, opencode-cli, zen-mcp, zen }` throws `ProviderNotSupportedException` (`HttpProviderClient.cs:57-92`) |

So there is **no C# harness adapter today**. The TypeScript implementations are mature and are the porting source:

- `packages/providers/src/claude-agent-provider.ts` — `ClaudeAgentProvider implements IAgentProvider, ICLIAgentProvider` (`name = 'claude-code'`). Spawns `claude -p --output-format stream-json`, parses the stream-json message protocol for progress + the final `result.cost_usd`, supports `--resume <sessionId>`, `--dangerously-skip-permissions`, `--allowedTools`, returns an `AgentTaskResult { success, output, costUsd, durationMs, error? }`.
- `packages/providers/src/opencode-provider.ts` — `OpenCodeProvider implements IAgentProvider, ICLIAgentProvider` (`name = 'opencode'`). Lazily imports `@opencode-ai/sdk`, connects to a **local** OpenCode server, creates/resumes a `session`, calls `client.session.prompt(...)`, returns the same `AgentTaskResult` shape (`sessionResume: true`).
- `packages/providers/src/zen-mcp-provider.ts` — MCP-transport harness (the `zen`/`zen-mcp` keys).
- Contracts: `packages/providers/src/agent-types.ts` (`IAgentProvider.executeTask(config, onProgress)`, `AgentTaskConfig`, `AgentProgressEvent`), and `AgentTaskResult` in `packages/shared/src/types/index.ts:245` (`{ success, output, costUsd, durationMs, error? }`).

This story ports a single-user **local** execution path for these into C#. It does **not** introduce harness execution to SaaS, does **not** change `/llm/call`, and does **not** change the API-provider path. It is the local sibling of 32-5: where 32-5's `IManagedAgent` resolves the **LLM-API-backed** backend, this story provides the **harness-backed** backend that the *single-user* resolver may return, so that single-user workflows treat all agents uniformly while SaaS sees only the one API path.

## Acceptance Criteria

1. An **`ICliAgentExecutor`** contract and concrete C# harness adapters exist under `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/`:
   - `ClaudeCodeAgentExecutor` — spawns `claude -p --output-format stream-json …` via `System.Diagnostics.Process`, parses the stream-json line protocol, captures the final `result.cost_usd` and `session_id`, supports resume/allowed-tools/permission-mode flags. Ports `claude-agent-provider.ts` semantics 1:1.
   - `OpenCodeAgentExecutor` — drives the local OpenCode server (create/resume `session` → `session.prompt`) via its local HTTP/SDK surface, returning the aggregate result. Ports `opencode-provider.ts` semantics.
   - (Stub-acceptable for v1) `ZenMcpAgentExecutor` — the MCP-transport harness; may be a documented `NotImplemented` placeholder behind the same contract if the C# MCP client (deep dive §4 / `ProviderSession.cs:87`) is not yet ported, **but** it MUST register and resolve so the surface is complete.
2. The adapters return a typed **`CliAgentRunResult`** carrying at minimum: `Provider`, `Model`, `Success`, `Output`/`ResponseText`, **`CostUsd` (aggregate only — see AC4)**, `DurationMs`, `SessionId?`, `ExitCode?`, and on failure `FailureCode` + `FailureReason`. The shape is convertible to the 32-5 `AgentRunResult` so single-user callers consume one result type (see AC6).
3. **This path NEVER traverses `POST /api/v1/llm/call`** and **NEVER resolves a remote provider credential.** The adapter holds no API key; the local agent process owns its own auth (e.g. the user's local `claude` login, the local OpenCode server). A unit/architecture test asserts the executor injects **no** `TammaApiClient`, **no** `IProviderCredentialResolver`, and **no** `HttpClient`-to-`/llm/call`.
4. **Cost is aggregate-only.** `CliAgentRunResult.CostUsd` is the harness's self-reported total (`result.cost_usd` for claude-code; the session total for opencode). There is **no input/output token split**, so **`IProviderPricingService.Compute(...)` is NOT used on this path** — metering differs from the API path. `InputTokens`/`OutputTokens` on the converted `AgentRunResult` are `0` (or `null` where the type allows) and a flag/marker (`CostBasis = "harness-aggregate"`) records that the cost is the harness aggregate, not a price-book computation. Downstream (32-9 / 34-5 / 36) MUST treat a harness run as `CredentialSource = "local-harness"` with no platform markup (markup applies only to platform-key API runs — rule 7).
5. **Single-user-only enforcement (defence in depth).** The adapter is registered/usable **only when `ITammaModeProvider` reports single-user mode.** In SaaS the adapter is never wired into the resolver, AND independently the 32-4 `ISaaSProviderGate` already denies any `AuthModel = "cli-token"` provider (`SAAS_PROVIDER_NOT_ALLOWED`). A test asserts: (a) in single-user mode a `claude-code` agent resolves to a harness executor; (b) in SaaS mode the same resolution is refused (gate-denied / never-wired) — the harness executor is structurally unreachable.
6. **Single-user resolver integration without `/llm/call`.** The single-user agent resolution path (32-2 / `IManagedAgentResolver`) returns a harness-backed `IManagedAgent` (or an `IManagedAgent` adapter wrapping `ICliAgentExecutor`) when the resolved provider's `AuthModel == "cli-token"`. `IManagedAgent.RunAsync(...)` for a harness-backed agent delegates to the `ICliAgentExecutor` and maps `CliAgentRunResult → AgentRunResult` (Success, ResponseText, CostUsd, DurationMs, CredentialSource=`local-harness`, FailureCode/Reason), so workflow callers and the 32-6 action trail / 32-8 outcome capture get the **same** `AgentRunResult` record regardless of backend — exactly as 32-5's "callers never branch on backend" contract states, but with the LLM call replaced by a local process spawn.
7. **`HttpProviderClient` rejection is no longer the single answer for single-user.** The `NonHttpProviders` reject path (`ProviderNotSupportedException`) remains the correct behaviour for the **HTTP** dispatch layer and for **SaaS**, and is **unchanged**; harness providers are simply routed to the new executor *before* reaching `HttpProviderClient` on the single-user managed path. A test asserts SaaS still throws/denies, while single-user routes to the executor.
8. **DCB events** mirror 32-5's run lifecycle so the action trail is uniform: `AGENT.RUN.STARTED`, and exactly one terminal `AGENT.RUN.SUCCESS` / `AGENT.RUN.FAILED`, emitted via the (single-user) `IEventRepository`, tagged `{ agentId, version, provider, model, role, correlationId, credentialSource:"local-harness", backend:"cli-harness" }`; `AGENT.RUN.FAILED` adds `failureCode`. No new event types are introduced — harness runs reuse the 32-5 lifecycle.
9. **Failures never lose the run record** (same posture as 32-5 AC7). A spawn failure (binary missing / non-zero exit), a local-SDK connection failure, a malformed stream-json line, a budget-exceeded (`maxBudgetUsd`), or a cancellation produces a typed `CliAgentRunResult { Success=false, FailureCode, FailureReason }` (with whatever aggregate cost the harness reported before failure) — never an unhandled exception that drops the run. The only allowable throw is a contract violation (e.g. null request).
10. **Security / sandboxing.** The spawned process is constrained to the tenant's (sole user's) checkout / working directory; the prompt and any agent stdout that re-enters Tamma context pass through the existing `IContentSanitizer` / secret-redaction path before being persisted or logged. Process args, env, and the rendered prompt are **never** logged verbatim if they could contain secrets; only the safe summary (provider, model, sessionId, exit code, cost, duration) is logged.
11. **Unit tests** cover: claude-code happy path (stream-json parsed → cost captured), opencode happy path (session create + prompt), spawn-failure, non-zero exit, malformed stream-json line, budget-exceeded, cancellation, single-user-allowed vs SaaS-denied (AC5), no-`/llm/call`-traversal + no-credential-injection (AC3), and `CliAgentRunResult → AgentRunResult` mapping (AC6) including `CostBasis = "harness-aggregate"` + `CredentialSource = "local-harness"`.

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/
  ICliAgentExecutor.cs              # NEW — the harness execution contract (the local sibling of the inline tool loop)
  CliAgentRunResult.cs             # NEW — harness outcome record (aggregate cost; convertible to AgentRunResult)
  CliAgentRunRequest.cs            # NEW — input (prompt, cwd, model, allowedTools, permissionMode, maxBudgetUsd, sessionId, correlationId)
  ClaudeCodeAgentExecutor.cs       # NEW — port of claude-agent-provider.ts (spawn + stream-json)
  OpenCodeAgentExecutor.cs         # NEW — port of opencode-provider.ts (local SDK session)
  ZenMcpAgentExecutor.cs           # NEW — MCP-transport harness (stub-acceptable behind contract for v1)
  StreamJsonMessageParser.cs       # NEW — parses `claude -p --output-format stream-json` line protocol
  HarnessAgentBackend.cs           # NEW — IManagedAgent adapter wrapping ICliAgentExecutor (single-user resolver target)
  CliAgentExecutorRegistry.cs      # NEW — provider-key → ICliAgentExecutor, single-user-gated registration

apps/tamma-elsa/src/Tamma.Api/Extensions/
  HarnessAgentServiceCollectionExtensions.cs   # NEW — DI wiring, registered ONLY in single-user mode
```

> **No new control-plane / public-schema table is created by this story.** The harness path persists only through the existing tenant `IEventRepository` (`domain_events`) and the 32-6 action trail. There is therefore **nothing to append to the `Program.cs` startup-reset DROP list** ("Wiping Tamma-managed public-schema tables"). If a later revision adds a CP table (e.g. a harness session registry), it MUST be appended to that wipe list and to the `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities` strict `BeEquivalentTo` list — but v1 adds neither.

### `ICliAgentExecutor` contract (C#)

```csharp
namespace Tamma.Api.Services.Agents.Cli;

/// <summary>
/// Single-user LOCAL harness/CLI agent execution. The local counterpart to the
/// API-provider path in <c>IManagedAgent</c> (32-5): the agent owns its OWN loop,
/// auth, streaming, and retries by spawning a local process / driving a local SDK
/// session. This path NEVER calls POST /api/v1/llm/call and NEVER resolves a remote
/// provider credential — there is no remote credential to centralize (design §5.3).
///
/// In SaaS this is structurally unreachable: the 32-4 gate denies AuthModel="cli-token"
/// (SAAS_PROVIDER_NOT_ALLOWED) and the executor is never wired into the SaaS resolver.
/// </summary>
public interface ICliAgentExecutor
{
    /// <summary>Canonical provider key this executor handles ("claude-code" | "opencode" | "zen-mcp").</summary>
    string ProviderKey { get; }

    /// <summary>True if the local harness binary / server is present and usable (ports isAvailable()).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    /// <summary>
    /// Run the local harness end-to-end. NEVER throws on an expected failure
    /// (spawn failure, non-zero exit, SDK connect failure, malformed output,
    /// budget exceeded, cancellation): returns a typed CliAgentRunResult with
    /// Success=false + FailureCode/Reason so the run record is always captured.
    /// Cost is the harness AGGREGATE only — no per-token split.
    /// </summary>
    Task<CliAgentRunResult> RunAsync(CliAgentRunRequest request, CancellationToken ct);
}
```

### `CliAgentRunRequest` (input — mirrors TS `AgentTaskConfig`)

```csharp
public sealed record CliAgentRunRequest
{
    public required string Provider { get; init; }       // "claude-code" | "opencode" | "zen-mcp"
    public string? Model { get; init; }
    public required string Prompt { get; init; }         // already rendered (Epic 27) + sanitized
    public required string WorkingDirectory { get; init; } // the tenant/sole-user checkout (sandbox)
    public IReadOnlyList<string>? AllowedTools { get; init; }
    public string? PermissionMode { get; init; }         // "default" | "bypassPermissions"
    public decimal? MaxBudgetUsd { get; init; }          // harness-enforced ceiling
    public string? SessionId { get; init; }              // resume (--resume / session.id)
    public required string CorrelationId { get; init; }  // workflow instance id (event/audit tag)
}
```

### `CliAgentRunResult` (output — aggregate cost)

```csharp
public sealed record CliAgentRunResult
{
    public required string Provider { get; init; }
    public string? Model { get; init; }
    public required bool Success { get; init; }
    public string? ResponseText { get; init; }           // the harness "output"

    public decimal CostUsd { get; init; }                // AGGREGATE ONLY (result.cost_usd / session total)
    public long DurationMs { get; init; }
    public string? SessionId { get; init; }              // for resume
    public int? ExitCode { get; init; }

    public required string CorrelationId { get; init; }

    // Populated only when Success == false:
    public string? FailureCode { get; init; }   // SPAWN_FAILED | NON_ZERO_EXIT | SDK_CONNECT_FAILED
                                                 // | MALFORMED_OUTPUT | BUDGET_EXCEEDED | CANCELLED | NOT_AVAILABLE
    public string? FailureReason { get; init; } // key-free, secret-free message
}
```

### Conversion to the shared `AgentRunResult` (32-5) — uniform consumer surface

`HarnessAgentBackend` adapts an `ICliAgentExecutor` into an `IManagedAgent` so the **single-user** resolver returns one type. The mapping makes the aggregate-vs-per-token difference explicit:

```csharp
// inside HarnessAgentBackend.RunAsync(ManagedAgentRequest req, CancellationToken ct):
//   1. emit AGENT.RUN.STARTED { ..., backend="cli-harness", credentialSource="local-harness" }
//   2. var cli = await _executor.RunAsync(MapRequest(req), ct);     // local spawn / SDK — NO /llm/call
//   3. var result = new AgentRunResult {
//          AgentId, Version, Provider = cli.Provider, Model = cli.Model ?? "",
//          Role = req.Role,
//          InputTokens = 0, OutputTokens = 0,            // harness gives no split
//          CostUsd = cli.CostUsd,                        // AGGREGATE — NOT IProviderPricingService.Compute(...)
//          DurationMs = cli.DurationMs,
//          Success = cli.Success, ResponseText = cli.ResponseText,
//          ToolCalls = Array.Empty<ToolCallSummary>(),   // harness owns its own loop; tool calls not surfaced per-call
//          CorrelationId = req.CorrelationId,
//          CredentialSource = "local-harness",           // distinct from "byok" | "platform" → no markup (rule 7)
//          FailureCode = cli.FailureCode, FailureReason = cli.FailureReason,
//      };
//   4. emit AGENT.RUN.SUCCESS / FAILED (exactly one terminal event)
//   5. return result;
```

> **Cost metering divergence (AC4) — load-bearing.** The API path computes cost from a per-token price book (`IProviderPricingService.Compute(provider, model, in, out)`). The harness path has **only an aggregate** `costUsd` from the agent itself. So this path **skips the price book entirely**, sets `InputTokens/OutputTokens = 0`, tags `CredentialSource = "local-harness"`, and records `CostBasis = "harness-aggregate"`. Markup (34-5) applies only to `platform` API runs; BYOK API runs zero the token sell price but still compute cost; **harness runs are neither** — they are local, user-paid, and reported at face value. 32-9 / 36 must branch on `CredentialSource`/`CostBasis` so a harness run is not double-priced or markup-applied.

### `ClaudeCodeAgentExecutor` — porting `claude-agent-provider.ts`

```csharp
// args mirror claude-agent-provider.ts buildArgs():
//   claude -p --output-format stream-json
//          [ --model <model> ]
//          [ --allowedTools "<csv>" ]
//          [ --dangerously-skip-permissions ]   // only when PermissionMode == bypassPermissions
//          [ --resume <sessionId> ]
// spawn via System.Diagnostics.Process (RedirectStandardOutput/Error, WorkingDirectory = req.WorkingDirectory).
// Read stdout line-by-line → StreamJsonMessageParser:
//   - "assistant"/"text" frames → progress (sanitized before any persist/log)
//   - terminal "result" frame → capture result.cost_usd (setCost) + session_id
// On exit: Success = (exitCode == 0 && result frame seen); CostUsd = totalCost; SessionId captured.
// Failure mapping: process start throws → SPAWN_FAILED; exit != 0 → NON_ZERO_EXIT;
//   no/garbled result frame → MALFORMED_OUTPUT; budget breach → BUDGET_EXCEEDED; ct → CANCELLED.
```

### `OpenCodeAgentExecutor` — porting `opencode-provider.ts`

```csharp
// Connects to the LOCAL OpenCode server (the SDK connects to a local process — opencode-provider.ts:66).
//   session = SessionId is null ? create() : resume(SessionId);
//   response = await session.prompt({ prompt, model?, allowedTools? });
//   CostUsd = response session total (aggregate); SessionId = session.id (sessionResume: true).
// Connection failure → SDK_CONNECT_FAILED; not running / unavailable → NOT_AVAILABLE.
// NOTE: the local OpenCode server's own auth is the user's — Tamma holds no key here.
```

### Single-user-only DI wiring

```csharp
// HarnessAgentServiceCollectionExtensions.AddHarnessAgentExecution(services, mode):
//   if (mode.IsSingleUser) {           // ITammaModeProvider — process-stable (TammaMode.cs)
//       services.AddSingleton<ICliAgentExecutor, ClaudeCodeAgentExecutor>();
//       services.AddSingleton<ICliAgentExecutor, OpenCodeAgentExecutor>();
//       services.AddSingleton<ICliAgentExecutor, ZenMcpAgentExecutor>();
//       services.AddSingleton<CliAgentExecutorRegistry>();
//       services.AddSingleton<HarnessAgentBackend>();   // IManagedAgent adapter the single-user resolver may return
//   }
//   // In SaaS: NOTHING is registered. The 32-4 gate is the independent backstop.
```

The single-user resolver (32-2) selects the harness backend when the resolved provider's `AuthModel == "cli-token"`; in SaaS that branch is never taken because such providers are gate-denied at selection.

## Dependencies

**Internal:**

- **Story 32-5** (Managed agent execution layer) — defines `AgentRunResult`, `IManagedAgent`, and the `AGENT.RUN.*` lifecycle this story maps onto. This story is the **local harness counterpart** to 32-5's API path; same result type, different backend. Hard prerequisite (for the shared `AgentRunResult`/event shape).
- **Story 32-4** (SaaS provider auth gating — API-key only) — provides `ISaaSProviderGate` that denies `AuthModel="cli-token"` providers (`SAAS_PROVIDER_NOT_ALLOWED`); the independent backstop that keeps this adapter unreachable in SaaS. Hard prerequisite.
- **Story 32-2** (Agent registry, resolution & RBAC API) — the single-user resolver returns a harness-backed `IManagedAgent` for `cli-token` providers. Prerequisite for the resolver integration (AC6).
- **Story 34-11** (Provider Cost Price-Book) — defines the `Provider` entity carrying `AuthModel` (`api-key` | `cli-token`), the field this path keys on. Note: this path deliberately does **not** use `ProviderModelPrice`/`IProviderPricingService.Compute` (aggregate cost only — AC4).
- **Epic 27** (prompt/convention render) — the prompt fed to the harness is still rendered tenant→system→error (never empty/plain); harness execution does not bypass prompt resolution.
- **`IContentSanitizer` / secret redaction** (existing) — prompt in + agent stdout out pass through sanitization (AC10).
- **`ITammaModeProvider`** (`TammaMode.cs`) — single-user vs SaaS gate for registration (AC5).

**Consumers (downstream, not blockers):**

- **Story 32-6** (action trail) — consumes the shared `AGENT.RUN.*` events; harness runs are indistinguishable in shape (`backend="cli-harness"` tag aside).
- **Story 32-8** (outcome capture) — consumes the `AgentRunResult` from a harness run.
- **Story 32-9** (usage & cost emission) — must branch on `CredentialSource="local-harness"` / `CostBasis="harness-aggregate"` so harness cost is reported at face value (no price-book recompute, no markup).

**External:**

- Local `claude` CLI (claude-code), a local OpenCode server + `@opencode-ai/sdk`-equivalent surface, and (deferred) a C# MCP client for zen-mcp. All are the **user's local** tools — Tamma holds none of their credentials. Porting source: `packages/providers/src/{claude-agent-provider,opencode-provider,zen-mcp-provider}.ts` + `agent-types.ts`.

## Testing Strategy

1. **Unit — claude-code happy path:** fake/seam over `System.Diagnostics.Process`; feed a recorded stream-json transcript ending in a `result` frame with `cost_usd` + `session_id`; assert `Success=true`, `CostUsd` = the frame value, `SessionId` captured, `ExitCode=0`.
2. **Unit — opencode happy path:** fake local SDK session; `create` then `prompt`; assert aggregate `CostUsd` + `SessionId` (resume) captured.
3. **Unit — spawn failure:** process start throws → `Success=false, FailureCode=SPAWN_FAILED`; no throw.
4. **Unit — non-zero exit:** exit code 1 → `FailureCode=NON_ZERO_EXIT`, any accrued cost preserved.
5. **Unit — malformed stream-json:** garbled / missing `result` frame → `FailureCode=MALFORMED_OUTPUT`.
6. **Unit — budget exceeded:** `MaxBudgetUsd` breached mid-run → `FailureCode=BUDGET_EXCEEDED`, accrued cost preserved.
7. **Unit — cancellation:** `ct` cancelled → `FailureCode=CANCELLED`; process killed; no orphan.
8. **Unit — `NOT_AVAILABLE`:** `IsAvailableAsync` false (binary/server absent) → typed failure, no throw.
9. **Mode gate (AC5):** single-user → `claude-code` resolves to `ClaudeCodeAgentExecutor`; SaaS → resolution refused (gate-denied) / executor never registered. Both asserted.
10. **No-`/llm/call` + no-credential (AC3):** architecture/DI test asserts the executor type injects **no** `TammaApiClient`, `IProviderCredentialResolver`, or `/llm/call` `HttpClient`. A behavioural test asserts a harness run makes zero calls to the call-LLM endpoint.
11. **`HttpProviderClient` unchanged (AC7):** SaaS still throws `ProviderNotSupportedException` for `claude-code`/`opencode`/`zen-mcp`; single-user routes to the executor *before* reaching `HttpProviderClient`.
12. **Mapping (AC6):** `CliAgentRunResult → AgentRunResult` sets `InputTokens=OutputTokens=0`, `CostUsd` = aggregate, `CredentialSource="local-harness"`, `CostBasis="harness-aggregate"`, and `IProviderPricingService.Compute` is **never** invoked (verified via a strict mock).
13. **Event lifecycle (AC8):** exactly one `AGENT.RUN.STARTED` + one terminal `AGENT.RUN.SUCCESS`/`FAILED` per run via a fake single-user `IEventRepository`, tagged `backend="cli-harness"`, `credentialSource="local-harness"`; FAILED adds `failureCode`.
14. **Sanitization (AC10):** an agent stdout line containing a secret-shaped token is redacted before persist/log; process args/prompt are not logged verbatim.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale).

## Estimated Effort

5-6 days (claude-code spawn + stream-json parser is the bulk; opencode SDK seam is moderate; zen-mcp stub-acceptable for v1). **Deferred — schedule only when single-user harness parity is wanted; not on the Wave-F critical path.**

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/ICliAgentExecutor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/CliAgentRunRequest.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/CliAgentRunResult.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/ClaudeCodeAgentExecutor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/OpenCodeAgentExecutor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/ZenMcpAgentExecutor.cs` | Create (stub-acceptable v1) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/StreamJsonMessageParser.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/HarnessAgentBackend.cs` | Create (IManagedAgent adapter) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/CliAgentExecutorRegistry.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/HarnessAgentServiceCollectionExtensions.cs` | Create (single-user-only DI) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (call `AddHarnessAgentExecution(mode)` — registers nothing in SaaS) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/Cli/ClaudeCodeAgentExecutorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/Cli/OpenCodeAgentExecutorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/Cli/HarnessAgentBackendTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/Cli/HarnessModeGateTests.cs` | Create |

> No EF migration, no new control-plane entity → **no `Program.cs` startup-reset DROP-list change** and **no `ControlPlaneDbContextModelTests` change** for v1. This story does not touch the single shared EF migration snapshot.

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions (esp. any prior subprocess/spawn findings)
3. Reviewed the porting source: `packages/providers/src/claude-agent-provider.ts`, `opencode-provider.ts`, `zen-mcp-provider.ts`, `agent-types.ts`, and `AgentTaskResult` (`packages/shared/src/types/index.ts:245`)
4. Reviewed `HttpProviderClient.NonHttpProviders` (the current reject path) and confirmed it stays the correct SaaS/HTTP behaviour
5. Confirmed 32-5 (`AgentRunResult`/`IManagedAgent`/`AGENT.RUN.*`) and 32-4 (`ISaaSProviderGate`) are landed before wiring
6. Planned TDD approach (Red-Green-Refactor) — write the stream-json parser tests first

### Key Design Decisions

- **DEFERRED, single-user-only, never `/llm/call`.** This is the most important constraint: the adapter is the **local** counterpart to 32-5's API path. It spawns a process / drives a local SDK and **does not mediate through the endpoint** — by design §5.3, mediating a local process adds a hop with no security benefit. SaaS is unaffected (gate-denied + never wired).
- **Port, don't reinvent.** The TS `claude-agent-provider`/`opencode-provider` are mature (stream-json protocol, session resume, cost capture, permission flags). The C# adapter ports their semantics 1:1; the stream-json parser is the trickiest piece and is unit-tested against recorded transcripts.
- **Aggregate cost, not the price book.** Harness providers report only a total `costUsd` — there is no token split, so `IProviderPricingService.Compute` is deliberately **not** called here, and the cost is recorded as `CostBasis="harness-aggregate"` / `CredentialSource="local-harness"`. Downstream metering must branch on this so a harness run is reported at face value with no markup (rule 7) and no double-pricing.
- **Two independent SaaS backstops.** (1) The adapter is not registered in SaaS (`ITammaModeProvider`). (2) Even if it were, the 32-4 gate denies `AuthModel="cli-token"`. Defence in depth — neither alone is relied upon.
- **Uniform result type.** `HarnessAgentBackend` maps `CliAgentRunResult → AgentRunResult` so 32-6/32-8/32-9 consume harness and API runs identically (one record, `backend`/`CredentialSource` tags distinguish provenance).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Is the harness/CLI path available? | **Yes** — the legitimate use case. `ICliAgentExecutor` registered; the resolver may return a harness-backed `IManagedAgent` for `AuthModel="cli-token"` providers. | **No** — never registered; the 32-4 gate denies `cli-token` providers (`SAAS_PROVIDER_NOT_ALLOWED`). Structurally unreachable. |
| Whose credential / auth does a run use? | The **sole user's local** agent auth (their `claude` login, their local OpenCode server). Tamma holds **no** key; nothing to centralize. | N/A — path unreachable. (SaaS uses only the mediated API path: BYOK cabinet key → platform key, 32-3.) |
| How is cost recorded? | Aggregate `costUsd` from the harness; `CredentialSource="local-harness"`, `CostBasis="harness-aggregate"`, no price-book compute, no markup. | N/A. (SaaS API runs: `byok` no token markup / `platform` cost × markup — 34-5.) |
| Where do `AGENT.RUN.*` events land, and who owns the data? | The user's (sole) tenant event store via the single-user `IEventRepository`; the user owns all performance/cost data. | N/A — no harness run occurs; SaaS run data is the tenant's, tenant-scoped, never cross-tenant. |
| Process sandbox | The user's own checkout/working directory; local tools shell out to the local filesystem. | N/A — no local process spawned in SaaS. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Harness path accidentally reachable in SaaS | High | Two independent backstops (not registered in SaaS + 32-4 gate denies `cli-token`); explicit SaaS-denied test (AC5). |
| Cost double-counted or markup-applied to a harness run | High | `CredentialSource="local-harness"` + `CostBasis="harness-aggregate"`; `IProviderPricingService.Compute` never called (strict-mock test); 32-9 branches on these tags. |
| stream-json protocol drift breaks cost/session capture | Medium | Parser unit-tested against recorded `claude -p --output-format stream-json` transcripts; failure → `MALFORMED_OUTPUT` (typed, not a throw). |
| Spawned process leaks secrets to logs/persisted context | Medium | Prompt in + stdout out through `IContentSanitizer`/redaction (AC10); args/prompt never logged verbatim; only safe summary logged. |
| Orphaned child process on cancel/crash | Medium | `ct`-bound process lifetime; kill-on-cancel; `CANCELLED` typed result; test asserts no orphan. |
| Local binary/server absent | Low | `IsAvailableAsync` → `NOT_AVAILABLE` typed failure, never a throw; clear operator message. |
| Depends on 32-5/32-4/32-2 not yet landed | Low (deferred) | Code to the interfaces; this story is explicitly sequenced after them; fakes in tests until they land. |

### Success Metrics

- [ ] In single-user mode, a `claude-code` / `opencode` agent runs locally and produces an `AgentRunResult` + exactly one terminal `AGENT.RUN.*` event — with **zero** calls to `POST /api/v1/llm/call` and **zero** credential resolution.
- [ ] In SaaS mode, the harness path is unreachable (gate-denied) — proven by test; `HttpProviderClient` reject behaviour unchanged.
- [ ] Harness runs are reported at face value (`CostBasis="harness-aggregate"`, no markup) — confirmed `IProviderPricingService.Compute` is never invoked on this path.
- [ ] TS↔C# parity: the C# claude-code/opencode adapters reproduce the `packages/providers` semantics (cost capture, session resume, permission flags).

## Related

- Managed-LLM deep dive: `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§1 provider duality; §6 item 6 — DEFERRED single-user C# harness/CLI adapter)
- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§5.3 local CLI agent providers legitimately exempt; §4.2 `Provider.AuthModel`)
- Sibling stories: `docs/stories/epic-32/story-32-5/` (the API-path counterpart), `story-32-4/` (the SaaS gate), `story-32-2/` (resolver), `docs/stories/epic-34/story-34-11/` (Provider entity + `AuthModel`), `story-32-9/` (usage emission must branch on `local-harness`)
- Porting source (TS): `packages/providers/src/claude-agent-provider.ts`, `opencode-provider.ts`, `zen-mcp-provider.ts`, `agent-types.ts`; `packages/shared/src/types/index.ts:245` (`AgentTaskResult`)
- Reject path retained: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs:57-92` (`NonHttpProviders`)

## Logging Requirements

- **INFO**: harness run started (provider, model, role, correlationId, sessionId?, cwd, mode=single-user), run completed (success, durationMs, costUsd, exitCode, sessionId), availability checks.
- **DEBUG**: spawn argv **summary** (flag names only, never secret-bearing values), stream-json frame counts, session create/resume.
- **WARN**: typed failure paths (`SPAWN_FAILED`, `NON_ZERO_EXIT`, `SDK_CONNECT_FAILED`, `MALFORMED_OUTPUT`, `BUDGET_EXCEEDED`, `CANCELLED`, `NOT_AVAILABLE`) with `failureCode` + correlationId.
- **ERROR**: contract violations (null request), DCB event append failure (the run still returns its result; the append failure is logged, not swallowed silently).
- **Structured context**: include `{ provider, model, role, correlationId, sessionId, mode, credentialSource:"local-harness", backend:"cli-harness" }` where applicable.
- **Credential / process safety**: NEVER log the rendered prompt verbatim, the spawned process's environment, or any agent stdout that has not passed `IContentSanitizer`/secret redaction. The local agent's own auth/login is never seen by Tamma; there is no API key on this path to log. Log only the safe summary (provider, model, sessionId, exit code, cost, duration).

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation | Claude |
