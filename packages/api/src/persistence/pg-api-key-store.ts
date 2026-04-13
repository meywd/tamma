/**
 * PostgreSQL-backed unified API key store.
 *
 * Reads/writes from the `api_keys` table created in migration 009.
 */

import type pg from 'pg';
import type {
  IApiKeyStore,
  ApiKeyRecord,
  ApiKeyScope,
  CreateUnifiedApiKeyInput,
} from './api-key-store.js';

/** Grace period for key rotation: 24 hours in SQL interval format. */
const ROTATION_GRACE_INTERVAL = "INTERVAL '24 hours'";

/** PostgreSQL-backed unified API key store. */
export class PgApiKeyStore implements IApiKeyStore {
  constructor(private readonly pool: pg.Pool) {}

  async createApiKey(input: CreateUnifiedApiKeyInput): Promise<ApiKeyRecord> {
    const permissions = input.permissions ?? [];
    const tenantId = input.tenantId ?? null;
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO api_keys (scope, owner_id, key_hash, key_prefix, label, permissions, tenant_id)
       VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7)
       RETURNING *`,
      [
        input.scope,
        input.ownerId,
        input.keyHash,
        input.keyPrefix,
        input.label,
        JSON.stringify(permissions),
        tenantId,
      ],
    );
    return this.mapRow(result.rows[0]!);
  }

  async findByKeyHash(hash: string): Promise<ApiKeyRecord | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      `SELECT * FROM api_keys
       WHERE key_hash = $1
         AND (revoked_at IS NULL OR revoked_at > NOW())`,
      [hash],
    );
    if (result.rows.length === 0) return null;
    return this.mapRow(result.rows[0]!);
  }

  async revokeApiKey(id: string): Promise<void> {
    const result = await this.pool.query(
      `UPDATE api_keys SET revoked_at = NOW() WHERE id = $1 AND (revoked_at IS NULL OR revoked_at > NOW())`,
      [id],
    );
    if (result.rowCount === 0) {
      throw new Error(`API key not found or already revoked: ${id}`);
    }
  }

  async rotateApiKey(id: string, newKeyHash: string, newKeyPrefix: string): Promise<ApiKeyRecord> {
    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');

      // Set grace period on old key
      const oldResult = await client.query<Record<string, unknown>>(
        `UPDATE api_keys
         SET revoked_at = NOW() + ${ROTATION_GRACE_INTERVAL}
         WHERE id = $1 AND (revoked_at IS NULL OR revoked_at > NOW())
         RETURNING *`,
        [id],
      );
      if (oldResult.rows.length === 0) {
        throw new Error(`API key not found or already revoked: ${id}`);
      }
      const oldKey = this.mapRow(oldResult.rows[0]!);

      // Create new key inheriting scope/owner/permissions/tenant
      const newResult = await client.query<Record<string, unknown>>(
        `INSERT INTO api_keys (scope, owner_id, key_hash, key_prefix, label, permissions, tenant_id, rotated_from)
         VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7, $8)
         RETURNING *`,
        [
          oldKey.scope,
          oldKey.ownerId,
          newKeyHash,
          newKeyPrefix,
          oldKey.label,
          JSON.stringify(oldKey.permissions),
          oldKey.tenantId,
          id,
        ],
      );

      await client.query('COMMIT');
      return this.mapRow(newResult.rows[0]!);
    } catch (err) {
      await client.query('ROLLBACK');
      throw err;
    } finally {
      client.release();
    }
  }

  async listByScope(scope: ApiKeyScope): Promise<ApiKeyRecord[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      `SELECT * FROM api_keys WHERE scope = $1 ORDER BY created_at DESC`,
      [scope],
    );
    return result.rows.map((r) => this.mapRow(r));
  }

  async updateLastUsed(id: string): Promise<void> {
    await this.pool.query(
      `UPDATE api_keys SET last_used_at = NOW() WHERE id = $1`,
      [id],
    );
  }

  private mapRow(row: Record<string, unknown>): ApiKeyRecord {
    let permissions: string[] = [];
    const rawPermissions = row['permissions'];
    if (Array.isArray(rawPermissions)) {
      permissions = rawPermissions as string[];
    } else if (typeof rawPermissions === 'string') {
      try {
        const parsed: unknown = JSON.parse(rawPermissions);
        if (Array.isArray(parsed)) {
          permissions = parsed as string[];
        }
      } catch {
        // leave as empty array
      }
    }

    return {
      id: String(row['id']),
      scope: String(row['scope']) as ApiKeyScope,
      ownerId: String(row['owner_id']),
      keyHash: String(row['key_hash']),
      keyPrefix: String(row['key_prefix']),
      label: String(row['label']),
      permissions,
      tenantId: row['tenant_id'] !== null && row['tenant_id'] !== undefined
        ? String(row['tenant_id'])
        : null,
      createdAt: String(row['created_at']),
      lastUsedAt: row['last_used_at'] !== null && row['last_used_at'] !== undefined
        ? String(row['last_used_at'])
        : null,
      revokedAt: row['revoked_at'] !== null && row['revoked_at'] !== undefined
        ? String(row['revoked_at'])
        : null,
      rotatedFrom: row['rotated_from'] !== null && row['rotated_from'] !== undefined
        ? String(row['rotated_from'])
        : null,
    };
  }
}
