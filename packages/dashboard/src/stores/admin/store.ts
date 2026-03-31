/**
 * Admin Zustand Store
 *
 * Central state for admin panel: users, API keys, system health, and current user.
 */

import { create } from 'zustand';
import type {
  AdminUser,
  ApiKeyEntry,
  ServiceHealth,
  CurrentUser,
  CreateApiKeyResult,
  InviteResult,
} from '../../services/admin/admin-api-client.js';
import {
  authApi,
  usersApi,
  apiKeysApi,
  systemHealthApi,
} from '../../services/admin/admin-api-client.js';

export interface AdminState {
  // Current user
  currentUser: CurrentUser | null;
  currentUserLoading: boolean;
  currentUserError: string | null;
  loadCurrentUser: () => Promise<void>;

  // Users
  users: AdminUser[];
  usersTotal: number;
  usersLoading: boolean;
  usersError: string | null;
  loadUsers: (options?: { limit?: number; offset?: number }) => Promise<void>;
  updateUserRole: (userId: string, role: 'owner' | 'admin' | 'member') => Promise<void>;
  removeUser: (userId: string) => Promise<void>;
  createInvite: (data: { email?: string; role: string }) => Promise<InviteResult>;

  // API Keys (per-user)
  apiKeys: ApiKeyEntry[];
  apiKeysUserId: string | null;
  apiKeysLoading: boolean;
  apiKeysError: string | null;
  loadApiKeys: (userId: string) => Promise<void>;
  loadAllApiKeys: () => Promise<void>;
  createApiKey: (userId: string, label: string) => Promise<CreateApiKeyResult>;
  revokeApiKey: (userId: string, keyId: string) => Promise<void>;

  // System Health
  services: ServiceHealth[];
  healthLoading: boolean;
  healthError: string | null;
  loadHealth: () => Promise<void>;
}

export const useAdminStore = create<AdminState>((set, get) => ({
  // Current User
  currentUser: null,
  currentUserLoading: false,
  currentUserError: null,
  loadCurrentUser: async () => {
    set({ currentUserLoading: true, currentUserError: null });
    try {
      const user = await authApi.getMe();
      set({ currentUser: user, currentUserLoading: false });
    } catch (err) {
      set({
        currentUserError: err instanceof Error ? err.message : 'Failed to load user',
        currentUserLoading: false,
      });
    }
  },

  // Users
  users: [],
  usersTotal: 0,
  usersLoading: false,
  usersError: null,
  loadUsers: async (options) => {
    set({ usersLoading: true, usersError: null });
    try {
      const result = await usersApi.list({ limit: 50, offset: 0, ...options });
      set({ users: result.users, usersTotal: result.total, usersLoading: false });
    } catch (err) {
      set({
        usersError: err instanceof Error ? err.message : 'Failed to load users',
        usersLoading: false,
      });
    }
  },

  updateUserRole: async (userId, role) => {
    try {
      await usersApi.updateRole(userId, role);
      await get().loadUsers();
    } catch (err) {
      set({ usersError: err instanceof Error ? err.message : 'Failed to update role' });
      throw err;
    }
  },

  removeUser: async (userId) => {
    try {
      await usersApi.remove(userId);
      await get().loadUsers();
    } catch (err) {
      set({ usersError: err instanceof Error ? err.message : 'Failed to remove user' });
      throw err;
    }
  },

  createInvite: async (data) => {
    const result = await usersApi.invite(data);
    return result;
  },

  // API Keys (per-user)
  apiKeys: [],
  apiKeysUserId: null,
  apiKeysLoading: false,
  apiKeysError: null,

  loadApiKeys: async (userId: string) => {
    set({ apiKeysLoading: true, apiKeysError: null, apiKeysUserId: userId });
    try {
      const keys = await apiKeysApi.list(userId);
      set({ apiKeys: keys, apiKeysLoading: false });
    } catch (err) {
      set({
        apiKeysError: err instanceof Error ? err.message : 'Failed to load API keys',
        apiKeysLoading: false,
      });
    }
  },

  loadAllApiKeys: async () => {
    const { users } = get();
    set({ apiKeysLoading: true, apiKeysError: null, apiKeysUserId: null });
    try {
      const allKeys: ApiKeyEntry[] = [];
      for (const user of users) {
        try {
          const keys = await apiKeysApi.list(user.id);
          allKeys.push(...keys);
        } catch {
          // Skip users whose keys we can't load
        }
      }
      set({ apiKeys: allKeys, apiKeysLoading: false });
    } catch (err) {
      set({
        apiKeysError: err instanceof Error ? err.message : 'Failed to load API keys',
        apiKeysLoading: false,
      });
    }
  },

  createApiKey: async (userId, label) => {
    const result = await apiKeysApi.create(userId, label);
    // Reload keys for this user
    await get().loadAllApiKeys();
    return result;
  },

  revokeApiKey: async (userId, keyId) => {
    try {
      await apiKeysApi.revoke(userId, keyId);
      await get().loadAllApiKeys();
    } catch (err) {
      set({ apiKeysError: err instanceof Error ? err.message : 'Failed to revoke API key' });
      throw err;
    }
  },

  // System Health
  services: [],
  healthLoading: false,
  healthError: null,
  loadHealth: async () => {
    set({ healthLoading: true, healthError: null });
    try {
      const result = await systemHealthApi.getHealth();
      set({ services: result.services, healthLoading: false });
    } catch (err) {
      set({
        healthError: err instanceof Error ? err.message : 'Failed to load health status',
        healthLoading: false,
      });
    }
  },
}));
