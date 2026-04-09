# Story 9-10: CLI Wiring — Implementation Plan

## Overview

Wire the CLI (`packages/cli/`) to construct the `RoleBasedAgentResolver` with store-backed dependencies and pass it to the engine. Add new CLI commands for viewing diagnostics, health status, and provider chains. Replace hardcoded agent construction with config-driven resolution. The existing `createAgentSetup()` function in `start.tsx` already does most of this wiring; this story extends it with persistent store integration and adds new CLI subcommands.

---

## Step-by-Step Implementation Tasks

### Task 1: Update createAgentSetup() for Persistent Stores (3 hours)

**File to modify**: `packages/cli/src/commands/start.tsx`

The existing `createAgentSetup()` function (lines 96-131) already constructs a `RoleBasedAgentResolver` with in-memory dependencies. Update it to optionally wire persistent stores when a database connection is available:

```typescript
interface AgentSetupResult {
  agentResolver: IRoleBasedAgentResolver;
  diagnosticsQueue: DiagnosticsQueue;
  costTracker: CostTracker;
  healthTracker: ProviderHealthTracker;  // expose for CLI health command
}

interface AgentSetupOptions {
  config: TammaConfig;
  logger: ILogger;
  /** Optional pg.Pool for persistent store mode (server/SaaS). */
  pool?: pg.Pool;
}

function createAgentSetup(options: AgentSetupOptions): AgentSetupResult {
  const { config, logger, pool } = options;
  const agentsConfig = normalizeAgentsConfig(config);

  // Health tracker with optional persistence sync
  let healthStore: IHealthStore | undefined;
  if (pool) {
    healthStore = new PgHealthStore(pool);
  }

  const healthTracker = new ProviderHealthTracker({
    onCircuitChange: healthStore
      ? (key, state) => { void healthStore!.syncCircuitChange(key, state).catch((err) => {
          logger.warn('Failed to sync circuit change', { key, state, error: err });
        });
      }
      : undefined,
  });

  const agentFactory = new AgentProviderFactory();
  const promptRegistry = new AgentPromptRegistry({ config: agentsConfig });

  const costStorePath = path.join(config.engine.workingDirectory, '.tamma', 'cost-data.json');
  const costTracker = createCostTracker({ storage: new FileStore(costStorePath) });

  const diagnosticsQueue = new DiagnosticsQueue({ drainIntervalMs: 5000, maxQueueSize: 1000 });

  // Diagnostics processor: write to persistent store if available, else in-memory only
  let diagnosticsStore: IDiagnosticsStore | undefined;
  if (pool) {
    diagnosticsStore = new PgDiagnosticsStore(pool);
  }

  diagnosticsQueue.setProcessor(createDiagnosticsProcessor({
    costTracker,
    mapProviderName: safeProviderName,
    mapTaskType: safeTaskType,
    logger,
    diagnosticsStore,  // new optional parameter from Story 9-2
  }));

  const sanitizer = config.security?.sanitizeContent !== false ? new ContentSanitizer() : undefined;

  const resolverOptions: ConstructorParameters<typeof RoleBasedAgentResolver>[0] = {
    config: agentsConfig,
    factory: agentFactory,
    health: healthTracker,
    promptRegistry,
    diagnostics: diagnosticsQueue,
    logger,
  };
  if (costTracker !== undefined) resolverOptions.costTracker = costTracker;
  if (sanitizer !== undefined) resolverOptions.sanitizer = sanitizer;

  const agentResolver: IRoleBasedAgentResolver = new RoleBasedAgentResolver(resolverOptions);

  return { agentResolver, diagnosticsQueue, costTracker, healthTracker };
}
```

---

### Task 2: Update server.ts for Server Mode (2 hours)

**File to modify**: `packages/cli/src/commands/server.ts`

Apply the same pattern but always with a pool (server mode has a database):

```typescript
// In server command:
import type pg from 'pg';

// Pool is created elsewhere for the Fastify app -- pass it to createAgentSetup:
const { agentResolver, diagnosticsQueue, costTracker, healthTracker } = createAgentSetup({
  config,
  logger,
  pool,  // from database connection
});

const engine = new TammaEngine({
  config,
  platform,
  agentResolver,
  logger,
});
```

---

### Task 3: Update Shutdown Sequence (1 hour)

**File to modify**: `packages/cli/src/commands/start.tsx`

The existing shutdown sequence (lines 187-200) already handles diagnosticsQueue and costTracker disposal. Ensure the health tracker gets no special disposal (it's in-memory, GC handles it). Add the healthStore cleanup if pool-backed:

```typescript
const shutdown = async (): Promise<void> => {
  if (shuttingDown) { process.exit(1); return; }
  shuttingDown = true;
  const shutdownTimer = setTimeout(() => { process.exit(1); }, 10_000);
  shutdownTimer.unref();
  running = false;

  // Each disposal wrapped in try/catch per acceptance criteria
  try { await engine.dispose(); }
  catch (err) { logger.error('Engine disposal failed', { error: err }); }

  try { await diagnosticsQueue.dispose(); }
  catch (err) { logger.error('DiagnosticsQueue disposal failed', { error: err }); }

  try { await costTracker.dispose(); }
  catch (err) { logger.error('CostTracker disposal failed', { error: err }); }

  removeLockfile();
  process.exit(0);
};
```

---

### Task 4: Create `tamma diagnostics` CLI Command (2 hours)

**File to create**: `packages/cli/src/commands/diagnostics.tsx`

```typescript
import React from 'react';
import { render, Text, Box } from 'ink';
import type { CostTracker } from '@tamma/cost-monitor';

interface DiagnosticsProps {
  costTracker: CostTracker;
}

function DiagnosticsView({ costTracker }: DiagnosticsProps): React.ReactElement {
  // Display:
  // - Total cost for current session
  // - Cost breakdown by provider
  // - Token usage summary
  // - Number of successful/failed calls
  // Uses costTracker.getReport() or costTracker.getSummary()
  return (
    <Box flexDirection="column">
      <Text bold>Diagnostics Summary</Text>
      {/* ... cost table, token counts, error rates ... */}
    </Box>
  );
}

export async function diagnosticsCommand(costTracker: CostTracker): Promise<void> {
  const { waitUntilExit } = render(<DiagnosticsView costTracker={costTracker} />);
  await waitUntilExit();
}
```

---

### Task 5: Create `tamma health` CLI Command (2 hours)

**File to create**: `packages/cli/src/commands/health.tsx`

```typescript
import React from 'react';
import { render, Text, Box } from 'ink';
import type { ProviderHealthTracker } from '@tamma/providers';

interface HealthProps {
  healthTracker: ProviderHealthTracker;
}

function HealthView({ healthTracker }: HealthProps): React.ReactElement {
  const status = healthTracker.getStatus();
  const entries = Object.entries(status);

  if (entries.length === 0) {
    return <Text>No provider health data tracked yet.</Text>;
  }

  return (
    <Box flexDirection="column">
      <Text bold>Provider Health Status</Text>
      <Box flexDirection="column" marginTop={1}>
        {entries.map(([key, entry]) => (
          <Box key={key} gap={2}>
            <Text>{key}</Text>
            <Text color={entry.healthy ? 'green' : 'red'}>
              {entry.healthy ? 'HEALTHY' : 'UNHEALTHY'}
            </Text>
            <Text dimColor>failures: {entry.failures}</Text>
            <Text dimColor>circuit: {entry.circuitOpen ? 'OPEN' : 'CLOSED'}</Text>
          </Box>
        ))}
      </Box>
    </Box>
  );
}

export async function healthCommand(healthTracker: ProviderHealthTracker): Promise<void> {
  const { waitUntilExit } = render(<HealthView healthTracker={healthTracker} />);
  await waitUntilExit();
}
```

---

### Task 6: Create `tamma providers` CLI Command (2 hours)

**File to create**: `packages/cli/src/commands/providers.tsx`

```typescript
import React from 'react';
import { render, Text, Box } from 'ink';
import type { IAgentsConfig, ProviderChainEntry } from '@tamma/shared';

interface ProvidersProps {
  config: IAgentsConfig;
}

function ProvidersView({ config }: ProvidersProps): React.ReactElement {
  const defaultChain = config.defaults.providerChain;
  const roles = config.roles ? Object.entries(config.roles) : [];

  return (
    <Box flexDirection="column">
      <Text bold>Provider Chain Configuration</Text>
      <Box flexDirection="column" marginTop={1}>
        <Text underline>Default Chain:</Text>
        {defaultChain.map((entry, i) => (
          <Text key={i}>  {i + 1}. {entry.provider}{entry.model ? ` (${entry.model})` : ''}</Text>
        ))}
      </Box>
      {roles.map(([role, roleConfig]) => {
        const chain = roleConfig?.providerChain;
        if (!chain || chain.length === 0) return null;
        return (
          <Box key={role} flexDirection="column" marginTop={1}>
            <Text underline>{role}:</Text>
            {chain.map((entry: ProviderChainEntry, i: number) => (
              <Text key={i}>  {i + 1}. {entry.provider}{entry.model ? ` (${entry.model})` : ''}</Text>
            ))}
          </Box>
        );
      })}
    </Box>
  );
}

export async function providersCommand(config: IAgentsConfig): Promise<void> {
  const { waitUntilExit } = render(<ProvidersView config={config} />);
  await waitUntilExit();
}
```

---

### Task 7: Tests (3 hours)

**File to create**: `packages/cli/src/commands/__tests__/diagnostics.test.tsx`

| # | Test | Assertion |
|---|------|-----------|
| 1 | DiagnosticsView renders cost summary | Contains "Diagnostics Summary" |
| 2 | DiagnosticsView shows provider breakdown | Provider names visible |

**File to create**: `packages/cli/src/commands/__tests__/health.test.tsx`

| # | Test | Assertion |
|---|------|-----------|
| 3 | HealthView shows "No provider health data" when empty | Text matches |
| 4 | HealthView shows healthy provider in green | "HEALTHY" text |
| 5 | HealthView shows unhealthy provider in red | "UNHEALTHY" text |

**File to create**: `packages/cli/src/commands/__tests__/providers.test.tsx`

| # | Test | Assertion |
|---|------|-----------|
| 6 | ProvidersView shows default chain | Provider names listed |
| 7 | ProvidersView shows role-specific chains | Role headers visible |

**File to modify**: `packages/cli/src/commands/__tests__/start.test.ts` (or create)

| # | Test | Assertion |
|---|------|-----------|
| 8 | createAgentSetup() with no pool uses in-memory stores | No PgHealthStore created |
| 9 | createAgentSetup() with pool wires persistent stores | PgHealthStore + PgDiagnosticsStore |
| 10 | Shutdown disposes all resources | All dispose methods called |
| 11 | Shutdown handles disposal errors gracefully | Logged, not re-thrown |
| 12 | agentResolver is typed as IRoleBasedAgentResolver | TypeScript compile check |

**Total tests**: ~12

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/cli/src/commands/diagnostics.tsx` | Diagnostics CLI command |
| 2 | `packages/cli/src/commands/health.tsx` | Health status CLI command |
| 3 | `packages/cli/src/commands/providers.tsx` | Provider chain CLI command |
| 4 | `packages/cli/src/commands/__tests__/diagnostics.test.tsx` | Diagnostics command tests |
| 5 | `packages/cli/src/commands/__tests__/health.test.tsx` | Health command tests |
| 6 | `packages/cli/src/commands/__tests__/providers.test.tsx` | Providers command tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/cli/src/commands/start.tsx` | Update createAgentSetup() for persistent stores, update shutdown |
| 2 | `packages/cli/src/commands/server.ts` | Wire pool-backed stores |
| 3 | `packages/cli/src/config.ts` | Ensure all new imports exported |

---

## Dependencies

- **Story 9-1** (normalizeAgentsConfig for config loading)
- **Story 9-9** (engine integration with resolver -- engine must accept agentResolver)
- **Story 9-11** (diagnostics queue wiring -- optional, can decouple)
- **Story 9-2** (PgDiagnosticsStore -- optional for in-memory fallback)
- **Story 9-3** (PgHealthStore -- optional for in-memory fallback)

## Migration from Existing Code

1. The existing `createAgentSetup()` in `packages/cli/src/commands/start.tsx` (lines 96-131) already constructs a `RoleBasedAgentResolver`. This story extends it with optional persistent store dependencies.
2. The `startCommand()` function already wires agentResolver into the engine (line 169). No changes needed there.
3. The shutdown sequence (lines 187-200) already handles diagnosticsQueue and costTracker. Only minor additions needed.
4. New CLI commands are additive -- no existing commands are modified or removed.
5. Legacy `agent` config still works via `normalizeAgentsConfig()` -- this function is unchanged.

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Update createAgentSetup() for persistent stores | 3 |
| Update server.ts | 2 |
| Shutdown sequence update | 1 |
| `tamma diagnostics` command | 2 |
| `tamma health` command | 2 |
| `tamma providers` command | 2 |
| Tests (12 tests) | 3 |
| **Total** | **15 hours** |
