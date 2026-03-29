# Story 22.3: Optional Cloud Sync

Status: planned

## Story

As a **developer using Tamma standalone**,
I want to optionally connect my local Tamma instance to Tamma Cloud for dashboard visibility,
so that I can monitor workflow progress, review cost reports, and share audit trails with my team without giving up local agent execution.

## Acceptance Criteria

1. When `config.cloud.apiKey` is set, the CLI reports engine events to Tamma Cloud via HTTPS POST
2. When `config.cloud.apiKey` is not set, the CLI works identically to today -- zero cloud traffic
3. Cloud sync is fire-and-forget: network failures do not block or slow the local engine pipeline
4. Events are buffered in memory and batched (default: every 5 seconds or 50 events, whichever comes first)
5. The buffer has a max size (default: 500 events); oldest events are dropped if the buffer overflows
6. Cloud sync transmits: engine state transitions, issue lifecycle events, cost data, and timing metrics. It does NOT transmit: source code, diffs, issue body text (only titles and numbers), or credentials
7. A `tamma cloud status` command shows connectivity status, events synced, events dropped, and last sync timestamp
8. A `tamma cloud disconnect` command removes the API key from config and stops sync immediately
9. Cloud sync adds less than 5ms overhead to each `eventStore.record()` call (enqueue only, no I/O on hot path)
10. Unit tests achieve 90%+ coverage on `CloudSyncTransport` including failure, backpressure, and data filtering scenarios

## Technical Context

### Architecture

```
TammaEngine
  |
  v
CompositeEventStore (implements IEventStore)
  |
  +-- LocalEventStore (JSONL, always active)
  |
  +-- CloudSyncTransport (optional, when config.cloud.apiKey is set)
        |
        +-- In-memory buffer (ring buffer, max 500 events)
        |
        +-- Background flush timer (every 5s)
        |
        v
        POST https://api.tamma.dev/v1/events/ingest
          Authorization: Bearer <config.cloud.apiKey>
          Content-Type: application/json
          Body: { events: [...], clientId: "...", timestamp: "..." }
```

### CompositeEventStore

Wraps multiple `IEventStore` implementations and delegates to all of them:

```typescript
// packages/orchestrator/src/event-stores/composite-event-store.ts

class CompositeEventStore implements IEventStore {
  constructor(private readonly stores: IEventStore[]) {}

  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): EngineEvent {
    // Primary store (first) does the actual creation
    const full = this.stores[0]!.record(event);
    // Secondary stores receive the completed event (fire-and-forget)
    for (let i = 1; i < this.stores.length; i++) {
      try {
        this.stores[i]!.record({ ...full });
      } catch {
        // Swallow -- secondary stores must not block primary
      }
    }
    return full;
  }

  getEvents(issueNumber?: number): EngineEvent[] {
    // Reads from primary store only
    return this.stores[0]!.getEvents(issueNumber);
  }

  getLastEvent(type: EngineEventType): EngineEvent | undefined {
    return this.stores[0]!.getLastEvent(type);
  }

  clear(): void {
    for (const store of this.stores) {
      store.clear();
    }
  }
}
```

### CloudSyncTransport

Implements `IEventStore` interface for composability, but internally buffers and batches events for async HTTP delivery:

```typescript
// packages/orchestrator/src/transports/cloud-sync-transport.ts

interface CloudSyncConfig {
  apiKey: string;
  apiUrl?: string;        // Default: https://api.tamma.dev
  batchSize?: number;     // Default: 50
  flushIntervalMs?: number; // Default: 5000
  maxBufferSize?: number;  // Default: 500
}

class CloudSyncTransport implements IEventStore {
  private buffer: EngineEvent[] = [];
  private flushTimer: ReturnType<typeof setInterval> | null = null;
  private synced = 0;
  private dropped = 0;
  private lastSyncAt: string | null = null;
  private connected = false;

  constructor(
    private readonly config: CloudSyncConfig,
    private readonly logger: ILogger,
  ) {
    this.startFlushTimer();
  }

  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): EngineEvent {
    // Create full event (same as other stores)
    const full: EngineEvent = {
      id: randomUUID(),
      timestamp: new Date().toISOString(),
      ...event,
    };

    // Filter sensitive data before buffering
    const sanitized = this.sanitizeEvent(full);

    // Buffer with backpressure
    if (this.buffer.length >= (this.config.maxBufferSize ?? 500)) {
      this.dropped++;
      this.buffer.shift(); // Drop oldest
    }
    this.buffer.push(sanitized);

    // Flush if batch size reached
    if (this.buffer.length >= (this.config.batchSize ?? 50)) {
      void this.flush();
    }

    return full; // Return unsanitized event to caller
  }

  private sanitizeEvent(event: EngineEvent): EngineEvent {
    // Strip sensitive data from event.data before transmission
    const sanitized = { ...event, data: { ...event.data } };
    // Remove source code, diffs, full issue bodies
    delete sanitized.data['sourceCode'];
    delete sanitized.data['diff'];
    delete sanitized.data['fullBody'];
    delete sanitized.data['context'];  // Analysis context may contain code
    // Truncate large string fields to 200 chars
    for (const [key, value] of Object.entries(sanitized.data)) {
      if (typeof value === 'string' && value.length > 200) {
        sanitized.data[key] = value.slice(0, 200) + '... [truncated]';
      }
    }
    return sanitized;
  }

  private async flush(): Promise<void> {
    if (this.buffer.length === 0) return;

    const batch = this.buffer.splice(0);
    const apiUrl = this.config.apiUrl ?? 'https://api.tamma.dev';

    try {
      const response = await fetch(`${apiUrl}/v1/events/ingest`, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${this.config.apiKey}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          events: batch,
          clientId: this.getClientId(),
          timestamp: new Date().toISOString(),
        }),
        signal: AbortSignal.timeout(10_000),
      });

      if (response.ok) {
        this.synced += batch.length;
        this.lastSyncAt = new Date().toISOString();
        this.connected = true;
      } else {
        // Re-buffer on failure (if space allows)
        this.reBuffer(batch);
        this.connected = false;
      }
    } catch {
      // Network error -- re-buffer silently
      this.reBuffer(batch);
      this.connected = false;
    }
  }

  // getEvents, getLastEvent, clear -- no-op or return empty
  // (reads go to primary LocalEventStore, not cloud)
}
```

### Config Extension

Add optional `cloud` section to `TammaConfig`:

```typescript
interface TammaConfig {
  // ... existing fields ...
  cloud?: {
    apiKey?: string;
    apiUrl?: string;
    batchSize?: number;
    flushIntervalMs?: number;
    maxBufferSize?: number;
    enabled?: boolean; // Default: true when apiKey is set
  };
}
```

Environment variable: `TAMMA_CLOUD_API_KEY` overrides `config.cloud.apiKey`.

### CLI Commands

```
tamma cloud status
  Connected: yes
  Events synced: 142
  Events dropped: 0
  Last sync: 2026-03-28T14:23:45.678Z
  Buffer size: 3 / 500

tamma cloud disconnect
  Cloud sync disabled. API key removed from ~/.tamma/providers.json.
  Local execution continues unchanged.
```

### Data Privacy Filter

Events sent to cloud contain ONLY:

| Included | Excluded |
|----------|----------|
| Event type (e.g., `STATE_TRANSITION`) | Issue body text |
| Issue number and title | Source code / diffs |
| Engine state transitions | API keys / tokens |
| Cost data (USD amounts) | Full analysis context |
| Duration metrics | File contents |
| Branch names | Error stack traces (redacted) |
| PR numbers and URLs | |

### Files to Create

- `packages/orchestrator/src/transports/cloud-sync-transport.ts` -- cloud sync implementation
- `packages/orchestrator/src/transports/cloud-sync-transport.test.ts` -- unit tests
- `packages/orchestrator/src/event-stores/composite-event-store.ts` -- composite wrapper
- `packages/orchestrator/src/event-stores/composite-event-store.test.ts` -- unit tests
- `packages/cli/src/commands/cloud.ts` -- `tamma cloud status` and `tamma cloud disconnect`

### Files to Modify

- `packages/shared/src/types/index.ts` -- add `cloud` to `TammaConfig`
- `packages/cli/src/config.ts` -- load `TAMMA_CLOUD_API_KEY` env var, add to `loadEnvConfig()`
- `packages/cli/src/commands/start.tsx` -- construct `CompositeEventStore` with cloud transport when configured
- `packages/cli/src/commands/registry.ts` -- register `cloud` command

## Implementation Notes

1. **Fire-and-forget is non-negotiable.** The `record()` method must return in under 5ms. All HTTP I/O happens in background flush cycles. The buffer is an in-memory array with a hard cap. If the cloud is unreachable for an extended period, events are silently dropped (oldest first) rather than consuming unbounded memory.

2. **The CompositeEventStore pattern is additive.** If no cloud config exists, the engine gets just a `LocalEventStore` (or `InMemoryEventStore` for dry-run). If cloud config exists, it gets a `CompositeEventStore([LocalEventStore, CloudSyncTransport])`. The engine code does not change -- it always calls `eventStore.record()`.

3. **Authentication uses the same API key format as the SaaS worker callback.** The `WorkerResultCallback` in `packages/cli/src/worker/result-callback.ts` already uses `TAMMA_API_KEY` and `TAMMA_API_URL`. Cloud sync uses the same API and similar auth, just with a different key name (`TAMMA_CLOUD_API_KEY`) to distinguish user-level cloud keys from worker-level API keys.

4. **Client ID for deduplication.** Each CLI instance generates a stable client ID from `os.hostname() + process.pid` (or a persistent UUID stored in `~/.tamma/client-id`). This allows the cloud API to deduplicate events if the same event is sent twice due to retry.

5. **Graceful shutdown.** When the engine shuts down, `CloudSyncTransport.dispose()` performs a final flush of any buffered events with a 5-second timeout. This ensures events from the last batch are not lost on normal shutdown.

6. **No cloud-side write-back.** Cloud sync is strictly unidirectional (CLI -> Cloud). The cloud cannot send commands back to the CLI. This prevents the cloud from becoming a control plane dependency. Dashboard features like "cancel workflow" only work for SaaS-mode engines that run on Tamma infrastructure.

## Dependencies

- **Story 22.2**: `LocalEventStore` and standalone factory must exist for the composite pattern
- `packages/orchestrator/src/event-stores/` -- `IEventStore` interface
- `packages/cli/src/worker/result-callback.ts` -- pattern reference for API key auth

## Estimated Effort

**10 hours**

- CloudSyncTransport + buffer + flush logic: 3 hours
- Data privacy filter + sanitization: 2 hours
- CompositeEventStore: 1 hour
- Config loading (env var, config file): 1 hour
- `tamma cloud status/disconnect` commands: 1.5 hours
- Unit tests (buffer overflow, flush failure, sanitization): 1.5 hours

## Testing Strategy

- **Unit tests (CloudSyncTransport)**: Test buffering (events accumulate until batch size), flush success (buffer clears, synced count increments), flush failure (events re-buffered), buffer overflow (oldest dropped, dropped count increments), sanitization (sensitive fields stripped), dispose (final flush fires).
- **Unit tests (CompositeEventStore)**: Test delegation to all stores, test secondary store failure does not affect primary, test `getEvents()` reads from primary only.
- **Unit tests (data privacy filter)**: Verify source code, diffs, issue bodies, API keys are removed. Verify issue number, title, event type, cost, duration are preserved. Verify long strings are truncated.
- **Integration test**: Mock HTTP server, run engine with cloud sync, verify events arrive at mock endpoint in batched format with correct auth header.
- **Performance test**: Measure `record()` latency with cloud sync enabled. Assert p99 < 5ms (should be ~0.1ms since it is a pure memory operation).

---

**Last Updated**: 2026-03-28
