/**
 * Boot-time degrade guard for `startServer`.
 *
 * When a vector store IS configured (env present) but is UNREACHABLE at boot —
 * e.g. the ChromaDB/pgvector host is down or a black hole — `store.initialize()`
 * throws. The sidecar must NOT crash: `startServer` catches the failure, boots
 * with an empty bundle, still binds `/health`, and reports `not_configured` so
 * the C# proxy can render a "degraded" banner instead of a dead port.
 *
 * The reviewer flagged this degrade path as untested. We exercise the REAL
 * `startServer` → `buildIntelligenceBundleFromEnv` path, mocking only the vector
 * store factory so `initialize()` rejects deterministically (no network, no
 * hang).
 */

import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';

// Make the REAL `buildIntelligenceBundleFromEnv` construct a store that fails to
// initialize. env-composition.ts calls `createVectorStore(config).initialize()`.
vi.mock('@tamma/intelligence/vector-store', async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>();
  return {
    ...actual,
    createVectorStore: () => ({
      initialize: () => Promise.reject(new Error('vector store unreachable at boot')),
    }),
  };
});

import { startServer } from '../server.js';

describe('startServer — degrades to not_configured when a configured store is unreachable at boot', () => {
  let warnSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    // A configured store (so buildIntelligenceBundleFromEnv attempts init) with
    // no embedder key (default openai → undefined embedder, irrelevant here).
    vi.stubEnv('CHROMADB_URL', 'http://black-hole.invalid:8000');
    warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });

  afterEach(() => {
    vi.unstubAllEnvs();
    warnSpy.mockRestore();
  });

  it('boots (binds an ephemeral port), serves /health, and reports not_configured', async () => {
    // port 0 → OS picks a free port; no fixed-port collision in CI.
    const app = await startServer({ port: 0, host: '127.0.0.1' });
    try {
      const health = await app.inject({ method: 'GET', url: '/health' });
      expect(health.json()).toEqual({ status: 'ok' });

      const status = await app.inject({ method: 'GET', url: '/kb/vector-db/status' });
      expect(status.json().status).toBe('not_configured');

      // The degrade path logged its warning rather than crashing.
      expect(warnSpy).toHaveBeenCalled();
    } finally {
      await app.close();
    }
  });
});
