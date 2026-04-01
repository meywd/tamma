---
title: "Story 22.4: CLI + SaaS Feature Parity Matrix"
sidebar:
  order: 220
---

Status: planned

## Story

As a **product owner**,
I want a documented and tested feature parity matrix between CLI standalone mode and SaaS mode,
so that we can guarantee no SaaS-only lock-in for core functionality and clearly communicate which features are available in which mode.

## Acceptance Criteria

1. A feature parity document exists at `docs/feature-parity-matrix.md` with a table listing every user-facing feature and its availability in standalone, SaaS, and hybrid modes
2. Core features (issue processing, plan generation, code implementation, PR creation, CI monitoring, merge) are marked as available in ALL modes
3. Features exclusive to SaaS mode are limited to: multi-repo management, team dashboards, GitHub App webhook triggers, and centralized billing
4. Features exclusive to standalone mode are limited to: interactive TUI, local agent selection, no-internet operation
5. Hybrid mode features (cloud sync for monitoring) are clearly marked as optional add-ons to standalone
6. A runtime parity check function (`assertModeParity()`) validates at startup that the current config has all required capabilities for the declared mode
7. An automated test reads the parity matrix document, validates that every listed "available" feature has a corresponding implementation test
8. The parity matrix is version-stamped and linked from the main README
9. No feature flagged as "core" is gated behind `config.mode !== 'standalone'` in the codebase

## Technical Context

### Feature Parity Matrix

| Feature | Standalone (CLI) | SaaS | Hybrid | Notes |
|---------|:---:|:---:|:---:|-------|
| **Core Pipeline** | | | | |
| Issue selection (poll/list) | Y | Y | Y | |
| Issue analysis | Y | Y | Y | |
| Plan generation | Y | Y | Y | |
| Plan approval (human gate) | Y (TUI) | Y (Dashboard) | Y (TUI) | Different UI, same gate |
| Code implementation | Y (local agent) | Y (remote runner) | Y (local agent) | IAgentExecutor abstraction |
| PR creation | Y | Y | Y | |
| CI monitoring | Y | Y | Y | |
| PR merge | Y | Y | Y | |
| Branch creation/cleanup | Y | Y | Y | |
| **Agent Execution** | | | | |
| Local CLI agents (Claude Code) | Y | -- | Y | Agents run on user's machine |
| Remote runner agents (GitHub Actions) | -- | Y | -- | Agents run on user's runners |
| Role-based agent resolution | Y | Y | Y | Same resolver, different executor |
| Multi-provider chain (failover) | Y | Y | Y | |
| Provider health tracking | Y | Y | Y | |
| Content sanitization | Y | Y | Y | |
| **Configuration** | | | | |
| `~/.tamma/providers.json` (user creds) | Y | -- | Y | SaaS uses GitHub App auth |
| `.tamma/config.json` (project settings) | Y | Y | Y | |
| Environment variables | Y | Y | Y | |
| CLI flag overrides | Y | -- | Y | SaaS has no CLI |
| **Event Store** | | | | |
| In-memory event store | Y | Y | Y | |
| File-backed event store (JSONL) | Y | -- | Y | |
| PostgreSQL event store | -- | Y | -- | SaaS server only |
| Cloud sync (optional) | -- | -- | Y | Story 22.3 |
| **Monitoring** | | | | |
| TUI (SessionLayout) | Y | -- | Y | Interactive CLI |
| Service mode (JSON logs) | Y | Y | Y | Headless |
| Dashboard (app.tamma.dev) | -- | Y | Y (read-only) | Cloud sync enables dashboard |
| Cost tracking | Y (local) | Y (centralized) | Y (local + sync) | |
| **Workflow Engine** | | | | |
| Local pipeline (no ELSA) | Y | -- | Y | LocalWorkflowAdapter |
| ELSA sidecar (Docker) | Y (optional) | -- | Y (optional) | User starts Docker manually |
| ELSA server (managed) | -- | Y | -- | SaaS infrastructure |
| **Multi-Repo** | | | | |
| Single repo (current directory) | Y | Y | Y | |
| Multi-repo (SaaSCoordinator) | -- | Y | -- | Requires GitHub App |
| **Triggers** | | | | |
| Poll-based (timer) | Y | Y | Y | |
| Webhook-based (push) | -- | Y | -- | Requires GitHub App |
| Manual (`--once`, `--interactive`) | Y | -- | Y | |
| **Security** | | | | |
| Credential encryption | Y | Y | Y | |
| Input validation | Y | Y | Y | |
| Error redaction | Y | Y | Y | |
| Budget clamping | Y | Y | Y | |
| Tool clamping | Y | Y | Y | |

### Runtime Parity Check

```typescript
// packages/shared/src/config/mode-parity.ts

interface ModeCapability {
  name: string;
  requiredFor: ('standalone' | 'saas' | 'hybrid')[];
  check: (config: TammaConfig) => boolean;
  suggestion: string;
}

const MODE_CAPABILITIES: ModeCapability[] = [
  {
    name: 'GitHub credentials',
    requiredFor: ['standalone', 'saas', 'hybrid'],
    check: (c) => c.github.authMode === 'pat' ? !!c.github.token : !!c.github.appId,
    suggestion: 'Set GITHUB_TOKEN or configure GitHub App credentials',
  },
  {
    name: 'Agent provider',
    requiredFor: ['standalone', 'hybrid'],
    check: (c) => !!c.agents || !!c.agent,
    suggestion: 'Configure at least one agent provider in ~/.tamma/providers.json',
  },
  {
    name: 'Remote workflow configuration',
    requiredFor: ['saas'],
    check: (c) => !!c.engine.remoteWorkflowId,
    suggestion: 'Set engine.remoteWorkflowId in config for SaaS mode',
  },
  {
    name: 'Cloud API key',
    requiredFor: ['hybrid'],
    check: (c) => !!c.cloud?.apiKey,
    suggestion: 'Set TAMMA_CLOUD_API_KEY for hybrid mode cloud sync',
  },
];

function assertModeParity(config: TammaConfig): { valid: boolean; errors: string[] } {
  const mode = config.mode ?? 'standalone';
  const errors: string[] = [];

  for (const cap of MODE_CAPABILITIES) {
    if (cap.requiredFor.includes(mode) && !cap.check(config)) {
      errors.push(`${cap.name}: ${cap.suggestion}`);
    }
  }

  return { valid: errors.length === 0, errors };
}
```

### Codebase Audit Script

A test script that scans the codebase for mode-gated code:

```typescript
// packages/shared/src/config/mode-parity.test.ts

test('no core feature is gated behind SaaS mode', async () => {
  // Grep for patterns that gate core features on mode
  const coreFiles = [
    'packages/orchestrator/src/engine.ts',
    'packages/cli/src/commands/start.tsx',
    'packages/providers/src/',
    'packages/platforms/src/',
  ];

  for (const file of coreFiles) {
    const content = await readFile(file, 'utf-8');
    // Check for patterns like: if (config.mode === 'saas') return;
    // These are only acceptable in non-core code paths
    const modeGates = content.match(/config\.mode\s*===?\s*['"]saas['"]/g) ?? [];
    for (const gate of modeGates) {
      // Each gate must be in a non-core code path
      // (This is a heuristic -- review manually if it fires)
    }
  }
});
```

### Files to Create

- `docs/feature-parity-matrix.md` -- the feature parity document (human-readable)
- `packages/shared/src/config/mode-parity.ts` -- runtime parity check
- `packages/shared/src/config/mode-parity.test.ts` -- unit tests + codebase audit

### Files to Modify

- `packages/cli/src/commands/start.tsx` -- call `assertModeParity()` at startup, print warnings for missing capabilities
- `packages/cli/src/commands/process-issue.ts` -- same parity check
- `docs/README.md` or project root `README.md` -- link to feature parity matrix (if README exists)

## Implementation Notes

1. **The parity matrix is the source of truth.** It is a living document that must be updated when new features are added. The automated test validates that the document is not stale by checking for corresponding test files or implementation files for each listed feature.

2. **"Core" designation is the key constraint.** Any feature marked as "core" in the matrix must work in all three modes. If a new feature is added that only works in SaaS mode, it must NOT be flagged as core. This is enforced by code review and the automated audit test.

3. **The runtime parity check is a startup diagnostic, not a blocker.** If a capability is missing, the engine logs a warning with a suggestion but does not refuse to start (except for truly fatal gaps like missing GitHub credentials). This follows the principle of graceful degradation.

4. **Mode naming convention:**
   - `standalone` = local CLI, local agents, no cloud
   - `saas` = Tamma Cloud, GitHub App, remote runners
   - `hybrid` = local CLI + local agents + optional cloud sync for monitoring

5. **The codebase audit test is a safety net.** It uses pattern matching to detect if someone adds `if (mode === 'saas')` gates around core functionality. It is intentionally aggressive (may produce false positives) to catch accidental lock-in. False positives should be suppressed with inline comments explaining why the gate is acceptable.

6. **Feature parity matrix versioning.** The document includes a version number and date. When the matrix changes, the version is bumped. The `assertModeParity()` function references the matrix version to detect mismatches between code and documentation.

## Dependencies

- **Story 22.1**: `IAgentExecutor` must exist to verify that agent execution works in all modes
- **Story 22.2**: Standalone workflow engine must exist to verify standalone completeness
- `packages/shared/src/types/index.ts` -- `TammaConfig` (for mode field)
- `packages/cli/src/commands/start.tsx` -- startup integration point
- `packages/cli/src/commands/process-issue.ts` -- worker integration point

## Estimated Effort

**6 hours**

- Feature parity matrix document: 2 hours (research + writing)
- Runtime parity check function + tests: 2 hours
- Codebase audit test: 1 hour
- Start command integration + warnings: 1 hour

## Testing Strategy

- **Unit tests (assertModeParity)**: Test each mode with complete config (all pass), test each mode with missing required capability (specific error returned), test unknown mode (treated as standalone).
- **Codebase audit test**: Scan orchestrator, CLI, providers, platforms packages for mode-gated code. Verify no core code path is behind a SaaS-only gate. This test runs in CI and fails if new SaaS-only gates are introduced without being explicitly marked as non-core.
- **Matrix validation test**: Parse `docs/feature-parity-matrix.md`, extract feature names marked as available, verify each has at least one corresponding test file in the test suite (heuristic: grep for feature name in test files).
- **Manual review**: Feature matrix reviewed by product owner before merge. This is a documentation deliverable, not just code.

---

**Last Updated**: 2026-03-28
