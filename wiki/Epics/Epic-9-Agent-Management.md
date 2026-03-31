# Epic 9: Config-Driven Multi-Agent Management

**Status:** Completed
**Stories:** 11 (9-1 through 9-11)
**Packages:** `@tamma/shared`, `@tamma/providers`, `@tamma/mcp-client`, `@tamma/orchestrator`, `@tamma/cli`

## Overview

Epic 9 replaces the hardcoded single-agent setup with a config-driven multi-agent system. Instead of every task going to the same AI provider, each agent _role_ (architect, implementer, reviewer, tester, etc.) now has its own ordered list of providers with automatic fallback. All provider calls are instrumented for cost tracking, usage reporting, and health monitoring.

The system is designed with defense-in-depth security: content is sanitized at both prompt input and LLM output boundaries, outbound URLs are validated against private IP ranges, shell commands are gated against a blocklist, and fetch requests are protected against SSRF.

### Key capabilities

- **Provider chains per role** — priority-ordered provider+model combinations with automatic fallback
- **Circuit breaker health tracking** — unhealthy providers automatically skipped, half-open probing for automatic recovery
- **Diagnostics collection** — costs, tokens, latency, and errors per provider+model, available for reporting
- **Role-based prompt resolution** — 6-level resolution chain with `{{variable}}` template interpolation
- **Content sanitization** — HTML stripping, zero-width character removal, prompt injection detection
- **Secure fetch and URL validation** — SSRF protection with numeric octet parsing for private IP detection
- **Action gating** — autonomous shell commands checked against a configurable blocklist
- **MCP tool interceptors** — pre/post hooks for sanitization and URL validation on MCP tool calls
- **Backward compatible** — existing single-agent configurations continue to work

---

## Story Overview

| Story | Title | Package(s) | Priority | Status |
|-------|-------|-----------|----------|--------|
| 9-1 | Configuration Schema | shared, cli | P0 | Done |
| 9-2 | Provider Diagnostics | shared, providers | P0 | Done |
| 9-3 | Provider Health Tracker | providers | P0 | Done |
| 9-4 | Agent Provider Factory | providers | P0 | Done |
| 9-5 | Provider Chain | providers | P0 | Done |
| 9-6 | Agent Prompt Registry | providers | P1 | Done |
| 9-7 | Content Sanitization | shared | P1 | Done |
| 9-8 | Role-Based Agent Resolver | providers | P0 | Done |
| 9-9 | Engine Integration | orchestrator | P0 | Done |
| 9-10 | CLI Wiring | cli | P0 | Done |
| 9-11 | Diagnostics Queue & MCP Interceptors | shared, mcp-client | P0 | Done |

---

## Architecture Diagram

```
CLI config (agents + security sections)
         |
         v
  RoleBasedAgentResolver
         |
         +---> AgentPromptRegistry  (6-level prompt resolution)
         |
         +---> ProviderChain (per role, cached)
         |           |
         |           +---> ProviderHealthTracker (circuit breaker)
         |           +---> ICostTracker          (budget check, fail-closed)
         |           +---> AgentProviderFactory  (create by name)
         |           +---> InstrumentedAgentProvider (emit diagnostics)
         |
         v
  SecureAgentProvider
  (wraps result of ProviderChain)
         |
         +---> ContentSanitizer  (sanitize prompt input, sanitize output)
         |
         v
  IAgentProvider.executeTask()
         |
  +------+------+
  |             |
  v             v
ClaudeAgent  OpenCode      (native tool-calling CLI agents)
OpenRouter   ZenMCP        (LLM providers, wrapped via wrapAsAgent())

  DiagnosticsQueue (async batch drain)
         |
         v
  DiagnosticsEventProcessor (storage, metrics, alerting)

  MCP Client layer
         |
         v
  ToolInterceptorChain
         |
         +---> Pre-interceptors (URL validation → replace blocked URLs)
         +---> Post-interceptors (content sanitization of tool results)
```

---

## Story 9-1: Configuration Schema

**File:** `packages/shared/src/types/agent-config.ts`, `packages/shared/src/types/security-config.ts`

### Types defined

```typescript
// Workflow phases in the autonomous development lifecycle
type WorkflowPhase =
  | 'ISSUE_SELECTION' | 'CONTEXT_ANALYSIS' | 'PLAN_GENERATION'
  | 'CODE_GENERATION' | 'PR_CREATION' | 'CODE_REVIEW'
  | 'TEST_EXECUTION' | 'STATUS_MONITORING';

// A single entry in a provider chain
interface IProviderChainEntry {
  provider: string;     // e.g., 'claude-code', 'openrouter'
  model?: string;       // e.g., 'claude-sonnet-4-5'
  apiKeyRef?: string;   // env var name (NOT a raw key), e.g., 'OPENROUTER_API_KEY'
  config?: Record<string, unknown>;  // provider-specific (baseUrl, timeout, etc.)
}

// Per-role configuration
interface IAgentRoleConfig {
  providerChain: IProviderChainEntry[];
  allowedTools?: string[];
  maxBudgetUsd?: number;
  permissionMode?: 'default' | 'bypassPermissions';
  systemPrompt?: string;
  providerPrompts?: Record<string, string>;
}

// Top-level multi-agent config
interface IAgentsConfig {
  defaults: IAgentRoleConfig;  // required, providerChain must be non-empty
  roles?: Partial<Record<AgentType, Partial<IAgentRoleConfig>>>;
  phaseRoleMap?: Partial<Record<WorkflowPhase, AgentType>>;
}

// Security configuration
interface SecurityConfig {
  sanitizeContent?: boolean;
  validateUrls?: boolean;
  gateActions?: boolean;
  maxFetchSizeBytes?: number;         // 0 to 1 GiB
  blockedCommandPatterns?: string[];  // regex strings, max 100
}
```

### Default phase-to-role mapping

```
ISSUE_SELECTION   → scrum_master
CONTEXT_ANALYSIS  → analyst
PLAN_GENERATION   → architect
CODE_GENERATION   → implementer
PR_CREATION       → implementer
CODE_REVIEW       → reviewer
TEST_EXECUTION    → tester
STATUS_MONITORING → scrum_master
```

### Validation

`validateAgentsConfig()` enforces:
- `defaults.providerChain` must be non-empty
- All provider names match `/^[a-z0-9][a-z0-9_-]{0,63}$/`
- Provider names `__proto__`, `constructor`, `prototype` are rejected
- `maxBudgetUsd` must be finite, between 0 and 100

`validateSecurityConfig()` enforces:
- `blockedCommandPatterns` max 100 entries, max 500 chars per pattern, must be valid regexes
- `maxFetchSizeBytes` between 0 and 1,073,741,824 (1 GiB)

---

## Story 9-2: Provider Diagnostics

**File:** `packages/shared/src/telemetry/diagnostics-event.ts`

Defines the typed `DiagnosticsEvent` discriminated union:

```typescript
// Emitted before executeTask() begins
type ProviderCallEvent = {
  type: 'provider:call';
  timestamp: number;
  providerName: string; model: string; agentType: AgentType;
  projectId: string; engineId: string; taskId: string; taskType: string;
};

// Emitted on successful task completion
type ProviderCompleteEvent = {
  type: 'provider:complete';
  // ...same as call...
  latencyMs: number; success: boolean; costUsd: number;
  tokens?: { input: number; output: number };
  errorCode?: DiagnosticsErrorCode;  // set when result.error is non-empty
};

// Emitted on exception (provider threw)
type ProviderErrorEvent = {
  type: 'provider:error';
  // ...same as call...
  latencyMs: number; success: false;
  errorCode: DiagnosticsErrorCode;
  errorMessage: string;  // sanitized (API keys stripped, truncated)
};
```

`DiagnosticsErrorCode` is a string union: `'TASK_FAILED' | 'TIMEOUT' | 'RATE_LIMITED' | 'AUTH_FAILED' | 'BUDGET_EXCEEDED' | 'UNKNOWN'`.

`sanitizeErrorMessage()` strips patterns matching API key formats and truncates to 500 chars before embedding error messages in diagnostics events.

---

## Story 9-3: Provider Health Tracker

**File:** `packages/providers/src/provider-health.ts`

`ProviderHealthTracker` implements `IProviderHealthTracker` with three-state circuit breaker logic:

```
States:
  CLOSED (healthy)   -- isHealthy() → true, requests proceed
  OPEN (unhealthy)   -- isHealthy() → false until circuitOpenDurationMs elapses
  HALF-OPEN (probe)  -- one caller allowed through; recordSuccess() → CLOSED, recordFailure() → OPEN

Failure counting:
  - Sliding window: only failures within failureWindowMs count
  - Threshold: >= failureThreshold failures within window → OPEN
  - Non-retryable ProviderError: NOT counted (config/caller errors, not health issues)
  - maxTrackedKeys: 1000 (prevents unbounded memory growth; new keys silently rejected when at capacity)
  - Half-open probe timeout: if probe caller disappears, auto-resets to OPEN after halfOpenProbeTimeoutMs

Key format:
  ProviderHealthTracker.buildKey('openrouter', 'z-ai/z1-mini') → 'openrouter:z-ai/z1-mini'
  ProviderHealthTracker.buildKey('claude-code')                → 'claude-code:default'
```

Default thresholds:

| Parameter | Default |
|-----------|---------|
| failureThreshold | 5 failures |
| failureWindowMs | 60,000 ms (1 minute) |
| circuitOpenDurationMs | 300,000 ms (5 minutes) |
| halfOpenProbeTimeoutMs | 30,000 ms (30 seconds) |
| maxTrackedKeys | 1,000 |

The `onCircuitChange` callback is invoked on state transitions (`'open'`, `'half-open'`, `'closed'`). The engine uses this callback to emit circuit breaker events to the diagnostics queue.

Circuit breaker state is in-memory only. Process restart resets all state. This is intentional: provider outages are typically transient, and persisted state would require distributed coordination.

---

## Story 9-4: Agent Provider Factory

**File:** `packages/providers/src/agent-provider-factory.ts`

`AgentProviderFactory` creates `IAgentProvider` instances by name. Built-in providers registered at construction:

| Name | Class | Interface |
|------|-------|-----------|
| `claude-code` | `ClaudeAgentProvider` | `IAgentProvider` (native CLI agent) |
| `opencode` | `OpenCodeProvider` | `IAgentProvider` (native CLI agent) |
| `openrouter` | `OpenRouterProvider` | `IAIProvider` (auto-wrapped via `wrapAsAgent()`) |
| `zen-mcp` | `ZenMCPProvider` | `IAIProvider` (auto-wrapped via `wrapAsAgent()`) |

After initial registration, built-in providers are locked and cannot be overridden. Third-party providers can be registered with new names.

**API key resolution:**

```typescript
// apiKeyRef is an env var name, never a raw key value
resolveApiKey({ provider: 'openrouter', apiKeyRef: 'OPENROUTER_API_KEY' })
// → process.env['OPENROUTER_API_KEY']
// throws if env var is not set

resolveApiKey({ provider: 'claude-code' })
// → '' (claude-code manages its own auth via the Claude CLI)
```

**`wrapAsAgent()` limitations:** LLM providers wrapped via `wrapAsAgent()` support single-turn prompt-to-response. They do not support tool-use loops, file tracking, exit code semantics, or streaming progress callbacks. These capabilities are only available through native agent providers (`claude-code`, `opencode`).

---

## Story 9-5: Provider Chain

**File:** `packages/providers/src/provider-chain.ts`

`ProviderChain` implements `IProviderChain`. On each `getProvider()` call, it iterates the configured entries in order and applies four skip conditions:

```
For each entry in entries:

  Step 1 — Health check
    health.isHealthy(key)?
    No  → skip (circuit open or half-open occupied)

  Step 2 — Budget check (only for known providers, fail-closed)
    costTracker.checkLimit({ provider, model })
    limit.allowed === false → skip
    checkLimit throws       → skip (fail-closed: treat as exceeded)

  Step 3 — Factory creation + availability
    factory.create(entry)        → creates IAgentProvider
    provider.isAvailable()       → checks if provider is reachable
    creation/availability fails  → recordFailure(key), dispose, skip

  Step 4 — Success path
    health.recordSuccess(key)    → closes circuit if half-open
    return new InstrumentedAgentProvider(provider, diagnostics, context)

If all entries exhausted:
  throw ProviderError('NO_AVAILABLE_PROVIDER')
```

The `ProviderChainContext` carries `agentType`, `projectId`, and `engineId`, which are forwarded to the `InstrumentedAgentProvider` for diagnostics tagging.

---

## Story 9-6: Agent Prompt Registry

**File:** `packages/providers/src/agent-prompt-registry.ts`

`AgentPromptRegistry` resolves system prompt templates using a 6-level priority chain:

```
Resolution order (first non-undefined wins):
  1. roles[role].providerPrompts?.[providerName]  -- per-role, per-provider
  2. roles[role].systemPrompt                      -- per-role default
  3. defaults.providerPrompts?.[providerName]      -- global per-provider
  4. defaults.systemPrompt                         -- global default
  5. builtinTemplates[role]                        -- built-in preamble
  6. GENERIC_FALLBACK                              -- last resort
```

Built-in preambles for each agent role:

| Role | Preamble (abbreviated) |
|------|----------------------|
| `architect` | "You are analyzing a GitHub issue to create a development plan. {{context}}" |
| `implementer` | "You are an autonomous coding agent. Implement the following plan for issue #{{issueNumber}}." |
| `reviewer` | "You are a code reviewer. Review the changes for correctness, style, and security." |
| `tester` | "You are a testing agent. Write and run tests for the described changes." |
| `analyst` | "You are analyzing project context to understand codebase structure and conventions." |
| `scrum_master` | "You are a project coordinator. Select the most appropriate issue to work on next." |
| `researcher` | "You are a research agent. Investigate and gather information about the topic at hand." |
| `planner` | "You are a planning agent. Create structured plans and organize work breakdown." |
| `documenter` | "You are a documentation agent. Write clear, comprehensive documentation for the codebase." |

**Template interpolation security:**
- Single-pass replacement: `{{B}}` inside variable `A`'s value is never expanded
- Variable values exceeding 100,000 chars are skipped with a warning
- Rendered templates exceeding 1,000,000 chars are truncated with a warning
- Prototype pollution keys (`__proto__`, `constructor`, `prototype`) are rejected as provider names in resolution and as role names in `registerBuiltin()`

---

## Story 9-7: Content Sanitization

**Files:** `packages/shared/src/security/content-sanitizer.ts`, `packages/shared/src/security/url-validator.ts`, `packages/shared/src/security/action-gating.ts`, `packages/shared/src/security/secure-fetch.ts`

### ContentSanitizer

Implements `IContentSanitizer`. Never throws — all errors return best-effort output.

**Input sanitization pipeline (`sanitize()`):**

```
1. Remove null bytes                    (always, even when disabled)
2. Strip HTML tags                      (quote-aware state machine)
3. Remove zero-width/invisible chars    (20+ code points, CVE-2021-42574 bidi)
4. Detect prompt injection:
   - Instruction override: "ignore previous instructions", "disregard above"
   - Role hijacking: "you are now", "act as", "pretend to be"
   - System prompt extraction: "repeat your system prompt"
   - Delimiter injection: ```system, [INST], <|im_start|>
5. NFKD normalization check             (detects encoding evasion attacks)
```

**Output sanitization pipeline (`sanitizeOutput()`):**

```
1. Remove null bytes                    (always)
2. Remove zero-width/invisible chars
3. Strip HTML tags outside code blocks  (code block content preserved verbatim)
```

Detected injection patterns produce warnings in the return value. They do not block execution — this is intentional to avoid false positives on legitimate prompts.

### URL Validator

`validateUrl(url)` returns `{ valid: boolean; warnings: string[] }`. Never throws.

Private host detection uses **numeric octet parsing** (not regex):

```
Blocked IPv4 ranges:
  0.0.0.0/8     (this network)
  10.0.0.0/8    (RFC 1918)
  127.0.0.0/8   (loopback)
  169.254.0.0/16 (link-local / AWS metadata)
  172.16.0.0/12 (RFC 1918)
  192.168.0.0/16 (RFC 1918)

Blocked literals:
  localhost, 0.0.0.0, 127.0.0.1
  [::], [::1], metadata.google.internal
  host.docker.internal, 169.254.169.254
  100.100.100.200 (Alibaba Cloud metadata)

IPv6 private ranges:
  fc00::/7  (unique local: fc, fd prefixes)
  fe80::/10 (link-local)

IPv6-mapped IPv4:
  [::ffff:x.x.x.x] → recursively checks x.x.x.x

Allowed protocols: http:, https:, ws:, wss:
```

URL length in error messages is truncated to 200 chars.

### Action Gating

`evaluateAction(command, options?)` returns `{ allowed: boolean; reason?: string }`. Never throws.

Normalization before matching: trim, lowercase, strip backslashes, strip empty quote pairs, collapse whitespace.

Default blocked patterns (excerpt):
- `rm -rf /`, `rm -rf ~`, `rm -rf *`, `rm -fr /` (alternate flag order)
- `mkfs`, `dd if=`, `:(){:|:&};:` (fork bomb), `format c:`
- `chmod -r 777 /`, `chown -r`
- `shutdown`, `reboot`, `halt`, `poweroff`, `init 0`, `init 6`
- `kill -9 1`, `killall`, `pkill -9`
- `wget | sh`, `curl | sh`, `wget | bash`, `curl | bash`
- `| sh`, `| bash`, `| eval`, `| /bin/sh`, `| /bin/bash`
- `base64 -d |`, `$(`, `${`, `| python`, `| perl`, `| ruby`, `| node`
- Backtick execution (checked separately as it is a single character)

Reason messages never reveal which pattern matched (prevents blocklist probing).

### Secure Fetch

`secureFetch(url, options?)` provides SSRF-protected HTTP:

```
1. Pre-validate URL via validateUrl()
2. Manual redirect loop (max 5 hops):
   a. Fetch with redirect: 'manual'
   b. On 301/302/307/308: extract Location, re-validate via validateUrl()
   c. Strip Authorization/Cookie/Proxy-Authorization on cross-origin redirects
3. Check Content-Type allowlist before reading body
4. Read via ReadableStream with running byte counter (default max 10 MB)
5. Abort on timeout (default 30 seconds)
6. Return { ok, status, body, headers, warnings }
```

---

## Story 9-8: Role-Based Agent Resolver

**File:** `packages/providers/src/role-based-agent-resolver.ts`

`RoleBasedAgentResolver` is the integration facade used by the engine. It connects all Epic 9 subsystems.

### Interface

```typescript
interface IRoleBasedAgentResolver {
  // Maps phase → role → provider chain → InstrumentedAgentProvider (+ optional SecureAgentProvider)
  getAgentForPhase(phase: WorkflowPhase, context: { projectId: string; engineId: string }): Promise<IAgentProvider>;
  getAgentForRole(role: AgentType, context: { projectId: string; engineId: string }): Promise<IAgentProvider>;
  // 3-level merge with clamping (defaults < role < overrides)
  getTaskConfig(role: AgentType, taskOverrides?: Partial<AgentTaskConfig>): Partial<AgentTaskConfig>;
  // Variable sanitization + prompt registry resolution
  getPrompt(role: AgentType, providerName: string, vars?: Record<string, string>): string;
  // Synchronous phase-to-role lookup
  getRoleForPhase(phase: WorkflowPhase): AgentType;
  dispose(): Promise<void>;
}
```

### Task configuration merge

`getTaskConfig()` performs a 3-level merge with the following clamping rules:

```
Level 1: defaults (from IAgentsConfig.defaults)
Level 2: roles[role] (partial overrides, undefined fields don't clobber)
Level 3: taskOverrides with clamping:
  - maxBudgetUsd: Math.min(override, ceiling from level 1/2)
  - permissionMode: 'bypassPermissions' requires TAMMA_ALLOW_BYPASS_PERMISSIONS=true
  - allowedTools: intersection only (can restrict, never expand)
  - prompt, cwd, model, sessionId, outputFormat: forwarded as-is
```

### Security properties

- Role names validated against `FORBIDDEN_KEYS` (`__proto__`, `constructor`, `prototype`)
- Template injection prevented: `getPrompt()` strips `{{` and `}}` from all variable values before rendering
- `bypassPermissions` gated on `TAMMA_ALLOW_BYPASS_PERMISSIONS=true` env var
- `SecureAgentProvider` wrapping applied when a sanitizer is configured (logs warning when no sanitizer provided)

---

## Story 9-9: Engine Integration

**File:** `packages/orchestrator/src/engine.ts` (updated)

The engine was updated to use `RoleBasedAgentResolver` at each step of the autonomous development loop:

```typescript
// In engine.ts (illustrative, not literal code)

// 1. Map EngineState to WorkflowPhase
const phase = ENGINE_STATE_TO_PHASE[engineState];

// 2. Get the configured agent for this phase
const agent = await resolver.getAgentForPhase(phase, { projectId, engineId });

// 3. Get merged task configuration
const taskConfig = resolver.getTaskConfig(roleForPhase, {
  maxBudgetUsd: stepBudgetOverride,
  allowedTools: stepToolSubset,
});

// 4. Get the rendered system prompt
const prompt = resolver.getPrompt(roleForPhase, providerName, {
  issueNumber: String(issue.number),
  context: codebaseContext,
});

// 5. Execute the task
const result = await agent.executeTask({ ...taskConfig, prompt });
```

The engine calls `resolver.dispose()` during graceful shutdown. DiagnosticsQueue is also disposed, flushing any remaining events.

---

## Story 9-10: CLI Wiring

**Package:** `@tamma/cli`

The CLI reads the multi-agent configuration from the config file:

```json
{
  "agents": {
    "defaults": {
      "providerChain": [
        { "provider": "claude-code" },
        { "provider": "openrouter", "model": "z-ai/z1-mini", "apiKeyRef": "OPENROUTER_API_KEY" }
      ]
    }
  },
  "security": {
    "sanitizeContent": true,
    "validateUrls": true,
    "gateActions": true,
    "maxFetchSizeBytes": 10485760
  }
}
```

The CLI startup sequence:
1. Load and parse config file
2. `validateAgentsConfig(config.agents)` — throws on invalid config
3. `validateSecurityConfig(config.security)` — throws on invalid config
4. Construct `ContentSanitizer` from security config
5. Construct `ProviderHealthTracker` with circuit breaker thresholds
6. Construct `AgentProviderFactory` (registers built-in providers)
7. Construct `AgentPromptRegistry` with loaded `AgentsConfig`
8. Construct `DiagnosticsQueue` and set processor
9. Construct `RoleBasedAgentResolver` with all subsystems
10. Pass resolver to engine
11. Surface config validation errors and chain warnings to the Ink UI

---

## Story 9-11: Diagnostics Queue and MCP Interceptors

### DiagnosticsQueue

**File:** `packages/shared/src/telemetry/diagnostics-queue.ts`

```
emit() [hot path, synchronous, O(1)]
  → push to queue
  → if queue.length >= maxQueueSize: shift() oldest, increment droppedCount

drain() [async, called by timer]
  → if drainPromise in flight: wait and return
  → splice all events from queue
  → processor(batch).catch(warn).finally(clear drainPromise)

dispose()
  → clearInterval(drainTimer)
  → loop: drain() until queue empty or 10 iterations
```

The drain timer uses `.unref()` so it does not prevent Node.js from exiting when only the timer is active.

### ToolInterceptorChain

**File:** `packages/mcp-client/src/interceptors.ts`

```typescript
class ToolInterceptorChain {
  addPreInterceptor(fn: PreInterceptor): void;
  addPostInterceptor(fn: PostInterceptor): void;

  // Runs all pre-interceptors in order, piping args
  // Fail-open: interceptor errors add warning, continue with unmodified args
  // Strips __proto__, constructor, prototype from returned args
  async runPre(toolName, args): Promise<{ args, warnings }>;

  // Runs all post-interceptors in order, piping result
  // Fail-open: interceptor errors add warning, continue with unmodified result
  async runPost(toolName, result): Promise<{ result, warnings }>;
}
```

**Built-in interceptor factories:**

```typescript
// Post-interceptor: sanitizes text content in tool results
createSanitizationInterceptor(sanitizer: IContentSanitizer): PostInterceptor

// Pre-interceptor: validates URL-like values in tool args
// Replaces blocked URLs with '[URL_BLOCKED_BY_POLICY]' (fail-closed)
createUrlValidationInterceptor(validateUrlFn: ValidateUrlFn): PreInterceptor
```

---

## Dependency Graph

```
Story 9-1 (config types)  ──────────────────────────────────────────┐
Story 9-7 (sanitization)  ──────────────────────┐                   │
Story 9-3 (health tracker)──┐                   │                   │
Story 9-4 (factory)       ──┤                   │                   │
Story 9-6 (prompts)       ──┤                   │                   │
Story 9-11a (DiagQueue)   ──┤                   │                   │
                             ↓                   │                   │
Story 9-2 (diagnostics)  ← needs 9-11a          │                   │
                          ↓                      │                   │
Story 9-5 (chain)        ← needs 9-2,9-3,9-4    │                   │
                                                  ↓                   ↓
Story 9-11b (ToolInterceptor) ← needs 9-7    Story 9-8 (resolver) ← needs 9-1,9-5,9-6,9-7
                                                                       ↓
                                               Story 9-9 (engine)  ← needs 9-8
                                                                       ↓
                                               Story 9-10 (CLI)    ← needs 9-1,9-9,9-11a,9-11b
```

---

## Key Interfaces

All Epic 9 subsystems follow the Dependency Inversion Principle. Consumers depend on interfaces, not concrete classes.

| Interface | Defined In | Implemented By |
|-----------|-----------|---------------|
| `IAgentsConfig` | `@tamma/shared/types/agent-config` | Config object (validated at load) |
| `SecurityConfig` | `@tamma/shared/types/security-config` | Config object (validated at load) |
| `IProviderHealthTracker` | `@tamma/providers/types` | `ProviderHealthTracker` |
| `IAgentProviderFactory` | `@tamma/providers/agent-provider-factory` | `AgentProviderFactory` |
| `IProviderChain` | `@tamma/providers/provider-chain` | `ProviderChain` |
| `IAgentPromptRegistry` | `@tamma/providers/agent-prompt-registry` | `AgentPromptRegistry` |
| `IContentSanitizer` | `@tamma/shared/security/content-sanitizer` | `ContentSanitizer` |
| `IRoleBasedAgentResolver` | `@tamma/providers/role-based-agent-resolver` | `RoleBasedAgentResolver` |
| `IDiagnosticsQueue` | `@tamma/shared/telemetry/diagnostics-queue` | `DiagnosticsQueue` |
| `IAgentProvider` | `@tamma/providers/agent-types` | `ClaudeAgentProvider`, `OpenCodeProvider`, `InstrumentedAgentProvider`, `SecureAgentProvider`, `wrapAsAgent()` result |

---

## Source File Map

| Component | File |
|-----------|------|
| Agent config types | `packages/shared/src/types/agent-config.ts` |
| Security config types | `packages/shared/src/types/security-config.ts` |
| Diagnostics events | `packages/shared/src/telemetry/diagnostics-event.ts` |
| Diagnostics queue | `packages/shared/src/telemetry/diagnostics-queue.ts` |
| Content sanitizer | `packages/shared/src/security/content-sanitizer.ts` |
| URL validator | `packages/shared/src/security/url-validator.ts` |
| Action gating | `packages/shared/src/security/action-gating.ts` |
| Secure fetch | `packages/shared/src/security/secure-fetch.ts` |
| Provider health tracker | `packages/providers/src/provider-health.ts` |
| Agent provider factory | `packages/providers/src/agent-provider-factory.ts` |
| Provider chain | `packages/providers/src/provider-chain.ts` |
| Agent prompt registry | `packages/providers/src/agent-prompt-registry.ts` |
| Instrumented agent provider | `packages/providers/src/instrumented-agent-provider.ts` |
| Secure agent provider | `packages/providers/src/secure-agent-provider.ts` |
| Role-based agent resolver | `packages/providers/src/role-based-agent-resolver.ts` |
| MCP tool interceptors | `packages/mcp-client/src/interceptors.ts` |

---

## Configuration Example

A complete `agents` configuration with role-specific overrides:

```json
{
  "agents": {
    "defaults": {
      "providerChain": [
        {
          "provider": "claude-code",
          "model": "claude-sonnet-4-5"
        },
        {
          "provider": "openrouter",
          "model": "z-ai/z1-mini",
          "apiKeyRef": "OPENROUTER_API_KEY"
        }
      ],
      "allowedTools": ["Read", "Write", "Bash", "Glob", "Grep"],
      "maxBudgetUsd": 5.0,
      "permissionMode": "default"
    },
    "roles": {
      "implementer": {
        "providerChain": [
          { "provider": "claude-code", "model": "claude-opus-4-6" }
        ],
        "maxBudgetUsd": 20.0,
        "allowedTools": ["Read", "Write", "Bash", "Glob", "Grep", "Edit"]
      },
      "reviewer": {
        "systemPrompt": "You are a strict code reviewer. Focus on security, correctness, and maintainability. Be concise and specific in your feedback.",
        "allowedTools": ["Read", "Glob", "Grep"],
        "maxBudgetUsd": 3.0
      },
      "tester": {
        "providerPrompts": {
          "openrouter": "You are a testing specialist. Write comprehensive test cases with edge cases and error scenarios."
        }
      }
    },
    "phaseRoleMap": {
      "CODE_REVIEW": "reviewer",
      "TEST_EXECUTION": "tester"
    }
  },
  "security": {
    "sanitizeContent": true,
    "validateUrls": true,
    "gateActions": true,
    "maxFetchSizeBytes": 10485760,
    "blockedCommandPatterns": [
      "curl.*production.*api\\.example\\.com"
    ]
  }
}
```

---

## Security Model

The Epic 9 security model is defense-in-depth: no single component is expected to catch everything. The layers work together:

```
External input (issue body, PR comments, MCP tool results)
         |
         v
MCP ToolInterceptorChain
  Pre: URL validation (block private IPs in tool args)
  Post: ContentSanitizer.sanitizeOutput() (strip HTML from results)
         |
         v
SecureAgentProvider (ContentSanitizer.sanitize())
  - Null byte removal (always)
  - HTML stripping
  - Zero-width char removal (bidi override protection)
  - Prompt injection detection (4 categories + encoding evasion)
         |
         v
IAgentProvider.executeTask()  [claude-code, opencode, openrouter, zen-mcp]
         |
         v
SecureAgentProvider post-call (ContentSanitizer.sanitizeOutput())
  - Strip HTML outside code blocks
  - Remove zero-width chars
         |
         v
AgentTaskResult → engine logic
```

Shell commands proposed by the agent:

```
evaluateAction(command)
  ← action-gating.ts
  - Normalize whitespace, lowercase, strip backslashes
  - Substring match against blocklist (no regex → no ReDoS)
  - Reason messages never reveal which pattern matched
```

Outbound fetch requests:

```
secureFetch(url)
  ← secure-fetch.ts
  - Pre-validate URL (private IP blocking)
  - Manual redirect with re-validation per hop
  - Strip auth headers on cross-origin redirect
  - Content-Type allowlist
  - Size-limited streaming read
  - Timeout enforcement
```

---

_For more details on the overall architecture, see [Architecture](Architecture)._

_For the story implementation plans, see [docs/stories/epic-9/](https://github.com/meywd/tamma/tree/main/docs/stories/epic-9) in the repository._
