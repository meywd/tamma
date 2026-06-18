# Story 32-5: Managed Agent Execution Layer (IManagedAgent over IAIProvider)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform engineer building agent-driven workflows**,
I want a single managed-LLM-agent execution abstraction (`IManagedAgent`) that turns a resolved agent + the existing inline LLM/tool-loop HTTP path into one coherent, instrumented agent run,
So that every managed run carries a stable agent identity, resolves its own credential, sanitizes and instruments uniformly, and produces a structured `AgentRunResult` that the action-trail (32-6), outcome-capture (32-8) and cost-emission (32-9) stories can consume — and so that SaaS has exactly **one** execution path (the LLM-API path), with CLI/token providers excluded by the 32-4 gate.

## Priority

P0 - The managed execution seam that every later Epic 32 story (action trail, panels, outcome capture, benchmarking, learning) builds on. Without it, agent runs stay ad-hoc role→LLM-call dispatch with no run record, no uniform credential/gate path, and no clean producer for the tracking dataset.

## Context

Today, agent-driven workflow steps dispatch an LLM call through `CallLlmActivity` → the inline `CallLlmInlineActivity`, which already contains a complete agentic tool loop (sanitization → multi-turn LLM call → tool-call validation → tool execution → context compaction → output sanitization). Agent config is resolved separately via `IAgentResolverService`; prompt/convention rendering happens in separate Context/LlmCall activities; budget/circuit-breaker/concurrency are separate guard activities. **There is no single object that represents "run this agent end-to-end and give me a structured result."** Consequently there is no natural place to attach the stable `agent_id` + config version (32-1), the per-tenant credential source (32-3), the SaaS gate (32-4), or the run record that 32-6/32-8/32-9 need.

This story introduces `IManagedAgent` / `ManagedAgent` as the **orchestration seam over** the existing inline path — it **reuses, does not fork**, the tool loop, `ContentSanitizer`, `ToolExecutorRegistry`, and `ContextCompactor` already living in `CallLlmInlineActivity`. The resolver (32-2) returns an `IManagedAgent` regardless of whether the backing executor is the LLM-API path (this story) or a CLI provider, so workflows treat all agents identically; **SaaS resolves only the LLM-API-backed `IManagedAgent`** because the 32-4 gate excludes `ICLIAgentProvider` outside single-user mode.

> **Architecture note — distinct from CLI providers.** `IManagedAgent` is the customization layer *above* the LLM API (provider + model + prompt + tools + RAG + budget), and is **not** an `ICLIAgentProvider`. The two execution backends converge on the same `AgentRunResult` so callers never branch on backend. Per the Epic 32 design spec, SaaS = API-key auth only → managed path only.

## Acceptance Criteria

1. An **`IManagedAgent`** interface and a **`ManagedAgent`** implementation exist (`apps/tamma-elsa/src/Tamma.Api/Services/Agents/`). `RunAsync(ManagedAgentRequest, CancellationToken)` orchestrates, in order: **resolve agent (32-2)** → **resolve credential (32-3)** → **SaaS gate (32-4)** → **context + RAG assembly** → **prompt render (Epic 27 prompt/convention stores)** → **agentic tool loop (the existing `CallLlmInlineActivity` path)** → **sanitize** → **instrument** → **outcome capture**, and returns a structured `AgentRunResult`.
2. `AgentRunResult` is a typed record carrying at minimum: `AgentId`, `Version`, `Provider`, `Model`, `Role`, `InputTokens`, `OutputTokens`, `CostUsd`, `DurationMs`, `Success`, `ToolCalls` (count + per-call summaries), `CorrelationId`, `CredentialSource` (`byok` | `platform`), `ResponseText`, and on failure a `FailureReason` + `FailureCode`. The same record shape is returned whether the run succeeded, failed, was budget-exceeded, or gate-denied.
3. `ManagedAgent` **reuses (does NOT fork)** the existing inline tool loop, `ContentSanitizer` (input/output/tool-output sanitization), `IToolExecutorRegistry`, `IToolCallValidator`, and `ContextCompactor` in `CallLlmInlineActivity`. No second copy of the tool loop, sanitizer, or compactor is created; the loop body is factored into a reusable seam invoked by both the legacy activity and `ManagedAgent`.
4. Clear separation is enforced: **`IManagedAgent` is the only execution path used by SaaS.** In SaaS mode the resolver never returns a CLI/token-backed agent (`ICLIAgentProvider` implementations are excluded by the 32-4 gate); attempting to resolve a CLI-backed agent in SaaS yields a typed gate-denied `AgentRunResult` (not a thrown bare exception).
5. An Elsa activity **`RunManagedAgentActivity`** (`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/`) exposes `IManagedAgent` to workflows, replacing ad-hoc role→llm-call dispatch in agent-driven steps. It accepts the agent/role + phase + issue context + per-task overrides, calls `IManagedAgent.RunAsync`, and writes the serialized `AgentRunResult` to a workflow variable.
6. Token/cost accounting in `AgentRunResult.CostUsd` is computed via the existing **`IProviderPricingService.Compute(provider, model, inputTokens, outputTokens)`** (cost basis), and the result is tagged with `CredentialSource` so downstream pricing/markup (Epic 34) and billing (Epic 35) can attribute it. BYOK runs are tagged `byok`; platform-key runs are tagged `platform`.
7. **Failures never lose the run record.** A provider error, budget-exceeded, gate denial, missing-credential, or tool-loop exception produces a typed `AgentRunResult { Success = false, FailureCode, FailureReason }` (with whatever token/cost was accrued before failure), never an unhandled exception that drops the run. The single exception is a programmer/contract error (e.g., null request), which may throw.
8. **DCB events** `AGENT.RUN.STARTED`, `AGENT.RUN.SUCCESS`, and `AGENT.RUN.FAILED` are emitted (one STARTED before the loop; exactly one terminal SUCCESS or FAILED) via the tenant `IEventRepository`, tagged `{ agentId, version, provider, model, role, correlationId, credentialSource, tenantId }`. `AGENT.RUN.FAILED` additionally tags `failureCode`.
9. **Unit + workflow tests** cover: happy path (all fields populated end-to-end), budget-exceeded, gate-denied (CLI-backed agent in SaaS), provider-failure, missing-credential, and tool-loop-exhausted; plus a test asserting every `AgentRunResult` field is populated on the happy path and that exactly one terminal DCB event is emitted per run.

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  IManagedAgent.cs                 # NEW — the managed execution contract
  ManagedAgent.cs                  # NEW — orchestrator over the inline LLM/tool-loop seam
  ManagedAgentRequest.cs           # NEW — input record (role/agentId, phase, issue ctx, overrides, correlationId)
  AgentRunResult.cs                # NEW — structured outcome record (the producer for 32-6/32-8/32-9)
  AgentRunEventTypes.cs            # NEW — AGENT.RUN.STARTED/SUCCESS/FAILED constants
  IManagedAgentResolver.cs         # NEW (or extends 32-2 resolver) — returns IManagedAgent for (tenant, role/agentId)

apps/tamma-elsa/src/Tamma.Activities/LlmCall/
  CallLlmInlineActivity.cs         # MODIFY — extract the agentic tool loop into a reusable seam (no behaviour change)
  IInlineToolLoopRunner.cs         # NEW — the extracted, reusable tool-loop seam (interface)
  InlineToolLoopRunner.cs          # NEW — the extracted loop body (moved verbatim from the activity)

apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/
  RunManagedAgentActivity.cs       # NEW — Elsa activity exposing IManagedAgent to workflows
```

### IManagedAgent contract (C#)

```csharp
namespace Tamma.Api.Services.Agents;

/// <summary>
/// The managed-LLM-agent execution layer (Epic 32). Composes a resolved agent +
/// credential + the existing inline tool loop into one instrumented run that
/// returns a structured <see cref="AgentRunResult"/>.
///
/// Distinct from <c>ICLIAgentProvider</c> (CLI/token providers, single-user only).
/// In SaaS mode this is the ONLY execution path; CLI-backed agents are excluded by
/// the 32-4 provider-auth gate.
/// </summary>
public interface IManagedAgent
{
    /// <summary>Stable agent identity + the pinned config version this instance runs.</summary>
    Guid AgentId { get; }
    int Version { get; }

    /// <summary>
    /// Run the agent end-to-end. Composition:
    ///   resolve agent (32-2) -> resolve credential (32-3) -> SaaS gate (32-4)
    ///   -> assemble context + RAG (Epic 6) -> render prompt (Epic 27)
    ///   -> agentic tool loop (reused CallLlmInlineActivity seam)
    ///   -> sanitize -> instrument (cost via IProviderPricingService) -> capture outcome.
    ///
    /// NEVER throws on an expected failure (provider error, budget exceeded, gate
    /// denial, missing credential, loop exhaustion): returns a typed
    /// <see cref="AgentRunResult"/> with Success=false and a FailureCode/Reason so
    /// the run record is always captured. Emits exactly one terminal DCB event.
    /// </summary>
    Task<AgentRunResult> RunAsync(ManagedAgentRequest request, CancellationToken ct);
}
```

### ManagedAgentRequest (input)

```csharp
public sealed record ManagedAgentRequest
{
    public required Guid? TenantId { get; init; }      // null => single-user / platform default
    public required string Role { get; init; }         // 8 valid roles (RolePhaseMap.ValidRoles)
    public Guid? AgentId { get; init; }                // explicit agent (32-1); else resolver picks for role
    public string? Phase { get; init; }                // workflow phase, for ResolveForPhaseAsync
    public required string UserPrompt { get; init; }   // task prompt (pre-render variables)
    public int? IssueNumber { get; init; }             // issue/PR context for RAG + event tags
    public TaskOverrides? Overrides { get; init; }     // clamped per resolver (budget min, tools intersect)
    public required string CorrelationId { get; init; } // ties the run to the workflow instance / dispatch
}
```

### AgentRunResult (output — the producer record)

```csharp
public sealed record AgentRunResult
{
    public required Guid AgentId { get; init; }
    public required int Version { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Role { get; init; }

    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal CostUsd { get; init; }              // IProviderPricingService.Compute(...)
    public long DurationMs { get; init; }

    public required bool Success { get; init; }
    public string? ResponseText { get; init; }
    public IReadOnlyList<ToolCallSummary> ToolCalls { get; init; } = Array.Empty<ToolCallSummary>();

    public required string CorrelationId { get; init; }
    public required string CredentialSource { get; init; }  // "byok" | "platform"

    // Populated only when Success == false:
    public string? FailureCode { get; init; }    // e.g. BUDGET_EXCEEDED | GATE_DENIED | PROVIDER_ERROR | NO_CREDENTIAL | LOOP_EXHAUSTED
    public string? FailureReason { get; init; }
}

public sealed record ToolCallSummary(string ToolName, bool Success, long DurationMs);
```

### Composition inside ManagedAgent.RunAsync

```
1. resolved   = await _resolver.ResolveForPhaseAsync(tenantId, phase, role, overrides)   // 32-2
                 -> ResolvedAgentConfig { Provider, Model, Temperature, MaxTokens, TokenBudget, Tools, ... }
2. credential = await _credentialResolver.ResolveAsync(tenantId, resolved.Provider)      // 32-3 (BYOK -> platform)
                 -> { ApiKey, BaseUrl, Source }; if null => FAILED(NO_CREDENTIAL)
3. gate       = _saasGate.Check(mode, resolved)                                           // 32-4
                 -> CLI-backed agent in SaaS => FAILED(GATE_DENIED)  (typed result, no throw)
4. budget     = _budgetGuard.Check(resolved, tenantId)                                    // reuses CheckBudgetActivity logic
                 -> over budget => FAILED(BUDGET_EXCEEDED)
5. context    = await _contextAssembler.AssembleAsync(role, issueNumber, ...)             // Epic 6: AssembleContextActivity / RAG pipeline
6. prompt     = await _promptRenderer.RenderAsync(role, phase/action, config, context)    // Epic 27 prompt + convention render (tenant->system->error)
7. emit AGENT.RUN.STARTED { agentId, version, provider, model, role, correlationId, credentialSource, tenantId }
8. loop       = await _toolLoop.RunAsync(provider=resolved.Provider, model=resolved.Model,
                     systemPrompt=prompt.System, userPrompt=prompt.User, tools=resolved.Tools,
                     credential, loopConfig)                                              // REUSED inline seam (sanitize+compact+validate inside)
9. cost       = _pricing.Compute(resolved.Provider, resolved.Model, loop.InputTokens, loop.OutputTokens)  // IProviderPricingService
10. result    = AgentRunResult { ... mapped from resolved + loop + cost + credential.Source ... }
11. emit AGENT.RUN.SUCCESS or AGENT.RUN.FAILED (exactly one terminal event)
12. return result
```

Steps 2–4, 8 each have a typed-failure exit that maps to `AgentRunResult { Success=false }` and emits `AGENT.RUN.FAILED` — never a propagated exception.

### Reusing (not forking) the inline tool loop

The agentic loop currently lives privately inside `CallLlmInlineActivity.AgenticToolLoop(...)` (sanitize prompts → multi-turn call → tool-call validation → sequential/parallel tool execution → tool-output sanitization + secret redaction → context compaction → token accounting). To satisfy AC3 the loop body is **extracted verbatim** into `InlineToolLoopRunner` behind `IInlineToolLoopRunner`, and:

- `CallLlmInlineActivity` delegates to the runner (behaviour and outputs unchanged — its existing sanitization tests `CallLlmInlineActivitySanitizationTests` must still pass byte-for-byte).
- `ManagedAgent` calls the **same** runner. No second copy of the loop, sanitizer, validator, compactor, or parallel executor.

This is a pure refactor-extract: same `ContentSanitizer`, `IToolExecutorRegistry`, `IToolCallValidator`, `ContextCompactor`, `ParallelToolExecutor`, and `ToolLoopEventEmitter` instances are injected into the runner.

### Integration with the C# Elsa workflow (Epic 9 unified API)

`RunManagedAgentActivity` is the new call site for agent-driven steps. It replaces the `ResolveAgentConfig → ResolveLlmPrompt → ResolveTools → CheckBudget → CallLlm` ad-hoc chain for managed agents with a single activity that delegates the full composition to `IManagedAgent`:

```csharp
[Activity("Tamma.AgentDispatch", "Run Managed Agent",
    "Execute a managed LLM agent end-to-end and capture a structured AgentRunResult",
    Kind = ActivityKind.Task)]
public class RunManagedAgentActivity : CodeActivity
{
    public Input<string> RoleProp { get; set; } = default!;
    public Input<string?> PhaseProp { get; set; } = default!;
    public Input<string> UserPromptProp { get; set; } = default!;
    public Input<int?> IssueNumberProp { get; set; } = default!;
    public Input<string?> OverridesJsonProp { get; set; } = default!;

    // Resolves IManagedAgent via IManagedAgentResolver (32-2), runs it, and
    // writes JsonSerializer.Serialize(AgentRunResult) to "AgentRunResult" variable.
}
```

The existing single-turn `CallLlmActivity`/`CallLlmInlineActivity` path remains for non-agent inline LLM calls; managed agent steps move to `RunManagedAgentActivity`. Per Epic 9, all agent resolution/credential/prompt access round-trips through the central API, so the engine activity stays thin.

### Cost basis

```csharp
// IProviderPricingService already exists (apps/tamma-elsa/src/Tamma.Api/Services/Providers/):
//   decimal Compute(string provider, string? model, int inputTokens, int outputTokens);
result.CostUsd = _pricing.Compute(resolved.Provider, resolved.Model,
                                  loop.InputTokens, loop.OutputTokens);
```

`AgentRunResult` is a **producer** record: 32-9 emits the usage/cost events from these fields; 34/35/36 consume them. The markup engine is 34-5, NOT this story — `CostUsd` here is the raw provider cost basis only.

## Dependencies

**Internal:**

- **Story 32-2** (Agent registry, resolution & RBAC API) — provides `IAgentResolverService` / `IManagedAgentResolver` returning the agent config (and, in this story, an `IManagedAgent`). Hard prerequisite.
- **Story 32-3** (Per-tenant provider credential resolution, BYOK → platform) — canonical owner of cabinet key wiring; provides the credential + `CredentialSource`. Hard prerequisite.
- **Story 32-4** (SaaS provider auth gating — API-key only) — provides the gate that excludes `ICLIAgentProvider` in SaaS; consumed at step 3. Hard prerequisite.
- **Epic 1** (provider abstraction) — `IProviderPricingService`, provider config/allowlist, normalized LLM responses.
- **Epic 6** (RAG/context) — `AssembleContextActivity` + the RAG pipeline supply the assembled context fed to prompt render.
- **Epic 27** (prompt/convention render) — prompt + convention resolution (tenant → system → error; NEVER empty/plain fallback).
- **Epic 9** (unified agent API) — engine ↔ central-API round-trips for resolution/credential/prompt; the call-site convention.

**Consumers (downstream, not blockers):**

- **Story 32-6** (action trail) — consumes `AGENT.RUN.*` events + `AgentRunResult`.
- **Story 32-8** (outcome capture & bug taxonomy) — consumes the run outcome.
- **Story 32-9** (usage & cost emission) — emits usage/cost events from `AgentRunResult` fields.

**External:** none new (reuses existing HTTP/provider stack).

## Testing Strategy

1. **Unit — happy path:** mock resolver/credential/gate/context/prompt/tool-loop/pricing; assert `RunAsync` returns `AgentRunResult` with **every** field populated (agentId, version, provider, model, role, input/output tokens, costUsd, durationMs, success=true, toolCalls, correlationId, credentialSource), and that the composition calls each collaborator exactly once in order.
2. **Unit — budget-exceeded:** budget guard reports over-budget → result `Success=false, FailureCode=BUDGET_EXCEEDED`; tool loop NOT invoked; `AGENT.RUN.FAILED` emitted once; no throw.
3. **Unit — gate-denied:** SaaS mode + CLI-backed agent → `FailureCode=GATE_DENIED`; tool loop NOT invoked; typed result, no throw. Mirror test in single-user mode: CLI-backed agent is allowed (gate passes).
4. **Unit — provider-failure:** tool-loop seam returns an unsuccessful `NormalizedLlmResponse` → `FailureCode=PROVIDER_ERROR`, accrued tokens/cost preserved, `AGENT.RUN.FAILED` emitted.
5. **Unit — missing-credential:** credential resolver returns null → `FailureCode=NO_CREDENTIAL`; provider never called.
6. **Unit — loop-exhausted:** tool loop returns `exhausted=true` → result still captured (`Success` per response), `AGENT.RUN.*` reflects it.
7. **Unit — credentialSource tagging:** BYOK credential → result + event tagged `byok`; platform credential → `platform`.
8. **Refactor-safety:** the existing `CallLlmInlineActivitySanitizationTests` (and the inline tool-loop tests) pass unchanged after the loop is extracted into `InlineToolLoopRunner` — proving AC3 (no fork, no behaviour drift).
9. **Workflow/integration:** `RunManagedAgentActivity` inside a minimal Elsa workflow writes a serialized `AgentRunResult` variable; assert one `AGENT.RUN.STARTED` + one terminal event per run via a fake `IEventRepository`.
10. **Event-shape:** assert `AGENT.RUN.*` tags include `{ agentId, version, provider, model, role, correlationId, credentialSource, tenantId }` and that FAILED adds `failureCode`.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale).

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IManagedAgent.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgentRequest.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRunResult.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRunEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IManagedAgentResolver.cs` | Create (or extend 32-2 resolver) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/IInlineToolLoopRunner.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/InlineToolLoopRunner.cs` | Create (loop extracted from activity) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Modify (delegate to runner; no behaviour change) |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/RunManagedAgentActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/ManagedAgentServiceCollectionExtensions.cs` | Create (DI wiring) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register IManagedAgent + activity) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/ManagedAgentTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/RunManagedAgentActivityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/InlineToolLoopRunnerTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. Reviewed `CallLlmInlineActivity.cs` (the existing tool loop you are reusing) and `IAgentResolverService` / `ResolvedAgentConfig`
4. Confirmed the 32-2/32-3/32-4 contracts (resolver, credential resolver, gate) are landed before wiring them in
5. Planned TDD approach (Red-Green-Refactor cycle)

### Key Design Decisions

- **Reuse, don't fork (AC3).** The tool loop is mature (sanitization, validation, parallel execution, compaction, token accounting). `ManagedAgent` must call the *same* code via an extracted `InlineToolLoopRunner`. The extraction is a pure move with the existing sanitization/loop tests as the regression net — if those change, the extraction is wrong.
- **Typed failures, never lost runs (AC7).** Expected failures (provider/budget/gate/credential/loop) are *data*, not exceptions. The only allowable throw is a contract violation (null request). Every `RunAsync` path that returns must have emitted exactly one terminal DCB event first.
- **`AgentRunResult` is a producer, not a consumer.** Keep cost at provider basis (`IProviderPricingService.Compute`); markup (34-5), invoicing (35), analytics (36) are downstream. Do not embed markup here.
- **Resolver returns `IManagedAgent` uniformly.** Per the design spec, the resolver hands back an `IManagedAgent` whether LLM-API- or CLI-backed; workflows never branch on backend. SaaS exclusion is enforced at the gate (step 3), so a CLI-backed agent in SaaS returns a typed `GATE_DENIED` result rather than being unrepresentable.
- **Credential source flows to the result (AC6).** `CredentialSource` from 32-3 (`byok`/`platform`) is copied onto `AgentRunResult` and the DCB tags so cost attribution is genuinely the tenant's (BYOK hits their account; platform is metered/billed).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Which execution backends are available? | LLM-API-backed `IManagedAgent` **and** CLI/token-backed agents (`ICLIAgentProvider`). | **Only** LLM-API-backed `IManagedAgent`. CLI/token providers excluded by the 32-4 gate. |
| Whose credential does a run use? | The sole user's configured key (BYOK) → else platform default. | The tenant's BYOK key (Epic 29 cabinet) → else platform-provided (metered). `CredentialSource` records which. |
| Where do `AGENT.RUN.*` events land? | The user's (sole) tenant event store; `TenantId` may be null/the implicit user scope. | The tenant's `t_<hex>` event store via the tenant-scoped `IEventRepository`; `TenantId` set. Performance/action data is ALWAYS tenant-scoped, never cross-tenant. |
| Who owns the run's performance data? | The user. | The tenant — platform admin sees none of it (design spec ownership rule). |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Loop-extraction drifts behaviour (AC3) | High | Pure move; the existing `CallLlmInlineActivitySanitizationTests` + inline-loop tests are the unchanged regression net; no logic edits during extraction. |
| A failure path throws and drops the run record (AC7) | High | Wrap each composition step; map exceptions to `AgentRunResult { Success=false }`; assert "exactly one terminal event per run" in tests. |
| CLI agent leaks into SaaS (AC4) | High | Gate check at step 3 before any provider call; explicit SaaS+CLI test; resolver never returns CLI-backed agent in SaaS. |
| Cost attribution wrong (BYOK vs platform) | Medium | `CredentialSource` is set by 32-3 and copied verbatim; unit test both branches; never re-derive it here. |
| Double-counting events when reused activity also emits | Medium | `ManagedAgent` owns `AGENT.RUN.*`; the reused tool-loop seam emits only its existing tool-loop streaming events, not run-level events. |
| Depends on 32-2/3/4 not yet landed | Medium | Code to the interfaces; gate the story behind those three; use fakes in tests until they land. |

### Success Metrics

- [ ] Every agent-driven workflow step routes through `RunManagedAgentActivity` → `IManagedAgent` (no remaining ad-hoc role→llm-call dispatch for managed agents).
- [ ] 100% of managed runs produce an `AgentRunResult` and exactly one terminal `AGENT.RUN.*` event (success or failure).
- [ ] Zero forks of the tool loop / sanitizer / compactor (single source confirmed by grep).

## Related

- Design spec: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
- Implementation plan: `docs/superpowers/plans/2026-06-17-32-5-managed-agent-execution-layer-plan.md`
- Sibling stories: `docs/stories/epic-32/story-32-2/`, `story-32-3/`, `story-32-4/`, `story-32-6/`, `story-32-8/`, `story-32-9/`
- Reused code: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

## Logging Requirements

- **INFO**: managed run started (agentId, version, provider, model, role, correlationId, credentialSource), run completed (success, durationMs, inputTokens, outputTokens, costUsd, toolCalls), gate decision (allow/deny + mode).
- **DEBUG**: composition step boundaries (resolve → credential → gate → context → prompt → loop → cost), assembled-context size, rendered-prompt token estimate.
- **WARN**: typed failure paths (budget-exceeded, gate-denied, provider-error, no-credential, loop-exhausted) with `failureCode` + correlationId.
- **ERROR**: contract violations (null request), DCB event append failure (the run still returns its result; the append failure is logged, not swallowed silently).
- **Structured context**: include `{ agentId, version, provider, model, role, correlationId, tenantId, credentialSource }` where applicable.
- **Credential safety**: NEVER log the resolved API key, BaseUrl auth, or raw provider headers. `CredentialSource` (the label) is safe to log; the key is not.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
