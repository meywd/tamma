/**
 * Tenant persistence.
 *
 * Manages organization/tenant records in the `tenants` table.
 * An "organization" in the Tamma platform IS a tenant from Epic 17.
 */

import type pg from 'pg';

/** Represents a tenant (organization) in the Tamma platform. */
export interface Tenant {
  id: string;
  name: string;
  slug: string;
  plan: 'free' | 'pro' | 'enterprise';
  settings: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  deletedAt: string | null;
}

/** Input for creating a tenant. */
export interface CreateTenantInput {
  name: string;
  slug: string;
  plan?: 'free' | 'pro' | 'enterprise';
  settings?: Record<string, unknown>;
}

/** Input for updating a tenant. */
export interface UpdateTenantInput {
  name?: string;
  slug?: string;
  plan?: 'free' | 'pro' | 'enterprise';
  settings?: Record<string, unknown>;
}

/** Interface for tenant persistence. */
export interface ITenantStore {
  createTenant(input: CreateTenantInput): Promise<Tenant>;
  getTenant(id: string): Promise<Tenant | null>;
  getTenantBySlug(slug: string): Promise<Tenant | null>;
  updateTenant(id: string, input: UpdateTenantInput): Promise<Tenant>;
  deleteTenant(id: string): Promise<void>;
  hardDeleteTenant(id: string): Promise<void>;
}

/** In-memory implementation for testing and development. */
export class InMemoryTenantStore implements ITenantStore {
  private tenants = new Map<string, Tenant>();
  private nextId = 1;

  async createTenant(input: CreateTenantInput): Promise<Tenant> {
    const now = new Date().toISOString();
    const id = `tenant-${this.nextId++}`;
    const tenant: Tenant = {
      id,
      name: input.name,
      slug: input.slug,
      plan: input.plan ?? 'free',
      settings: input.settings ?? {},
      createdAt: now,
      updatedAt: now,
      deletedAt: null,
    };
    this.tenants.set(id, tenant);
    return { ...tenant };
  }

  async getTenant(id: string): Promise<Tenant | null> {
    const t = this.tenants.get(id);
    if (!t) return null;
    return { ...t };
  }

  async getTenantBySlug(slug: string): Promise<Tenant | null> {
    for (const t of this.tenants.values()) {
      if (t.slug === slug && !t.deletedAt) return { ...t };
    }
    return null;
  }

  async updateTenant(id: string, input: UpdateTenantInput): Promise<Tenant> {
    const t = this.tenants.get(id);
    if (!t || t.deletedAt) {
      throw new Error(`Tenant not found: ${id}`);
    }
    if (input.name !== undefined) t.name = input.name;
    if (input.slug !== undefined) t.slug = input.slug;
    if (input.plan !== undefined) t.plan = input.plan;
    if (input.settings !== undefined) t.settings = input.settings;
    t.updatedAt = new Date().toISOString();
    return { ...t };
  }

  async deleteTenant(id: string): Promise<void> {
    const t = this.tenants.get(id);
    if (!t) throw new Error(`Tenant not found: ${id}`);
    t.deletedAt = new Date().toISOString();
    t.updatedAt = new Date().toISOString();
  }

  async hardDeleteTenant(id: string): Promise<void> {
    if (!this.tenants.has(id)) {
      throw new Error(`Tenant not found: ${id}`);
    }
    this.tenants.delete(id);
  }
}

/** PostgreSQL-backed tenant store. */
export class PgTenantStore implements ITenantStore {
  constructor(private readonly pool: pg.Pool) {}

  async createTenant(input: CreateTenantInput): Promise<Tenant> {
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO tenants (name, slug, plan, settings)
       VALUES ($1, $2, $3, $4)
       RETURNING *`,
      [input.name, input.slug, input.plan ?? 'free', JSON.stringify(input.settings ?? {})],
    );
    return this._mapRow(result.rows[0]!);
  }

  async getTenant(id: string): Promise<Tenant | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenants WHERE id = $1',
      [id],
    );
    if (result.rows.length === 0) return null;
    return this._mapRow(result.rows[0]!);
  }

  async getTenantBySlug(slug: string): Promise<Tenant | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenants WHERE slug = $1 AND deleted_at IS NULL',
      [slug],
    );
    if (result.rows.length === 0) return null;
    return this._mapRow(result.rows[0]!);
  }

  async updateTenant(id: string, input: UpdateTenantInput): Promise<Tenant> {
    const sets: string[] = [];
    const vals: unknown[] = [];
    let idx = 1;

    if (input.name !== undefined) {
      sets.push(`name = $${idx++}`);
      vals.push(input.name);
    }
    if (input.slug !== undefined) {
      sets.push(`slug = $${idx++}`);
      vals.push(input.slug);
    }
    if (input.plan !== undefined) {
      sets.push(`plan = $${idx++}`);
      vals.push(input.plan);
    }
    if (input.settings !== undefined) {
      sets.push(`settings = $${idx++}`);
      vals.push(JSON.stringify(input.settings));
    }
    sets.push(`updated_at = NOW()`);
    vals.push(id);

    const result = await this.pool.query<Record<string, unknown>>(
      `UPDATE tenants SET ${sets.join(', ')} WHERE id = $${idx} AND deleted_at IS NULL RETURNING *`,
      vals,
    );
    if (result.rows.length === 0) throw new Error(`Tenant not found: ${id}`);
    return this._mapRow(result.rows[0]!);
  }

  async deleteTenant(id: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE tenants SET deleted_at = NOW(), updated_at = NOW() WHERE id = $1 AND deleted_at IS NULL',
      [id],
    );
    if (result.rowCount === 0) throw new Error(`Tenant not found: ${id}`);
  }

  async hardDeleteTenant(id: string): Promise<void> {
    const result = await this.pool.query(
      'DELETE FROM tenants WHERE id = $1',
      [id],
    );
    if (result.rowCount === 0) throw new Error(`Tenant not found: ${id}`);
  }

  private _mapRow(row: Record<string, unknown>): Tenant {
    return {
      id: String(row['id']),
      name: String(row['name']),
      slug: String(row['slug']),
      plan: String(row['plan']) as Tenant['plan'],
      settings: (typeof row['settings'] === 'object' && row['settings'] !== null
        ? row['settings']
        : {}) as Record<string, unknown>,
      createdAt: String(row['created_at']),
      updatedAt: String(row['updated_at']),
      deletedAt: row['deleted_at'] ? String(row['deleted_at']) : null,
    };
  }
}
