/**
 * PostgreSQL-backed Health Store
 *
 * Story 9-3: Health Tracker Service + API
 *
 * Persists circuit breaker state to the provider_health table
 * created in migration 015.
 */

import type pg from 'pg';

import type {
  IHealthStore,
  HealthStatusSummary,
  RecordFailureInput,
  RecordFailureResult,
} from './health-store.js';

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

const KEY_PATTERN = /^[a-zA-Z0-9._\-:/]+$/;
const MAX_KEY_LENGTH = 256;

function validateKey(key: string): void {
  if (key.length === 0) {
    throw new Error('Health key must not be empty');
  }
  if (key.length > MAX_KEY_LENGTH) {
    throw new Error(`Health key too long (max ${MAX_KEY_LENGTH})`);
  }
  if (!KEY_PATTERN.test(key)) {
    throw new Error(`Health key contains invalid characters: ${key.slice(0, 50)}`);
  }
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const DEFAULT_FAILURE_THRESHOLD = 5;
const DEFAULT_CIRCUIT_OPEN_DURATION_MS = 300_000;

// ---------------------------------------------------------------------------
// PgHealthStore
// ---------------------------------------------------------------------------

export class PgHealthStore implements IHealthStore {
  private readonly failureThreshold: number;
  private readonly circuitOpenDurationMs: number;

  constructor(
    private readonly pool: pg.Pool,
    options?: {
      failureThreshold?: number;
      circuitOpenDurationMs?: number;
    },
  ) {
    this.failureThreshold = options?.failureThreshold ?? DEFAULT_FAILURE_THRESHOLD;
    this.circuitOpenDurationMs = options?.circuitOpenDurationMs ?? DEFAULT_CIRCUIT_OPEN_DURATION_MS;
  }

  async getAll(): Promise<Record<string, HealthStatusSummary>> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM provider_health',
    );

    const output: Record<string, HealthStatusSummary> = {};
    const now = new Date();

    for (const row of result.rows) {
      const key = String(row['key']);
      const circuitOpen = Boolean(row['circuit_open']);
      const circuitOpenUntil = row['circuit_open_until'] ? String(row['circuit_open_until']) : null;
      const isOpen = circuitOpen && circuitOpenUntil !== null
        && new Date(circuitOpenUntil).getTime() > now.getTime();

      output[key] = {
        healthy: !isOpen,
        failures: Number(row['failure_count'] ?? 0),
        circuitOpen: isOpen,
        circuitOpenUntil: isOpen ? circuitOpenUntil : null,
        halfOpen: Boolean(row['half_open_in_progress']),
      };
    }

    return output;
  }

  async get(key: string): Promise<HealthStatusSummary | null> {
    validateKey(key);

    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM provider_health WHERE key = $1',
      [key],
    );

    if (result.rows.length === 0) return null;

    const row = result.rows[0]!;
    const now = new Date();
    const circuitOpen = Boolean(row['circuit_open']);
    const circuitOpenUntil = row['circuit_open_until'] ? String(row['circuit_open_until']) : null;
    const isOpen = circuitOpen && circuitOpenUntil !== null
      && new Date(circuitOpenUntil).getTime() > now.getTime();

    return {
      healthy: !isOpen,
      failures: Number(row['failure_count'] ?? 0),
      circuitOpen: isOpen,
      circuitOpenUntil: isOpen ? circuitOpenUntil : null,
      halfOpen: Boolean(row['half_open_in_progress']),
    };
  }

  async recordFailure(key: string, input?: RecordFailureInput): Promise<RecordFailureResult> {
    validateKey(key);

    // Non-retryable errors do not trip the circuit breaker
    if (input?.retryable === false) {
      const existing = await this.get(key);
      return {
        circuitOpen: existing?.circuitOpen ?? false,
        failures: existing?.failures ?? 0,
      };
    }

    const circuitOpenUntil = new Date(Date.now() + this.circuitOpenDurationMs).toISOString();

    // Upsert: increment failure_count, maybe open circuit
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO provider_health (key, failure_count, last_failure_at, updated_at)
       VALUES ($1, 1, NOW(), NOW())
       ON CONFLICT (key) DO UPDATE SET
         failure_count = provider_health.failure_count + 1,
         last_failure_at = NOW(),
         half_open_in_progress = false,
         circuit_open = CASE
           WHEN provider_health.half_open_in_progress THEN true
           WHEN provider_health.failure_count + 1 >= $2 THEN true
           ELSE provider_health.circuit_open
         END,
         circuit_open_until = CASE
           WHEN provider_health.half_open_in_progress THEN $3::timestamptz
           WHEN provider_health.failure_count + 1 >= $2 THEN $3::timestamptz
           ELSE provider_health.circuit_open_until
         END,
         updated_at = NOW()
       RETURNING circuit_open, failure_count`,
      [key, this.failureThreshold, circuitOpenUntil],
    );

    const row = result.rows[0]!;
    return {
      circuitOpen: Boolean(row['circuit_open']),
      failures: Number(row['failure_count']),
    };
  }

  async recordSuccess(key: string): Promise<{ circuitOpen: false; failures: 0 }> {
    validateKey(key);

    await this.pool.query(
      `INSERT INTO provider_health (key, circuit_open, failure_count, last_success_at, half_open_in_progress, updated_at)
       VALUES ($1, false, 0, NOW(), false, NOW())
       ON CONFLICT (key) DO UPDATE SET
         circuit_open = false,
         circuit_open_until = NULL,
         failure_count = 0,
         half_open_in_progress = false,
         last_success_at = NOW(),
         updated_at = NOW()`,
      [key],
    );

    return { circuitOpen: false, failures: 0 };
  }

  async reset(key: string): Promise<boolean> {
    validateKey(key);

    const result = await this.pool.query(
      'DELETE FROM provider_health WHERE key = $1',
      [key],
    );

    return (result.rowCount ?? 0) > 0;
  }

  async syncCircuitChange(key: string, state: 'open' | 'half-open' | 'closed', _metadata?: Record<string, unknown>): Promise<void> {
    validateKey(key);

    if (state === 'closed') {
      await this.recordSuccess(key);
    } else if (state === 'open') {
      const openUntil = new Date(Date.now() + this.circuitOpenDurationMs).toISOString();
      await this.pool.query(
        `INSERT INTO provider_health (key, circuit_open, circuit_open_until, half_open_in_progress, updated_at)
         VALUES ($1, true, $2, false, NOW())
         ON CONFLICT (key) DO UPDATE SET
           circuit_open = true,
           circuit_open_until = $2,
           half_open_in_progress = false,
           updated_at = NOW()`,
        [key, openUntil],
      );
    } else {
      // half-open
      await this.pool.query(
        `INSERT INTO provider_health (key, circuit_open, half_open_in_progress, updated_at)
         VALUES ($1, true, true, NOW())
         ON CONFLICT (key) DO UPDATE SET
           half_open_in_progress = true,
           updated_at = NOW()`,
        [key],
      );
    }
  }
}
