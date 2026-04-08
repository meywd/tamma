# Workflow: LLM Call

**Definition ID:** `llm-call`
**Class:** `LlmCallWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`

## Purpose

The LLM Call is the **universal building block** for all AI operations in Tamma. Every workflow that needs to call an LLM dispatches this workflow. It handles:

- Multi-provider chain resolution (try providers in order)
- Circuit breaker logic (skip failing providers)
- Budget enforcement (stop when spend exceeds cap)
- Provider allowlist filtering (security)
- **Concurrency gating** (wait-loop until an LLM slot opens)
- Retry with exponential backoff (transient errors)
- Agent config resolution from ELSA Agents DB
- Prompt registry resolution (role + action to rendered template)
- Agentic tool loop (multi-turn tool calling)
- Per-attempt diagnostics collection

## Flow Diagram

```
+--------------------+
| Initialize Inputs  |
| (typed or legacy)  |
+--------+-----------+
         |
         v
+--------------------+
| Resolve Prompt     |
| (registry:         |
|  role + action     |
|  → template)       |
+--------+-----------+
         |
         v
+--------------------+
| Setup Budget       |
| (parse cap from    |
|  input)            |
+--------+-----------+
         |
         v
+--------------------+
| Resolve Agent      |
| Config (DB)        |
| (prompt, chain,    |
|  settings)         |
+--------+-----------+
         |
         v
+--------------------+
| Resolve Provider   |
| Chain              |
| (input > DB >      |
|  default)          |
+--------+-----------+
         |
         v
+--------------------+
| Check LLM          |<---------+
| Concurrency        |          |
+--------+-----------+          |
         |                      |
    +----+----+                 |
    OK        AtLimit           |
    |            |              |
    |            v              |
    |     +-------------+      |
    |     | Concurrency |      |
    |     | Wait        |------+
    |     | (delay)     |
    |     +-------------+
    v
+--------------------+
| For Each Provider  |---> (for each provider in chain)
| in Chain           |
+--------+-----------+
         |
    +----+-----+
    |            |
    v            v
 [Already     [Try Provider]
  Succeeded?]     |
    |             +---> Circuit Breaker Open?
   skip                  |
                    +----+----+
                   YES        NO
                    |          |
                    v          v
              [Record      Budget Exhausted?
               CB Skip]        |
                          +----+----+
                         YES        NO
                          |          |
                          v          v
                    [Record      [Resolve Tools]
                     Budget           |
                     Skip]            v
                               +------------+
                               | Retry Loop |
                               | (While:    |
                               |  !success  |
                               |  && attempt|
                               |  <= max)   |
                               +-----+------+
                                     |
                               +-----+------+
                               | Call LLM   |
                               | (CallLlm   |
                               |  Inline    |
                               |  Activity) |
                               +-----+------+
                                     |
                                     v
                               +------------+
                               | Record     |
                               | Diagnostics|
                               +-----+------+
                                     |
                                     v
                               +------------+
                               | LLM        |
                               | Succeeded? |
                               +--+------+--+
                                 YES      NO
                                  |        |
                                  v        v
                            [Set Success] [Transient?]
                            [Build Output]  |
                                       +---+---+
                                      YES      NO
                                       |        |
                                       v        v
                                 [Increment] [Exhaust
                                  Attempt]   Attempts]
                                       |
                                       +---> (loop)
         |
         v
+--------------------+
| Call Succeeded?    |
+--+--------------+--+
  YES               NO
   |                 |
   v                 v
[Set Outputs]  +------------------+
               | Build Failure    |
               | Output           |
               | ("All providers  |
               |  failed")        |
               +--------+---------+
                        |
                        v
                   [Set Outputs]
```

## Provider Chain Resolution

The provider chain determines which LLM providers are tried and in what order. Resolution priority:

1. **Caller input** -- Explicit `ProviderChain` in the input JSON
2. **Agent config from DB** -- Resolved by `ResolveAgentConfigActivity` based on `agentRole`
3. **Default chain** -- `["anthropic", "openai", "openrouter"]`

All providers are then filtered through the **provider allowlist** (`ProviderAllowlist.FilterAllowedDefault()`). If all providers are rejected, the workflow fails with a clear error.

## Circuit Breaker

Each provider has an independent circuit breaker state:

| State | Behavior |
|-------|----------|
| **Closed** | Provider available, requests pass through |
| **Open** | Provider blocked, requests skip to next |
| **Half-Open** | After cooldown period, one probe request is allowed |

When a circuit breaker is open and its cooldown has not elapsed, the provider is skipped and a diagnostic record is emitted. The circuit breaker state is tracked in a serialized JSON dictionary (`CircuitBreakerStatesJson`).

**Security note:** If the circuit breaker state cannot be parsed (corrupt JSON), the provider is blocked (fail closed).

## Budget Enforcement

The workflow tracks spending against a configurable budget cap (`BudgetCapUsd`). If the budget is exhausted, remaining providers are skipped.

**Security note:** If the budget state cannot be parsed, spending is denied (fail closed).

## Concurrency Gating

Before entering the provider chain loop, the workflow checks LLM concurrency via `CheckLlmConcurrencyActivity`. This activity determines whether a slot is available for a new LLM call:

| Outcome | Behavior |
|---------|----------|
| **OK** | A slot is available; proceed to the provider chain |
| **AtLimit** | All slots are occupied; wait via `ConcurrencyWaitDelayActivity` and re-check |

The wait-loop continues until a slot opens. This prevents overloading LLM providers when many workflows are running concurrently.

## Retry Loop

Each provider gets up to `MaxRetries` (default 3) attempts. Only **transient errors** trigger retries:

| HTTP Status | Meaning |
|-------------|---------|
| 429 | Rate limited |
| 502 | Bad gateway |
| 503 | Service unavailable |
| 504 | Gateway timeout |
| 0 | Network error |

Non-transient errors (400, 401, 403, 404) immediately exhaust all attempts for that provider.

## Tool Loop (Agentic)

When `enableToolLoop` is `true`, the `CallLlmInlineActivity` runs an agentic tool loop:
- The LLM can request tool calls
- Tools are executed and results fed back
- The loop continues until the LLM produces a final response or limits are hit

Tool loop metrics are captured in the output: `toolLoopTokens`, `toolLoopTurns`, `toolLoopExhausted`.

## Inputs

The workflow supports two input modes:

### Typed Inputs (preferred)

| Input | Type | Description |
|-------|------|-------------|
| `agentRole` | string | Agent role for config resolution (e.g., `"analyst"`, `"implementer"`, `"mentor"`) |
| `taskPrompt` | string | User prompt content |
| `context` | string | Serialized context object |
| `sessionId` | string | Session ID for tracking |
| `systemPromptOverride` | string | Override the system prompt from agent config |
| `enableToolLoop` | bool | Enable agentic tool loop |
| `toolLoopConfig` | string | Tool loop configuration JSON |

### Legacy Input (fallback)

| Input | Type | Description |
|-------|------|-------------|
| `InputJson` | string | Serialized `LlmCallWorkflowInput` JSON |

### Dict-Style Inputs (from Blocker Diagnosis etc.)

| Input | Type | Description |
|-------|------|-------------|
| `role` | string | Agent role |
| `content` | string | Prompt content |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | Whether any provider succeeded |
| `llmResponse` | string | LLM response text |
| `providerUsed` | string | Name of the provider that succeeded |
| `costUsd` | decimal | Estimated total cost |
| `tokensUsed` | int | Total tokens (prompt + completion) |
| `toolLoopTokens` | int | Tokens consumed in tool loop |
| `toolLoopTurns` | int | Number of tool loop turns |
| `toolLoopExhausted` | bool | Whether tool loop hit its limit |
| `workflowOutput` | string | Full serialized `LlmCallWorkflowOutput` JSON |

## Diagnostics

Every attempt (success or failure) generates a `ProviderAttemptDiagnostic` record containing:
- Provider name and attempt number
- Success/failure status
- Duration in milliseconds
- HTTP status code
- Whether circuit breaker or budget caused a skip
- Error message (if failed)

All diagnostics are collected in a list and included in the workflow output for observability.

---

_See also: [Mentorship](Workflow-Mentorship) | [TDD Cycle](Workflow-TDD-Cycle) | [Workflows Index](Workflows)_
