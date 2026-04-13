/**
 * Unified API Key persistence.
 *
 * Supports three scopes: user, installation, service.
 * Stores only the hash (never the raw key).
 */

import { randomUUID } from 'node:crypto';

/** Valid API key scopes. */
export type ApiKeyScope = 'user' | 'installation' | 'service';

/** A unified API key record (never contains the raw key). */
export interface ApiKeyRecord {
  id: string;
  scope: ApiKeyScope;
  ownerId: string;
  keyHash: string;
  keyPrefix: string;
  label: string;
  permissions: string[];
  tenantId: string | null;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
  rotatedFrom: string | null;
}

/** Input for creating a new unified API key record. */
export interface CreateUnifiedApiKeyInput {
  scope: ApiKeyScope;
  ownerId: string;
  keyHash: string;
  keyPrefix: string;
  label: string;
  permissions?: string[];
  tenantId?: string | null;
}

/** Interface for unified API key persistence. */
export interface IApiKeyStore {
  /** Create a new API key record (caller provides pre-hashed key). */
  createApiKey(input: CreateUnifiedApiKeyInput): Promise<ApiKeyRecord>;

  /**
   * Find an API key by its hash.
   * Returns keys where revoked_at IS NULL OR revoked_at > NOW()
   * (supports rotation grace period).
   */
  findByKeyHash(hash: string): Promise<ApiKeyRecord | null>;

  /** Immediately revoke an API key (sets revoked_at = NOW). */
  revokeApiKey(id: string): Promise<void>;

  /**
   * Rotate an API key: create a new key with rotated_from pointing to old,
   * and set old key's revoked_at = NOW() + 24h (grace period).
   * Returns the new key record.
   */
  rotateApiKey(id: string, newKeyHash: string, newKeyPrefix: string): Promise<ApiKeyRecord>;

  /** List all keys for a given scope. */
  listByScope(scope: ApiKeyScope): Promise<ApiKeyRecord[]>;

  /** Update the last_used_at timestamp for a key. */
  updateLastUsed(id: string): Promise<void>;
}

/** Grace period for key rotation (24 hours in milliseconds). */
const ROTATION_GRACE_MS = 24 * 60 * 60 * 1000;

/** In-memory implementation for testing and development. */
export class InMemoryApiKeyStore implements IApiKeyStore {
  private keys = new Map<string, ApiKeyRecord>();

  async createApiKey(input: CreateUnifiedApiKeyInput): Promise<ApiKeyRecord> {
    const id = randomUUID();
    const now = new Date().toISOString();
    const record: ApiKeyRecord = {
      id,
      scope: input.scope,
      ownerId: input.ownerId,
      keyHash: input.keyHash,
      keyPrefix: input.keyPrefix,
      label: input.label,
      permissions: input.permissions ?? [],
      tenantId: input.tenantId ?? null,
      createdAt: now,
      lastUsedAt: null,
      revokedAt: null,
      rotatedFrom: null,
    };
    this.keys.set(id, record);
    return { ...record };
  }

  async findByKeyHash(hash: string): Promise<ApiKeyRecord | null> {
    const now = new Date();
    for (const key of this.keys.values()) {
      if (key.keyHash === hash) {
        // Active if not revoked, or revoked_at is in the future (grace period)
        if (key.revokedAt === null || new Date(key.revokedAt) > now) {
          return { ...key };
        }
      }
    }
    return null;
  }

  async revokeApiKey(id: string): Promise<void> {
    const key = this.keys.get(id);
    if (!key) {
      throw new Error(`API key not found: ${id}`);
    }
    key.revokedAt = new Date().toISOString();
  }

  async rotateApiKey(id: string, newKeyHash: string, newKeyPrefix: string): Promise<ApiKeyRecord> {
    const oldKey = this.keys.get(id);
    if (!oldKey) {
      throw new Error(`API key not found: ${id}`);
    }

    // Set grace period on old key
    const graceEnd = new Date(Date.now() + ROTATION_GRACE_MS);
    oldKey.revokedAt = graceEnd.toISOString();

    // Create new key with same scope/owner/permissions/tenant
    const newId = randomUUID();
    const now = new Date().toISOString();
    const newRecord: ApiKeyRecord = {
      id: newId,
      scope: oldKey.scope,
      ownerId: oldKey.ownerId,
      keyHash: newKeyHash,
      keyPrefix: newKeyPrefix,
      label: oldKey.label,
      permissions: [...oldKey.permissions],
      tenantId: oldKey.tenantId,
      createdAt: now,
      lastUsedAt: null,
      revokedAt: null,
      rotatedFrom: id,
    };
    this.keys.set(newId, newRecord);
    return { ...newRecord };
  }

  async listByScope(scope: ApiKeyScope): Promise<ApiKeyRecord[]> {
    return [...this.keys.values()]
      .filter((k) => k.scope === scope)
      .map((k) => ({ ...k }));
  }

  async updateLastUsed(id: string): Promise<void> {
    const key = this.keys.get(id);
    if (key) {
      key.lastUsedAt = new Date().toISOString();
    }
  }
}
