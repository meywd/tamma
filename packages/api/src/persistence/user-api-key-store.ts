/**
 * User API Key persistence.
 *
 * Stores per-user API keys with only the hash (never the raw key).
 * Supports creation, listing (without the full key), and revocation.
 */

import type pg from 'pg';

/** A user API key record (never contains the raw key). */
export interface UserApiKey {
  id: string;
  userId: string;
  tenantId: string;
  keyPrefix: string;
  label: string;
  lastUsedAt: string | null;
  createdAt: string;
  revokedAt: string | null;
}

/** Input for creating a new API key record. */
export interface CreateApiKeyInput {
  userId: string;
  keyHash: string;
  keyPrefix: string;
  label: string;
  tenantId?: string;
}

/** Interface for user API key persistence. */
export interface IUserApiKeyStore {
  /** Create a new API key record (caller provides pre-hashed key). */
  createApiKey(input: CreateApiKeyInput): Promise<UserApiKey>;

  /** List active (non-revoked) API keys for a user. */
  listApiKeys(userId: string): Promise<UserApiKey[]>;

  /** Revoke an API key. userId is used for ownership verification. */
  revokeApiKey(keyId: string, userId: string): Promise<void>;

  /** Revoke all API keys for a user (e.g. on soft delete). */
  revokeAllForUser(userId: string): Promise<void>;

  /** Find a user API key by its hash (for authentication). */
  findByKeyHash(keyHash: string): Promise<(UserApiKey & { keyHash: string }) | null>;

  /** Update the last_used_at timestamp for a key. */
  updateLastUsed(keyId: string): Promise<void>;
}

/** In-memory implementation for testing and development. */
export class InMemoryUserApiKeyStore implements IUserApiKeyStore {
  private keys = new Map<string, UserApiKey & { keyHash: string }>();
  private nextId = 1;

  async createApiKey(input: CreateApiKeyInput): Promise<UserApiKey> {
    const id = String(this.nextId++);
    const now = new Date().toISOString();
    const record = {
      id,
      userId: input.userId,
      tenantId: input.tenantId ?? '00000000-0000-0000-0000-000000000000',
      keyHash: input.keyHash,
      keyPrefix: input.keyPrefix,
      label: input.label,
      lastUsedAt: null,
      createdAt: now,
      revokedAt: null,
    };
    this.keys.set(id, record);
    // Return without keyHash
    const { keyHash: _kh, ...rest } = record;
    return rest;
  }

  async listApiKeys(userId: string): Promise<UserApiKey[]> {
    return [...this.keys.values()]
      .filter((k) => k.userId === userId && k.revokedAt === null)
      .map(({ keyHash: _kh, ...rest }) => rest);
  }

  async revokeApiKey(keyId: string, userId: string): Promise<void> {
    const key = this.keys.get(keyId);
    if (!key || key.userId !== userId) {
      throw new Error(`API key not found: ${keyId}`);
    }
    key.revokedAt = new Date().toISOString();
  }

  async revokeAllForUser(userId: string): Promise<void> {
    const now = new Date().toISOString();
    for (const key of this.keys.values()) {
      if (key.userId === userId && key.revokedAt === null) {
        key.revokedAt = now;
      }
    }
  }

  async findByKeyHash(keyHash: string): Promise<(UserApiKey & { keyHash: string }) | null> {
    for (const key of this.keys.values()) {
      if (key.keyHash === keyHash && key.revokedAt === null) {
        return { ...key };
      }
    }
    return null;
  }

  async updateLastUsed(keyId: string): Promise<void> {
    const key = this.keys.get(keyId);
    if (key) {
      key.lastUsedAt = new Date().toISOString();
    }
  }
}

/** PostgreSQL-backed user API key store. */
export class PgUserApiKeyStore implements IUserApiKeyStore {
  constructor(private readonly pool: pg.Pool) {}

  async createApiKey(input: CreateApiKeyInput): Promise<UserApiKey> {
    const tenantId = input.tenantId ?? '00000000-0000-0000-0000-000000000000';
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO user_api_keys (user_id, key_hash, key_prefix, label, tenant_id)
       VALUES ($1, $2, $3, $4, $5)
       RETURNING *`,
      [input.userId, input.keyHash, input.keyPrefix, input.label, tenantId],
    );
    return this.mapKey(result.rows[0]!);
  }

  async listApiKeys(userId: string): Promise<UserApiKey[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM user_api_keys WHERE user_id = $1 AND revoked_at IS NULL ORDER BY created_at DESC',
      [userId],
    );
    return result.rows.map((r) => this.mapKey(r));
  }

  async revokeApiKey(keyId: string, userId: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE user_api_keys SET revoked_at = NOW() WHERE id = $1 AND user_id = $2 AND revoked_at IS NULL',
      [keyId, userId],
    );
    if (result.rowCount === 0) {
      throw new Error(`API key not found: ${keyId}`);
    }
  }

  async revokeAllForUser(userId: string): Promise<void> {
    await this.pool.query(
      'UPDATE user_api_keys SET revoked_at = NOW() WHERE user_id = $1 AND revoked_at IS NULL',
      [userId],
    );
  }

  async findByKeyHash(keyHash: string): Promise<(UserApiKey & { keyHash: string }) | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM user_api_keys WHERE key_hash = $1 AND revoked_at IS NULL',
      [keyHash],
    );
    if (result.rows.length === 0) return null;
    const row = result.rows[0]!;
    return {
      ...this.mapKey(row),
      keyHash: String(row['key_hash']),
    };
  }

  async updateLastUsed(keyId: string): Promise<void> {
    await this.pool.query(
      'UPDATE user_api_keys SET last_used_at = NOW() WHERE id = $1',
      [keyId],
    );
  }

  private mapKey(row: Record<string, unknown>): UserApiKey {
    return {
      id: String(row['id']),
      userId: String(row['user_id']),
      tenantId: String(row['tenant_id'] ?? '00000000-0000-0000-0000-000000000000'),
      keyPrefix: String(row['key_prefix']),
      label: String(row['label']),
      lastUsedAt: row['last_used_at'] !== null && row['last_used_at'] !== undefined
        ? String(row['last_used_at'])
        : null,
      createdAt: String(row['created_at']),
      revokedAt: row['revoked_at'] !== null && row['revoked_at'] !== undefined
        ? String(row['revoked_at'])
        : null,
    };
  }
}
