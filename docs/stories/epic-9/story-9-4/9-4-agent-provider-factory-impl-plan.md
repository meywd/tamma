# Story 9-4: Provider Factory API — Implementation Plan

## Overview

Expose the existing `AgentProviderFactory` functionality via Fastify API endpoints with session-based lifecycle management. Elsa workflows create providers via `POST /providers/create`, execute tasks via `POST /providers/:handle/execute`, and dispose via `DELETE /providers/:handle`. The TS engine continues using the factory in-process. A `ProviderSessionService` wraps the factory with UUID-keyed session tracking and TTL-based cleanup.

---

## Step-by-Step Implementation Tasks

### Task 1: Create ProviderSessionService (4 hours)

**File to create**: `packages/api/src/services/provider-session.ts`

```typescript
import { randomUUID } from 'node:crypto';
import type { IAgentProviderFactory, ProviderChainEntry } from '@tamma/providers';
import type { IAgentProvider, AgentTaskConfig, AgentTaskResult } from '@tamma/providers';

/** Tracked provider session. */
export interface ProviderSession {
  handle: string;
  provider: IAgentProvider;
  providerName: string;
  model: string;
  createdAt: number;
  lastUsedAt: number;
}

/** Options for ProviderSessionService. */
export interface ProviderSessionServiceOptions {
  factory: IAgentProviderFactory;
  /** Maximum idle time before session cleanup (ms). Default: 30 minutes. */
  sessionTtlMs?: number;
  /** Cleanup sweep interval (ms). Default: 60 seconds. */
  cleanupIntervalMs?: number;
  /** Maximum concurrent sessions. Default: 100. */
  maxSessions?: number;
  logger?: { warn(msg: string, ctx?: Record<string, unknown>): void; info?(msg: string, ctx?: Record<string, unknown>): void };
}

/** Interface for the provider session service. */
export interface IProviderSessionService {
  /** Create a provider session, returning a handle UUID. */
  create(entry: ProviderChainEntry): Promise<{ handle: string; provider: string; model: string }>;
  /** Execute a task on an active session. */
  execute(handle: string, config: AgentTaskConfig): Promise<AgentTaskResult>;
  /** Dispose and remove a session. */
  dispose(handle: string): Promise<boolean>;
  /** List active sessions (for diagnostics). */
  listSessions(): ProviderSession[];
  /** Shut down: dispose all sessions and stop cleanup timer. */
  shutdown(): Promise<void>;
}

export class ProviderSessionService implements IProviderSessionService {
  private readonly sessions = new Map<string, ProviderSession>();
  private readonly factory: IAgentProviderFactory;
  private readonly sessionTtlMs: number;
  private readonly maxSessions: number;
  private readonly logger: ProviderSessionServiceOptions['logger'];
  private cleanupTimer: ReturnType<typeof setInterval> | null = null;

  constructor(options: ProviderSessionServiceOptions) {
    this.factory = options.factory;
    this.sessionTtlMs = options.sessionTtlMs ?? 30 * 60 * 1000;
    this.maxSessions = options.maxSessions ?? 100;
    this.logger = options.logger;

    // Start periodic cleanup
    const interval = options.cleanupIntervalMs ?? 60_000;
    this.cleanupTimer = setInterval(() => { void this._cleanup(); }, interval);
    this.cleanupTimer.unref(); // Don't prevent process exit
  }

  async create(entry: ProviderChainEntry): Promise<{ handle: string; provider: string; model: string }> {
    if (this.sessions.size >= this.maxSessions) {
      throw new Error(`Maximum sessions (${this.maxSessions}) reached`);
    }

    const provider = await this.factory.create(entry);
    const handle = randomUUID();
    const now = Date.now();

    this.sessions.set(handle, {
      handle,
      provider,
      providerName: entry.provider,
      model: entry.model ?? 'default',
      createdAt: now,
      lastUsedAt: now,
    });

    this.logger?.info?.('Provider session created', { handle, provider: entry.provider });
    return { handle, provider: entry.provider, model: entry.model ?? 'default' };
  }

  async execute(handle: string, config: AgentTaskConfig): Promise<AgentTaskResult> {
    const session = this.sessions.get(handle);
    if (!session) {
      throw new Error(`Session not found: ${handle}`);
    }

    session.lastUsedAt = Date.now();
    return session.provider.executeTask(config);
  }

  async dispose(handle: string): Promise<boolean> {
    const session = this.sessions.get(handle);
    if (!session) return false;

    try {
      await session.provider.dispose();
    } catch (err) {
      this.logger?.warn('Provider disposal error', {
        handle,
        error: err instanceof Error ? err.message : String(err),
      });
    }

    this.sessions.delete(handle);
    return true;
  }

  listSessions(): ProviderSession[] {
    return [...this.sessions.values()];
  }

  async shutdown(): Promise<void> {
    if (this.cleanupTimer !== null) {
      clearInterval(this.cleanupTimer);
      this.cleanupTimer = null;
    }
    const handles = [...this.sessions.keys()];
    for (const handle of handles) {
      await this.dispose(handle);
    }
  }

  private async _cleanup(): Promise<void> {
    const now = Date.now();
    const expired: string[] = [];
    for (const [handle, session] of this.sessions) {
      if (now - session.lastUsedAt > this.sessionTtlMs) {
        expired.push(handle);
      }
    }
    for (const handle of expired) {
      this.logger?.info?.('Cleaning up expired session', { handle });
      await this.dispose(handle);
    }
  }
}
```

---

### Task 2: Implement Fastify Routes (3 hours)

**File to create**: `packages/api/src/routes/settings/providers-factory-routes.ts`

```typescript
import type { FastifyInstance } from 'fastify';
import type { IProviderSessionService } from '../../services/provider-session.js';
import type { ProviderChainEntry, AgentTaskConfig } from '@tamma/providers';

export function registerProviderFactoryRoutes(app: FastifyInstance, sessionService: IProviderSessionService): void {
  // POST /api/v1/providers/create
  app.post('/providers/create', {
    schema: {
      body: {
        type: 'object',
        required: ['provider'],
        properties: {
          provider: { type: 'string' },
          model: { type: 'string' },
          apiKeyRef: { type: 'string' },
          config: { type: 'object' },
        },
      },
      response: {
        200: {
          type: 'object',
          properties: {
            handle: { type: 'string', format: 'uuid' },
            provider: { type: 'string' },
            model: { type: 'string' },
          },
        },
      },
    },
  }, async (request, reply) => {
    const entry = request.body as ProviderChainEntry;
    const result = await sessionService.create(entry);
    return reply.send(result);
  });

  // POST /api/v1/providers/:handle/execute
  app.post('/providers/:handle/execute', {
    schema: {
      params: { type: 'object', properties: { handle: { type: 'string', format: 'uuid' } } },
      body: { type: 'object', required: ['prompt'], properties: { prompt: { type: 'string' } } },
    },
  }, async (request, reply) => {
    const { handle } = request.params as { handle: string };
    const config = request.body as AgentTaskConfig;
    const result = await sessionService.execute(handle, config);
    return reply.send(result);
  });

  // DELETE /api/v1/providers/:handle
  app.delete('/providers/:handle', {
    schema: {
      params: { type: 'object', properties: { handle: { type: 'string', format: 'uuid' } } },
    },
  }, async (request, reply) => {
    const { handle } = request.params as { handle: string };
    const disposed = await sessionService.dispose(handle);
    if (!disposed) {
      return reply.status(404).send({ error: 'Session not found' });
    }
    return reply.send({ disposed: true });
  });

  // GET /api/v1/providers/sessions — list active sessions (admin diagnostics)
  app.get('/providers/sessions', async (_request, reply) => {
    const sessions = sessionService.listSessions().map((s) => ({
      handle: s.handle,
      providerName: s.providerName,
      model: s.model,
      createdAt: new Date(s.createdAt).toISOString(),
      lastUsedAt: new Date(s.lastUsedAt).toISOString(),
    }));
    return reply.send({ sessions, count: sessions.length });
  });
}
```

---

### Task 3: Wire into Settings Index (1 hour)

**File to modify**: `packages/api/src/routes/settings/index.ts`

```typescript
import { registerProviderFactoryRoutes } from './providers-factory-routes.js';

export interface SettingsServices {
  // ... existing
  sessionService: IProviderSessionService;
}

// In registerSettingsRoutes:
registerProviderFactoryRoutes(instance, svc.sessionService);
```

---

### Task 4: Tests (4 hours)

**File to create**: `packages/api/src/services/provider-session.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 1 | `create()` returns valid handle, provider, model | UUID handle, matching names |
| 2 | `create()` rejects when max sessions reached | Error thrown |
| 3 | `execute()` delegates to provider.executeTask | Result matches |
| 4 | `execute()` updates lastUsedAt | Timestamp advanced |
| 5 | `execute()` for unknown handle throws | Error message |
| 6 | `dispose()` calls provider.dispose() | Provider cleaned up |
| 7 | `dispose()` removes from sessions map | Subsequent execute fails |
| 8 | `dispose()` for unknown handle returns false | No error |
| 9 | `dispose()` handles provider.dispose() throwing | Logged, still removed |
| 10 | `listSessions()` returns active sessions | Correct count |
| 11 | TTL cleanup removes expired sessions | Session disposed after TTL |
| 12 | `shutdown()` disposes all sessions | Sessions map empty |
| 13 | `shutdown()` clears cleanup timer | No more cleanup runs |

**File to create**: `packages/api/src/routes/settings/__tests__/providers-factory-routes.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 14 | POST /providers/create returns 200 with handle | UUID in response |
| 15 | POST /providers/:handle/execute returns task result | Success shape |
| 16 | DELETE /providers/:handle returns disposed=true | 200 OK |
| 17 | DELETE /providers/:handle for unknown returns 404 | Error message |
| 18 | GET /providers/sessions lists active | Correct count |

**Total tests**: ~18

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/api/src/services/provider-session.ts` | Session management wrapping factory |
| 2 | `packages/api/src/routes/settings/providers-factory-routes.ts` | API routes |
| 3 | `packages/api/src/services/provider-session.test.ts` | Service tests |
| 4 | `packages/api/src/routes/settings/__tests__/providers-factory-routes.test.ts` | Route tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/routes/settings/index.ts` | Register factory routes, add sessionService to SettingsServices |
| 2 | `packages/api/src/routes/settings/providers-routes.ts` | May need to coexist or merge with factory routes |

---

## Dependencies

- None (factory is self-contained; no account scoping needed)
- `packages/providers/src/agent-provider-factory.ts` (used as-is, no changes)

## Migration from Existing Code

1. The existing `AgentProviderFactory` in `packages/providers/src/agent-provider-factory.ts` is used as-is inside `ProviderSessionService`.
2. No changes to the factory class -- `ProviderSessionService` wraps it with session lifecycle.
3. Elsa's `CallLlmActivity.cs` transitions from direct HTTP calls to LLM APIs to the three-step create/execute/dispose pattern via `ProviderSessionService` (wired in Story 9-11).
4. The TS engine continues to use `AgentProviderFactory.create()` directly for in-process usage.

---

## Estimated Effort

| Task | Hours |
|------|-------|
| ProviderSessionService (create, execute, dispose, TTL, shutdown) | 4 |
| Fastify routes (4 endpoints) | 3 |
| Settings index wiring | 1 |
| Tests (18 tests) | 4 |
| **Total** | **12 hours** |
