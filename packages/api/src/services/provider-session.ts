/**
 * Provider Session Service
 *
 * Story 9-4: Provider Factory API
 *
 * Manages provider instances via session handles (UUID) with TTL-based
 * cleanup for abandoned sessions. Wraps AgentProviderFactory for use
 * by both the TS engine (in-process) and Elsa workflows (via HTTP API).
 */

import { randomUUID } from 'node:crypto';
import type { IAgentProvider, AgentTaskConfig } from '@tamma/providers';
import type { AgentTaskResult } from '@tamma/shared';
import type { IAgentProviderFactory, ProviderChainEntry } from '@tamma/providers';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** Session metadata. */
export interface ProviderSession {
  handle: string;
  provider: string;
  model: string;
  createdAt: number;
  lastUsed: number;
}

/** Result of creating a provider session. */
export interface CreateSessionResult {
  handle: string;
  provider: string;
  model: string;
}

/** Input for creating a provider session. */
export interface CreateSessionInput {
  provider: string;
  model?: string;
  apiKeyRef?: string;
  config?: Record<string, unknown>;
}

// ---------------------------------------------------------------------------
// IProviderSessionService Interface
// ---------------------------------------------------------------------------

export interface IProviderSessionService {
  /** Create a provider session, returning a handle for subsequent calls. */
  create(input: CreateSessionInput): Promise<CreateSessionResult>;

  /** Execute a task on a provider identified by handle. */
  execute(handle: string, config: AgentTaskConfig): Promise<AgentTaskResult>;

  /** Dispose a provider and remove the session. */
  dispose(handle: string): Promise<boolean>;

  /** List active sessions. */
  listSessions(): ProviderSession[];

  /** Cleanup idle sessions older than TTL. */
  cleanup(): Promise<number>;

  /** Dispose all sessions. */
  disposeAll(): Promise<void>;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default session TTL: 30 minutes of inactivity. */
const DEFAULT_SESSION_TTL_MS = 30 * 60 * 1000;

/** Maximum concurrent sessions. */
const MAX_SESSIONS = 100;

// ---------------------------------------------------------------------------
// ProviderSessionService
// ---------------------------------------------------------------------------

export class ProviderSessionService implements IProviderSessionService {
  private sessions = new Map<string, {
    provider: IAgentProvider;
    meta: ProviderSession;
  }>();
  private cleanupTimer: ReturnType<typeof setInterval> | null = null;
  private readonly sessionTtlMs: number;

  constructor(
    private readonly factory: IAgentProviderFactory,
    options?: { sessionTtlMs?: number; autoCleanup?: boolean },
  ) {
    this.sessionTtlMs = options?.sessionTtlMs ?? DEFAULT_SESSION_TTL_MS;

    // Start periodic cleanup if requested
    if (options?.autoCleanup !== false) {
      this.cleanupTimer = setInterval(() => {
        void this.cleanup();
      }, 60_000); // Check every minute
      // Allow the process to exit even if the timer is running
      if (this.cleanupTimer.unref) {
        this.cleanupTimer.unref();
      }
    }
  }

  async create(input: CreateSessionInput): Promise<CreateSessionResult> {
    if (this.sessions.size >= MAX_SESSIONS) {
      throw new Error(`Maximum concurrent sessions (${MAX_SESSIONS}) reached`);
    }

    if (!input.provider || input.provider.length === 0) {
      throw new Error('provider is required');
    }

    const entry: ProviderChainEntry = {
      provider: input.provider,
    };
    if (input.model !== undefined) {
      entry.model = input.model;
    }
    if (input.apiKeyRef !== undefined) {
      entry.apiKeyRef = input.apiKeyRef;
    }
    if (input.config !== undefined) {
      entry.config = input.config;
    }

    const provider = await this.factory.create(entry);
    const handle = randomUUID();
    const now = Date.now();

    const meta: ProviderSession = {
      handle,
      provider: input.provider,
      model: input.model ?? 'default',
      createdAt: now,
      lastUsed: now,
    };

    this.sessions.set(handle, { provider, meta });

    return {
      handle,
      provider: input.provider,
      model: meta.model,
    };
  }

  async execute(handle: string, config: AgentTaskConfig): Promise<AgentTaskResult> {
    const session = this.sessions.get(handle);
    if (!session) {
      throw new Error(`Session not found: ${handle}`);
    }

    session.meta.lastUsed = Date.now();
    return session.provider.executeTask(config);
  }

  async dispose(handle: string): Promise<boolean> {
    const session = this.sessions.get(handle);
    if (!session) return false;

    try {
      await session.provider.dispose();
    } catch {
      // Best-effort dispose
    }

    this.sessions.delete(handle);
    return true;
  }

  listSessions(): ProviderSession[] {
    const result: ProviderSession[] = [];
    for (const { meta } of this.sessions.values()) {
      result.push({ ...meta });
    }
    return result;
  }

  async cleanup(): Promise<number> {
    const now = Date.now();
    const expiredHandles: string[] = [];

    for (const [handle, { meta }] of this.sessions) {
      if (now - meta.lastUsed > this.sessionTtlMs) {
        expiredHandles.push(handle);
      }
    }

    let cleaned = 0;
    for (const handle of expiredHandles) {
      const disposed = await this.dispose(handle);
      if (disposed) cleaned++;
    }

    return cleaned;
  }

  async disposeAll(): Promise<void> {
    if (this.cleanupTimer) {
      clearInterval(this.cleanupTimer);
      this.cleanupTimer = null;
    }

    const handles = [...this.sessions.keys()];
    for (const handle of handles) {
      await this.dispose(handle);
    }
  }
}
