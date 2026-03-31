/**
 * User Invite persistence.
 *
 * Manages invitation records for onboarding new users with pre-assigned roles.
 * Invitations expire after 72 hours and can only be accepted once.
 */

import type pg from 'pg';

/** A user invitation record. */
export interface UserInvite {
  id: string;
  email: string | null;
  role: 'owner' | 'admin' | 'member';
  inviteToken: string;
  invitedBy: string;
  expiresAt: string;
  acceptedAt: string | null;
  createdAt: string;
}

/** Input for creating an invitation. */
export interface CreateInviteInput {
  email: string | null;
  role: 'owner' | 'admin' | 'member';
  inviteToken: string;
  invitedBy: string;
  expiresAt: string;
}

/** Interface for invite persistence. */
export interface IInviteStore {
  /** Create a new invitation. */
  createInvite(input: CreateInviteInput): Promise<UserInvite>;

  /** Look up an invitation by its token. */
  getInviteByToken(token: string): Promise<UserInvite | null>;

  /** Mark an invitation as accepted. */
  acceptInvite(id: string): Promise<void>;

  /** List all pending (not accepted, not expired) invitations. */
  listPendingInvites(): Promise<UserInvite[]>;

  /** Revoke (delete) an invitation. */
  revokeInvite(id: string): Promise<void>;
}

/** In-memory implementation for testing and development. */
export class InMemoryInviteStore implements IInviteStore {
  private invites = new Map<string, UserInvite>();
  private nextId = 1;

  async createInvite(input: CreateInviteInput): Promise<UserInvite> {
    const id = String(this.nextId++);
    const now = new Date().toISOString();
    const invite: UserInvite = {
      id,
      email: input.email,
      role: input.role,
      inviteToken: input.inviteToken,
      invitedBy: input.invitedBy,
      expiresAt: input.expiresAt,
      acceptedAt: null,
      createdAt: now,
    };
    this.invites.set(id, invite);
    return invite;
  }

  async getInviteByToken(token: string): Promise<UserInvite | null> {
    for (const invite of this.invites.values()) {
      if (invite.inviteToken === token) return invite;
    }
    return null;
  }

  async acceptInvite(id: string): Promise<void> {
    const invite = this.invites.get(id);
    if (!invite) {
      throw new Error(`Invite not found: ${id}`);
    }
    invite.acceptedAt = new Date().toISOString();
  }

  async listPendingInvites(): Promise<UserInvite[]> {
    const now = new Date().toISOString();
    return [...this.invites.values()].filter(
      (inv) => inv.acceptedAt === null && inv.expiresAt > now,
    );
  }

  async revokeInvite(id: string): Promise<void> {
    if (!this.invites.has(id)) {
      throw new Error(`Invite not found: ${id}`);
    }
    this.invites.delete(id);
  }
}

/** PostgreSQL-backed invite store. */
export class PgInviteStore implements IInviteStore {
  constructor(private readonly pool: pg.Pool) {}

  async createInvite(input: CreateInviteInput): Promise<UserInvite> {
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO user_invites (email, role, invite_token, invited_by, expires_at)
       VALUES ($1, $2, $3, $4, $5)
       RETURNING *`,
      [input.email, input.role, input.inviteToken, input.invitedBy, input.expiresAt],
    );
    return this.mapInvite(result.rows[0]!);
  }

  async getInviteByToken(token: string): Promise<UserInvite | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM user_invites WHERE invite_token = $1',
      [token],
    );
    if (result.rows.length === 0) return null;
    return this.mapInvite(result.rows[0]!);
  }

  async acceptInvite(id: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE user_invites SET accepted_at = NOW() WHERE id = $1 AND accepted_at IS NULL',
      [id],
    );
    if (result.rowCount === 0) {
      throw new Error(`Invite not found or already accepted: ${id}`);
    }
  }

  async listPendingInvites(): Promise<UserInvite[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM user_invites WHERE accepted_at IS NULL AND expires_at > NOW() ORDER BY created_at DESC',
    );
    return result.rows.map((r) => this.mapInvite(r));
  }

  async revokeInvite(id: string): Promise<void> {
    const result = await this.pool.query(
      'DELETE FROM user_invites WHERE id = $1',
      [id],
    );
    if (result.rowCount === 0) {
      throw new Error(`Invite not found: ${id}`);
    }
  }

  private mapInvite(row: Record<string, unknown>): UserInvite {
    return {
      id: String(row['id']),
      email: row['email'] !== null && row['email'] !== undefined ? String(row['email']) : null,
      role: String(row['role']) as 'owner' | 'admin' | 'member',
      inviteToken: String(row['invite_token']),
      invitedBy: String(row['invited_by']),
      expiresAt: String(row['expires_at']),
      acceptedAt: row['accepted_at'] !== null && row['accepted_at'] !== undefined
        ? String(row['accepted_at'])
        : null,
      createdAt: String(row['created_at']),
    };
  }
}
