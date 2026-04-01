---
title: "Story 11.5: Fail-Closed Guards & Provider Allowlist"
sidebar:
  order: 110
---

Status: ready-for-dev

## Story

As a **security engineer**,
I want circuit breaker and budget check failures to fail closed (deny the request) rather than fail open (allow the request), and provider names validated against a known allowlist,
so that infrastructure errors cannot be exploited to bypass safety controls, and unknown/malicious provider names cannot be injected into the LLM call chain.

## Acceptance Criteria

1. `LlmCallWorkflow.IsCircuitBreakerOpen()` returns `true` (circuit open = deny) when an exception occurs, not `false` (was `false` = allow)
2. `LlmCallWorkflow.IsBudgetExhausted()` returns `true` (budget exhausted = deny) when an exception occurs, not `false` (was `false` = allow)
3. `CheckCircuitBreakerActivity` returns circuit-open (deny) on any exception during the check
4. `CheckBudgetActivity` returns budget-exhausted (deny) on any exception during the check
5. `ProviderAllowlist` class exists with a `HashSet<string>` of known provider names
6. Provider names are validated against the allowlist in the `ResolveChain` step and `LoadProviderConfig()` path
7. Unknown provider names are rejected with a clear error message (not silently dropped)
8. The allowlist is configurable via `IOptions<ProviderAllowlistOptions>` but ships with sane defaults covering all supported providers
9. 14+ tests covering fail-closed behavior, provider allowlist enforcement, and configuration

## Technical Context

### Fail-Closed Principle

The current code has a critical security flaw: when circuit breaker or budget checks throw exceptions (e.g., database connection failure, timeout), the `catch` blocks return `false` — meaning "circuit is NOT open" and "budget is NOT exhausted". This allows LLM calls to proceed when the safety infrastructure is broken.

**Current (vulnerable):**
```csharp
bool IsCircuitBreakerOpen()
{
    try { return CheckCircuitBreaker(); }
    catch { return false; }  // BUG: allows call when check fails
}
```

**Fixed (fail-closed):**
```csharp
bool IsCircuitBreakerOpen()
{
    try { return CheckCircuitBreaker(); }
    catch { return true; }  // SAFE: denies call when check fails
}
```

### Provider Allowlist

Provider names flow through configuration and are used to select LLM API clients. If an attacker can inject a provider name (via config manipulation or prompt injection), they could redirect LLM calls to a malicious endpoint.

**Known providers** (default allowlist):
```
anthropic, openai, openrouter, google, github-copilot, local-llm, opencode, z-ai, zen-mcp
```

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/Security/ProviderAllowlist.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ProviderAllowlistOptions.cs`

### Files to Modify

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` — fix `IsCircuitBreakerOpen()` and `IsBudgetExhausted()` catch blocks
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckCircuitBreakerActivity.cs` — fix catch to return circuit-open
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckBudgetActivity.cs` — fix catch to return budget-exhausted
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveAgentConfigActivity.cs` — validate provider name against allowlist in ResolveChain
- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` — register `ProviderAllowlist` and `ProviderAllowlistOptions` in DI

## Implementation Notes

1. The fail-closed fix is a one-line change per catch block (`false` to `true`). However, add a WARN-level log in each catch to alert operators that a safety check failed: `"Circuit breaker check failed, defaulting to CLOSED (deny): {exceptionMessage}"`.
2. `ProviderAllowlist` should be injected into `ResolveAgentConfigActivity` and called during provider chain resolution. If a provider name is not in the allowlist, skip it (log WARN) and try the next provider in the chain. If no providers pass, fail the activity with a clear error.
3. The allowlist should be case-insensitive (normalize to lowercase before comparison).
4. `ProviderAllowlistOptions` should allow operators to add custom provider names (for self-hosted/custom providers) via configuration: `Security:ProviderAllowlist:AdditionalProviders: ["my-custom-llm"]`.
5. This story has NO dependency on Story 11.1 and can be implemented in parallel with it.

## Testing Strategy

- **Fail-closed tests** (8):
  - `IsCircuitBreakerOpen()` returns `true` when check throws `DbConnectionException`
  - `IsCircuitBreakerOpen()` returns `true` when check throws `TimeoutException`
  - `IsCircuitBreakerOpen()` returns `false` when circuit is actually closed (normal operation)
  - `IsCircuitBreakerOpen()` returns `true` when circuit is actually open (normal operation)
  - Same 4 tests for `IsBudgetExhausted()`
- **Provider allowlist tests** (6):
  - Known provider name passes
  - Unknown provider name rejected
  - Case-insensitive matching
  - Additional providers from config accepted
  - Empty provider name rejected
  - Provider chain with mixed valid/invalid — invalid skipped, valid used
- **Test files**:
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/FailClosedGuardTests.cs`
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/ProviderAllowlistTests.cs`

## Dependencies

- **None** (independent of Story 11.1, can be implemented in parallel)

## Estimated Effort

1.5 days

## Logging Requirements

### Existing Coverage

Line 71 mentions: "add a WARN-level log in each catch to alert operators that a safety check failed." Line 72 mentions: "log WARN" when a provider is not in the allowlist. This is a good start but needs formalization.

### Required Additions

`ProviderAllowlist` **must** inject `ILogger<T>`. Fail-closed catch blocks use existing workflow/activity loggers.

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| Circuit breaker check failed, defaulting to CLOSED (deny) | WARN | `{ExceptionType}`, `{ExceptionMessage}`, `{Provider}`, `{WorkflowInstanceId}` | Critical security event — infrastructure failure caused a safety default |
| Budget check failed, defaulting to EXHAUSTED (deny) | WARN | `{ExceptionType}`, `{ExceptionMessage}`, `{Provider}`, `{WorkflowInstanceId}` | Critical security event — budget system failure |
| Circuit breaker check succeeded | DEBUG | `{IsOpen}`, `{Provider}`, `{WorkflowInstanceId}` | Normal operation trace |
| Budget check succeeded | DEBUG | `{IsExhausted}`, `{Provider}`, `{WorkflowInstanceId}` | Normal operation trace |
| Provider rejected by allowlist | WARN | `{ProviderName}`, `{WorkflowInstanceId}` | Security event — unknown provider attempted |
| Provider accepted by allowlist | DEBUG | `{ProviderName}` | Normal operation trace |
| Provider chain resolution: all providers rejected | ERROR | `{RejectedProviders}` (list of names), `{WorkflowInstanceId}` | No valid providers available — activity will fail |
| Provider chain resolution: provider skipped, trying next | INFO | `{SkippedProvider}`, `{NextProvider}`, `{Reason}` ("not in allowlist"), `{WorkflowInstanceId}` | Fallback in the provider chain |
| Allowlist configuration loaded | INFO | `{DefaultProviderCount}`, `{AdditionalProviderCount}`, `{TotalProviders}` | Startup/configuration log — emitted once during DI registration |

### Sensitive Data Redaction

- Do NOT log API keys, tokens, or configuration secrets when logging provider names.
- Exception messages from DB connection failures may contain connection strings — ensure `{ExceptionMessage}` is truncated and does not include credential segments.

### Correlation IDs

- Fail-closed catch blocks in `LlmCallWorkflow`, `CheckCircuitBreakerActivity`, and `CheckBudgetActivity` must include `{WorkflowInstanceId}` and `{Provider}` for tracing.
- `ProviderAllowlist` logs during `ResolveAgentConfigActivity` should include the same correlation context.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/llm-injection-security-fix.md` Phases 6+7 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |
