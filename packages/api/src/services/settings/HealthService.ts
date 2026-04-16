/**
 * Health Service
 *
 * Wraps ProviderHealthTracker to expose health status via API.
 * When an IHealthStore is available, delegates reads to the persistent store
 * so that circuit breaker state is shared across callers (TS engine + Elsa).
 */

import type { IProviderHealthTracker, HealthStatusEntry } from '@tamma/providers';
import type { IHealthStore } from '../health-store.js';

export class HealthService {
  private tracker: IProviderHealthTracker | null;
  private store: IHealthStore | null;

  constructor(tracker?: IProviderHealthTracker, store?: IHealthStore) {
    this.tracker = tracker ?? null;
    this.store = store ?? null;
  }

  async getStatus(): Promise<Record<string, HealthStatusEntry>> {
    // Prefer persistent store when available for cross-caller consistency
    if (this.store) {
      const storeStatus = await this.store.getAll();
      // Map HealthStatusSummary -> HealthStatusEntry (compatible shape)
      const result: Record<string, HealthStatusEntry> = {};
      for (const [key, status] of Object.entries(storeStatus)) {
        result[key] = {
          healthy: status.healthy,
          failures: status.failures,
          circuitOpen: status.circuitOpen,
        };
      }
      return result;
    }

    if (!this.tracker) {
      return {};
    }
    return this.tracker.getStatus();
  }

  setTracker(tracker: IProviderHealthTracker): void {
    this.tracker = tracker;
  }

  setStore(store: IHealthStore): void {
    this.store = store;
  }
}
