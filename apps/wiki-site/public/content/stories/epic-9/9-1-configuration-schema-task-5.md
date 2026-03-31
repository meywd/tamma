---
title: "Task 5: Add Environment Variable Support for Security Config"
sidebar:
  order: 90
---

**Story:** 9-1-configuration-schema - Multi-Agent Configuration Schema
**Epic:** 9

## Task Description

Extend the `loadEnvConfig()` function in `packages/cli/src/config.ts` to support new environment variables for the `SecurityConfig` fields and the `TAMMA_AGENT_PROVIDER` variable. Also update the barrel re-export in `packages/shared/src/types/index.ts` if not already done, and ensure all new types are accessible from `@tamma/shared`.

## Acceptance Criteria

- `TAMMA_SANITIZE_CONTENT=true` sets `security.sanitizeContent` to `true`
- `TAMMA_SANITIZE_CONTENT=false` sets `security.sanitizeContent` to `false`
- Invalid values for `TAMMA_SANITIZE_CONTENT` (e.g., `'yes'`, `'1'`) are ignored
- `TAMMA_VALIDATE_URLS=true/false` sets `security.validateUrls`
- `TAMMA_GATE_ACTIONS=true/false` sets `security.gateActions`
- `TAMMA_MAX_FETCH_SIZE_BYTES=1048576` sets `security.maxFetchSizeBytes` to `1048576`
- Non-numeric `TAMMA_MAX_FETCH_SIZE_BYTES` values are ignored
- `TAMMA_AGENT_PROVIDER=anthropic` sets `agent.provider` to `'anthropic'`
- `TAMMA_AGENT_PROVIDER=openai` sets `agent.provider` to `'openai'`
- `TAMMA_AGENT_PROVIDER=local` sets `agent.provider` to `'local'`
- Invalid `TAMMA_AGENT_PROVIDER` values are ignored
- Security config from env vars is merged through `mergeConfig()` into the final config

## Implementation Details

### Technical Requirements

- [ ] Import `SecurityConfig` type in `packages/cli/src/config.ts` (add to existing import)
- [ ] Add security config parsing to `loadEnvConfig()`:
  ```typescript
  // Security config from env
  const sanitize = env['TAMMA_SANITIZE_CONTENT'];
  const validateUrls = env['TAMMA_VALIDATE_URLS'];
  const gateActions = env['TAMMA_GATE_ACTIONS'];
  const maxFetchSize = env['TAMMA_MAX_FETCH_SIZE_BYTES'];

  const securityOverrides: Partial<SecurityConfig> = {};
  if (sanitize === 'true' || sanitize === 'false')
    securityOverrides.sanitizeContent = sanitize === 'true';
  if (validateUrls === 'true' || validateUrls === 'false')
    securityOverrides.validateUrls = validateUrls === 'true';
  if (gateActions === 'true' || gateActions === 'false')
    securityOverrides.gateActions = gateActions === 'true';
  if (maxFetchSize !== undefined) {
    const parsed = parseInt(maxFetchSize, 10);
    if (!Number.isNaN(parsed)) securityOverrides.maxFetchSizeBytes = parsed;
  }
  if (Object.keys(securityOverrides).length > 0) {
    config.security = securityOverrides as SecurityConfig;
  }
  ```
- [ ] Add agent provider parsing to `loadEnvConfig()`:
  ```typescript
  const agentProvider = env['TAMMA_AGENT_PROVIDER'];
  if (agentProvider === 'anthropic' || agentProvider === 'openai' || agentProvider === 'local') {
    if (!config.agent) config.agent = {} as AgentConfig;
    (config.agent as Partial<AgentConfig>).provider = agentProvider;
  }
  ```
- [ ] Ensure the new env vars integrate with the existing 3-layer merge (defaults -> file -> env)

### Files to Modify/Create

- `packages/cli/src/config.ts` -- **MODIFY** -- Extend `loadEnvConfig()` with security and agent provider env vars
- `packages/shared/src/types/index.ts` -- **VERIFY** -- Ensure re-export of agent-config types (should be done in Task 2)

### Dependencies

- [ ] Task 1 must be completed (SecurityConfig type)
- [ ] Task 2 must be completed (TammaConfig.security field)
- [ ] Task 3 must be completed (mergeConfig propagates security)

## Testing Strategy

### Unit Tests

- [ ] Test: `TAMMA_SANITIZE_CONTENT=true` -> `config.security.sanitizeContent === true`
- [ ] Test: `TAMMA_SANITIZE_CONTENT=false` -> `config.security.sanitizeContent === false`
- [ ] Test: `TAMMA_SANITIZE_CONTENT=yes` -> `config.security` does not have `sanitizeContent`
- [ ] Test: `TAMMA_SANITIZE_CONTENT` not set -> `config.security` is undefined
- [ ] Test: `TAMMA_VALIDATE_URLS=true` -> `config.security.validateUrls === true`
- [ ] Test: `TAMMA_VALIDATE_URLS=false` -> `config.security.validateUrls === false`
- [ ] Test: `TAMMA_GATE_ACTIONS=true` -> `config.security.gateActions === true`
- [ ] Test: `TAMMA_MAX_FETCH_SIZE_BYTES=1048576` -> `config.security.maxFetchSizeBytes === 1048576`
- [ ] Test: `TAMMA_MAX_FETCH_SIZE_BYTES=abc` -> `config.security` does not have `maxFetchSizeBytes`
- [ ] Test: `TAMMA_MAX_FETCH_SIZE_BYTES=0` -> `config.security.maxFetchSizeBytes === 0` (zero is valid)
- [ ] Test: Multiple security env vars set at once -> all fields populated in `config.security`
- [ ] Test: `TAMMA_AGENT_PROVIDER=anthropic` -> `config.agent.provider === 'anthropic'`
- [ ] Test: `TAMMA_AGENT_PROVIDER=openai` -> `config.agent.provider === 'openai'`
- [ ] Test: `TAMMA_AGENT_PROVIDER=local` -> `config.agent.provider === 'local'`
- [ ] Test: `TAMMA_AGENT_PROVIDER=gemini` (invalid) -> `config.agent.provider` is unchanged
- [ ] Test: Full stack integration -- file config has `agents`, env has security overrides -> both present in final config

### Validation Steps

1. [ ] Add security env var parsing to `loadEnvConfig()`
2. [ ] Add agent provider env var parsing to `loadEnvConfig()`
3. [ ] Run `pnpm --filter @tamma/cli run typecheck` -- must pass
4. [ ] Write unit tests exercising each env var
5. [ ] Run `pnpm --filter @tamma/cli test`
6. [ ] Manual verification: set `TAMMA_SANITIZE_CONTENT=true` and `TAMMA_VALIDATE_URLS=true`, run config loading, inspect output

## Notes & Considerations

- Boolean env vars only accept exact `'true'` or `'false'` strings. Values like `'1'`, `'yes'`, `'TRUE'` are intentionally ignored to avoid ambiguity.
- `parseInt(maxFetchSize, 10)` with `Number.isNaN` check handles non-numeric strings gracefully.
- The `config.agent = {} as AgentConfig` pattern for agent provider is safe because `loadEnvConfig()` returns partial overrides that are merged later. The cast is needed because `AgentConfig` has required fields, but only partial values are being set.
- The security config is set on the partial config object and will be propagated by the fixed `mergeConfig()` from Task 3.
- `TAMMA_BLOCKED_COMMAND_PATTERNS` is NOT included as an env var (arrays are complex in env vars; use config file for `blockedCommandPatterns`).
- `maxFetchSizeBytes` from env vars should still be subject to the validation rules from Task 1 (>= 0 and <= 1_073_741_824). Invalid range values parsed from env should be ignored or throw, consistent with the config validation approach.
- `TAMMA_ALLOW_BYPASS_PERMISSIONS=true` is a separate env var (not parsed here) that gates whether `bypassPermissions` takes effect. It is checked at runtime when `permissionMode === 'bypassPermissions'` is encountered. See Task 1 validation rules.
- Update `generateEnvExample()` to include the new env vars as commented placeholders for documentation.

## Completion Checklist

- [ ] Security env vars (TAMMA_SANITIZE_CONTENT, TAMMA_VALIDATE_URLS, TAMMA_GATE_ACTIONS, TAMMA_MAX_FETCH_SIZE_BYTES) parsed in loadEnvConfig()
- [ ] Agent provider env var (TAMMA_AGENT_PROVIDER) parsed in loadEnvConfig()
- [ ] Boolean parsing strict (only 'true'/'false')
- [ ] Integer parsing with NaN guard
- [ ] Invalid values silently ignored
- [ ] All new types re-exported from @tamma/shared
- [ ] TypeScript compilation passes
- [ ] Unit tests written and passing
- [ ] generateEnvExample() updated with new env vars (optional enhancement)
