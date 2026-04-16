/**
 * Tenant Membership persistence (Story 18-3).
 *
 * Manages the M:N relationship between users and tenants via tenant_memberships,
 * and tenant invitations via tenant_invites.
 */

import { createHash, randomBytes } from 'node:crypto';
import type pg from 'pg';

/** A user's membership in a tenant. */
export interface TenantMembership {
  tenantId: string;
  userId: string;
  role: 'owner' | 'admin' | 'member';
  joinedAt: string;
}

/** A tenant invitation. */
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

/** Paginated list options. */
export interface ListMembersOptions {
  tenantId: string;
  limit: number;
  offset: number;
}

/** Paginated member list result. */
export interface ListMembersResult {
  members: TenantMembership[];
  total: number;
}

/** Interface for tenant membership persistence. */
export interface ITenantMembershipStore {
  /** Add a user to a tenant with a role. */
  addMember(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership>;
  /** Remove a user from a tenant. */
  removeMember(tenantId: string, userId: string): Promise<void>;
  /** Update a member's role within a tenant. */
  updateMemberRole(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership>;
  /** List members of a tenant with pagination. */
  listMembers(options: ListMembersOptions): Promise<ListMembersResult>;
  /** Get all tenants a user belongs to. */
  getUserTenants(userId: string): Promise<TenantMembership[]>;
  /** Get a specific membership. */
  getMembership(tenantId: string, userId: string): Promise<TenantMembership | null>;
  /** Count owners in a tenant. */
  countOwners(tenantId: string): Promise<number>;

  // --- Invite methods ---
  /** Create a tenant invite. */
  createInvite(input: CreateTenantInviteInput): Promise<TenantInvite>;
  /** Look up an invite by its token hash. */
  getInviteByTokenHash(tokenHash: string): Promise<TenantInvite | null>;
  /** Mark an invite as accepted. */
  acceptInvite(id: string): Promise<void>;
  /** List pending invites for a tenant. */
  listPendingInvites(tenantId: string): Promise<TenantInvite[]>;
  /** Revoke (delete) an invite. */
  revokeInvite(id: string): Promise<void>;

  /** Look up an invite by its primary key. */
  getInviteById(id: string): Promise<TenantInvite | null>;

  /** List all tenants a user belongs to with their membership info. */
  listTenantsWithMembership(userId: string): Promise<Array<TenantMembership & { tenant?: { id: string; name: string; slug: string; plan: string } }>>;
}

/** In-memory implementation for testing. */
export class InMemoryTenantMembershipStore implements ITenantMembershipStore {
  private memberships: TenantMembership[] = [];
  private invites = new Map<string, TenantInvite>();
  private nextInviteId = 1;

  async addMember(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership> {
    // Check for existing membership
    const existing = this.memberships.find((m) => m.tenantId === tenantId && m.userId === userId);
    if (existing) {
      throw new Error('User is already a member of this tenant');
    }
    const membership: TenantMembership = {
      tenantId,
      userId,
      role,
      joinedAt: new Date().toISOString(),
    };
    this.memberships.push(membership);
    return membership;
  }

  async removeMember(tenantId: string, userId: string): Promise<void> {
    const idx = this.memberships.findIndex((m) => m.tenantId === tenantId && m.userId === userId);
    if (idx === -1) throw new Error('Membership not found');
    this.memberships.splice(idx, 1);
  }

  async updateMemberRole(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership> {
    const membership = this.memberships.find((m) => m.tenantId === tenantId && m.userId === userId);
    if (!membership) throw new Error('Membership not found');
    membership.role = role;
    return membership;
  }

  async listMembers(options: ListMembersOptions): Promise<ListMembersResult> {
    const filtered = this.memberships.filter((m) => m.tenantId === options.tenantId);
    const total = filtered.length;
    const members = filtered.slice(options.offset, options.offset + options.limit);
    return { members, total };
  }

  async getUserTenants(userId: string): Promise<TenantMembership[]> {
    return this.memberships.filter((m) => m.userId === userId);
  }

  async getMembership(tenantId: string, userId: string): Promise<TenantMembership | null> {
    return this.memberships.find((m) => m.tenantId === tenantId && m.userId === userId) ?? null;
  }

  async countOwners(tenantId: string): Promise<number> {
    return this.memberships.filter((m) => m.tenantId === tenantId && m.role === 'owner').length;
  }

  // --- Invite methods ---

  async createInvite(input: CreateTenantInviteInput): Promise<TenantInvite> {
    const id = String(this.nextInviteId++);
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
    return invite;
  }

  async getInviteByTokenHash(tokenHash: string): Promise<TenantInvite | null> {
    for (const invite of this.invites.values()) {
      if (invite.inviteTokenHash === tokenHash) return invite;
    }
    return null;
  }

  async acceptInvite(id: string): Promise<void> {
    const invite = this.invites.get(id);
    if (!invite) throw new Error('Invite not found');
    invite.acceptedAt = new Date().toISOString();
  }

  async listPendingInvites(tenantId: string): Promise<TenantInvite[]> {
    const now = new Date().toISOString();
    return [...this.invites.values()].filter(
      (inv) => inv.tenantId === tenantId && inv.acceptedAt === null && inv.expiresAt > now,
    );
  }

  async revokeInvite(id: string): Promise<void> {
    if (!this.invites.has(id)) throw new Error('Invite not found');
    this.invites.delete(id);
  }

  async getInviteById(id: string): Promise<TenantInvite | null> {
    return this.invites.get(id) ?? null;
  }

  async listTenantsWithMembership(userId: string): Promise<Array<TenantMembership & { tenant?: { id: string; name: string; slug: string; plan: string } }>> {
    return this.memberships
      .filter((m) => m.userId === userId)
      .map((m) => ({ ...m }));
  }
}

/** PostgreSQL-backed tenant membership store. */
export class PgTenantMembershipStore implements ITenantMembershipStore {
  constructor(private readonly pool: pg.Pool) {}

  async addMember(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership> {
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO tenant_memberships (tenant_id, user_id, role)
       VALUES ($1, $2, $3) RETURNING *`,
      [tenantId, userId, role],
    );
    return this.mapMembership(result.rows[0]!);
  }

  async removeMember(tenantId: string, userId: string): Promise<void> {
    const result = await this.pool.query(
      'DELETE FROM tenant_memberships WHERE tenant_id = $1 AND user_id = $2',
      [tenantId, userId],
    );
    if (result.rowCount === 0) {
      throw new Error('Membership not found');
    }
  }

  async updateMemberRole(tenantId: string, userId: string, role: 'owner' | 'admin' | 'member'): Promise<TenantMembership> {
    const result = await this.pool.query<Record<string, unknown>>(
      `UPDATE tenant_memberships SET role = $3 WHERE tenant_id = $1 AND user_id = $2 RETURNING *`,
      [tenantId, userId, role],
    );
    if (result.rows.length === 0) {
      throw new Error('Membership not found');
    }
    return this.mapMembership(result.rows[0]!);
  }

  async listMembers(options: ListMembersOptions): Promise<ListMembersResult> {
    const countResult = await this.pool.query<Record<string, unknown>>(
      'SELECT COUNT(*)::int AS total FROM tenant_memberships WHERE tenant_id = $1',
      [options.tenantId],
    );
    const total = Number(countResult.rows[0]!['total']);

    const dataResult = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_memberships WHERE tenant_id = $1 ORDER BY joined_at LIMIT $2 OFFSET $3',
      [options.tenantId, options.limit, options.offset],
    );
    const members = dataResult.rows.map((r) => this.mapMembership(r));
    return { members, total };
  }

  async getUserTenants(userId: string): Promise<TenantMembership[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_memberships WHERE user_id = $1 ORDER BY joined_at',
      [userId],
    );
    return result.rows.map((r) => this.mapMembership(r));
  }

  async getMembership(tenantId: string, userId: string): Promise<TenantMembership | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_memberships WHERE tenant_id = $1 AND user_id = $2',
      [tenantId, userId],
    );
    if (result.rows.length === 0) return null;
    return this.mapMembership(result.rows[0]!);
  }

  async countOwners(tenantId: string): Promise<number> {
    const result = await this.pool.query<Record<string, unknown>>(
      "SELECT COUNT(*)::int AS count FROM tenant_memberships WHERE tenant_id = $1 AND role = 'owner'",
      [tenantId],
    );
    return Number(result.rows[0]!['count']);
  }

  // --- Invite methods ---

  async createInvite(input: CreateTenantInviteInput): Promise<TenantInvite> {
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO tenant_invites (tenant_id, email, role, invite_token_hash, invited_by, expires_at)
       VALUES ($1, $2, $3, $4, $5, $6) RETURNING *`,
      [input.tenantId, input.email, input.role, input.inviteTokenHash, input.invitedBy, input.expiresAt],
    );
    return this.mapInvite(result.rows[0]!);
  }

  async getInviteByTokenHash(tokenHash: string): Promise<TenantInvite | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_invites WHERE invite_token_hash = $1',
      [tokenHash],
    );
    if (result.rows.length === 0) return null;
    return this.mapInvite(result.rows[0]!);
  }

  async acceptInvite(id: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE tenant_invites SET accepted_at = NOW() WHERE id = $1 AND accepted_at IS NULL',
      [id],
    );
    if (result.rowCount === 0) {
      throw new Error('Invite not found or already accepted');
    }
  }

  async listPendingInvites(tenantId: string): Promise<TenantInvite[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_invites WHERE tenant_id = $1 AND accepted_at IS NULL AND expires_at > NOW() ORDER BY created_at DESC',
      [tenantId],
    );
    return result.rows.map((r) => this.mapInvite(r));
  }

  async revokeInvite(id: string): Promise<void> {
    const result = await this.pool.query(
      'DELETE FROM tenant_invites WHERE id = $1',
      [id],
    );
    if (result.rowCount === 0) {
      throw new Error('Invite not found');
    }
  }

  async getInviteById(id: string): Promise<TenantInvite | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM tenant_invites WHERE id = $1',
      [id],
    );
    if (result.rows.length === 0) return null;
    return this.mapInvite(result.rows[0]!);
  }

  async listTenantsWithMembership(userId: string): Promise<Array<TenantMembership & { tenant?: { id: string; name: string; slug: string; plan: string } }>> {
    const result = await this.pool.query<Record<string, unknown>>(
      `SELECT tm.*, t.id AS t_id, t.name AS t_name, t.slug AS t_slug, t.plan AS t_plan
       FROM tenant_memberships tm
       JOIN tenants t ON t.id = tm.tenant_id AND t.deleted_at IS NULL
       WHERE tm.user_id = $1
       ORDER BY tm.joined_at`,
      [userId],
    );
    return result.rows.map((r) => ({
      ...this.mapMembership(r),
      tenant: {
        id: String(r['t_id']),
        name: String(r['t_name']),
        slug: String(r['t_slug']),
        plan: String(r['t_plan']),
      },
    }));
  }

  private mapMembership(row: Record<string, unknown>): TenantMembership {
    return {
      tenantId: String(row['tenant_id']),
      userId: String(row['user_id']),
      role: String(row['role']) as 'owner' | 'admin' | 'member',
      joinedAt: String(row['joined_at']),
    };
  }

  private mapInvite(row: Record<string, unknown>): TenantInvite {
    return {
      id: String(row['id']),
      tenantId: String(row['tenant_id']),
      email: String(row['email']),
      role: String(row['role']) as 'owner' | 'admin' | 'member',
      inviteTokenHash: String(row['invite_token_hash']),
      invitedBy: String(row['invited_by']),
      expiresAt: String(row['expires_at']),
      acceptedAt: row['accepted_at'] !== null && row['accepted_at'] !== undefined
        ? String(row['accepted_at'])
        : null,
      createdAt: String(row['created_at']),
    };
  }
}

// --- Token helper utilities ---

/** Generate a raw token (32 bytes, hex-encoded). */
export function generateToken(): string {
  return randomBytes(32).toString('hex');
}

/** Hash a raw token with SHA-256. */
export function hashToken(rawToken: string): string {
  return createHash('sha256').update(rawToken).digest('hex');
}
