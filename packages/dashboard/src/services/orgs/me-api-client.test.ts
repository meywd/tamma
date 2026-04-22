import { describe, expect, it } from 'vitest';
import { isTenantAdmin, resolveActiveTenantRole } from './me-api-client.js';

describe('resolveActiveTenantRole', () => {
  it('returns the role for the active tenant when membership exists', () => {
    const me = {
      id: 'u1',
      email: 'a@example.com',
      displayName: 'Alice',
      githubId: null,
      username: null,
      role: 'user',
      platformRole: 'user',
      authMethod: 'password',
      tenantId: 't1',
      memberships: [
        { tenantId: 't1', tenantName: 'Acme', role: 'admin' as const },
        { tenantId: 't2', tenantName: 'Other', role: 'member' as const },
      ],
    };
    expect(resolveActiveTenantRole(me)).toEqual({ tenantId: 't1', role: 'admin' });
  });

  it('returns null when no active tenant is set', () => {
    const me = {
      id: 'u1',
      email: 'a@example.com',
      displayName: 'Alice',
      githubId: null,
      username: null,
      role: 'user',
      platformRole: 'user',
      authMethod: 'password',
      tenantId: null,
      memberships: [
        { tenantId: 't1', tenantName: 'Acme', role: 'admin' as const },
      ],
    };
    expect(resolveActiveTenantRole(me)).toBeNull();
  });

  it('returns null when active tenant id has no matching membership', () => {
    const me = {
      id: 'u1',
      email: 'a@example.com',
      displayName: 'Alice',
      githubId: null,
      username: null,
      role: 'user',
      platformRole: 'user',
      authMethod: 'password',
      tenantId: 't-orphan',
      memberships: [
        { tenantId: 't1', tenantName: 'Acme', role: 'admin' as const },
      ],
    };
    expect(resolveActiveTenantRole(me)).toBeNull();
  });
});

describe('isTenantAdmin', () => {
  it('returns true for owner + admin', () => {
    expect(isTenantAdmin('owner')).toBe(true);
    expect(isTenantAdmin('admin')).toBe(true);
  });

  it('returns false for member + null', () => {
    expect(isTenantAdmin('member')).toBe(false);
    expect(isTenantAdmin(null)).toBe(false);
  });
});
