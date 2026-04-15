/**
 * Login Lockout Service (Story 18-2)
 *
 * Tracks failed login attempts per email address and locks accounts
 * after too many failures within a time window.
 *
 * Config:
 *   - 5 failed attempts within 15 minutes → 30-minute lockout
 *   - Lockout is per-email (not per-user-ID) to prevent enumeration
 *   - Successful login resets the counter
 */

/** Lockout configuration. */
export interface LockoutConfig {
  /** Maximum failed attempts before lockout. Default: 5 */
  maxAttempts: number;
  /** Window for counting failed attempts (ms). Default: 15 minutes */
  windowMs: number;
  /** Duration of lockout (ms). Default: 30 minutes */
  lockoutMs: number;
}

/** Default lockout configuration. */
const DEFAULT_CONFIG: LockoutConfig = {
  maxAttempts: 5,
  windowMs: 15 * 60 * 1000,    // 15 minutes
  lockoutMs: 30 * 60 * 1000,   // 30 minutes
};

interface AttemptRecord {
  timestamps: number[];
  lockedUntil: number | null;
}

/** Interface for login lockout service. */
export interface ILoginLockoutService {
  /** Record a failed login attempt. Returns true if the account is now locked. */
  recordFailedAttempt(email: string): boolean;
  /** Check if an email is currently locked out. */
  isLocked(email: string): boolean;
  /** Reset attempts after a successful login. */
  resetAttempts(email: string): void;
  /** Get remaining lockout time in seconds (0 if not locked). */
  getRemainingLockoutSeconds(email: string): number;
}

/**
 * In-memory login lockout service.
 *
 * For production, this could be replaced with a Redis-backed implementation
 * for shared state across multiple API instances.
 */
export class LoginLockoutService implements ILoginLockoutService {
  private attempts = new Map<string, AttemptRecord>();
  private readonly config: LockoutConfig;

  constructor(config?: Partial<LockoutConfig>) {
    this.config = { ...DEFAULT_CONFIG, ...config };
  }

  recordFailedAttempt(email: string): boolean {
    const normalized = email.toLowerCase().trim();
    const now = Date.now();

    let record = this.attempts.get(normalized);
    if (!record) {
      record = { timestamps: [], lockedUntil: null };
      this.attempts.set(normalized, record);
    }

    // If currently locked, don't add more attempts
    if (record.lockedUntil !== null && now < record.lockedUntil) {
      return true;
    }

    // Clear expired lockout
    if (record.lockedUntil !== null && now >= record.lockedUntil) {
      record.lockedUntil = null;
      record.timestamps = [];
    }

    // Add current attempt and prune old ones outside the window
    record.timestamps.push(now);
    const windowStart = now - this.config.windowMs;
    record.timestamps = record.timestamps.filter((t) => t >= windowStart);

    // Check if we've exceeded the threshold
    if (record.timestamps.length >= this.config.maxAttempts) {
      record.lockedUntil = now + this.config.lockoutMs;
      return true;
    }

    return false;
  }

  isLocked(email: string): boolean {
    const normalized = email.toLowerCase().trim();
    const record = this.attempts.get(normalized);
    if (!record || record.lockedUntil === null) return false;

    if (Date.now() >= record.lockedUntil) {
      // Lockout expired — clean up
      record.lockedUntil = null;
      record.timestamps = [];
      return false;
    }

    return true;
  }

  resetAttempts(email: string): void {
    const normalized = email.toLowerCase().trim();
    this.attempts.delete(normalized);
  }

  getRemainingLockoutSeconds(email: string): number {
    const normalized = email.toLowerCase().trim();
    const record = this.attempts.get(normalized);
    if (!record || record.lockedUntil === null) return 0;

    const remaining = record.lockedUntil - Date.now();
    return remaining > 0 ? Math.ceil(remaining / 1000) : 0;
  }
}
