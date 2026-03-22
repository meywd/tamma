# Story 7-1B: LLM Call Sub-Workflow

## User Story

As the **Tamma mentorship engine**, I need a reusable ELSA workflow that executes LLM calls with a config-driven provider chain, circuit breaker, retry logic, and prompt resolution — all as visible ELSA activities — so that every AI interaction is auditable, resumable, and independently invocable.

## Description

Implement an ELSA code-first workflow (`LlmCallWorkflow`) that serves as the universal building block for all AI-powered operations in the mentorship system. Every sub-workflow that needs LLM analysis (assessment, review, diagnosis, guidance, TDD) calls this workflow via `RunWorkflow` with a specific agent role and task prompt.

The workflow reads the provider chain configuration for the given agent role, iterates through providers with circuit breaker checks, resolves the appropriate prompt template using a 6-level fallback hierarchy, attaches role-specific tools, and executes the LLM call. Each provider attempt is a separate ELSA activity, making every retry and fallback visible in ELSA Studio's execution log.

**Enhances**: Stories 9-1 (config schema), 9-3 (circuit breaker), 9-5 (provider chain)

## Acceptance Criteria

### AC1: Workflow Registration
- [ ] Workflow defined as C# code-first `IWorkflow` in `Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`
- [ ] Registered at startup via `services.AddWorkflow<LlmCallWorkflow>()`
- [ ] Visible in ELSA Studio as "LLM Call" workflow
- [ ] Can be invoked standalone via ELSA REST API
- [ ] Can be invoked as child workflow via `RunWorkflow` from any parent

### AC2: Input/Output Contract
- [ ] **Inputs**: `agentRole` (string), `taskPrompt` (string), `context` (object), `sessionId` (Guid)
- [ ] **Outputs**: `llmResponse` (string), `providerUsed` (string), `costUsd` (decimal), `tokensUsed` (int), `latencyMs` (long)
- [ ] Inputs validated — `agentRole` and `taskPrompt` are required, workflow faults on missing values
- [ ] Outputs set as workflow variables accessible to parent workflow

### AC3: Provider Chain Resolution
- [ ] Reads `AgentsConfig` section from `appsettings.json` / environment variables
- [ ] Resolves provider chain for the given `agentRole` (e.g., `analyst` → `["anthropic", "openai", "openrouter"]`)
- [ ] Falls back to `default` chain when role-specific chain is not configured
- [ ] Chain order determines provider priority (first = preferred, last = fallback)
- [ ] Empty chain or missing config results in `NoProviderChainConfigured` fault

### AC4: Provider Iteration with Circuit Breaker
- [ ] `ForEach` activity iterates through providers in chain order
- [ ] `CheckCircuitBreaker` activity: reads circuit breaker state from workflow variables
  - Circuit breaker states: `Closed` (healthy), `Open` (failed, skip), `HalfOpen` (test one request)
  - Open threshold: 5 failures within 60 seconds
  - Recovery timeout: 300 seconds before transitioning to HalfOpen
  - State persisted in workflow variables (survives restart)
- [ ] `CheckBudget` activity: verifies provider is within cost limits for the session
  - Per-session budget limit from config (default: $5.00)
  - Per-provider budget limit from config (optional)
  - Skips provider if budget exceeded
- [ ] Provider skipped (next iteration) if circuit is Open or budget exceeded

### AC5: Prompt Resolution (6-Level Hierarchy)
- [ ] `ResolveLlmPrompt` custom activity resolves the system prompt using 6-level fallback:
  1. Per-provider + per-role prompt (e.g., `Prompts:anthropic:analyst`)
  2. Per-provider generic prompt (e.g., `Prompts:anthropic:default`)
  3. Per-role prompt (e.g., `Prompts:analyst`)
  4. Role category prompt (e.g., `Prompts:analysis` for analyst/reviewer roles)
  5. Generic system prompt (e.g., `Prompts:default`)
  6. Hardcoded fallback in code
- [ ] Prompt includes skill-level adaptation instructions when `context.skillLevel` is present
- [ ] Prompt resolution result logged with resolved level for debugging

### AC6: Tool Resolution
- [ ] `ResolveTools` custom activity determines available tools per provider + role
- [ ] Tool restrictions per role from config (e.g., `implementer` gets `file_write`, `analyst` does not)
- [ ] Tool restrictions per provider from config (some providers don't support tool use)
- [ ] Resolved tool list passed to `CallLlm` activity

### AC7: LLM Call Execution
- [ ] `CallLlm` custom activity sends the request to the provider's API
  - Uses `IHttpClientFactory` for HTTP calls
  - Supports streaming responses (collected into full response)
  - Timeout per provider from config (default: 120 seconds)
  - Includes `taskPrompt` as user message, resolved prompt as system message
  - Includes `context` object serialized into the conversation
- [ ] On success: sets output variables, emits `RecordSuccess` activity
- [ ] On failure (HTTP error, timeout, parse error): emits `RecordFailure`, updates circuit breaker, continues to next provider
- [ ] Retry within single provider: exponential backoff using `Elsa.Delay` activities
  - Max 2 retries per provider (configurable)
  - Backoff: 1s, 4s (base * 2^attempt)
  - Each retry is a visible ELSA activity in the execution log

### AC8: Diagnostics and Observability
- [ ] `RecordDiagnostics` activity emitted after each provider attempt (success or failure)
  - Records: provider name, latency, token count, cost, success/failure, error message
  - Stored as workflow variable array `diagnostics[]`
- [ ] On workflow completion, total cost and token count are computed across all attempts
- [ ] Circuit breaker state changes logged with structured fields

### AC9: Error Handling
- [ ] All providers exhausted → workflow faults with `NoAvailableProvider` containing:
  - List of providers attempted
  - Failure reason per provider
  - Circuit breaker states
  - Total time elapsed
- [ ] Individual provider errors do not fault the workflow (only skip to next)
- [ ] Unexpected errors (config parse failure, serialization error) fault immediately with context

## Technical Design

### Workflow Structure (Pseudocode)

```
Flowchart: LlmCallWorkflow
├── ValidateInputs (fault if agentRole/taskPrompt missing)
├── ReadProviderConfig (resolve chain for agentRole)
├── InitializeDiagnostics (empty array)
├── ForEach provider in chain:
│   ├── CheckCircuitBreaker
│   │   ├── Open → LogSkip → Continue
│   │   └── Closed/HalfOpen → proceed
│   ├── CheckBudget
│   │   ├── Exceeded → LogSkip → Continue
│   │   └── Within → proceed
│   ├── ResolveLlmPrompt (6-level hierarchy)
│   ├── ResolveTools (per-role, per-provider)
│   ├── RetryLoop (max 2):
│   │   ├── CallLlm
│   │   │   ├── Success → RecordSuccess → RecordDiagnostics → Break (exit ForEach)
│   │   │   └── Failure → RecordFailure → UpdateCircuitBreaker
│   │   └── Delay (exponential backoff)
│   └── RecordDiagnostics (failure)
├── [If no provider succeeded]:
│   └── Fault: NoAvailableProvider
└── SetOutputs (response, providerUsed, cost, tokens, latency)
```

### Custom Activities

```csharp
// New activities to create in Tamma.Activities/LlmCall/
[Activity("Tamma.LlmCall", "Check Circuit Breaker", "Check if provider circuit is open or closed")]
public class CheckCircuitBreakerActivity : CodeActivity<CircuitBreakerResult> { ... }

[Activity("Tamma.LlmCall", "Check Budget", "Verify provider is within session budget")]
public class CheckBudgetActivity : CodeActivity<BudgetCheckResult> { ... }

[Activity("Tamma.LlmCall", "Resolve LLM Prompt", "Resolve system prompt using 6-level hierarchy")]
public class ResolveLlmPromptActivity : CodeActivity<PromptResolutionResult> { ... }

[Activity("Tamma.LlmCall", "Resolve Tools", "Determine available tools for role+provider")]
public class ResolveToolsActivity : CodeActivity<ToolResolutionResult> { ... }

[Activity("Tamma.LlmCall", "Call LLM", "Execute HTTP call to LLM provider API")]
public class CallLlmActivity : CodeActivity<LlmCallResult> { ... }

[Activity("Tamma.LlmCall", "Record Diagnostics", "Record attempt diagnostics for observability")]
public class RecordDiagnosticsActivity : CodeActivity { ... }
```

### Configuration Schema

```json
{
  "AgentsConfig": {
    "ProviderChains": {
      "analyst": ["anthropic", "openai", "openrouter"],
      "implementer": ["anthropic", "openai"],
      "reviewer": ["anthropic", "openai"],
      "tester": ["anthropic", "openai"],
      "debugger": ["anthropic", "openai", "openrouter"],
      "default": ["anthropic", "openai"]
    },
    "CircuitBreaker": {
      "FailureThreshold": 5,
      "FailureWindowSeconds": 60,
      "RecoveryTimeoutSeconds": 300
    },
    "Budget": {
      "PerSessionUsd": 5.00,
      "PerProviderUsd": null
    },
    "Retry": {
      "MaxAttemptsPerProvider": 2,
      "BaseDelaySeconds": 1
    },
    "Providers": {
      "anthropic": {
        "ApiUrl": "https://api.anthropic.com/v1/messages",
        "Model": "claude-sonnet-4-20250514",
        "TimeoutSeconds": 120,
        "ApiKeyEnvVar": "ANTHROPIC_API_KEY"
      },
      "openai": {
        "ApiUrl": "https://api.openai.com/v1/chat/completions",
        "Model": "gpt-4o",
        "TimeoutSeconds": 120,
        "ApiKeyEnvVar": "OPENAI_API_KEY"
      },
      "openrouter": {
        "ApiUrl": "https://openrouter.ai/api/v1/chat/completions",
        "Model": "anthropic/claude-sonnet-4-20250514",
        "TimeoutSeconds": 120,
        "ApiKeyEnvVar": "OPENROUTER_API_KEY"
      }
    },
    "Prompts": {
      "anthropic:analyst": "You are a senior code analyst...",
      "analyst": "Analyze the following code...",
      "default": "You are a helpful AI assistant..."
    },
    "Tools": {
      "implementer": ["file_read", "file_write", "shell_exec", "git_commit"],
      "analyst": ["file_read", "search_code"],
      "reviewer": ["file_read", "search_code", "suggest_change"],
      "tester": ["file_read", "file_write", "shell_exec"],
      "debugger": ["file_read", "file_write", "shell_exec", "git_diff", "git_log"]
    }
  }
}
```

### Output Schema

```csharp
public record LlmCallWorkflowOutput
{
    public string LlmResponse { get; init; } = string.Empty;
    public string ProviderUsed { get; init; } = string.Empty;
    public decimal CostUsd { get; init; }
    public int TokensUsed { get; init; }
    public long LatencyMs { get; init; }
    public List<ProviderAttemptDiagnostic> Diagnostics { get; init; } = new();
}

public record ProviderAttemptDiagnostic
{
    public string Provider { get; init; } = string.Empty;
    public bool Success { get; init; }
    public long LatencyMs { get; init; }
    public int TokensUsed { get; init; }
    public decimal CostUsd { get; init; }
    public string? ErrorMessage { get; init; }
    public string? CircuitBreakerState { get; init; }
    public DateTime Timestamp { get; init; }
}
```

## Dependencies

- `Tamma.Core.Enums.AnalysisType` (existing)
- `IHttpClientFactory` (.NET built-in)
- `IConfiguration` for `AgentsConfig` section
- ELSA 3.x `Flowchart`, `ForEach`, `FlowDecision`, `Fault` activities
- Stories 9-1 (config schema), 9-3 (circuit breaker), 9-5 (provider chain) — design references
- No dependencies on other 7-1x sub-workflows (this is the foundation)

## Files to Create/Modify

| File | Action | Purpose |
|------|--------|---------|
| `Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Create | Code-first workflow definition |
| `Tamma.Activities/LlmCall/CheckCircuitBreakerActivity.cs` | Create | Circuit breaker check activity |
| `Tamma.Activities/LlmCall/CheckBudgetActivity.cs` | Create | Budget check activity |
| `Tamma.Activities/LlmCall/ResolveLlmPromptActivity.cs` | Create | 6-level prompt resolution |
| `Tamma.Activities/LlmCall/ResolveToolsActivity.cs` | Create | Tool resolution per role+provider |
| `Tamma.Activities/LlmCall/CallLlmActivity.cs` | Create | HTTP call to LLM API |
| `Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs` | Create | Diagnostics recording |
| `Tamma.Activities/LlmCall/Models/` | Create | DTOs for inputs/outputs |
| `Tamma.ElsaServer/Program.cs` | Modify | Register `LlmCallWorkflow` |
| `appsettings.json` | Modify | Add `AgentsConfig` section |

## Testing Strategy

### Unit Tests
- Provider chain resolution: correct chain for known role, default fallback, missing config
- Circuit breaker logic: state transitions (Closed→Open, Open→HalfOpen, HalfOpen→Closed/Open)
- Budget check: within limit, at limit, exceeded, no limit configured
- Prompt resolution: all 6 levels tested, correct priority order
- Tool resolution: role-specific, provider-restricted, combined restrictions
- Input validation: missing agentRole faults, missing taskPrompt faults

### Integration Tests
- Full workflow execution with mock HTTP server (MSW-equivalent for .NET: WireMock.Net)
- Provider chain fallback: first provider fails → second succeeds
- Circuit breaker trip: 5 failures → provider skipped on 6th call
- All providers fail → `NoAvailableProvider` fault
- Standalone invocation via ELSA REST API
- Child workflow invocation from a test parent workflow

### Performance Tests
- Single LLM call workflow execution: <200ms overhead (excluding actual API call)
- Provider chain with 3 providers, first succeeding: <300ms overhead
- Circuit breaker state check: <5ms

## Configuration

```yaml
# appsettings.yaml equivalent
AgentsConfig:
  ProviderChains:
    analyst: ["anthropic", "openai", "openrouter"]
    implementer: ["anthropic", "openai"]
    reviewer: ["anthropic", "openai"]
    tester: ["anthropic", "openai"]
    debugger: ["anthropic", "openai", "openrouter"]
    default: ["anthropic", "openai"]
  CircuitBreaker:
    FailureThreshold: 5
    FailureWindowSeconds: 60
    RecoveryTimeoutSeconds: 300
  Budget:
    PerSessionUsd: 5.00
  Retry:
    MaxAttemptsPerProvider: 2
    BaseDelaySeconds: 1
```

## Success Metrics

- All 6 prompt resolution levels work correctly in order
- Circuit breaker correctly trips after threshold failures
- Provider chain fallback completes in <500ms overhead per skipped provider
- 100% of provider attempts visible as individual activities in ELSA Studio
- Standalone invocation works via REST API
- Budget enforcement prevents overspend within 1% margin
