/**
 * Tenant persistence.
 *
 * Manages tenant lifecycle for multi-tenancy support.
 * A default sentinel tenant is pre-seeded for CLI/self-hosted mode.
 */

import { randomUUID } from 'node:crypto';

import type { Tenant, TenantPlan } from '@tamma/shared';
import { DEFAULT_TENANT_ID } from '@tamma/shared';

/** Input for creating a new tenant. */
export interface CreateTenantInput {
  name: string;
  slug: string;
  externalId?: string | null;
  plan?: TenantPlan;
  settings?: Record<string, unknown>;
}

/** Interface for tenant persistence. */
export interface ITenantStore {
  createTenant(input: CreateTenantInput): Promise<Tenant>;
  getTenant(id: string): Promise<Tenant | null>;
  getTenantByExternalId(externalId: string): Promise<Tenant | null>;
  getTenantBySlug(slug: string): Promise<Tenant | null>;
  updateTenant(id: string, update: Partial<Pick<Tenant, 'name' | 'slug' | 'plan' | 'settings'>>): Promise<Tenant>;
  deleteTenant(id: string): Promise<void>;  // soft delete
  listTenants(): Promise<Tenant[]>;
}

/** In-memory implementation for testing and development. */
export class InMemoryTenantStore implements ITenantStore {
  private tenants = new Map<string, Tenant>();

  constructor() {
    // Pre-seed the default tenant sentinel
    const now = new Date().toISOString();
    this.tenants.set(DEFAULT_TENANT_ID, {
      id: DEFAULT_TENANT_ID,
      name: 'Default',
      slug: 'default',
      externalId: null,
      plan: 'free',
      settings: {},
      createdAt: now,
      updatedAt: now,
      deletedAt: null,
    });
  }

  async createTenant(input: CreateTenantInput): Promise<Tenant> {
    // Check for duplicate slug
    for (const tenant of this.tenants.values()) {
      if (tenant.slug === input.slug && tenant.deletedAt === null) {
        throw new Error(`Tenant with slug "${input.slug}" already exists`);
      }
    }

    // Check for duplicate externalId
    const externalId = input.externalId ?? null;
    if (externalId !== null) {
      for (const tenant of this.tenants.values()) {
        if (tenant.externalId === externalId && tenant.deletedAt === null) {
          throw new Error(`Tenant with externalId "${externalId}" already exists`);
        }
      }
    }

    const now = new Date().toISOString();
    const id = randomUUID();
    const tenant: Tenant = {
      id,
      name: input.name,
      slug: input.slug,
      externalId,
      plan: input.plan ?? 'free',
      settings: input.settings ?? {},
      createdAt: now,
      updatedAt: now,
      deletedAt: null,
    };
    this.tenants.set(id, tenant);
    return tenant;
  }

  async getTenant(id: string): Promise<Tenant | null> {
    const tenant = this.tenants.get(id);
    if (!tenant || tenant.deletedAt !== null) return null;
    return tenant;
  }

  async getTenantByExternalId(externalId: string): Promise<Tenant | null> {
    for (const tenant of this.tenants.values()) {
      if (tenant.externalId === externalId && tenant.deletedAt === null) {
        return tenant;
      }
    }
    return null;
  }

  async getTenantBySlug(slug: string): Promise<Tenant | null> {
    for (const tenant of this.tenants.values()) {
      if (tenant.slug === slug && tenant.deletedAt === null) {
        return tenant;
      }
    }
    return null;
  }

  async updateTenant(id: string, update: Partial<Pick<Tenant, 'name' | 'slug' | 'plan' | 'settings'>>): Promise<Tenant> {
    const tenant = this.tenants.get(id);
    if (!tenant || tenant.deletedAt !== null) {
      throw new Error(`Tenant not found: ${id}`);
    }

    // Check for duplicate slug if slug is being changed
    if (update.slug !== undefined && update.slug !== tenant.slug) {
      for (const other of this.tenants.values()) {
        if (other.slug === update.slug && other.id !== id && other.deletedAt === null) {
          throw new Error(`Tenant with slug "${update.slug}" already exists`);
        }
      }
    }

    if (update.name !== undefined) {
      tenant.name = update.name;
    }
    if (update.slug !== undefined) {
      tenant.slug = update.slug;
    }
    if (update.plan !== undefined) {
      tenant.plan = update.plan;
    }
    if (update.settings !== undefined) {
      tenant.settings = update.settings;
    }
    tenant.updatedAt = new Date().toISOString();

    return tenant;
  }

  async deleteTenant(id: string): Promise<void> {
    const tenant = this.tenants.get(id);
    if (!tenant) {
      throw new Error(`Tenant not found: ${id}`);
    }
    tenant.deletedAt = new Date().toISOString();
    tenant.updatedAt = new Date().toISOString();
  }

  async listTenants(): Promise<Tenant[]> {
    return [...this.tenants.values()].filter((t) => t.deletedAt === null);
  }
}
