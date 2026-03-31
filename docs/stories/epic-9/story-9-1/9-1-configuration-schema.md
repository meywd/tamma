# Story 1: Multi-Agent Configuration Schema

## Goal
Define the config types so users can configure provider chains per agent role, map workflow phases to roles, and set per-role prompts/tools/budgets. Keep `agent: AgentConfig` required in `TammaConfig` for backward compatibility; add `agents` and `security` alongside it.

## Design

**New file: `packages/shared/src/types/agent-config.ts`**

```typescript
import type { AgentType } from './knowledge.js';

export type WorkflowPhase =
  | 'ISSUE_SELECTION' | 'CONTEXT_ANALYSIS' | 'PLAN_GENERATION'
  | 'CODE_GENERATION' | 'PR_CREATION' | 'CODE_REVIEW'
  | 'TEST_EXECUTION' | 'STATUS_MONITORING';

export const DEFAULT_PHASE_ROLE_MAP: Record<WorkflowPhase, AgentType> = Object.freeze({
  ISSUE_SELECTION: 'scrum_master',
  CONTEXT_ANALYSIS: 'analyst',
  PLAN_GENERATION: 'architect',
  CODE_GENERATION: 'implementer',
  PR_CREATION: 'implementer',
  CODE_REVIEW: 'reviewer',
  TEST_EXECUTION: 'tester',
  STATUS_MONITORING: 'scrum_master',
} as const satisfies Record<WorkflowPhase, AgentType>);

/**
 * Maps EngineState values to their corresponding WorkflowPhase.
 * Note: CODE_REVIEW and TEST_EXECUTION have no direct EngineState equivalent --
 * they are sub-phases within the implementation cycle.
 */
export const ENGINE_STATE_TO_PHASE: Partial<Record<string, WorkflowPhase>> = Object.freeze({
  SELECTING_ISSUE: 'ISSUE_SELECTION',
  ANALYZING: 'CONTEXT_ANALYSIS',
  PLANNING: 'PLAN_GENERATION',
  IMPLEMENTING: 'CODE_GENERATION',
  CREATING_PR: 'PR_CREATION',
  MONITORING: 'STATUS_MONITORING',
});

export interface ProviderChainEntry {
  provider: string;                         // 'claude-code', 'opencode', 'openrouter', 'zen-mcp'
  model?: string;                           // 'claude-sonnet-4-5', 'z-ai/z1-mini'
  /**
   * Reference to environment variable containing the API key.
   * The key is resolved at runtime via `process.env[apiKeyRef]`.
   * Raw API keys must NOT be stored in config files.
   */
  apiKeyRef?: string;                       // e.g. 'OPENROUTER_API_KEY'
  config?: Record<string, unknown>;         // provider-specific (baseUrl, timeout, etc.)
}

export type PermissionMode = 'bypassPermissions' | 'default';

export interface AgentRoleConfig {
  providerChain: ProviderChainEntry[];
  allowedTools?: string[];
  maxBudgetUsd?: number;
  permissionMode?: PermissionMode;
  systemPrompt?: string;
  providerPrompts?: Record<string, string>; // key = provider name
}

export interface AgentsConfig {
  defaults: AgentRoleConfig;
  roles?: Partial<Record<AgentType, Partial<AgentRoleConfig>>>;
  phaseRoleMap?: Partial<Record<WorkflowPhase, AgentType>>;
}
```

**New file: `packages/shared/src/types/security-config.ts`**

```typescript
export interface SecurityConfig {
  sanitizeContent?: boolean;
  validateUrls?: boolean;
  gateActions?: boolean;
  maxFetchSizeBytes?: number;
  blockedCommandPatterns?: string[];
}
```

**Config Validation Rules** (enforced at load time):

- `blockedCommandPatterns`: each pattern must compile as valid regex; max pattern length 500 chars; max 100 patterns total
- `maxFetchSizeBytes`: must be >= 0 and <= 1_073_741_824 (1 GiB)
- `maxBudgetUsd`: must be >= 0 and <= 100 (configurable upper bound); must be a finite number
- `providerChain`: must be non-empty in `defaults`
- `provider` string: must match `/^[a-z0-9][a-z0-9_-]{0,63}$/` and must not be `__proto__`, `constructor`, or `prototype` (prototype pollution guard)
- `bypassPermissions`: emit WARN log at startup; require `TAMMA_ALLOW_BYPASS_PERMISSIONS=true` env var to take effect

**Modify: `packages/shared/src/types/index.ts`** -- Add to `TammaConfig`:

`agent` stays **required** (it is the existing, working field). The new fields are optional additions:

```typescript
export interface TammaConfig {
  mode: 'standalone' | 'orchestrator' | 'worker';
  logLevel: 'debug' | 'info' | 'warn' | 'error';
  github: GitHubConfig;
  agent: AgentConfig;                       // REQUIRED -- existing field, kept as-is
  engine: EngineConfig;
  aiProviders?: AIProviderConfig[];
  defaultProvider?: AIProviderType;
  elsa?: ElsaConfig;
  server?: ServerConfig;
  agents?: AgentsConfig;                    // NEW -- multi-agent config
  security?: SecurityConfig;                // NEW -- security settings
}
```

**Modify: `packages/cli/src/config.ts`** -- Fix `mergeConfig()` to propagate new fields:

```typescript
function mergeConfig(base: TammaConfig, override: Partial<TammaConfig>): TammaConfig {
  return {
    mode: override.mode ?? base.mode,
    logLevel: override.logLevel ?? base.logLevel,
    github: { ...base.github, ...override.github },
    agent: { ...base.agent, ...override.agent },
    engine: { ...base.engine, ...override.engine },
    // Preserve optional top-level fields -- currently silently dropped
    // Shallow-merge agents and security so base keys are not lost
    ...(base.agents || override.agents
      ? { agents: { ...base.agents, ...override.agents } }
      : {}),
    ...(base.security || override.security
      ? { security: { ...base.security, ...override.security } }
      : {}),
    ...(base.aiProviders || override.aiProviders
      ? { aiProviders: override.aiProviders ?? base.aiProviders }
      : {}),
    ...(base.defaultProvider || override.defaultProvider
      ? { defaultProvider: override.defaultProvider ?? base.defaultProvider }
      : {}),
    ...(base.elsa || override.elsa
      ? { elsa: override.elsa ?? base.elsa }
      : {}),
    ...(base.server || override.server
      ? { server: override.server ?? base.server }
      : {}),
  };
}
```

**New file: `packages/shared/src/config/normalize-agents.ts`** -- Add `normalizeAgentsConfig()`:

This function lives in `@tamma/shared` (not the CLI) so that any package (orchestrator, workers, etc.) can normalize legacy config without depending on the CLI. The CLI can re-export for convenience.

`agent.provider` is typed `AIProviderType = 'anthropic' | 'openai' | 'local'`. The normalizer must map these to the correct provider chain name, not hardcode `'claude-code'`:

```typescript
/** Map legacy AIProviderType to provider chain name */
const LEGACY_PROVIDER_MAP: Record<AIProviderType, string> = Object.freeze({
  anthropic: 'claude-code',
  openai: 'openrouter',    // OpenAI models go through OpenRouter gateway
  local: 'local',          // Local providers keep their identity; the factory handles Ollama/llama.cpp/vLLM routing
});

export function normalizeAgentsConfig(config: TammaConfig): AgentsConfig {
  if (config.agents) return structuredClone(config.agents);

  const legacy = config.agent;
  const providerName = LEGACY_PROVIDER_MAP[legacy.provider ?? 'anthropic'];

  return {
    defaults: {
      providerChain: [{ provider: providerName, model: legacy.model }],
      allowedTools: legacy.allowedTools,
      maxBudgetUsd: legacy.maxBudgetUsd,
      permissionMode: legacy.permissionMode,
    },
  };
}
```

**Modify: `packages/cli/src/config.ts`** -- Add env var support for new fields in `loadEnvConfig()`:

```typescript
// Security config from env
const sanitize = env['TAMMA_SANITIZE_CONTENT'];
const validateUrls = env['TAMMA_VALIDATE_URLS'];
const gateActions = env['TAMMA_GATE_ACTIONS'];
const maxFetchSize = env['TAMMA_MAX_FETCH_SIZE_BYTES'];

const securityOverrides: Partial<SecurityConfig> = {};
if (sanitize === 'true' || sanitize === 'false') securityOverrides.sanitizeContent = sanitize === 'true';
if (validateUrls === 'true' || validateUrls === 'false') securityOverrides.validateUrls = validateUrls === 'true';
if (gateActions === 'true' || gateActions === 'false') securityOverrides.gateActions = gateActions === 'true';
if (maxFetchSize !== undefined) {
  const parsed = parseInt(maxFetchSize, 10);
  if (!Number.isNaN(parsed)) securityOverrides.maxFetchSizeBytes = parsed;
}
if (Object.keys(securityOverrides).length > 0) {
  config.security = securityOverrides as SecurityConfig;
}

// Agent provider from env (maps to provider chain)
const agentProvider = env['TAMMA_AGENT_PROVIDER'];
if (agentProvider === 'anthropic' || agentProvider === 'openai' || agentProvider === 'local') {
  if (!config.agent) config.agent = {} as AgentConfig;
  (config.agent as Partial<AgentConfig>).provider = agentProvider;
}
```

## Files
- CREATE `packages/shared/src/types/agent-config.ts` -- `WorkflowPhase`, `DEFAULT_PHASE_ROLE_MAP`, `ENGINE_STATE_TO_PHASE`, `ProviderChainEntry`, `AgentRoleConfig`, `AgentsConfig`, `PermissionMode`
- CREATE `packages/shared/src/types/security-config.ts` -- `SecurityConfig` (extracted to its own file)
- CREATE `packages/shared/src/config/normalize-agents.ts` -- `normalizeAgentsConfig()` with `LEGACY_PROVIDER_MAP` (lives in shared, not CLI, to avoid upward dependencies)
- MODIFY `packages/shared/src/types/index.ts` -- add `agents?: AgentsConfig`, `security?: SecurityConfig` to `TammaConfig` (keep `agent` required); re-export from `agent-config.ts` and `security-config.ts`
- MODIFY `packages/shared/src/index.ts` -- re-export new types and `normalizeAgentsConfig()`
- MODIFY `packages/cli/src/config.ts` -- fix `mergeConfig()` to propagate `agents`/`security`/other optional fields with shallow merge; add env var support; re-export `normalizeAgentsConfig` from `@tamma/shared` for convenience
- CREATE `packages/shared/src/types/agent-config.test.ts`
- CREATE `packages/shared/src/config/normalize-agents.test.ts`

## Example Config
```json
{
  "agent": {
    "model": "claude-sonnet-4-5",
    "maxBudgetUsd": 1.0,
    "allowedTools": ["Read", "Write", "Edit", "Bash", "Glob", "Grep"],
    "permissionMode": "default"
  },
  "agents": {
    "defaults": {
      "providerChain": [
        { "provider": "openrouter", "model": "z-ai/z1-mini" },
        { "provider": "opencode" }
      ],
      "maxBudgetUsd": 1.0
    },
    "roles": {
      "architect": {
        "providerChain": [
          { "provider": "openrouter", "model": "anthropic/claude-opus-4" }
        ],
        "maxBudgetUsd": 2.0
      },
      "implementer": {
        "providerChain": [
          { "provider": "claude-code", "model": "claude-sonnet-4-5" },
          { "provider": "opencode" }
        ],
        "allowedTools": ["Read", "Write", "Edit", "Bash", "Glob", "Grep"],
        "maxBudgetUsd": 5.0
      }
    }
  },
  "security": {
    "sanitizeContent": true,
    "validateUrls": true
  }
}
```

## Verify
- `pnpm --filter @tamma/shared run typecheck`
- `pnpm vitest run packages/shared/src/types/agent-config`
- `pnpm vitest run packages/shared/src/config/normalize-agents`
- Legacy config (only `agent`) still works via `normalizeAgentsConfig()`
- `normalizeAgentsConfig()` maps `provider: 'anthropic'` to chain entry `{ provider: 'claude-code' }`, maps `provider: 'openai'` to `{ provider: 'openrouter' }`, maps `provider: 'local'` to `{ provider: 'local' }`
- `normalizeAgentsConfig()` returns `structuredClone` when `config.agents` is already set (not the same reference)
- `mergeConfig()` shallow-merges `agents` and `security` (not wholesale replace) from both file config and env config layers
- `SecurityConfig` is in its own file (`security-config.ts`), re-exported from `types/index.ts`
- `ENGINE_STATE_TO_PHASE` mapping is exported and covers all mappable engine states
- `PermissionMode` type is exported and used in `AgentRoleConfig.permissionMode`
- `DEFAULT_PHASE_ROLE_MAP` and `LEGACY_PROVIDER_MAP` are frozen with `Object.freeze()`
- Config validation: `blockedCommandPatterns` regex compilation checked at load time, `maxFetchSizeBytes` in range [0, 1 GiB], `maxBudgetUsd` in range [0, 100], `providerChain` non-empty in defaults, `provider` string matches `/^[a-z0-9][a-z0-9_-]{0,63}$/` and rejects `__proto__`/`constructor`/`prototype`, `bypassPermissions` emits WARN and requires `TAMMA_ALLOW_BYPASS_PERMISSIONS=true`
- `apiKeyRef` (not `apiKey`) is used in `ProviderChainEntry` -- references env var name, resolved at runtime

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
