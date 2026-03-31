---
title: "Task 3: Fix Shutdown Lifecycle (diagnosticsQueue + costTracker Disposal)"
sidebar:
  order: 90
---

**Story:** 9-10-cli-wiring - CLI Wiring
**Epic:** 9

## Task Description

Fix the shutdown lifecycle in all three shutdown handlers (`start.tsx` service mode, `start.tsx` interactive mode, `server.ts`) to properly dispose `diagnosticsQueue` and `costTracker` BEFORE `process.exit(0)`. This task focuses on the critical constraint that `process.on('beforeExit')` NEVER fires when `process.exit(0)` is called -- all disposal must happen explicitly inside the `shutdown()` function.

## Acceptance Criteria

- `diagnosticsQueue.dispose()` is called before `process.exit(0)` in all three shutdown handlers
- `costTracker.dispose()` is called after `diagnosticsQueue.dispose()` and before `process.exit(0)` in all three shutdown handlers
- `process.on('beforeExit')` is NOT used anywhere for disposal (verified by grep)
- Each disposal call is wrapped in try/catch so a single failure does not prevent subsequent disposals or process exit
- Each shutdown handler includes a `shuttingDown` re-entrancy guard -- second signal causes immediate `process.exit(1)`
- Each shutdown handler sets a 10-second unref'd timeout that forces `process.exit(1)` if disposal hangs
- Disposal order in start.tsx service mode: `engine.dispose()` -> `diagnosticsQueue.dispose()` -> `costTracker.dispose()` -> `removeLockfile()` -> `process.exit(0)`
- Disposal order in start.tsx interactive mode: `engine.dispose()` -> `diagnosticsQueue.dispose()` -> `costTracker.dispose()` -> `removeLockfile()` -> `process.exit(0)`
- Disposal order in server.ts: `engineRegistry.disposeAll()` -> `diagnosticsQueue.dispose()` -> `costTracker.dispose()` -> `app.close()` -> `process.exit(0)`
- Cost data file is written to disk before process exits (verified by FileStore.dispose() calling flush())
- Dry-run mode disposes diagnosticsQueue and costTracker before exit
- Interactive mode early-exit paths (no candidates, user skips) dispose diagnosticsQueue and costTracker

## Implementation Details

### Technical Requirements

#### Critical Constraint: `process.on('beforeExit')` NEVER Fires with `process.exit(0)`

The Node.js `beforeExit` event only fires when the event loop drains naturally. Calling `process.exit(0)` bypasses it entirely. Both `start.tsx` (lines 124, 211) and `server.ts` (line 101) call `process.exit(0)` inside their `shutdown()` functions. Therefore, any disposal that needs to happen MUST be awaited inside `shutdown()` before the `process.exit(0)` call.

**DO NOT** attempt patterns like:
```typescript
// BAD: This handler will NEVER fire
process.on('beforeExit', async () => {
  await diagnosticsQueue.dispose();
  await costTracker.dispose();
});
```

#### start.tsx Service Mode Shutdown (around line 118)

Current code:
```typescript
const shutdown = async (): Promise<void> => {
  running = false;
  logger.info('Shutting down engine (service mode)...');
  removeHealthSentinel();
  await engine.dispose();
  removeLockfile();
  process.exit(0);
};
```

Updated code:
```typescript
let shuttingDown = false;
const shutdown = async (): Promise<void> => {
  if (shuttingDown) { process.exit(1); return; }
  shuttingDown = true;
  const shutdownTimer = setTimeout(() => { process.exit(1); }, 10_000);
  shutdownTimer.unref();
  running = false;
  logger.info('Shutting down engine (service mode)...');
  removeHealthSentinel();
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

#### start.tsx Interactive Mode Shutdown (around line 206)

Current code:
```typescript
const shutdown = async (): Promise<void> => {
  running = false;
  logger.info('Shutting down...');
  await engine.dispose();
  removeLockfile();
  process.exit(0);
};
```

Updated code:
```typescript
let shuttingDown = false;
const shutdown = async (): Promise<void> => {
  if (shuttingDown) { process.exit(1); return; }
  shuttingDown = true;
  const shutdownTimer = setTimeout(() => { process.exit(1); }, 10_000);
  shutdownTimer.unref();
  running = false;
  logger.info('Shutting down...');
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

#### server.ts Shutdown (around line 97)

Current code:
```typescript
const shutdown = async (): Promise<void> => {
  logger.info('Shutting down server...');
  await engineRegistry.disposeAll();
  await app.close();
  process.exit(0);
};
```

Updated code:
```typescript
let shuttingDown = false;
const shutdown = async (): Promise<void> => {
  if (shuttingDown) { process.exit(1); return; }
  shuttingDown = true;
  const shutdownTimer = setTimeout(() => { process.exit(1); }, 10_000);
  shutdownTimer.unref();
  logger.info('Shutting down server...');
  try { await engineRegistry.disposeAll(); }
  catch (err) { logger.error('Engine registry disposal failed', { error: err }); }
  try { await diagnosticsQueue.dispose(); }
  catch (err) { logger.error('DiagnosticsQueue disposal failed', { error: err }); }
  try { await costTracker.dispose(); }
  catch (err) { logger.error('CostTracker disposal failed', { error: err }); }
  await app.close();
  process.exit(0);
};
```

### Disposal Order Rationale

1. **Re-entrancy guard**: A `shuttingDown` boolean prevents double-entry. A second signal during shutdown causes immediate `process.exit(1)` to avoid hanging on a stuck disposal.
2. **Shutdown timeout**: A 10-second `setTimeout` (unref'd so it doesn't keep the process alive) forces `process.exit(1)` if disposal hangs. This prevents zombie processes.
3. **Engine first**: Stop all engine processing. Engines may still be emitting diagnostics events during their final operations.
4. **DiagnosticsQueue second**: The `dispose()` method drains any remaining events in the queue and calls the processor, which records usage to the cost tracker.
5. **CostTracker third**: The `dispose()` method calls `storage.flush()` (FileStore.flush()), which writes all in-memory cost data to `.tamma/cost-data.json` on disk. This must happen AFTER diagnosticsQueue because the queue's drain may produce additional cost records.
6. **Lockfile/app**: Clean up lockfiles or close HTTP server.
7. **process.exit(0)**: Final exit.

**Error handling**: Each disposal call is wrapped in try/catch. If one disposal fails, the others still execute and the process still exits. Errors are logged but do not prevent shutdown.

### Why `diagnosticsQueue.dispose()` BEFORE `costTracker.dispose()`

The diagnostics processor (created via `createDiagnosticsProcessor(costTracker, logger)`) converts `DiagnosticsEvent` objects into `costTracker.recordUsage()` calls. If `costTracker.dispose()` runs first, the final batch of diagnostics events would try to record usage on an already-disposed tracker. By disposing the queue first, all pending events are processed and their cost records are written to the tracker before the tracker flushes to disk.

### Files to Modify

- `packages/cli/src/commands/start.tsx` -- **MODIFY** -- Both shutdown handlers
- `packages/cli/src/commands/server.ts` -- **MODIFY** -- Shutdown handler

Note: If Task 1 and Task 2 already include these changes, this task serves as verification and testing only.

### Dependencies

- [ ] Task 1 (start.tsx wiring) should be done first or simultaneously
- [ ] Task 2 (server.ts wiring) should be done first or simultaneously
- [ ] `DiagnosticsQueue.dispose()` must exist (Story 9-11)
- [ ] `CostTracker.dispose()` must exist (already exists -- calls `storage.flush()`)

## Testing Strategy

### Unit Tests

- [ ] Test service mode shutdown calls `engine.dispose()` before `diagnosticsQueue.dispose()`
- [ ] Test service mode shutdown calls `diagnosticsQueue.dispose()` before `costTracker.dispose()`
- [ ] Test service mode shutdown calls `costTracker.dispose()` before `process.exit(0)`
- [ ] Test interactive mode shutdown calls disposal in same order
- [ ] Test server shutdown calls `engineRegistry.disposeAll()` before `diagnosticsQueue.dispose()`
- [ ] Test server shutdown calls `costTracker.dispose()` before `app.close()`
- [ ] Test that `process.on('beforeExit')` is NOT registered anywhere in start.tsx or server.ts
- [ ] Test that all three shutdown handlers complete without errors when disposal succeeds
- [ ] Test that shutdown still calls `process.exit(0)` even if `diagnosticsQueue.dispose()` throws (each call is in try/catch)
- [ ] Test that shutdown still calls `process.exit(0)` even if `engine.dispose()` throws
- [ ] Test that shutdown still calls `process.exit(0)` even if `costTracker.dispose()` throws
- [ ] Test re-entrancy guard: second signal during shutdown causes immediate `process.exit(1)`
- [ ] Test shutdown timeout: if disposal takes longer than 10 seconds, `process.exit(1)` is forced
- [ ] Test dry-run mode disposes diagnosticsQueue and costTracker before exit
- [ ] Test interactive mode early-exit paths (no candidates, user skips) dispose diagnosticsQueue and costTracker

### Integration Verification

- [ ] Start engine, process one issue, send SIGINT -- verify `.tamma/cost-data.json` exists and contains data
- [ ] Start server, trigger SIGTERM -- verify cost data is flushed before exit
- [ ] Check that the health sentinel is removed in service mode (existing behavior preserved)

### Validation Steps

1. [ ] Update all three shutdown handlers
2. [ ] Verify no `process.on('beforeExit')` usage: `grep -r 'beforeExit' packages/cli/src/`
3. [ ] Run `pnpm --filter @tamma/cli run typecheck` -- must pass
4. [ ] Run `pnpm --filter @tamma/cli test` -- must pass
5. [ ] Manual: run engine with `--once`, verify cost-data.json is written on exit

## Notes & Considerations

- `removeLockfile()` is synchronous (`fs.unlinkSync` inside) -- it can safely be called after async disposal.
- `removeHealthSentinel()` is also synchronous -- it should stay before `engine.dispose()` as it currently is (marks the process as unhealthy immediately).
- Each disposal call MUST be wrapped in try/catch. If `engine.dispose()`, `diagnosticsQueue.dispose()`, or `costTracker.dispose()` throw, the remaining disposals and `process.exit(0)` must still execute. Errors are logged but do not prevent shutdown.
- A `shuttingDown` re-entrancy guard prevents double-entry into the shutdown function. If a second signal arrives while shutdown is in progress, `process.exit(1)` is called immediately.
- A 10-second `setTimeout` (unref'd) forces `process.exit(1)` if disposal hangs, preventing zombie processes.
- Dry-run mode and interactive early-exit paths (no candidates, user skips) must also dispose diagnosticsQueue and costTracker before returning/exiting.

## Completion Checklist

- [ ] Service mode shutdown: engine -> diagnosticsQueue -> costTracker -> lockfile -> exit
- [ ] Interactive mode shutdown: engine -> diagnosticsQueue -> costTracker -> lockfile -> exit
- [ ] Server shutdown: engines -> diagnosticsQueue -> costTracker -> app.close -> exit
- [ ] No `process.on('beforeExit')` usage
- [ ] Each disposal call wrapped in try/catch
- [ ] Re-entrancy guard (`shuttingDown`) in all three shutdown handlers
- [ ] 10-second unref'd shutdown timeout in all three shutdown handlers
- [ ] Dry-run mode disposes diagnosticsQueue and costTracker
- [ ] Interactive early-exit paths dispose diagnosticsQueue and costTracker
- [ ] Cost data file written to disk before exit
- [ ] TypeScript compilation passes
- [ ] Unit tests written and passing
