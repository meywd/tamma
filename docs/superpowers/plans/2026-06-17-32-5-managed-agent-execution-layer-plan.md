# Story 32-5 — Managed Agent Execution Layer (`IManagedAgent` over `IAIProvider`)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Goal:** Introduce a single managed-LLM-agent execution abstraction (`IManagedAgent` /
`ManagedAgent`) that turns a resolved agent (32-2) + per-tenant credential (32-3) + the SaaS gate
(32-4) + RAG/context (Epic 6) + prompt render (Epic 27) + the **existing** inline LLM/tool-loop path
(`CallLlmInlineActivity`) + sanitize + instrument + outcome capture into one coherent run that
returns a structured `AgentRunResult`. This is the producer record every later Epic 32 story (action
trail 32-6, outcome capture 32-8, cost emission 32-9, benchmarking 32-10) consumes. SaaS uses ONLY
this path; CLI/token providers (`ICLIAgentProvider`) are excluded by the 32-4 gate.

**Story file:** `docs/stories/epic-32/story-32-5/32-5-managed-agent-execution-layer.md`
**Design spec:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (central API `Tamma.Api` + activities
`Tamma.Activities` + engine). Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/` and
`apps/tamma-elsa/tests/Tamma.Activities.Tests/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale; plain `dotnet build` needs no
wrapper). **`packages/api` is DELETED — there is no TypeScript execution path; all of this is C#.**

---

## Non-goals (YAGNI guard)

- **NO new tool loop, sanitizer, validator, or compactor.** AC3 is "reuse, don't fork." The agentic
  loop already exists, complete, inside `CallLlmInlineActivity.AgenticToolLoop(...)`
  (sanitize → multi-turn call → tool-call validation → sequential/parallel tool exec → tool-output
  sanitize + secret redaction → context compaction → token accounting). This plan EXTRACTS it into a
  reusable seam and calls it from two places — it does not reimplement any of it.
- **NO markup / invoicing / analytics.** `AgentRunResult.CostUsd` is the raw provider cost basis from
  `IProviderPricingService.Compute`. Markup is 34-5; invoicing 35; analytics 36. This story is a
  *producer* only.
- **NO new providers.** Reuse the existing provider stack and `IProviderPricingService`.
- **NO change to single-turn `CallLlmActivity`/`CallLlmInlineActivity` behaviour.** Non-agent inline
  calls keep working byte-for-byte; only agent-driven steps move to `RunManagedAgentActivity`. The
  existing `CallLlmInlineActivitySanitizationTests` are the regression net for the extraction.
- **NO per-user override layer / cross-tenant data.** Performance/action data is ALWAYS tenant-scoped
  (design spec). `AGENT.RUN.*` events go to the tenant `IEventRepository`.
- **NO implementation of 32-2/32-3/32-4 here.** Code to their interfaces; this story is gated behind
  them and uses fakes in tests until they land.

---

## Current-state findings (verified 2026-06-17, repo @ main)

| Seam | Where it is today | How 32-5 uses it |
|---|---|---|
| **Agentic tool loop** | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` — private `AgenticToolLoop(...)` (lines ~396–834): multi-turn Anthropic/OpenAI calls, `IToolCallValidator`, `IToolExecutorRegistry`, `ParallelToolExecutor`, `ContextCompactor`, `ToolLoopEventEmitter`, token accounting; sanitization via `IContentSanitizer` (input/output/tool-output) + `ToolOutputHelper.RedactSecrets`. | **Extract** into `IInlineToolLoopRunner`/`InlineToolLoopRunner` (verbatim move); activity delegates; `ManagedAgent` calls the same runner. |
| **Sanitizer** | `Tamma.Activities/Security/IContentSanitizer.cs` + `Tamma.Api/Services/Sanitization/ContentSanitizer.cs` | Reused inside the extracted runner — unchanged. |
| **Agent resolution** | `Tamma.Api/Services/Agents/IAgentResolverService.cs` → `ResolvedAgentConfig` (Provider, Model, Temperature, MaxTokens, TokenBudget, Tools, SystemPrompt, MaxBudgetUsd, PermissionMode, AllowedTools). `ResolveForPhaseAsync(tenantId, phase, role, TaskOverrides?)` clamps budget/tools. | 32-2 extends this to return `IManagedAgent`; 32-5 calls it at composition step 1. |
| **Cost basis** | `Tamma.Api/Services/Providers/IProviderPricingService.cs` — `decimal Compute(string provider, string? model, int inputTokens, int outputTokens)`. | `AgentRunResult.CostUsd = _pricing.Compute(...)` at step 9. |
| **DCB events** | `Tamma.Data/Repositories/IEventRepository.cs` — `Task<DomainEvent> AppendAsync(DomainEvent)`, tenant-scoped. Existing `AGENT.DISPATCH.*` family in analytics/alerts. | Emit `AGENT.RUN.STARTED/SUCCESS/FAILED` via `AppendAsync`, tenant-scoped. |
| **Budget / circuit / concurrency guards** | `Tamma.Activities/LlmCall/CheckBudgetActivity.cs`, `CheckCircuitBreakerActivity.cs`, `CheckLlmConcurrencyActivity.cs`. | `ManagedAgent` reuses the budget-check logic at step 4 (typed `BUDGET_EXCEEDED` failure). |
| **Context / RAG** | `Tamma.Activities/Context/AssembleContextActivity.cs` (`CodeActivity<AssembledContext>`) + `packages/intelligence/src/rag/rag-pipeline.ts` (TS RAG pipeline used by the engine via the central API). | Step 5 assembles context; fed to prompt render. (If the C# context assembler is sufficient, call it directly; otherwise round-trip per Epic 9.) |
| **Prompt + convention render** | Epic 27 prompt store + convention store (central API; engine round-trips). Resolution is tenant → system → error, NEVER empty/plain. | Step 6 renders system+user prompt from agent config + assembled context. |
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (SingleUser | SaaS), process-stable. | Step 3 gate consults mode; CLI-backed agent in SaaS → typed `GATE_DENIED`. |

**Key insight:** the only genuinely new code is the *orchestration shell* (`ManagedAgent`),
the *records* (`ManagedAgentRequest`, `AgentRunResult`), the *Elsa activity*
(`RunManagedAgentActivity`), and the *extraction* of the existing loop into a runner. Everything else
is wiring existing collaborators in order with typed-failure exits.

---

## Architecture

**Compose, instrument, capture — over the existing inline path:**

```
RunManagedAgentActivity (Elsa)            -- workflow call site (replaces ad-hoc role->llm dispatch)
        |
        v
IManagedAgentResolver.Resolve(tenant, role/agentId)   -- 32-2, returns IManagedAgent
        |
        v
IManagedAgent.RunAsync(ManagedAgentRequest)           -- ManagedAgent orchestrates:
  1 resolve agent config (32-2)                        \
  2 resolve credential   (32-3)  BYOK -> platform       |  typed-failure exits:
  3 SaaS gate            (32-4)  CLI in SaaS => denied   |  NO_CREDENTIAL / GATE_DENIED /
  4 budget guard                                          >  BUDGET_EXCEEDED / PROVIDER_ERROR /
  5 assemble context + RAG (Epic 6)                      |  LOOP_EXHAUSTED  -> AgentRunResult{Success=false}
  6 render prompt (Epic 27)                              |  (NEVER a bare throw)
  7 emit AGENT.RUN.STARTED                              /
  8 IInlineToolLoopRunner.RunAsync(...)  <-- REUSED inline seam (sanitize+validate+compact inside)
  9 cost = IProviderPricingService.Compute(...)
 10 map -> AgentRunResult{...}
 11 emit AGENT.RUN.SUCCESS | AGENT.RUN.FAILED  (exactly one terminal event)
 12 return AgentRunResult
```

Per-mode ownership (CLAUDE.md two-scoping-model rule): single-user = LLM-API **and** CLI backends, the
sole user's credential, events in the user's store; SaaS = LLM-API backend **only**, tenant BYOK →
platform credential, events in the tenant `t_<hex>` store, data never cross-tenant. Mode from
`ITammaModeProvider`.

---

## Task breakdown

Order: T1 (records) → T2 (extract loop) → T3 (ManagedAgent core) → T4 (failure paths + events) →
T5 (Elsa activity + wiring) → T6 (mode/RBAC + isolation tests). T1 and T2 are parallel-safe; T3
needs both.

### T1 — Records + event-type constants (`AgentRunResult`, `ManagedAgentRequest`)

**Scope:** The producer/consumer data shapes. No behaviour.

**Files (new):** `Services/Agents/ManagedAgentRequest.cs`, `Services/Agents/AgentRunResult.cs`
(+ `ToolCallSummary`), `Services/Agents/AgentRunEventTypes.cs` (`AGENT.RUN.STARTED`,
`AGENT.RUN.SUCCESS`, `AGENT.RUN.FAILED`), `Services/Agents/IManagedAgent.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentRunResultTests.cs` — record equality, required
fields enforced, `Success=false` always carries `FailureCode`+`FailureReason`, success carries none;
`CredentialSource` is one of `byok|platform`.

**Acceptance:**
- [ ] `AgentRunResult` has all AC2 fields (AgentId, Version, Provider, Model, Role, InputTokens,
      OutputTokens, CostUsd, DurationMs, Success, ResponseText, ToolCalls, CorrelationId,
      CredentialSource, FailureCode?, FailureReason?).
- [ ] Builds clean; no analyzer warnings.

### T2 — Extract the agentic tool loop into a reusable seam (pure refactor, AC3)

**Scope:** Move `CallLlmInlineActivity.AgenticToolLoop(...)` and its private helpers (multi-turn
builders, parsers, `LoadProviderConfig`, sanitize/redact calls, compaction call) verbatim into
`InlineToolLoopRunner : IInlineToolLoopRunner`. The activity becomes a thin delegate. **No logic
change.**

**Files:** new `Tamma.Activities/LlmCall/IInlineToolLoopRunner.cs`,
`Tamma.Activities/LlmCall/InlineToolLoopRunner.cs`; modify
`Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` (delegate to runner; keep single-turn path as-is);
DI registration alongside the activity.

**Seam shape (provider/model/prompt/tools/credential in; result+tokens+toolcalls out):**
```csharp
public interface IInlineToolLoopRunner
{
    Task<InlineToolLoopResult> RunAsync(
        string provider, LlmProviderConfig providerConfig, string model,
        string systemPrompt, string userPrompt, int maxTokens, double temperature,
        IReadOnlyList<ResolvedTool>? tools, ToolLoopConfig loopConfig,
        string workflowInstanceId, CancellationToken ct);
}
// InlineToolLoopResult { NormalizedLlmResponse Response, int InputTokens, int OutputTokens,
//                        int Turns, bool Exhausted, IReadOnlyList<ToolCallSummary> ToolCalls }
```

**Tests (first):** `tests/Tamma.Activities.Tests/LlmCall/InlineToolLoopRunnerTests.cs` — port/point
the existing loop assertions at the runner; **and the existing
`CallLlmInlineActivitySanitizationTests` MUST pass unchanged** (the regression net proving no drift).

**Acceptance:**
- [ ] `CallLlmInlineActivity` delegates to `InlineToolLoopRunner`; its tool-loop output variables are
      byte-for-byte identical (token totals, turns, exhausted, sanitized output).
- [ ] `grep` confirms exactly ONE copy of the loop / one sanitizer call-site cluster (no fork).
- [ ] Full activities suite green via `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/..."`.

### T3 — `ManagedAgent` core composition (happy path)

**Scope:** `ManagedAgent : IManagedAgent`. Wire collaborators in order 1→10 for the success path;
return a fully-populated `AgentRunResult`. Cost via `IProviderPricingService`.

**Files:** new `Services/Agents/ManagedAgent.cs`, `Services/Agents/IManagedAgentResolver.cs` (thin —
returns an `IManagedAgent` for `(tenantId, role/agentId)`; concrete impl may live in 32-2, this story
defines/consumes the interface).

**Collaborators (constructor-injected interfaces — fakes in tests):** `IAgentResolverService` (32-2),
`IProviderCredentialResolver` (32-3, yields `{ApiKey, BaseUrl, Source}`), `ISaasProviderGate` (32-4),
budget guard (reuse `CheckBudgetActivity` logic behind a small interface), context assembler (Epic 6),
prompt renderer (Epic 27), `IInlineToolLoopRunner` (T2), `IProviderPricingService`,
`IEventRepository`, `ITammaModeProvider`, `ILogger<ManagedAgent>`.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/ManagedAgentTests.cs` — happy path: every field
populated; collaborators each called once in order; `CostUsd == _pricing.Compute(...)`;
`CredentialSource` copied from credential resolver.

**Acceptance:**
- [ ] Happy-path `RunAsync` returns `AgentRunResult{Success=true}` with **all** fields populated.
- [ ] BYOK credential → `CredentialSource="byok"`; platform → `"platform"`.

### T4 — Typed failure paths + DCB events (AC7, AC8)

**Scope:** Each composition step gets a typed-failure exit mapping to
`AgentRunResult{Success=false, FailureCode, FailureReason}` with accrued tokens/cost. Emit exactly
one `AGENT.RUN.STARTED` (before the loop, step 7) and exactly one terminal `AGENT.RUN.SUCCESS|FAILED`.
No expected failure throws.

**Failure codes:** `NO_CREDENTIAL` (step 2), `GATE_DENIED` (step 3), `BUDGET_EXCEEDED` (step 4),
`PROVIDER_ERROR` (step 8 unsuccessful response / exception), `LOOP_EXHAUSTED` (step 8 exhausted +
no usable response). Contract violation (null request) MAY throw `ArgumentNullException`.

**Event tags:** `{ agentId, version, provider, model, role, correlationId, credentialSource,
tenantId }`; FAILED adds `failureCode`.

**Tests (first):** extend `ManagedAgentTests` — one test per failure code: tool loop NOT invoked for
pre-loop failures; accrued tokens/cost preserved for in-loop failures; `AGENT.RUN.FAILED` emitted
exactly once; never a propagated exception; "exactly one terminal event per run" invariant via a fake
`IEventRepository` that records appends.

**Acceptance:**
- [ ] All five failure codes produce typed results; none throw.
- [ ] Every `RunAsync` return is preceded by exactly one terminal DCB event (STARTED only emitted
      once the run reaches the loop; pre-loop failures emit STARTED+FAILED or FAILED-only per chosen
      contract — pin it in a test).
- [ ] Event tags match AC8.

### T5 — `RunManagedAgentActivity` + DI wiring (AC5)

**Scope:** Elsa activity exposing `IManagedAgent` to workflows; replaces the ad-hoc
`ResolveAgentConfig → ResolveLlmPrompt → ResolveTools → CheckBudget → CallLlm` chain for managed
agents. Resolves `IManagedAgent` via `IManagedAgentResolver`, runs it, serializes `AgentRunResult` to
the `AgentRunResult` workflow variable.

**Files:** new `Tamma.Activities/AgentDispatch/RunManagedAgentActivity.cs`,
`Tamma.Api/Extensions/ManagedAgentServiceCollectionExtensions.cs`; modify `Tamma.Api/Program.cs`
(register `IManagedAgent`, `IManagedAgentResolver`, `IInlineToolLoopRunner`, and the activity —
mirror existing activity registration patterns).

**Tests (first):** `tests/Tamma.Activities.Tests/AgentDispatch/RunManagedAgentActivityTests.cs` —
activity inside a minimal Elsa workflow writes a serialized `AgentRunResult`; one STARTED + one
terminal event per run via a fake `IEventRepository`; inputs (role/phase/userPrompt/issue/overrides)
flow into `ManagedAgentRequest`.

**Acceptance:**
- [ ] `RunManagedAgentActivity` produces the `AgentRunResult` variable for both success and failure.
- [ ] DI resolves the whole chain at host startup (smoke test / `WebApplicationFactory`).

### T6 — Mode separation, RBAC posture & isolation (AC4)

**Scope:** Prove SaaS uses ONLY the managed path and CLI-backed agents are gate-denied; prove
single-user allows CLI backends; prove events land in the tenant store and never cross-tenant.

**Files:** extend `ManagedAgentTests` (mode matrix) + a small isolation test asserting
`AGENT.RUN.*` append uses the tenant-scoped `IEventRepository` with `TenantId` set in SaaS.

**Tests (first):**
- SaaS + CLI-backed agent → `GATE_DENIED` typed result, loop never invoked.
- SaaS + LLM-API agent → runs normally.
- single-user + CLI-backed agent → gate passes (allowed).
- two tenants → events tagged with their own `TenantId`; no leakage (fake repo asserts per-tenant).

**Acceptance:**
- [ ] Mode matrix passes; SaaS never reaches a CLI provider.
- [ ] `AGENT.RUN.*` events carry the correct `tenantId`; cross-tenant assertion holds.

---

## Story order & dependencies

External prereqs (must land first): **32-2** (resolver/`IManagedAgent` return), **32-3**
(credential resolver + `CredentialSource`), **32-4** (SaaS gate). Code to their interfaces; use fakes
until landed. Internal: T1 ∥ T2 → T3 → T4 → T5 → T6. Downstream consumers (32-6/8/9) depend on this;
they are NOT blockers.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Agents"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/ --filter FullyQualifiedName~LlmCall|FullyQualifiedName~AgentDispatch"
# AC3 no-fork check: exactly one tool-loop implementation
grep -rn "AgenticToolLoop\|class InlineToolLoopRunner" apps/tamma-elsa/src
```

## Risks

- **Extraction drift (T2, AC3):** the loop is large and security-critical (injection sanitization at
  every boundary). Mitigation: pure move, zero logic edits, existing `CallLlmInlineActivitySanitizationTests`
  unchanged as the net. If those tests need edits, the extraction is wrong — stop and re-do as a move.
- **Lost run records (T4, AC7):** any throw on an expected failure drops the record. Mitigation: the
  "exactly one terminal event per run" invariant test + per-failure-code tests; only null-request may
  throw.
- **CLI leak into SaaS (T6, AC4):** gate must run BEFORE any provider call. Mitigation: gate at step 3;
  explicit SaaS+CLI test asserting the loop is never invoked.
- **Cost mis-attribution:** `CredentialSource` must be copied from 32-3, never re-derived. Mitigation:
  both-branch tests; keep `CostUsd` at provider basis (no markup — that's 34-5).
- **Double event emission:** the reused tool-loop seam emits its own *tool-loop streaming* events;
  `ManagedAgent` owns the *run-level* `AGENT.RUN.*` events. Keep the two event families distinct so
  there's no double counting in 32-6/32-9.
- **Dependency timing:** 32-2/3/4 may land after this is scheduled. Mitigation: interfaces + fakes;
  this story is the integrator, not the owner, of those seams.
