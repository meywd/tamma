/**
 * API Key generation, hashing, and prefix extraction utilities.
 *
 * Keys follow the format: tamma_sk_ + 32 random bytes encoded as base64url.
 */

import { randomBytes, scryptSync } from 'node:crypto';

/** Prefix prepended to all generated API keys. */
const API_KEY_PREFIX = 'tamma_sk_';

/** Number of random bytes used for key generation. */
const KEY_BYTES = 32;

/** Number of characters from the full key to use as a display prefix. */
const DISPLAY_PREFIX_LENGTH = 12;

/**
 * Fixed salt for API key hashing. Uses scrypt (memory-hard KDF) to satisfy
 * CodeQL's password-hashing requirements while keeping lookups deterministic.
 * The security comes from the 256-bit random API key itself.
 */
const HASH_SALT = 'tamma-api-key-hash-v1';

/** scrypt cost parameter (N=16384, r=8, p=1 — OWASP minimum recommendation). */
const SCRYPT_COST = 16384;
const SCRYPT_BLOCK_SIZE = 8;
const SCRYPT_PARALLELIZATION = 1;
const SCRYPT_KEY_LENGTH = 32;

/**
 * Generate a new API key.
 *
 * Format: `tamma_sk_<32 random bytes base64url>`
 * Example: `tamma_sk_a1b2c3d4e5f6...`
 */
export function generateApiKey(): string {
  const random = randomBytes(KEY_BYTES).toString('base64url');
  return `${API_KEY_PREFIX}${random}`;
}

/**
 * Compute the scrypt hash (hex) of an API key.
 * Uses memory-hard KDF for resistance against brute-force attacks.
 * Used for storage and lookup (never store the raw key).
 */
export function hashApiKey(key: string): string {
  const derived = scryptSync(key, HASH_SALT, SCRYPT_KEY_LENGTH, {
    N: SCRYPT_COST,
    r: SCRYPT_BLOCK_SIZE,
    p: SCRYPT_PARALLELIZATION,
  });
  return derived.toString('hex');
}

/**
 * Extract the first 12 characters of the key for safe display.
 * Example: `tamma_sk_a1b2` (enough to identify, not enough to use).
 */
export function getApiKeyPrefix(key: string): string {
  return key.slice(0, DISPLAY_PREFIX_LENGTH);
}
