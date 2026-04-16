import { useAdminStore, type AdminState } from '../store.js';
import { ADMIN_USERS, API_KEYS, HEALTHY_SERVICES, OWNER_USER } from '../../../test/fixtures.js';

// Mock the API client
vi.mock('../../../services/admin/admin-api-client.js', () => ({
  authApi: {
    getMe: vi.fn(),
  },
  usersApi: {
    list: vi.fn(),
    updateRole: vi.fn(),
    remove: vi.fn(),
    invite: vi.fn(),
  },
  apiKeysApi: {
    list: vi.fn(),
    create: vi.fn(),
    revoke: vi.fn(),
  },
  systemHealthApi: {
    getHealth: vi.fn(),
  },
}));

// Import mocked modules
const { authApi, usersApi, apiKeysApi, systemHealthApi } = await import(
  '../../../services/admin/admin-api-client.js'
);

function resetStore() {
  useAdminStore.setState({
    currentUser: null,
    currentUserLoading: false,
    currentUserError: null,
    users: [],
    usersTotal: 0,
    usersLoading: false,
    usersError: null,
    apiKeys: [],
    apiKeysUserId: null,
    apiKeysLoading: false,
    apiKeysError: null,
    services: [],
    healthLoading: false,
    healthError: null,
  } as Partial<AdminState> as AdminState);
}

describe('Admin Store', () => {
  beforeEach(() => {
    resetStore();
    vi.clearAllMocks();
  });

  describe('loadCurrentUser', () => {
    it('sets currentUser on success', async () => {
      vi.mocked(authApi.getMe).mockResolvedValue(OWNER_USER);
      await useAdminStore.getState().loadCurrentUser();
      const state = useAdminStore.getState();
      expect(state.currentUser).toEqual(OWNER_USER);
      expect(state.currentUserLoading).toBe(false);
      expect(state.currentUserError).toBeNull();
    });

    it('sets error on failure', async () => {
      vi.mocked(authApi.getMe).mockRejectedValue(new Error('Network error'));
      await useAdminStore.getState().loadCurrentUser();
      const state = useAdminStore.getState();
      expect(state.currentUser).toBeNull();
      expect(state.currentUserLoading).toBe(false);
      expect(state.currentUserError).toBe('Network error');
    });
  });

  describe('loadUsers', () => {
    it('sets users and total on success', async () => {
      vi.mocked(usersApi.list).mockResolvedValue({ users: ADMIN_USERS, total: 3 });
      await useAdminStore.getState().loadUsers();
      const state = useAdminStore.getState();
      expect(state.users).toEqual(ADMIN_USERS);
      expect(state.usersTotal).toBe(3);
      expect(state.usersLoading).toBe(false);
    });

    it('sets error on failure', async () => {
      vi.mocked(usersApi.list).mockRejectedValue(new Error('Server down'));
      await useAdminStore.getState().loadUsers();
      const state = useAdminStore.getState();
      expect(state.usersError).toBe('Server down');
      expect(state.usersLoading).toBe(false);
    });
  });

  describe('updateUserRole', () => {
    it('calls API and reloads users on success', async () => {
      vi.mocked(usersApi.updateRole).mockResolvedValue({ user: ADMIN_USERS[0]! });
      vi.mocked(usersApi.list).mockResolvedValue({ users: ADMIN_USERS, total: 3 });
      await useAdminStore.getState().updateUserRole('user-1', 'admin');
      expect(usersApi.updateRole).toHaveBeenCalledWith('user-1', 'admin');
      expect(usersApi.list).toHaveBeenCalled();
    });

    it('sets error and rethrows on failure', async () => {
      vi.mocked(usersApi.updateRole).mockRejectedValue(new Error('Forbidden'));
      await expect(useAdminStore.getState().updateUserRole('user-1', 'admin')).rejects.toThrow(
        'Forbidden',
      );
      expect(useAdminStore.getState().usersError).toBe('Forbidden');
    });
  });

  describe('removeUser', () => {
    it('calls API and reloads users on success', async () => {
      vi.mocked(usersApi.remove).mockResolvedValue({ ok: true });
      vi.mocked(usersApi.list).mockResolvedValue({ users: ADMIN_USERS.slice(1), total: 2 });
      await useAdminStore.getState().removeUser('user-1');
      expect(usersApi.remove).toHaveBeenCalledWith('user-1');
      expect(usersApi.list).toHaveBeenCalled();
    });
  });

  describe('createInvite', () => {
    it('returns invite result from API', async () => {
      const inviteResult = {
        id: 'inv-1',
        inviteLink: 'https://app.tamma.dev/invite/abc',
        role: 'member',
        expiresAt: '2026-05-01T00:00:00.000Z',
      };
      vi.mocked(usersApi.invite).mockResolvedValue(inviteResult);
      const result = await useAdminStore.getState().createInvite({ role: 'member' });
      expect(result).toEqual(inviteResult);
    });
  });

  describe('loadAllApiKeys', () => {
    it('aggregates keys from all users and tolerates per-user errors', async () => {
      // Set up users first
      useAdminStore.setState({ users: ADMIN_USERS });

      // First user returns keys, second throws, third returns keys
      vi.mocked(apiKeysApi.list)
        .mockResolvedValueOnce([API_KEYS[0]!])
        .mockRejectedValueOnce(new Error('user keys error'))
        .mockResolvedValueOnce([API_KEYS[1]!]);

      await useAdminStore.getState().loadAllApiKeys();
      const state = useAdminStore.getState();
      // Should have keys from user 1 and user 3, skipping user 2
      expect(state.apiKeys).toHaveLength(2);
      expect(state.apiKeysLoading).toBe(false);
    });
  });

  describe('createApiKey', () => {
    it('creates key and reloads all keys', async () => {
      useAdminStore.setState({ users: ADMIN_USERS });
      const createResult = {
        id: 'key-new',
        key: 'tmk_full_secret_key',
        prefix: 'tmk_full',
        label: 'New Key',
        createdAt: '2026-04-15T10:00:00.000Z',
      };
      vi.mocked(apiKeysApi.create).mockResolvedValue(createResult);
      vi.mocked(apiKeysApi.list).mockResolvedValue([]);

      const result = await useAdminStore.getState().createApiKey('user-1', 'New Key');
      expect(result).toEqual(createResult);
      expect(apiKeysApi.create).toHaveBeenCalledWith('user-1', 'New Key');
    });
  });

  describe('revokeApiKey', () => {
    it('revokes key and reloads', async () => {
      useAdminStore.setState({ users: ADMIN_USERS });
      vi.mocked(apiKeysApi.revoke).mockResolvedValue({ ok: true });
      vi.mocked(apiKeysApi.list).mockResolvedValue([]);
      await useAdminStore.getState().revokeApiKey('user-1', 'key-1');
      expect(apiKeysApi.revoke).toHaveBeenCalledWith('user-1', 'key-1');
    });

    it('sets error and rethrows on failure', async () => {
      vi.mocked(apiKeysApi.revoke).mockRejectedValue(new Error('Not found'));
      await expect(useAdminStore.getState().revokeApiKey('user-1', 'key-1')).rejects.toThrow(
        'Not found',
      );
      expect(useAdminStore.getState().apiKeysError).toBe('Not found');
    });
  });

  describe('loadHealth', () => {
    it('sets services on success', async () => {
      vi.mocked(systemHealthApi.getHealth).mockResolvedValue({
        services: HEALTHY_SERVICES,
        checkedAt: '2026-04-15T10:00:00.000Z',
      });
      await useAdminStore.getState().loadHealth();
      const state = useAdminStore.getState();
      expect(state.services).toEqual(HEALTHY_SERVICES);
      expect(state.healthLoading).toBe(false);
    });
  });
});
