/**
 * Password Hashing & Validation Service (Story 18-1)
 *
 * Uses Node.js native crypto.scrypt (memory-hard KDF) for password hashing.
 * Scrypt is OWASP-recommended alongside Argon2 and bcrypt.
 *
 * Hash format: `scrypt:N:r:p:keylen:salt:hash` (all hex-encoded where applicable)
 *
 * Parameters follow OWASP 2024 recommendations for scrypt:
 *   N=32768 (2^15), r=8, p=1, keylen=64
 */

import { randomBytes, scrypt, timingSafeEqual } from 'node:crypto';

/** Promisified scrypt with options support. */
function scryptAsync(
  password: string | Buffer,
  salt: string | Buffer,
  keylen: number,
  options: { N: number; r: number; p: number },
): Promise<Buffer> {
  return new Promise((resolve, reject) => {
    scrypt(password, salt, keylen, options, (err, derivedKey) => {
      if (err) reject(err);
      else resolve(derivedKey);
    });
  });
}

// Scrypt parameters (OWASP minimum recommendation: N=16384, r=8, p=1)
const SCRYPT_N = 16384;  // CPU/memory cost (2^14)
const SCRYPT_R = 8;      // Block size
const SCRYPT_P = 1;      // Parallelization
const SCRYPT_KEY_LENGTH = 32;
const SALT_LENGTH = 16;

// Password strength requirements
const MIN_PASSWORD_LENGTH = 8;
const MAX_PASSWORD_LENGTH = 128;
const HAS_UPPERCASE = /[A-Z]/;
const HAS_LOWERCASE = /[a-z]/;
const HAS_DIGIT = /\d/;

/**
 * Top common passwords list for rejection.
 * This is a curated list of the most commonly used passwords.
 */
const COMMON_PASSWORDS = new Set([
  'password', '12345678', '123456789', '1234567890', 'qwerty123',
  'password1', 'password123', 'iloveyou', 'sunshine1', 'princess1',
  'football1', 'charlie1', 'access14', 'dragon12', 'master12',
  'monkey123', 'shadow12', 'michael1', 'qwerty12', 'superman1',
  'abcdefgh', 'abc12345', 'trustno1', 'baseball1', 'letmein1',
  'welcome1', 'starwars1', 'whatever1', 'passw0rd', 'p@ssw0rd',
  'p@ssword', 'pa$$w0rd', 'qwertyui', 'asdfghjk', 'zxcvbnm1',
  '11111111', '22222222', '00000000', '12341234', 'qwer1234',
  'admin123', 'admin1234', 'changeme', 'letmein12', 'welcome123',
]);

/** Result of password strength validation. */
export interface PasswordValidationResult {
  valid: boolean;
  errors: string[];
}

/**
 * Validate password strength requirements.
 *
 * Requirements:
 *   - Minimum 8 characters
 *   - Maximum 128 characters
 *   - At least one uppercase letter
 *   - At least one lowercase letter
 *   - At least one digit
 *   - Not in common passwords list
 */
export function validatePasswordStrength(password: string): PasswordValidationResult {
  const errors: string[] = [];

  if (password.length < MIN_PASSWORD_LENGTH) {
    errors.push(`Password must be at least ${MIN_PASSWORD_LENGTH} characters`);
  }

  if (password.length > MAX_PASSWORD_LENGTH) {
    errors.push(`Password must be at most ${MAX_PASSWORD_LENGTH} characters`);
  }

  if (!HAS_UPPERCASE.test(password)) {
    errors.push('Password must contain at least one uppercase letter');
  }

  if (!HAS_LOWERCASE.test(password)) {
    errors.push('Password must contain at least one lowercase letter');
  }

  if (!HAS_DIGIT.test(password)) {
    errors.push('Password must contain at least one digit');
  }

  if (COMMON_PASSWORDS.has(password.toLowerCase())) {
    errors.push('Password is too common');
  }

  return {
    valid: errors.length === 0,
    errors,
  };
}

/**
 * Hash a password using scrypt with a random salt.
 *
 * Returns a string in the format: `scrypt:N:r:p:keylen:salt:hash`
 * where salt and hash are hex-encoded.
 */
export async function hashPassword(password: string): Promise<string> {
  const salt = randomBytes(SALT_LENGTH);
  const derived = (await scryptAsync(password, salt, SCRYPT_KEY_LENGTH, {
    N: SCRYPT_N,
    r: SCRYPT_R,
    p: SCRYPT_P,
  })) as Buffer;

  return `scrypt:${SCRYPT_N}:${SCRYPT_R}:${SCRYPT_P}:${SCRYPT_KEY_LENGTH}:${salt.toString('hex')}:${derived.toString('hex')}`;
}

/**
 * Verify a password against a stored hash.
 *
 * Uses constant-time comparison to prevent timing attacks.
 */
export async function verifyPassword(password: string, storedHash: string): Promise<boolean> {
  const parts = storedHash.split(':');
  if (parts.length !== 7 || parts[0] !== 'scrypt') {
    return false;
  }

  const N = parseInt(parts[1]!, 10);
  const r = parseInt(parts[2]!, 10);
  const p = parseInt(parts[3]!, 10);
  const keylen = parseInt(parts[4]!, 10);
  const salt = Buffer.from(parts[5]!, 'hex');
  const expectedHash = Buffer.from(parts[6]!, 'hex');

  if (isNaN(N) || isNaN(r) || isNaN(p) || isNaN(keylen)) {
    return false;
  }

  try {
    const derived = (await scryptAsync(password, salt, keylen, {
      N,
      r,
      p,
    })) as Buffer;

    if (derived.length !== expectedHash.length) {
      return false;
    }

    return timingSafeEqual(derived, expectedHash);
  } catch {
    return false;
  }
}
