/**
 * Health Store Interface + InMemory Implementation
 *
 * Story 9-3: Health Tracker Service + API
 *
 * Persists circuit breaker state so that when a provider is marked unhealthy
 * by one caller (TS engine or Elsa workflow), all callers skip it.
 * The existing ProviderHealthTracker class in @tamma/providers handles
 * in-process circuit breaking; this store provides persistence and cross-caller
 * state sharing.
 */

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** Persistent health status for a provider+model key. */
export interface HealthRecord {
  key: string;
  circuitOpen: boolean;
  circuitOpenUntil: string | null;
  failureCount: number;
  lastFailureAt: string | null;
  lastSuccessAt: string | null;
  halfOpenInProgress: boolean;
  updatedAt: string;
}

/** Summary health status returned by list operations. */
export interface HealthStatusSummary {
  healthy: boolean;
  failures: number;
  circuitOpen: boolean;
  circuitOpenUntil: string | null;
  halfOpen: boolean;
}

/** Input for recording a failure. */
export interface RecordFailureInput {
  error?: string;
  retryable?: boolean;
}

/** Result of recording a failure. */
export interface RecordFailureResult {
  circuitOpen: boolean;
  failures: number;
}

// ---------------------------------------------------------------------------
// IHealthStore Interface
// ---------------------------------------------------------------------------

export interface IHealthStore {
  /** Get health status for all tracked provider+model keys. */
  getAll(): Promise<Record<string, HealthStatusSummary>>;

  /** Get health status for a specific key. */
  get(key: string): Promise<HealthStatusSummary | null>;

  /** Record a failure for a key. May open the circuit. */
  recordFailure(key: string, input?: RecordFailureInput): Promise<RecordFailureResult>;

  /** Record a success for a key. Closes the circuit. */
  recordSuccess(key: string): Promise<{ circuitOpen: false; failures: 0 }>;

  /** Reset (delete) health state for a key. Admin operation. */
  reset(key: string): Promise<boolean>;

  /**
   * Sync a circuit state change from the in-process ProviderHealthTracker
   * to the persistent store. Called via onCircuitChange callback.
   */
  syncCircuitChange(key: string, state: 'open' | 'half-open' | 'closed', metadata?: Record<string, unknown>): Promise<void>;
}

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
// InMemoryHealthStore
// ---------------------------------------------------------------------------

/** Default circuit breaker configuration. */
const DEFAULT_FAILURE_THRESHOLD = 5;
const DEFAULT_CIRCUIT_OPEN_DURATION_MS = 300_000; // 5 minutes

export class InMemoryHealthStore implements IHealthStore {
  private records = new Map<string, HealthRecord>();
  private readonly failureThreshold: number;
  private readonly circuitOpenDurationMs: number;

  constructor(options?: {
    failureThreshold?: number;
    circuitOpenDurationMs?: number;
  }) {
    this.failureThreshold = options?.failureThreshold ?? DEFAULT_FAILURE_THRESHOLD;
    this.circuitOpenDurationMs = options?.circuitOpenDurationMs ?? DEFAULT_CIRCUIT_OPEN_DURATION_MS;
  }

  async getAll(): Promise<Record<string, HealthStatusSummary>> {
    const result: Record<string, HealthStatusSummary> = {};
    const now = new Date();

    for (const [key, record] of this.records) {
      const isOpen = record.circuitOpen && record.circuitOpenUntil !== null
        && new Date(record.circuitOpenUntil).getTime() > now.getTime();

      result[key] = {
        healthy: !isOpen,
        failures: record.failureCount,
        circuitOpen: isOpen,
        circuitOpenUntil: isOpen ? record.circuitOpenUntil : null,
        halfOpen: record.halfOpenInProgress,
      };
    }

    return result;
  }

  async get(key: string): Promise<HealthStatusSummary | null> {
    validateKey(key);
    const record = this.records.get(key);
    if (!record) return null;

    const now = new Date();
    const isOpen = record.circuitOpen && record.circuitOpenUntil !== null
      && new Date(record.circuitOpenUntil).getTime() > now.getTime();

    return {
      healthy: !isOpen,
      failures: record.failureCount,
      circuitOpen: isOpen,
      circuitOpenUntil: isOpen ? record.circuitOpenUntil : null,
      halfOpen: record.halfOpenInProgress,
    };
  }

  async recordFailure(key: string, input?: RecordFailureInput): Promise<RecordFailureResult> {
    validateKey(key);

    // Non-retryable errors do not trip the circuit breaker
    if (input?.retryable === false) {
      const existing = this.records.get(key);
      return {
        circuitOpen: existing?.circuitOpen ?? false,
        failures: existing?.failureCount ?? 0,
      };
    }

    const now = new Date();
    const nowIso = now.toISOString();
    let record = this.records.get(key);

    if (!record) {
      record = {
        key,
        circuitOpen: false,
        circuitOpenUntil: null,
        failureCount: 0,
        lastFailureAt: null,
        lastSuccessAt: null,
        halfOpenInProgress: false,
        updatedAt: nowIso,
      };
      this.records.set(key, record);
    }

    record.failureCount++;
    record.lastFailureAt = nowIso;
    record.updatedAt = nowIso;

    // If half-open probe failed, re-open immediately
    if (record.halfOpenInProgress) {
      record.halfOpenInProgress = false;
      record.circuitOpen = true;
      record.circuitOpenUntil = new Date(now.getTime() + this.circuitOpenDurationMs).toISOString();
    }

    // Check threshold
    if (!record.circuitOpen && record.failureCount >= this.failureThreshold) {
      record.circuitOpen = true;
      record.circuitOpenUntil = new Date(now.getTime() + this.circuitOpenDurationMs).toISOString();
    }

    return {
      circuitOpen: record.circuitOpen,
      failures: record.failureCount,
    };
  }

  async recordSuccess(key: string): Promise<{ circuitOpen: false; failures: 0 }> {
    validateKey(key);

    const nowIso = new Date().toISOString();
    const record = this.records.get(key);

    if (record) {
      record.circuitOpen = false;
      record.circuitOpenUntil = null;
      record.failureCount = 0;
      record.halfOpenInProgress = false;
      record.lastSuccessAt = nowIso;
      record.updatedAt = nowIso;
    }

    return { circuitOpen: false, failures: 0 };
  }

  async reset(key: string): Promise<boolean> {
    validateKey(key);
    return this.records.delete(key);
  }

  async syncCircuitChange(key: string, state: 'open' | 'half-open' | 'closed', _metadata?: Record<string, unknown>): Promise<void> {
    validateKey(key);

    if (state === 'closed') {
      await this.recordSuccess(key);
      return;
    }

    const now = new Date();
    const nowIso = now.toISOString();
    let record = this.records.get(key);

    if (!record) {
      record = {
        key,
        circuitOpen: false,
        circuitOpenUntil: null,
        failureCount: 0,
        lastFailureAt: null,
        lastSuccessAt: null,
        halfOpenInProgress: false,
        updatedAt: nowIso,
      };
      this.records.set(key, record);
    }

    if (state === 'open') {
      record.circuitOpen = true;
      record.circuitOpenUntil = new Date(now.getTime() + this.circuitOpenDurationMs).toISOString();
      record.halfOpenInProgress = false;
      record.updatedAt = nowIso;
    } else {
      // half-open: circuit is still open (awaiting probe), set circuitOpenUntil
      // so get() correctly reports circuitOpen=true
      record.circuitOpen = true;
      record.circuitOpenUntil = new Date(now.getTime() + this.circuitOpenDurationMs).toISOString();
      record.halfOpenInProgress = true;
      record.updatedAt = nowIso;
    }
  }
}
