/**
 * PostgreSQL-backed tenant store.
 *
 * Maps snake_case DB columns to camelCase Tenant interface properties.
 */

import type pg from 'pg';

import type { Tenant } from '@tamma/shared';

import type { ITenantStore, CreateTenantInput } from './tenant-store.js';

/** PostgreSQL implementation of ITenantStore. */
export class PgTenantStore implements ITenantStore {
  constructor(private readonly pool: pg.Pool) {}

  async createTenant(input: CreateTenantInput): Promise<Tenant> {
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO tenants (name, slug, external_id, plan, settings)
       VALUES ($1, $2, $3, $4, $5)
       RETURNING *`,
      [
        input.name,
        input.slug,
        input.externalId ?? null,
        input.plan ?? 'free',
        JSON.stringify(input.settings ?? {}),
      ],
    );
    return this.mapTenant(result.rows[0]!);
  }

  async getTenant(id: string): Promise<Tenant | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenants WHERE id = $1 AND deleted_at IS NULL',
      [id],
    );
    if (result.rows.length === 0) return null;
    return this.mapTenant(result.rows[0]!);
  }

  async getTenantByExternalId(externalId: string): Promise<Tenant | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenants WHERE external_id = $1 AND deleted_at IS NULL',
      [externalId],
    );
    if (result.rows.length === 0) return null;
    return this.mapTenant(result.rows[0]!);
  }

  async getTenantBySlug(slug: string): Promise<Tenant | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenants WHERE slug = $1 AND deleted_at IS NULL',
      [slug],
    );
    if (result.rows.length === 0) return null;
    return this.mapTenant(result.rows[0]!);
  }

  async updateTenant(id: string, update: Partial<Pick<Tenant, 'name' | 'slug' | 'plan' | 'settings'>>): Promise<Tenant> {
    const sets: string[] = [];
    const params: unknown[] = [];
    let paramIndex = 1;

    if (update.name !== undefined) {
      sets.push(`name = $${paramIndex}`);
      params.push(update.name);
      paramIndex++;
    }
    if (update.slug !== undefined) {
      sets.push(`slug = $${paramIndex}`);
      params.push(update.slug);
      paramIndex++;
    }
    if (update.plan !== undefined) {
      sets.push(`plan = $${paramIndex}`);
      params.push(update.plan);
      paramIndex++;
    }
    if (update.settings !== undefined) {
      sets.push(`settings = $${paramIndex}`);
      params.push(JSON.stringify(update.settings));
      paramIndex++;
    }

    if (sets.length === 0) {
      const existing = await this.getTenant(id);
      if (!existing) {
        throw new Error(`Tenant not found: ${id}`);
      }
      return existing;
    }

    sets.push('updated_at = NOW()');
    params.push(id);

    const result = await this.pool.query<Record<string, unknown>>(
      `UPDATE tenants SET ${sets.join(', ')} WHERE id = $${paramIndex} AND deleted_at IS NULL RETURNING *`,
      params,
    );

    if (result.rows.length === 0) {
      throw new Error(`Tenant not found: ${id}`);
    }
    return this.mapTenant(result.rows[0]!);
  }

  async deleteTenant(id: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE tenants SET deleted_at = NOW(), updated_at = NOW() WHERE id = $1 AND deleted_at IS NULL',
      [id],
    );
    if (result.rowCount === 0) {
      throw new Error(`Tenant not found: ${id}`);
    }
  }

  async listTenants(): Promise<Tenant[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenants WHERE deleted_at IS NULL ORDER BY created_at',
    );
    return result.rows.map((r) => this.mapTenant(r));
  }

  private mapTenant(row: Record<string, unknown>): Tenant {
    return {
      id: String(row['id']),
      name: String(row['name']),
      slug: String(row['slug']),
      externalId: row['external_id'] !== null && row['external_id'] !== undefined
        ? String(row['external_id'])
        : null,
      plan: String(row['plan']) as Tenant['plan'],
      settings: (row['settings'] ?? {}) as Record<string, unknown>,
      createdAt: String(row['created_at']),
      updatedAt: String(row['updated_at']),
      deletedAt: row['deleted_at'] !== null && row['deleted_at'] !== undefined
        ? String(row['deleted_at'])
        : null,
    };
  }
}
