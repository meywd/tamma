/**
 * Password Reset Token persistence (Story 18-6).
 *
 * Manages single-use password reset tokens.
 * Tokens are stored as SHA-256 hashes — raw tokens are never persisted.
 */

import type pg from 'pg';

/** A password reset token record. */
export interface PasswordResetToken {
  id: string;
  userId: string;
  tokenHash: string;
  expiresAt: string;
  consumedAt: string | null;
  createdAt: string;
}

/** Interface for password reset token persistence. */
export interface IPasswordResetStore {
  /** Create a reset token. */
  createResetToken(userId: string, tokenHash: string, expiresAt: string): Promise<PasswordResetToken>;
  /** Look up a reset token by its hash. */
  getResetTokenByHash(tokenHash: string): Promise<PasswordResetToken | null>;
  /** Mark a reset token as consumed. */
  consumeResetToken(id: string): Promise<void>;
  /** Clean up expired tokens. */
  cleanupExpired(): Promise<number>;
}

/** In-memory implementation for testing. */
export class InMemoryPasswordResetStore implements IPasswordResetStore {
  private tokens = new Map<string, PasswordResetToken>();
  private nextId = 1;

  async createResetToken(userId: string, tokenHash: string, expiresAt: string): Promise<PasswordResetToken> {
    const id = String(this.nextId++);
    const token: PasswordResetToken = {
      id,
      userId,
      tokenHash,
      expiresAt,
      consumedAt: null,
      createdAt: new Date().toISOString(),
    };
    this.tokens.set(id, token);
    return token;
  }

  async getResetTokenByHash(tokenHash: string): Promise<PasswordResetToken | null> {
    for (const token of this.tokens.values()) {
      if (token.tokenHash === tokenHash) return token;
    }
    return null;
  }

  async consumeResetToken(id: string): Promise<void> {
    const token = this.tokens.get(id);
    if (token) {
      token.consumedAt = new Date().toISOString();
    }
  }

  async cleanupExpired(): Promise<number> {
    const now = new Date().toISOString();
    let count = 0;
    for (const [id, token] of this.tokens.entries()) {
      if (token.expiresAt < now) {
        this.tokens.delete(id);
        count++;
      }
    }
    return count;
  }
}

/** PostgreSQL-backed password reset store. */
export class PgPasswordResetStore implements IPasswordResetStore {
  constructor(private readonly pool: pg.Pool) {}

  async createResetToken(userId: string, tokenHash: string, expiresAt: string): Promise<PasswordResetToken> {
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO password_reset_tokens (user_id, token_hash, expires_at)
       VALUES ($1, $2, $3) RETURNING *`,
      [userId, tokenHash, expiresAt],
    );
    return this.mapToken(result.rows[0]!);
  }

  async getResetTokenByHash(tokenHash: string): Promise<PasswordResetToken | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM password_reset_tokens WHERE token_hash = $1',
      [tokenHash],
    );
    if (result.rows.length === 0) return null;
    return this.mapToken(result.rows[0]!);
  }

  async consumeResetToken(id: string): Promise<void> {
    await this.pool.query(
      'UPDATE password_reset_tokens SET consumed_at = NOW() WHERE id = $1 AND consumed_at IS NULL',
      [id],
    );
  }

  async cleanupExpired(): Promise<number> {
    const result = await this.pool.query(
      'DELETE FROM password_reset_tokens WHERE expires_at < NOW()',
    );
    return result.rowCount ?? 0;
  }

  private mapToken(row: Record<string, unknown>): PasswordResetToken {
    return {
      id: String(row['id']),
      userId: String(row['user_id']),
      tokenHash: String(row['token_hash']),
      expiresAt: String(row['expires_at']),
      consumedAt: row['consumed_at'] !== null && row['consumed_at'] !== undefined
        ? String(row['consumed_at'])
        : null,
      createdAt: String(row['created_at']),
    };
  }
}
