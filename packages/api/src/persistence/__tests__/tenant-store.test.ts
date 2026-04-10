/**
 * InMemoryTenantStore Tests
 *
 * Tests the ITenantStore interface using the in-memory implementation.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryTenantStore } from '../tenant-store.js';
import type { ITenantStore } from '../tenant-store.js';
import { DEFAULT_TENANT_ID } from '@tamma/shared';

describe('InMemoryTenantStore', () => {
  let store: ITenantStore;

  beforeEach(() => {
    store = new InMemoryTenantStore();
  });

  // -----------------------------------------------------------------------
  // Default tenant
  // -----------------------------------------------------------------------

  describe('default tenant', () => {
    it('exists on initialization', async () => {
      const tenant = await store.getTenant(DEFAULT_TENANT_ID);
      expect(tenant).not.toBeNull();
      expect(tenant!.id).toBe(DEFAULT_TENANT_ID);
      expect(tenant!.name).toBe('Default');
      expect(tenant!.slug).toBe('default');
      expect(tenant!.plan).toBe('free');
      expect(tenant!.externalId).toBeNull();
      expect(tenant!.deletedAt).toBeNull();
    });

    it('is included in listTenants', async () => {
      const tenants = await store.listTenants();
      expect(tenants.some((t) => t.id === DEFAULT_TENANT_ID)).toBe(true);
    });
  });

  // -----------------------------------------------------------------------
  // createTenant
  // -----------------------------------------------------------------------

  describe('createTenant', () => {
    it('creates and returns a tenant with generated UUID', async () => {
      const tenant = await store.createTenant({
        name: 'Acme Corp',
        slug: 'acme-corp',
      });

      expect(tenant.id).toBeDefined();
      expect(tenant.id).not.toBe(DEFAULT_TENANT_ID);
      expect(tenant.name).toBe('Acme Corp');
      expect(tenant.slug).toBe('acme-corp');
      expect(tenant.plan).toBe('free');
      expect(tenant.externalId).toBeNull();
      expect(tenant.settings).toEqual({});
      expect(tenant.createdAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
      expect(tenant.updatedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
      expect(tenant.deletedAt).toBeNull();
    });

    it('creates a tenant with all fields specified', async () => {
      const tenant = await store.createTenant({
        name: 'Enterprise Inc',
        slug: 'enterprise-inc',
        externalId: 'stripe_cus_123',
        plan: 'enterprise',
        settings: { maxUsers: 100 },
      });

      expect(tenant.plan).toBe('enterprise');
      expect(tenant.externalId).toBe('stripe_cus_123');
      expect(tenant.settings).toEqual({ maxUsers: 100 });
    });

    it('throws on duplicate slug', async () => {
      await store.createTenant({ name: 'First', slug: 'duplicate' });
      await expect(
        store.createTenant({ name: 'Second', slug: 'duplicate' }),
      ).rejects.toThrow(/slug.*duplicate/i);
    });

    it('throws on duplicate externalId', async () => {
      await store.createTenant({
        name: 'First',
        slug: 'first',
        externalId: 'ext-123',
      });
      await expect(
        store.createTenant({
          name: 'Second',
          slug: 'second',
          externalId: 'ext-123',
        }),
      ).rejects.toThrow(/externalId.*ext-123/i);
    });

    it('allows null externalId on multiple tenants', async () => {
      const t1 = await store.createTenant({ name: 'A', slug: 'a' });
      const t2 = await store.createTenant({ name: 'B', slug: 'b' });
      expect(t1.externalId).toBeNull();
      expect(t2.externalId).toBeNull();
    });
  });

  // -----------------------------------------------------------------------
  // getTenant
  // -----------------------------------------------------------------------

  describe('getTenant', () => {
    it('returns null for nonexistent ID', async () => {
      expect(await store.getTenant('00000000-0000-0000-0000-999999999999')).toBeNull();
    });

    it('returns the tenant by id', async () => {
      const created = await store.createTenant({ name: 'Test', slug: 'test' });
      const found = await store.getTenant(created.id);
      expect(found).not.toBeNull();
      expect(found!.name).toBe('Test');
    });
  });

  // -----------------------------------------------------------------------
  // getTenantByExternalId
  // -----------------------------------------------------------------------

  describe('getTenantByExternalId', () => {
    it('returns the correct tenant', async () => {
      await store.createTenant({
        name: 'External',
        slug: 'external',
        externalId: 'ext-abc',
      });

      const found = await store.getTenantByExternalId('ext-abc');
      expect(found).not.toBeNull();
      expect(found!.slug).toBe('external');
    });

    it('returns null for unknown externalId', async () => {
      expect(await store.getTenantByExternalId('unknown')).toBeNull();
    });
  });

  // -----------------------------------------------------------------------
  // getTenantBySlug
  // -----------------------------------------------------------------------

  describe('getTenantBySlug', () => {
    it('returns the correct tenant', async () => {
      await store.createTenant({ name: 'By Slug', slug: 'by-slug' });

      const found = await store.getTenantBySlug('by-slug');
      expect(found).not.toBeNull();
      expect(found!.name).toBe('By Slug');
    });

    it('returns null for unknown slug', async () => {
      expect(await store.getTenantBySlug('nonexistent')).toBeNull();
    });
  });

  // -----------------------------------------------------------------------
  // updateTenant
  // -----------------------------------------------------------------------

  describe('updateTenant', () => {
    it('updates only specified fields', async () => {
      const tenant = await store.createTenant({
        name: 'Original',
        slug: 'original',
        plan: 'free',
      });

      const updated = await store.updateTenant(tenant.id, { name: 'Updated' });
      expect(updated.name).toBe('Updated');
      expect(updated.slug).toBe('original');
      expect(updated.plan).toBe('free');
    });

    it('bumps updatedAt', async () => {
      const tenant = await store.createTenant({ name: 'Bump', slug: 'bump' });
      const originalUpdatedAt = tenant.updatedAt;

      // Small delay to ensure different timestamp
      await new Promise((resolve) => setTimeout(resolve, 5));
      const updated = await store.updateTenant(tenant.id, { plan: 'pro' });
      expect(updated.updatedAt).not.toBe(originalUpdatedAt);
    });

    it('throws for nonexistent tenant', async () => {
      await expect(
        store.updateTenant('nonexistent-id', { name: 'Nope' }),
      ).rejects.toThrow(/not found/i);
    });

    it('throws on duplicate slug when updating', async () => {
      await store.createTenant({ name: 'First', slug: 'first-slug' });
      const second = await store.createTenant({ name: 'Second', slug: 'second-slug' });

      await expect(
        store.updateTenant(second.id, { slug: 'first-slug' }),
      ).rejects.toThrow(/slug.*first-slug/i);
    });
  });

  // -----------------------------------------------------------------------
  // deleteTenant (soft delete)
  // -----------------------------------------------------------------------

  describe('deleteTenant', () => {
    it('sets deletedAt (soft delete)', async () => {
      const tenant = await store.createTenant({ name: 'ToDelete', slug: 'to-delete' });
      await store.deleteTenant(tenant.id);

      // getTenant should return null for soft-deleted
      expect(await store.getTenant(tenant.id)).toBeNull();
    });

    it('throws for nonexistent tenant', async () => {
      await expect(store.deleteTenant('nonexistent')).rejects.toThrow(/not found/i);
    });
  });

  // -----------------------------------------------------------------------
  // listTenants
  // -----------------------------------------------------------------------

  describe('listTenants', () => {
    it('excludes soft-deleted tenants', async () => {
      const tenant = await store.createTenant({ name: 'Deletable', slug: 'deletable' });
      await store.createTenant({ name: 'Keeper', slug: 'keeper' });

      await store.deleteTenant(tenant.id);

      const tenants = await store.listTenants();
      expect(tenants.find((t) => t.slug === 'deletable')).toBeUndefined();
      expect(tenants.find((t) => t.slug === 'keeper')).toBeDefined();
    });

    it('includes the default tenant', async () => {
      const tenants = await store.listTenants();
      expect(tenants.some((t) => t.id === DEFAULT_TENANT_ID)).toBe(true);
    });
  });
});
