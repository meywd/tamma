---
title: "Task 4: Add cost-monitor Dependency and Ensure normalizeAgentsConfig Is Exported"
sidebar:
  order: 90
---

**Story:** 9-10-cli-wiring - CLI Wiring
**Epic:** 9

## Task Description

Add `@tamma/cost-monitor` as a workspace dependency in `packages/cli/package.json` so that the imports added in Tasks 1 and 2 (`createCostTracker`, `FileStore`) resolve correctly. Also verify that `normalizeAgentsConfig()` is exported from `packages/cli/src/config.ts` (it should have been added by Story 9-1 Task 4, but verify it is present and exported).

## Acceptance Criteria

- `@tamma/cost-monitor` listed as a dependency in `packages/cli/package.json` with `"workspace:*"` version
- `normalizeAgentsConfig` is exported from `packages/cli/src/config.ts` (named export)
- `pnpm install` completes without errors after package.json change
- Import `{ createCostTracker, FileStore } from '@tamma/cost-monitor'` resolves in CLI package
- Import `{ normalizeAgentsConfig } from '../config.js'` resolves in CLI command files (note: re-exported from `@tamma/shared` per Story 9-1)
- Story 9-1's `mergeConfig()` fix is verified to handle `config.agents` and `config.security` sections (otherwise they are silently dropped during config loading)

## Implementation Details

### Technical Requirements

#### 1. Add @tamma/cost-monitor dependency to packages/cli/package.json

Current dependencies section:
```json
{
  "dependencies": {
    "@tamma/api": "workspace:*",
    "@tamma/shared": "workspace:*",
    "@tamma/orchestrator": "workspace:*",
    "@tamma/observability": "workspace:*",
    "@tamma/providers": "workspace:*",
    "@tamma/platforms": "workspace:*",
    "ink": "^5.0.1",
    "ink-spinner": "^5.0.0",
    "ink-text-input": "^6.0.0",
    "ink-select-input": "^6.0.0",
    "react": "^18.3.1",
    "commander": "^12.1.0",
    "dotenv": "^16.4.7"
  }
}
```

Add `@tamma/cost-monitor`:
```json
{
  "dependencies": {
    "@tamma/api": "workspace:*",
    "@tamma/cost-monitor": "workspace:*",
    "@tamma/shared": "workspace:*",
    "@tamma/orchestrator": "workspace:*",
    "@tamma/observability": "workspace:*",
    "@tamma/providers": "workspace:*",
    "@tamma/platforms": "workspace:*",
    "ink": "^5.0.1",
    "ink-spinner": "^5.0.0",
    "ink-text-input": "^6.0.0",
    "ink-select-input": "^6.0.0",
    "react": "^18.3.1",
    "commander": "^12.1.0",
    "dotenv": "^16.4.7"
  }
}
```

Note: Keep dependencies in alphabetical order. `@tamma/cost-monitor` comes after `@tamma/api` and before `@tamma/shared`.

#### 2. Verify normalizeAgentsConfig() is exported from config.ts

Story 9-1 Task 4 should have already added `normalizeAgentsConfig()` to `packages/cli/src/config.ts`. Verify:

- The function exists in the file
- It is exported (has `export` keyword)
- The signature matches: `normalizeAgentsConfig(config: TammaConfig): AgentsConfig`

If it is NOT present (Story 9-1 not yet implemented), add a placeholder:

```typescript
import type { AgentsConfig, TammaConfig } from '@tamma/shared';

export function normalizeAgentsConfig(config: TammaConfig): AgentsConfig {
  if (config.agents) {
    return config.agents;
  }

  // Derive from legacy config.agent
  const providerName = 'claude-code'; // default fallback
  return {
    defaults: {
      providerChain: [{ provider: providerName, model: config.agent.model }],
      allowedTools: config.agent.allowedTools,
      maxBudgetUsd: config.agent.maxBudgetUsd,
      permissionMode: config.agent.permissionMode,
    },
  };
}
```

However, the full implementation with `LEGACY_PROVIDER_MAP` belongs to Story 9-1 Task 4. Only add a stub if absolutely necessary for this story to compile.

#### 3. Run pnpm install

After modifying `package.json`:

```bash
pnpm install
```

This updates the lockfile to include the new workspace dependency.

### Files to Modify

- `packages/cli/package.json` -- **MODIFY** -- Add @tamma/cost-monitor dependency
- `packages/cli/src/config.ts` -- **VERIFY** (or MODIFY if normalizeAgentsConfig is missing)

### Dependencies

- [ ] `packages/cost-monitor/package.json` must exist with name `@tamma/cost-monitor` (already exists)
- [ ] Story 9-1 Task 4 should be complete (normalizeAgentsConfig in config.ts)

## Testing Strategy

### Validation Steps

1. [ ] Add `@tamma/cost-monitor` to `packages/cli/package.json` dependencies
2. [ ] Run `pnpm install` -- must succeed
3. [ ] Verify `normalizeAgentsConfig` is exported: `grep -n 'export.*normalizeAgentsConfig' packages/cli/src/config.ts`
4. [ ] Run `pnpm --filter @tamma/cli run typecheck` -- must pass (after Tasks 1 and 2 are also done)
5. [ ] Verify import resolves: create a minimal test import in a test file

### Unit Tests

- [ ] Test that `normalizeAgentsConfig` is importable from config module
- [ ] Test that `normalizeAgentsConfig({ agents: someConfig })` returns `someConfig` directly
- [ ] Test that `normalizeAgentsConfig({ agent: legacyConfig })` returns a valid `AgentsConfig` with provider chain

Note: Comprehensive tests for `normalizeAgentsConfig()` belong in Story 9-1. This task only verifies the export is accessible.

## Notes & Considerations

- The `@tamma/cost-monitor` package already exists at `packages/cost-monitor/` with `name: "@tamma/cost-monitor"` in its package.json. The workspace dependency will resolve via pnpm workspaces.
- The `pnpm-workspace.yaml` file includes `packages/*` which covers `packages/cost-monitor`.
- If Story 9-1 is not yet complete, `normalizeAgentsConfig()` may not exist in `config.ts`. In that case, this task should add a minimal implementation that can be replaced by the full Story 9-1 implementation later. Coordinate with Story 9-1 to avoid merge conflicts.
- This task is intentionally small and can be done first (before Tasks 1-3) as a prerequisite, or done in parallel.
- `normalizeAgentsConfig` is re-exported from `@tamma/shared` per Story 9-1. The CLI's `config.ts` re-exports it for local import convenience.
- **Critical dependency**: Story 9-1's `mergeConfig()` fix must be implemented before or alongside Story 9-10, otherwise `config.agents` and `config.security` will be silently dropped during config loading. Verify that `mergeConfig()` properly handles these config sections before considering this task complete.

## Completion Checklist

- [ ] `@tamma/cost-monitor` added to `packages/cli/package.json` dependencies
- [ ] Dependencies in alphabetical order
- [ ] `pnpm install` succeeds
- [ ] `normalizeAgentsConfig` is exported from `packages/cli/src/config.ts`
- [ ] TypeScript compilation passes (after all tasks complete)
