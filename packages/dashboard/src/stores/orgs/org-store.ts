/**
 * Org Zustand Store (Story 18-8)
 *
 * Tenant-admin state: members table, pending invites, audit-log rows.
 * Mirrors the shape of `useAdminStore` for the platform-admin panel —
 * separate store keeps tenant scope cleanly separated from platform
 * scope so routing between tenants doesn't accidentally cross-pollute
 * cached data.
 */

import { create } from 'zustand';
import {
  orgMembersApi,
  orgInvitesApi,
  orgAuditApi,
  type AuditEvent,
  type OrgMember,
  type PendingInvite,
  type TenantRole,
} from '../../services/orgs/org-api-client.js';

export interface OrgState {
  /** The tenant the store is currently scoped to. Switching tenants
   *  via {@link setActiveTenant} clears all cached data. */
  activeTenantId: string | null;
  setActiveTenant: (tenantId: string | null) => void;

  // ── Members ───────────────────────────────────────────────────────────
  members: OrgMember[];
  membersTotal: number;
  membersLoading: boolean;
  membersError: string | null;
  loadMembers: (options?: { limit?: number; offset?: number }) => Promise<void>;
  updateMemberRole: (userId: string, role: TenantRole) => Promise<void>;
  removeMember: (userId: string) => Promise<void>;

  // ── Invites ───────────────────────────────────────────────────────────
  invites: PendingInvite[];
  invitesLoading: boolean;
  invitesError: string | null;
  loadInvites: () => Promise<void>;
  createInvite: (email: string, role: TenantRole) => Promise<void>;
  resendInvite: (inviteId: string) => Promise<void>;
  revokeInvite: (inviteId: string) => Promise<void>;

  // ── Audit ─────────────────────────────────────────────────────────────
  auditEvents: AuditEvent[];
  auditTotal: number;
  auditLoading: boolean;
  auditError: string | null;
  loadAudit: (options?: { limit?: number; offset?: number; type?: string }) => Promise<void>;
}

function requireTenant(tenantId: string | null): asserts tenantId is string {
  if (!tenantId) {
    throw new Error('No active tenant set. Call setActiveTenant first.');
  }
}

export const useOrgStore = create<OrgState>((set, get) => ({
  activeTenantId: null,
  setActiveTenant: (tenantId) =>
    set({
      activeTenantId: tenantId,
      // Switching tenant invalidates every cached page.
      members: [],
      membersTotal: 0,
      membersError: null,
      invites: [],
      invitesError: null,
      auditEvents: [],
      auditTotal: 0,
      auditError: null,
    }),

  // ── Members ─────────────────────────────────────────────────────────
  members: [],
  membersTotal: 0,
  membersLoading: false,
  membersError: null,
  loadMembers: async (options) => {
    const tid = get().activeTenantId;
    if (!tid) return;
    set({ membersLoading: true, membersError: null });
    try {
      const result = await orgMembersApi.list(tid, { limit: 50, offset: 0, ...options });
      set({
        members: result.members,
        membersTotal: result.total,
        membersLoading: false,
      });
    } catch (err) {
      set({
        membersError: err instanceof Error ? err.message : 'Failed to load members',
        membersLoading: false,
      });
    }
  },
  updateMemberRole: async (userId, role) => {
    const tid = get().activeTenantId;
    requireTenant(tid);
    // Optimistic — update in place, revert on error.
    const previous = get().members;
    set({
      members: previous.map((m) =>
        m.userId === userId ? { ...m, role } : m,
      ),
    });
    try {
      await orgMembersApi.updateRole(tid, userId, role);
      // Refetch so derived counts (e.g. owner count) stay accurate.
      await get().loadMembers();
    } catch (err) {
      set({
        members: previous,
        membersError: err instanceof Error ? err.message : 'Failed to update role',
      });
      throw err;
    }
  },
  removeMember: async (userId) => {
    const tid = get().activeTenantId;
    requireTenant(tid);
    const previous = get().members;
    set({ members: previous.filter((m) => m.userId !== userId) });
    try {
      await orgMembersApi.remove(tid, userId);
      await get().loadMembers();
    } catch (err) {
      set({
        members: previous,
        membersError: err instanceof Error ? err.message : 'Failed to remove member',
      });
      throw err;
    }
  },

  // ── Invites ─────────────────────────────────────────────────────────
  invites: [],
  invitesLoading: false,
  invitesError: null,
  loadInvites: async () => {
    const tid = get().activeTenantId;
    if (!tid) return;
    set({ invitesLoading: true, invitesError: null });
    try {
      const result = await orgInvitesApi.list(tid);
      set({ invites: result.invites, invitesLoading: false });
    } catch (err) {
      set({
        invitesError: err instanceof Error ? err.message : 'Failed to load invites',
        invitesLoading: false,
      });
    }
  },
  createInvite: async (email, role) => {
    const tid = get().activeTenantId;
    requireTenant(tid);
    try {
      await orgInvitesApi.create(tid, { email, role });
      await get().loadInvites();
    } catch (err) {
      set({
        invitesError: err instanceof Error ? err.message : 'Failed to create invite',
      });
      throw err;
    }
  },
  resendInvite: async (inviteId) => {
    const tid = get().activeTenantId;
    requireTenant(tid);
    try {
      const result = await orgInvitesApi.resend(tid, inviteId);
      // Optimistic: update the row's expiresAt with the server response.
      set((s) => ({
        invites: s.invites.map((i) =>
          i.id === inviteId ? { ...i, expiresAt: result.expiresAt } : i,
        ),
      }));
    } catch (err) {
      set({
        invitesError: err instanceof Error ? err.message : 'Failed to resend invite',
      });
      throw err;
    }
  },
  revokeInvite: async (inviteId) => {
    const tid = get().activeTenantId;
    requireTenant(tid);
    const previous = get().invites;
    set({ invites: previous.filter((i) => i.id !== inviteId) });
    try {
      await orgInvitesApi.revoke(tid, inviteId);
    } catch (err) {
      set({
        invites: previous,
        invitesError: err instanceof Error ? err.message : 'Failed to revoke invite',
      });
      throw err;
    }
  },

  // ── Audit ───────────────────────────────────────────────────────────
  auditEvents: [],
  auditTotal: 0,
  auditLoading: false,
  auditError: null,
  loadAudit: async (options) => {
    const tid = get().activeTenantId;
    if (!tid) return;
    set({ auditLoading: true, auditError: null });
    try {
      const result = await orgAuditApi.list(tid, { limit: 50, offset: 0, ...options });
      set({
        auditEvents: result.events,
        auditTotal: result.total,
        auditLoading: false,
      });
    } catch (err) {
      set({
        auditError: err instanceof Error ? err.message : 'Failed to load audit log',
        auditLoading: false,
      });
    }
  },
}));
