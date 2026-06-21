# Story 32-24 — C# Harness / CLI Agent Adapter (single-user local — DEFERRED)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Goal:** Port the TypeScript harness/CLI agent providers (`claude-code`, `opencode`, `zen-mcp`) into
a **single-user / self-hosted LOCAL** C# execution path that spawns the local agent process (or drives
its local SDK session), captures the harness's self-reported **aggregate** `costUsd`, and slots into
the single-user managed-run surface **without ever calling `POST /api/v1/llm/call`** and **without
resolving any remote credential**. This restores the harness parity single-user mode has today only in
the TS path; SaaS is untouched (the 32-4 gate denies `AuthModel="cli-token"` and the adapter is never
registered there).

**Story file:** `docs/stories/epic-32/story-32-24/32-24-csharp-harness-cli-adapter.md`
**Design refs:** `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§1 provider
duality; §6 item 6 — DEFERRED single-user C# harness/CLI adapter) + `2026-06-20-epic-32-revised-agent-architecture.md`
(§5.3 local CLI agent providers legitimately exempt; §4.2 `Provider.AuthModel`).

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (central API `Tamma.Api`). Tests live in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale; plain `dotnet build` needs no
wrapper). **`packages/api` is DELETED — the TS code under `packages/providers` is the PORTING SOURCE,
not a live path.**

---

## ⚠️ DEFERRED — read before scheduling

This story is **P3 / DEFERRED**. It is **not** needed for SaaS and is **not** a blocker for the
call-LLM endpoint (32-5), the gate (32-4), billing, or analytics. Schedule it only when single-user
harness execution parity is wanted. It depends on 32-5 / 32-4 / 32-2 being landed (it reuses their
`AgentRunResult` / gate / resolver seams), so it sequences **after** them — never before.

---

## Non-goals (YAGNI guard)

- **NO `/llm/call` traversal.** The whole point (design §5.3) is that a local harness process is
  exempt from endpoint mediation — routing it through `/llm/call` adds a hop with no security benefit.
  The adapter spawns a local process / drives a local SDK; it never builds an `LlmCallRequest` and
  never calls `TammaApiClient.CallLlmAsync`.
- **NO remote credential resolution.** The local agent owns its own auth (the user's `claude` login,
  the local OpenCode server). The adapter injects **no** `IProviderCredentialResolver`, **no** API key,
  **no** cabinet reader. There is nothing to centralize.
- **NO price-book cost.** Harness providers report only an aggregate `costUsd` (no token split), so
  `IProviderPricingService.Compute(...)` is **never** called on this path. Cost is recorded as
  `CostBasis="harness-aggregate"`, `CredentialSource="local-harness"` — no markup (rule 7), no
  double-pricing.
- **NO SaaS reachability.** The adapter is registered ONLY in single-user mode (`ITammaModeProvider`),
  and the 32-4 gate independently denies `cli-token` providers in SaaS. Two backstops; never one.
- **NO new tool loop / sanitizer.** The harness owns its OWN loop. Reuse the existing
  `IContentSanitizer`/secret redaction only for prompt-in / stdout-out boundary safety.
- **NO new control-plane / public-schema table, NO EF migration.** v1 persists only through the
  existing tenant `IEventRepository` (`domain_events`) + the 32-6 action trail. → **No `Program.cs`
  startup-reset DROP-list change; no `ControlPlaneDbContextModelTests` change; no touch to the shared
  EF migration snapshot.** If a later revision adds a CP table, append it to BOTH the
  "Wiping Tamma-managed public-schema tables" wipe list AND the strict `BeEquivalentTo` model test.
- **NO change to `HttpProviderClient.NonHttpProviders`.** Its `ProviderNotSupportedException` reject
  stays correct for the HTTP dispatch layer and for SaaS. Single-user routes to the new executor
  *before* reaching `HttpProviderClient`.
- **NO implementation of 32-5/32-4/32-2 here.** Code to their interfaces; use fakes in tests.

---

## Current-state findings (verified 2026-06-21, repo @ main)

| Seam | Where it is today | How 32-24 uses it |
|---|---|---|
| **Harness reject path** | `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs:57-92` — `NonHttpProviders = { claude-code, claude-code-cli, opencode, opencode-cli, zen-mcp, zen }` → `ProviderNotSupportedException` ("not yet ported to C#"). | Unchanged. Single-user managed path routes harness providers to the new executor BEFORE this; SaaS still hits this reject. |
| **TS porting source — claude-code** | `packages/providers/src/claude-agent-provider.ts` — `ClaudeAgentProvider implements IAgentProvider, ICLIAgentProvider` (`name='claude-code'`). `spawn('claude', ['-p','--output-format','stream-json', …])`; parses stream-json frames; captures `result.cost_usd` + `session_id`; flags `--model`, `--allowedTools`, `--dangerously-skip-permissions`, `--resume`. Returns `AgentTaskResult`. | Port semantics 1:1 → `ClaudeCodeAgentExecutor` + `StreamJsonMessageParser`. |
| **TS porting source — opencode** | `packages/providers/src/opencode-provider.ts` — `OpenCodeProvider implements IAgentProvider, ICLIAgentProvider` (`name='opencode'`, `sessionResume:true`). Lazy `@opencode-ai/sdk`; connects to LOCAL server; `session.create()`/resume → `session.prompt(...)`. | Port → `OpenCodeAgentExecutor` (local SDK/HTTP seam). |
| **TS porting source — zen-mcp** | `packages/providers/src/zen-mcp-provider.ts` — MCP-transport harness. C# MCP client not yet ported (`ProviderSession.cs:87` "MCP transport not yet ported"). | `ZenMcpAgentExecutor` stub-acceptable for v1 (typed `NOT_AVAILABLE`/`NotImplemented` behind the contract; still registers/resolves). |
| **Result contracts (TS)** | `packages/providers/src/agent-types.ts` (`IAgentProvider.executeTask(config, onProgress)`, `AgentTaskConfig`, `AgentProgressEvent`); `AgentTaskResult` in `packages/shared/src/types/index.ts:245` (`{ success, output, costUsd, durationMs, error? }`). | Shapes `CliAgentRunRequest` / `CliAgentRunResult`. |
| **Shared run record (C#)** | 32-5 `AgentRunResult` + `IManagedAgent` + `AGENT.RUN.STARTED/SUCCESS/FAILED` (`Tamma.Api/Services/Agents/`). | `HarnessAgentBackend` maps `CliAgentRunResult → AgentRunResult`; reuses the same event lifecycle. |
| **SaaS gate** | 32-4 `ISaaSProviderGate` (`Tamma.Api/Services/Security/`) — denies `AuthModel="cli-token"` (`SAAS_PROVIDER_NOT_ALLOWED`). | The independent SaaS backstop (AC5/AC7). |
| **Provider entity / AuthModel** | 34-11 `Provider.AuthModel` (`api-key` | `cli-token`). | The resolver keys on `cli-token` to select the harness backend. |
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (SingleUser | SaaS), process-stable. | Gate for DI registration (single-user only). |
| **Sanitizer** | `Tamma.Api/Services/Sanitization/ContentSanitizer.cs` + `IContentSanitizer`; `ToolOutputHelper.RedactSecrets`. | Prompt-in / stdout-out boundary safety (AC10). |
| **Resolver** | 32-2 `IAgentResolverService`/`IManagedAgentResolver` (`Tamma.Api/Services/Agents/`). | Single-user resolution returns the harness-backed `IManagedAgent` for `cli-token`. |

---

## Phase 0 — Prep & guardrails (no code)

- [ ] Read `BEFORE_YOU_CODE.md`, search `.dev/{spikes,bugs,findings,decisions}` for prior subprocess /
      `Process` / stream-json / harness findings.
- [ ] Read the three TS porting sources end-to-end; record a recorded `claude -p --output-format
      stream-json` transcript (happy path + a non-zero-exit + a malformed line) as test fixtures.
- [ ] Confirm 32-5 (`AgentRunResult`, `IManagedAgent`, `AGENT.RUN.*`), 32-4 (`ISaaSProviderGate`),
      32-2 (resolver), 34-11 (`Provider.AuthModel`) seams are landed; if not, code to interfaces + fake.
- [ ] Confirm **no EF migration / no CP entity** is needed (it is not, for v1) — so the snapshot,
      DROP-list, and model test are untouched.

## Phase 1 — Contracts & records (TDD: shapes first)

- [ ] **Test:** `CliAgentRunResultTests` — `Success=false` requires `FailureCode`; aggregate `CostUsd`
      preserved on failure; round-trips through `JsonSerializer`.
- [ ] Implement `ICliAgentExecutor.cs`, `CliAgentRunRequest.cs`, `CliAgentRunResult.cs` (records per the
      story Technical Design). Add `FailureCode` constants (`SPAWN_FAILED`, `NON_ZERO_EXIT`,
      `SDK_CONNECT_FAILED`, `MALFORMED_OUTPUT`, `BUDGET_EXCEEDED`, `CANCELLED`, `NOT_AVAILABLE`).

## Phase 2 — Stream-json parser (the trickiest piece, TDD)

- [ ] **Test:** `StreamJsonMessageParserTests` against the recorded fixtures —
      (a) happy: progress/text frames + terminal `result` frame → `cost_usd` + `session_id` captured;
      (b) malformed line → typed parse error (mapped to `MALFORMED_OUTPUT`, not a throw);
      (c) missing `result` frame → `MALFORMED_OUTPUT`.
- [ ] Implement `StreamJsonMessageParser.cs` — line-by-line JSON frame parse; expose `OnProgress`,
      `Cost`, `SessionId`, `ResultSeen`. Ports the `processStreamMessage` logic from
      `claude-agent-provider.ts`.

## Phase 3 — `ClaudeCodeAgentExecutor` (TDD, behind a process seam)

- [ ] Introduce a thin `IProcessRunner` seam (wrap `System.Diagnostics.Process`) so spawning is fakeable.
- [ ] **Tests:** `ClaudeCodeAgentExecutorTests` — happy path (fixture transcript → `Success=true`,
      `CostUsd`, `SessionId`, `ExitCode=0`); spawn-throws → `SPAWN_FAILED`; exit=1 → `NON_ZERO_EXIT`
      (accrued cost preserved); malformed → `MALFORMED_OUTPUT`; `MaxBudgetUsd` breach → `BUDGET_EXCEEDED`;
      `ct` cancel → `CANCELLED` + process killed (no orphan); `IsAvailableAsync` false → `NOT_AVAILABLE`.
- [ ] Implement `ClaudeCodeAgentExecutor.cs` — build argv (`-p --output-format stream-json [--model]
      [--allowedTools] [--dangerously-skip-permissions] [--resume]`), `WorkingDirectory =
      req.WorkingDirectory` (sandbox), stream stdout → parser, map exit/parse to `CliAgentRunResult`.
      **No `TammaApiClient`, no `IProviderCredentialResolver`, no `/llm/call` `HttpClient` injected.**

## Phase 4 — `OpenCodeAgentExecutor` + `ZenMcpAgentExecutor`

- [ ] Introduce an `IOpenCodeSessionClient` seam over the local OpenCode server (create/resume/prompt).
- [ ] **Tests:** `OpenCodeAgentExecutorTests` — happy (create→prompt→aggregate cost + sessionId);
      resume path; connect-fail → `SDK_CONNECT_FAILED`; not-running → `NOT_AVAILABLE`.
- [ ] Implement `OpenCodeAgentExecutor.cs` (local session client).
- [ ] Implement `ZenMcpAgentExecutor.cs` — v1 stub: registers, `IsAvailableAsync→false` /
      `RunAsync→NOT_AVAILABLE`, documented TODO referencing the C# MCP-client gap (`ProviderSession.cs:87`).

## Phase 5 — `HarnessAgentBackend` (IManagedAgent adapter) + mapping

- [ ] **Tests:** `HarnessAgentBackendTests` — `CliAgentRunResult → AgentRunResult` sets
      `InputTokens=OutputTokens=0`, `CostUsd` = aggregate, `CredentialSource="local-harness"`,
      `CostBasis="harness-aggregate"`; `IProviderPricingService.Compute` **never** invoked (strict mock);
      exactly one `AGENT.RUN.STARTED` + one terminal `SUCCESS`/`FAILED` via a fake single-user
      `IEventRepository`, tagged `backend="cli-harness"`; FAILED adds `failureCode`; prompt-in /
      stdout-out pass through `IContentSanitizer` (secret redacted before persist/log).
- [ ] Implement `HarnessAgentBackend.cs` (`IManagedAgent`): emit STARTED → `_executor.RunAsync` →
      map → emit terminal → return. Implement `CliAgentExecutorRegistry.cs` (provider-key → executor).

## Phase 6 — DI wiring (single-user-only) + resolver integration

- [ ] **Tests:** `HarnessModeGateTests` — single-user: `claude-code` resolves to the harness executor;
      SaaS: resolution refused (gate-denied) / executor never registered (AC5). Architecture test:
      executor types inject no `TammaApiClient` / `IProviderCredentialResolver` / `/llm/call`
      `HttpClient` (AC3). `HttpProviderClient` SaaS reject unchanged (AC7).
- [ ] Implement `HarnessAgentServiceCollectionExtensions.AddHarnessAgentExecution(services, mode)` —
      register the executors + registry + backend **only when `mode.IsSingleUser`**; SaaS registers nothing.
- [ ] Wire 32-2 single-user resolver to return `HarnessAgentBackend` when resolved
      `Provider.AuthModel == "cli-token"`. In SaaS that branch is unreachable (gate-denied at selection).
- [ ] Modify `Program.cs` to call `AddHarnessAgentExecution(mode)` (registers nothing in SaaS). **No
      DROP-list / model-test change** (no CP table).

## Phase 7 — Verify & document

- [ ] `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Cli"`
      green; full `Tamma.Api.Tests` green; `dotnet build` clean (no wrapper).
- [ ] Confirm via grep: zero `/llm/call` / `CallLlmAsync` / credential-resolver references under
      `Services/Agents/Cli/`; `IProviderPricingService.Compute` not referenced on the harness path.
- [ ] Update the story Change Log / mark ACs satisfied (controller updates `sprint-status.yaml`).

---

## Test list (consolidated)

| # | Test | Asserts AC |
|---|---|---|
| 1 | `CliAgentRunResultTests` | record/JSON shape; failure preserves cost | 2, 9 |
| 2 | `StreamJsonMessageParserTests` (happy/malformed/missing-result) | cost + session capture; typed parse failure | 1, 9 |
| 3 | `ClaudeCodeAgentExecutorTests.HappyPath` | stream-json → cost/session/exit | 1, 2 |
| 4 | `ClaudeCodeAgentExecutorTests.SpawnFailed/NonZeroExit/Malformed/BudgetExceeded/Cancelled/NotAvailable` | typed failures, no throw, no orphan | 9 |
| 5 | `OpenCodeAgentExecutorTests` (happy/resume/connect-fail/not-running) | local SDK session; aggregate cost | 1 |
| 6 | `HarnessAgentBackendTests.Mapping` | `local-harness`/`harness-aggregate`; `Compute` never called | 4, 6 |
| 7 | `HarnessAgentBackendTests.Events` | one STARTED + one terminal; `backend`/`credentialSource` tags | 8 |
| 8 | `HarnessAgentBackendTests.Sanitization` | prompt-in / stdout-out redacted | 10 |
| 9 | `HarnessModeGateTests.SingleUserAllowed` | resolves to harness executor | 5, 6 |
| 10 | `HarnessModeGateTests.SaaSDenied` | refused / never wired | 5, 7 |
| 11 | `HarnessModeGateTests.NoLlmCallNoCredential` (architecture + behavioural) | no `TammaApiClient`/credential/`/llm/call` | 3 |
| 12 | `HttpProviderClientSaaSRejectUnchangedTests` | SaaS still throws `ProviderNotSupportedException` | 7 |

---

## Files created / modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/ICliAgentExecutor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/CliAgentRunRequest.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/CliAgentRunResult.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/StreamJsonMessageParser.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/ClaudeCodeAgentExecutor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/OpenCodeAgentExecutor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/ZenMcpAgentExecutor.cs` | Create (stub-acceptable v1) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/HarnessAgentBackend.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/CliAgentExecutorRegistry.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/Cli/IProcessRunner.cs` (+ default impl) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/HarnessAgentServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (call `AddHarnessAgentExecution(mode)`) |
| 32-2 single-user resolver (`Tamma.Api/Services/Agents/…Resolver…`) | Modify (return harness backend for `cli-token`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/Cli/*` (per test list) | Create |

> **No `Program.cs` startup-reset DROP-list change, no `ControlPlaneDbContextModelTests` change, no EF
> migration** — v1 adds no control-plane / public-schema table and does not touch the shared snapshot.

---

## Risks & mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Harness path reachable in SaaS | High | Two independent backstops (not registered in SaaS + 32-4 gate denies `cli-token`); explicit SaaS-denied + no-wire test. |
| Cost double-counted / markup applied | High | `CredentialSource="local-harness"` + `CostBasis="harness-aggregate"`; `Compute` never called (strict-mock test); 32-9 must branch on these tags. |
| stream-json drift breaks cost/session capture | Medium | Parser unit-tested against recorded transcripts; failure → `MALFORMED_OUTPUT` (typed, not a throw). |
| Secret leak via spawned-process stdout/args | Medium | `IContentSanitizer`/redaction on prompt-in + stdout-out; never log args/prompt/env verbatim; only safe summary. |
| Orphan child process on cancel/crash | Medium | `ct`-bound process lifetime; kill-on-cancel; `CANCELLED` typed result; orphan-free test. |
| Accidental `/llm/call` / credential coupling creeps in | Medium | Architecture test forbids `TammaApiClient`/`IProviderCredentialResolver`/`/llm/call` `HttpClient` injection on the harness path. |
| zen-mcp blocked on un-ported C# MCP client | Low | v1 stub behind the contract (typed `NOT_AVAILABLE`); full impl tracked with the MCP-porting story (deep dive §6 item 2). |
| Implemented before 32-5/32-4/32-2 land | Low (deferred) | Code to interfaces; this story is explicitly sequenced after them; fakes until they land. |

---

## Definition of done

- [ ] All Phase 1–6 tests green via `sg docker -c "dotnet test ..."`; `dotnet build` clean.
- [ ] Single-user: `claude-code`/`opencode` run locally → `AgentRunResult` + one terminal `AGENT.RUN.*`,
      **zero** `/llm/call` calls, **zero** credential resolution (grep-verified).
- [ ] SaaS: harness path unreachable (gate-denied test) + `HttpProviderClient` reject unchanged.
- [ ] Harness runs reported at face value (`CostBasis="harness-aggregate"`, no markup); `Compute` never
      invoked on this path.
- [ ] No EF migration / CP-table / DROP-list / model-test change.
- [ ] Logging never emits the rendered prompt, process env, or un-sanitized agent stdout; credential
      safety: there is no API key on this path to log.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial plan creation  | Claude |
