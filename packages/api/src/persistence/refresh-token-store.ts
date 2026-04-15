/**
 * Refresh Token persistence (Story 18-2).
 *
 * Manages opaque refresh tokens with rotation support.
 * Tokens are stored as SHA-256 hashes — raw tokens are never persisted.
 */

import type pg from 'pg';

/** A refresh token record. */
export interface RefreshToken {
  id: string;
  userId: string;
  tokenHash: string;
  expiresAt: string;
  revokedAt: string | null;
  createdAt: string;
}

/** Interface for refresh token persistence. */
export interface IRefreshTokenStore {
  /** Store a new refresh token. */
  createToken(userId: string, tokenHash: string, expiresAt: string): Promise<RefreshToken>;
  /** Look up a refresh token by its hash. */
  getTokenByHash(tokenHash: string): Promise<RefreshToken | null>;
  /** Revoke a specific refresh token. */
  revokeToken(id: string): Promise<void>;
  /** Revoke ALL refresh tokens for a user (e.g., on password change or compromise). */
  revokeAllForUser(userId: string): Promise<void>;
  /** Clean up expired tokens. */
  cleanupExpired(): Promise<number>;
}

/** In-memory implementation for testing. */
export class InMemoryRefreshTokenStore implements IRefreshTokenStore {
  private tokens = new Map<string, RefreshToken>();
  private nextId = 1;

  async createToken(userId: string, tokenHash: string, expiresAt: string): Promise<RefreshToken> {
    const id = String(this.nextId++);
    const token: RefreshToken = {
      id,
      userId,
      tokenHash,
      expiresAt,
      revokedAt: null,
      createdAt: new Date().toISOString(),
    };
    this.tokens.set(id, token);
    return token;
  }

  async getTokenByHash(tokenHash: string): Promise<RefreshToken | null> {
    for (const token of this.tokens.values()) {
      if (token.tokenHash === tokenHash) return token;
    }
    return null;
  }

  async revokeToken(id: string): Promise<void> {
    const token = this.tokens.get(id);
    if (token) {
      token.revokedAt = new Date().toISOString();
    }
  }

  async revokeAllForUser(userId: string): Promise<void> {
    const now = new Date().toISOString();
    for (const token of this.tokens.values()) {
      if (token.userId === userId && token.revokedAt === null) {
        token.revokedAt = now;
      }
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

/** PostgreSQL-backed refresh token store. */
export class PgRefreshTokenStore implements IRefreshTokenStore {
  constructor(private readonly pool: pg.Pool) {}

  async createToken(userId: string, tokenHash: string, expiresAt: string): Promise<RefreshToken> {
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO refresh_tokens (user_id, token_hash, expires_at)
       VALUES ($1, $2, $3) RETURNING *`,
      [userId, tokenHash, expiresAt],
    );
    return this.mapToken(result.rows[0]!);
  }

  async getTokenByHash(tokenHash: string): Promise<RefreshToken | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM refresh_tokens WHERE token_hash = $1',
      [tokenHash],
    );
    if (result.rows.length === 0) return null;
    return this.mapToken(result.rows[0]!);
  }

  async revokeToken(id: string): Promise<void> {
    await this.pool.query(
      'UPDATE refresh_tokens SET revoked_at = NOW() WHERE id = $1 AND revoked_at IS NULL',
      [id],
    );
  }

  async revokeAllForUser(userId: string): Promise<void> {
    await this.pool.query(
      'UPDATE refresh_tokens SET revoked_at = NOW() WHERE user_id = $1 AND revoked_at IS NULL',
      [userId],
    );
  }

  async cleanupExpired(): Promise<number> {
    const result = await this.pool.query(
      'DELETE FROM refresh_tokens WHERE expires_at < NOW()',
    );
    return result.rowCount ?? 0;
  }

  private mapToken(row: Record<string, unknown>): RefreshToken {
    return {
      id: String(row['id']),
      userId: String(row['user_id']),
      tokenHash: String(row['token_hash']),
      expiresAt: String(row['expires_at']),
      revokedAt: row['revoked_at'] !== null && row['revoked_at'] !== undefined
        ? String(row['revoked_at'])
        : null,
      createdAt: String(row['created_at']),
    };
  }
}
