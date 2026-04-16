/**
 * Tenant Membership & Invite persistence.
 *
 * Manages the M:N relationship between users and tenants,
 * plus tenant-scoped invitation records.
 */

import { randomBytes, createHmac } from 'node:crypto';
import type pg from 'pg';
import type { Tenant } from './tenant-store.js';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** A membership linking a user to a tenant with a role. */
export interface TenantMembership {
  tenantId: string;
  userId: string;
  role: 'owner' | 'admin' | 'member';
  joinedAt: string;
}

/** A membership with the associated tenant data for listing. */
export interface TenantMembershipWithTenant extends TenantMembership {
  tenant: Tenant;
}

/** A tenant invitation record. */
export interface TenantInvite {
  id: string;
  tenantId: string;
  email: string;
  role: 'owner' | 'admin' | 'member';
  inviteTokenHash: string;
  invitedBy: string;
  expiresAt: string;
  acceptedAt: string | null;
  createdAt: string;
}

/** Input for creating a tenant invite. */
export interface CreateTenantInviteInput {
  tenantId: string;
  email: string;
  role: 'owner' | 'admin' | 'member';
  inviteTokenHash: string;
  invitedBy: string;
  expiresAt: string;
}

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------

/** Interface for tenant membership persistence. */
export interface ITenantMembershipStore {
  addMember(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership>;
  removeMember(tenantId: string, userId: string): Promise<void>;
  updateMemberRole(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership>;
  listMembers(tenantId: string): Promise<TenantMembership[]>;
  getUserTenants(userId: string): Promise<TenantMembership[]>;
  getMembership(tenantId: string, userId: string): Promise<TenantMembership | null>;
  countOwners(tenantId: string): Promise<number>;

  /** List all tenants a user belongs to, with full tenant data. */
  listTenantsWithMembership(userId: string): Promise<TenantMembershipWithTenant[]>;

  // Invite methods
  createInvite(input: CreateTenantInviteInput): Promise<TenantInvite>;
  getInviteById(id: string): Promise<TenantInvite | null>;
  getInviteByTokenHash(tokenHash: string): Promise<TenantInvite | null>;
  acceptInvite(id: string): Promise<void>;
  listPendingInvites(tenantId: string): Promise<TenantInvite[]>;
  revokeInvite(id: string): Promise<void>;
}

// ---------------------------------------------------------------------------
// Token helpers
// ---------------------------------------------------------------------------

/** Generate a cryptographically secure invite token (base64url). */
export function generateToken(): string {
  return randomBytes(32).toString('base64url');
}

/** Hash an invite token with SHA-256 for storage. */
export function hashToken(token: string): string {
  return createHmac('sha256', 'tamma-invite-salt').update(token).digest('hex');
}

// ---------------------------------------------------------------------------
// In-memory implementation
// ---------------------------------------------------------------------------

export class InMemoryTenantMembershipStore implements ITenantMembershipStore {
  private memberships: TenantMembership[] = [];
  private invites = new Map<string, TenantInvite>();
  private nextInviteId = 1;

  /** Optional tenant data for listTenantsWithMembership. Set by tests or via setTenantStore(). */
  tenantData = new Map<string, Tenant>();

  /** Optional reference to a tenant store for dynamic lookup in listTenantsWithMembership. */
  private _tenantStore: { getTenant(id: string): Promise<Tenant | null> } | null = null;

  /** Wire a tenant store so listTenantsWithMembership can resolve tenant data dynamically. */
  setTenantStore(store: { getTenant(id: string): Promise<Tenant | null> }): void {
    this._tenantStore = store;
  }

  async addMember(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership> {
    const existing = this.memberships.find((m) => m.tenantId === tenantId && m.userId === userId);
    if (existing) {
      throw new Error(`User ${userId} is already a member of tenant ${tenantId}`);
    }
    const membership: TenantMembership = {
      tenantId,
      userId,
      role,
      joinedAt: new Date().toISOString(),
    };
    this.memberships.push(membership);
    return { ...membership };
  }

  async removeMember(tenantId: string, userId: string): Promise<void> {
    const idx = this.memberships.findIndex((m) => m.tenantId === tenantId && m.userId === userId);
    if (idx === -1) throw new Error(`Membership not found`);
    this.memberships.splice(idx, 1);
  }

  async updateMemberRole(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership> {
    const m = this.memberships.find((m) => m.tenantId === tenantId && m.userId === userId);
    if (!m) throw new Error(`Membership not found`);
    m.role = role;
    return { ...m };
  }

  async listMembers(tenantId: string): Promise<TenantMembership[]> {
    return this.memberships
      .filter((m) => m.tenantId === tenantId)
      .map((m) => ({ ...m }));
  }

  async getUserTenants(userId: string): Promise<TenantMembership[]> {
    return this.memberships
      .filter((m) => m.userId === userId)
      .map((m) => ({ ...m }));
  }

  async getMembership(tenantId: string, userId: string): Promise<TenantMembership | null> {
    const m = this.memberships.find((m) => m.tenantId === tenantId && m.userId === userId);
    return m ? { ...m } : null;
  }

  async countOwners(tenantId: string): Promise<number> {
    return this.memberships.filter((m) => m.tenantId === tenantId && m.role === 'owner').length;
  }

  async listTenantsWithMembership(userId: string): Promise<TenantMembershipWithTenant[]> {
    const userMemberships = this.memberships.filter((m) => m.userId === userId);
    const results: TenantMembershipWithTenant[] = [];

    for (const m of userMemberships) {
      let tenant = this.tenantData.get(m.tenantId) ?? null;
      if (!tenant && this._tenantStore) {
        tenant = await this._tenantStore.getTenant(m.tenantId);
      }
      if (tenant && !tenant.deletedAt) {
        results.push({ ...m, tenant: { ...tenant } });
      }
    }

    return results;
  }

  // -- Invite methods --

  async createInvite(input: CreateTenantInviteInput): Promise<TenantInvite> {
    const id = `invite-${this.nextInviteId++}`;
    const invite: TenantInvite = {
      id,
      tenantId: input.tenantId,
      email: input.email,
      role: input.role,
      inviteTokenHash: input.inviteTokenHash,
      invitedBy: input.invitedBy,
      expiresAt: input.expiresAt,
      acceptedAt: null,
      createdAt: new Date().toISOString(),
    };
    this.invites.set(id, invite);
    return { ...invite };
  }

  async getInviteById(id: string): Promise<TenantInvite | null> {
    const inv = this.invites.get(id);
    return inv ? { ...inv } : null;
  }

  async getInviteByTokenHash(tokenHash: string): Promise<TenantInvite | null> {
    for (const inv of this.invites.values()) {
      if (inv.inviteTokenHash === tokenHash) return { ...inv };
    }
    return null;
  }

  async acceptInvite(id: string): Promise<void> {
    const inv = this.invites.get(id);
    if (!inv) throw new Error(`Invite not found: ${id}`);
    inv.acceptedAt = new Date().toISOString();
  }

  async listPendingInvites(tenantId: string): Promise<TenantInvite[]> {
    const now = new Date().toISOString();
    return [...this.invites.values()]
      .filter((inv) => inv.tenantId === tenantId && inv.acceptedAt === null && inv.expiresAt > now)
      .map((inv) => ({ ...inv }));
  }

  async revokeInvite(id: string): Promise<void> {
    if (!this.invites.has(id)) throw new Error(`Invite not found: ${id}`);
    this.invites.delete(id);
  }
}

// ---------------------------------------------------------------------------
// PostgreSQL implementation
// ---------------------------------------------------------------------------

export class PgTenantMembershipStore implements ITenantMembershipStore {
  constructor(private readonly pool: pg.Pool) {}

  async addMember(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership> {
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO tenant_memberships (tenant_id, user_id, role)
       VALUES ($1, $2, $3)
       RETURNING *`,
      [tenantId, userId, role],
    );
    return this._mapMembership(result.rows[0]!);
  }

  async removeMember(tenantId: string, userId: string): Promise<void> {
    const result = await this.pool.query(
      'DELETE FROM tenant_memberships WHERE tenant_id = $1 AND user_id = $2',
      [tenantId, userId],
    );
    if (result.rowCount === 0) throw new Error('Membership not found');
  }

  async updateMemberRole(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership> {
    const result = await this.pool.query<Record<string, unknown>>(
      `UPDATE tenant_memberships SET role = $3
       WHERE tenant_id = $1 AND user_id = $2
       RETURNING *`,
      [tenantId, userId, role],
    );
    if (result.rows.length === 0) throw new Error('Membership not found');
    return this._mapMembership(result.rows[0]!);
  }

  async listMembers(tenantId: string): Promise<TenantMembership[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_memberships WHERE tenant_id = $1 ORDER BY joined_at',
      [tenantId],
    );
    return result.rows.map((r) => this._mapMembership(r));
  }

  async getUserTenants(userId: string): Promise<TenantMembership[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_memberships WHERE user_id = $1 ORDER BY joined_at',
      [userId],
    );
    return result.rows.map((r) => this._mapMembership(r));
  }

  async getMembership(tenantId: string, userId: string): Promise<TenantMembership | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_memberships WHERE tenant_id = $1 AND user_id = $2',
      [tenantId, userId],
    );
    if (result.rows.length === 0) return null;
    return this._mapMembership(result.rows[0]!);
  }

  async countOwners(tenantId: string): Promise<number> {
    const result = await this.pool.query<{ count: string }>(
      `SELECT COUNT(*) as count FROM tenant_memberships WHERE tenant_id = $1 AND role = 'owner'`,
      [tenantId],
    );
    return parseInt(result.rows[0]!.count, 10);
  }

  async listTenantsWithMembership(userId: string): Promise<TenantMembershipWithTenant[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      `SELECT tm.*, t.name, t.slug, t.plan, t.settings, t.created_at as t_created_at,
              t.updated_at as t_updated_at, t.deleted_at as t_deleted_at
       FROM tenant_memberships tm
       JOIN tenants t ON t.id = tm.tenant_id
       WHERE tm.user_id = $1 AND t.deleted_at IS NULL
       ORDER BY tm.joined_at`,
      [userId],
    );
    return result.rows.map((r) => ({
      ...this._mapMembership(r),
      tenant: {
        id: String(r['tenant_id']),
        name: String(r['name']),
        slug: String(r['slug']),
        plan: String(r['plan']) as Tenant['plan'],
        settings: (typeof r['settings'] === 'object' && r['settings'] !== null
          ? r['settings']
          : {}) as Record<string, unknown>,
        createdAt: String(r['t_created_at']),
        updatedAt: String(r['t_updated_at']),
        deletedAt: r['t_deleted_at'] ? String(r['t_deleted_at']) : null,
      },
    }));
  }

  // -- Invite methods --

  async createInvite(input: CreateTenantInviteInput): Promise<TenantInvite> {
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO tenant_invites (tenant_id, email, role, invite_token_hash, invited_by, expires_at)
       VALUES ($1, $2, $3, $4, $5, $6)
       RETURNING *`,
      [input.tenantId, input.email, input.role, input.inviteTokenHash, input.invitedBy, input.expiresAt],
    );
    return this._mapInvite(result.rows[0]!);
  }

  async getInviteById(id: string): Promise<TenantInvite | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_invites WHERE id = $1',
      [id],
    );
    if (result.rows.length === 0) return null;
    return this._mapInvite(result.rows[0]!);
  }

  async getInviteByTokenHash(tokenHash: string): Promise<TenantInvite | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_invites WHERE invite_token_hash = $1',
      [tokenHash],
    );
    if (result.rows.length === 0) return null;
    return this._mapInvite(result.rows[0]!);
  }

  async acceptInvite(id: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE tenant_invites SET accepted_at = NOW() WHERE id = $1 AND accepted_at IS NULL',
      [id],
    );
    if (result.rowCount === 0) throw new Error(`Invite not found or already accepted: ${id}`);
  }

  async listPendingInvites(tenantId: string): Promise<TenantInvite[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      `SELECT * FROM tenant_invites
       WHERE tenant_id = $1 AND accepted_at IS NULL AND expires_at > NOW()
       ORDER BY created_at DESC`,
      [tenantId],
    );
    return result.rows.map((r) => this._mapInvite(r));
  }

  async revokeInvite(id: string): Promise<void> {
    const result = await this.pool.query(
      'DELETE FROM tenant_invites WHERE id = $1',
      [id],
    );
    if (result.rowCount === 0) throw new Error(`Invite not found: ${id}`);
  }

  // -- Mapping helpers --

  private _mapMembership(row: Record<string, unknown>): TenantMembership {
    return {
      tenantId: String(row['tenant_id']),
      userId: String(row['user_id']),
      role: String(row['role']) as TenantMembership['role'],
      joinedAt: String(row['joined_at']),
    };
  }

  private _mapInvite(row: Record<string, unknown>): TenantInvite {
    return {
      id: String(row['id']),
      tenantId: String(row['tenant_id']),
      email: String(row['email']),
      role: String(row['role']) as TenantInvite['role'],
      inviteTokenHash: String(row['invite_token_hash']),
      invitedBy: String(row['invited_by']),
      expiresAt: String(row['expires_at']),
      acceptedAt: row['accepted_at'] ? String(row['accepted_at']) : null,
      createdAt: String(row['created_at']),
    };
  }
}
