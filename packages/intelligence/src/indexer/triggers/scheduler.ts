/**
 * Scheduler Trigger
 *
 * Simple setInterval-based scheduler for periodic re-indexing.
 * No external cron dependency needed for MVP.
 */

/**
 * Scheduler configuration
 */
export interface SchedulerConfig {
  /** Interval between re-index runs in milliseconds (default: 30 minutes) */
  intervalMs?: number;
  /** Whether to run immediately on start (default: false) */
  runImmediately?: boolean;
}

/**
 * Callback type for scheduler trigger
 */
export type SchedulerCallback = () => void | Promise<void>;

/**
 * Simple interval-based scheduler for triggering re-indexing
 */
export class Scheduler {
  private intervalMs: number;
  private runImmediately: boolean;
  private intervalId: ReturnType<typeof setInterval> | null = null;
  private callback: SchedulerCallback | null = null;
  private running = false;
  private lastRunAt: Date | null = null;

  constructor(config: SchedulerConfig = {}) {
    this.intervalMs = config.intervalMs ?? 30 * 60 * 1000; // Default: 30 minutes
    this.runImmediately = config.runImmediately ?? false;
  }

  /**
   * Start the scheduler
   *
   * @param callback - Function to call at each interval
   */
  start(callback: SchedulerCallback): void {
    if (this.running) {
      return;
    }

    this.callback = callback;
    this.running = true;

    // Run immediately if configured
    if (this.runImmediately) {
      this.executeCallback();
    }

    this.intervalId = setInterval(() => {
      this.executeCallback();
    }, this.intervalMs);
  }

  /**
   * Stop the scheduler
   */
  stop(): void {
    this.running = false;

    if (this.intervalId) {
      clearInterval(this.intervalId);
      this.intervalId = null;
    }

    this.callback = null;
  }

  /**
   * Check if the scheduler is running
   */
  isRunning(): boolean {
    return this.running;
  }

  /**
   * Get the configured interval in milliseconds
   */
  getIntervalMs(): number {
    return this.intervalMs;
  }

  /**
   * Update the interval (restarts if running)
   *
   * @param intervalMs - New interval in milliseconds
   */
  setIntervalMs(intervalMs: number): void {
    if (intervalMs <= 0) {
      throw new Error('Interval must be positive');
    }

    this.intervalMs = intervalMs;

    // Restart with new interval if running
    if (this.running && this.callback) {
      const cb = this.callback;
      this.stop();
      this.start(cb);
    }
  }

  /**
   * Get the last time the callback was executed
   */
  getLastRunAt(): Date | null {
    return this.lastRunAt;
  }

  /**
   * Execute the callback, tracking the run time
   */
  private executeCallback(): void {
    if (!this.callback || !this.running) {
      return;
    }

    this.lastRunAt = new Date();

    try {
      // Handle both sync and async callbacks
      const result = this.callback();
      if (result && typeof result === 'object' && 'catch' in result) {
        (result as Promise<void>).catch((error: unknown) => {
          console.warn('[Scheduler] Async callback error:', error instanceof Error ? error.message : String(error));
        });
      }
    } catch (error) {
      console.warn('[Scheduler] Sync callback error:', error instanceof Error ? error.message : String(error));
    }
  }
}

/**
 * Create a scheduler instance
 *
 * @param config - Scheduler configuration
 * @returns Scheduler instance
 */
export function createScheduler(config?: SchedulerConfig): Scheduler {
  return new Scheduler(config);
}

/**
 * Parse a simple interval string to milliseconds
 * Supports: "30m", "1h", "2h30m", "1d", "60s"
 *
 * @param interval - Interval string
 * @returns Milliseconds
 */
export function parseInterval(interval: string): number {
  const pattern = /^(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$/;
  const match = interval.trim().match(pattern);

  if (!match || match.slice(1).every((v) => v === undefined)) {
    throw new Error(`Invalid interval format: "${interval}". Use format like "30m", "1h", "2h30m", "1d".`);
  }

  const days = parseInt(match[1] || '0', 10);
  const hours = parseInt(match[2] || '0', 10);
  const minutes = parseInt(match[3] || '0', 10);
  const seconds = parseInt(match[4] || '0', 10);

  const ms =
    days * 24 * 60 * 60 * 1000 +
    hours * 60 * 60 * 1000 +
    minutes * 60 * 1000 +
    seconds * 1000;

  if (ms <= 0) {
    throw new Error('Interval must be positive');
  }

  return ms;
}
