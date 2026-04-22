// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiKeysTab } from '../ApiKeysTab.js';
import { ADMIN_USERS, API_KEYS, OWNER_USER } from '../../../test/fixtures.js';

// Mocks
const mockRevoke = vi.fn();
const mockCreate = vi.fn();

const mockUseApiKeys = vi.fn();
const mockUseUsers = vi.fn();
const mockUseCurrentUser = vi.fn();

vi.mock('../../../hooks/admin/useApiKeys.js', () => ({
  useApiKeys: () => mockUseApiKeys(),
}));

vi.mock('../../../hooks/admin/useUsers.js', () => ({
  useUsers: () => mockUseUsers(),
}));

vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

function setupDefaults(overrides?: {
  apiKeys?: typeof API_KEYS;
  loading?: boolean;
  error?: string | null;
}) {
  const { apiKeys = API_KEYS, loading = false, error = null } = overrides ?? {};

  mockUseApiKeys.mockReturnValue({
    apiKeys,
    loading,
    error,
    reload: vi.fn(),
    create: mockCreate,
    revoke: mockRevoke,
  });

  mockUseUsers.mockReturnValue({
    users: ADMIN_USERS,
    total: ADMIN_USERS.length,
    loading: false,
    error: null,
  });

  mockUseCurrentUser.mockReturnValue({
    user: OWNER_USER,
    loading: false,
    isAdmin: true,
    isOwner: true,
  });
}

describe('ApiKeysTab', () => {
  const user = userEvent.setup();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows loading spinner when loading with no keys', () => {
    setupDefaults({ apiKeys: [], loading: true });
    render(<ApiKeysTab />);
    expect(document.querySelector('.animate-spin')).toBeInTheDocument();
  });

  it('shows error banner on error', () => {
    setupDefaults({ error: 'Failed to fetch keys' });
    render(<ApiKeysTab />);
    expect(screen.getByText('Failed to fetch keys')).toBeInTheDocument();
  });

  it('shows empty state when no keys', () => {
    setupDefaults({ apiKeys: [] });
    render(<ApiKeysTab />);
    expect(screen.getByText('No API keys')).toBeInTheDocument();
  });

  it('renders key rows with prefix, label, user, dates', () => {
    setupDefaults();
    render(<ApiKeysTab />);
    expect(screen.getByText('tmk_abc1...')).toBeInTheDocument();
    expect(screen.getByText('CI Pipeline')).toBeInTheDocument();
    expect(screen.getByText('tmk_def2...')).toBeInTheDocument();
    expect(screen.getByText('Dev Machine')).toBeInTheDocument();
    // User column shows githubLogin mapped from userId
    expect(screen.getByText('owner-user')).toBeInTheDocument();
    expect(screen.getByText('admin-user')).toBeInTheDocument();
  });

  it('revoke flow: click Revoke -> confirm -> calls revoke()', async () => {
    setupDefaults();
    render(<ApiKeysTab />);
    const revokeButtons = screen.getAllByText('Revoke');
    await user.click(revokeButtons[0]!);

    // Confirm dialog
    expect(screen.getByText('Revoke API Key')).toBeInTheDocument();
    expect(screen.getByText(/Are you sure you want to revoke/)).toBeInTheDocument();

    // The ConfirmDialog has a "Revoke" button as confirmLabel
    const confirmBtn = screen.getAllByText('Revoke');
    // The last one should be the confirm button
    await user.click(confirmBtn[confirmBtn.length - 1]!);
    expect(mockRevoke).toHaveBeenCalledWith('user-1', 'key-1');
  });

  describe('CreateApiKeyDialog', () => {
    it('requires label (shows error on empty submit)', async () => {
      setupDefaults();
      render(<ApiKeysTab />);
      await user.click(screen.getByText('Create API Key'));

      // Dialog opens
      expect(screen.getByText('Create API Key', { selector: 'h3' })).toBeInTheDocument();

      // Submit without label
      await user.click(screen.getByText('Create Key'));
      expect(screen.getByText('Label is required')).toBeInTheDocument();
    });

    it('requires user selection (shows error)', async () => {
      setupDefaults();
      render(<ApiKeysTab />);
      await user.click(screen.getByText('Create API Key'));

      // Clear the user selection (set to empty)
      const userSelect = screen.getByDisplayValue(/owner-user/);
      await user.selectOptions(userSelect, '');

      // Fill in label
      const labelInput = screen.getByPlaceholderText('e.g. CI Pipeline, Dev Machine');
      await user.type(labelInput, 'Test Key');

      await user.click(screen.getByText('Create Key'));
      expect(screen.getByText('User is required')).toBeInTheDocument();
    });

    it('shows generated key on success with warning banner', async () => {
      mockCreate.mockResolvedValue({
        id: 'key-new',
        key: 'tmk_full_secret_key_value_12345',
        prefix: 'tmk_full',
        label: 'Test Key',
        createdAt: '2026-04-15T10:00:00.000Z',
      });
      setupDefaults();
      render(<ApiKeysTab />);
      await user.click(screen.getByText('Create API Key'));

      const labelInput = screen.getByPlaceholderText('e.g. CI Pipeline, Dev Machine');
      await user.type(labelInput, 'Test Key');
      await user.click(screen.getByText('Create Key'));

      await waitFor(() => {
        expect(screen.getByText('tmk_full_secret_key_value_12345')).toBeInTheDocument();
      });
      expect(screen.getByText(/You will not be able to see it again/)).toBeInTheDocument();
    });

    it('copy button writes key to clipboard and shows Copied!', async () => {
      mockCreate.mockResolvedValue({
        id: 'key-new',
        key: 'tmk_secret_123',
        prefix: 'tmk_secr',
        label: 'Test',
        createdAt: '2026-04-15T10:00:00.000Z',
      });
      setupDefaults();
      const clipboardSpy = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue(undefined);
      render(<ApiKeysTab />);
      await user.click(screen.getByText('Create API Key'));

      const labelInput = screen.getByPlaceholderText('e.g. CI Pipeline, Dev Machine');
      await user.type(labelInput, 'Test');
      await user.click(screen.getByText('Create Key'));

      await waitFor(() => {
        expect(screen.getByText('Copy')).toBeInTheDocument();
      });
      await user.click(screen.getByText('Copy'));
      expect(clipboardSpy).toHaveBeenCalledWith('tmk_secret_123');

      await waitFor(() => {
        expect(screen.getByText('Copied!')).toBeInTheDocument();
      });
      clipboardSpy.mockRestore();
    });

    it('does not persist generated key to localStorage', async () => {
      const setItemSpy = vi.spyOn(Storage.prototype, 'setItem');
      mockCreate.mockResolvedValue({
        id: 'key-new',
        key: 'tmk_secret_123',
        prefix: 'tmk_secr',
        label: 'Test',
        createdAt: '2026-04-15T10:00:00.000Z',
      });
      setupDefaults();
      render(<ApiKeysTab />);
      await user.click(screen.getByText('Create API Key'));

      const labelInput = screen.getByPlaceholderText('e.g. CI Pipeline, Dev Machine');
      await user.type(labelInput, 'Test');
      await user.click(screen.getByText('Create Key'));

      await waitFor(() => {
        expect(screen.getByText('tmk_secret_123')).toBeInTheDocument();
      });

      // Verify localStorage was NOT called with the key
      const calls = setItemSpy.mock.calls;
      const storedSecret = calls.some(
        ([, val]) => typeof val === 'string' && val.includes('tmk_secret_123'),
      );
      expect(storedSecret).toBe(false);
      setItemSpy.mockRestore();
    });
  });
});
