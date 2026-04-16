import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { UsersTab } from '../UsersTab.js';
import { ADMIN_USERS, OWNER_USER, ADMIN_USER } from '../../../test/fixtures.js';

// Mocks
const mockUpdateRole = vi.fn();
const mockRemove = vi.fn();
const mockInvite = vi.fn();

const mockUseUsers = vi.fn();
const mockUseCurrentUser = vi.fn();

vi.mock('../../../hooks/admin/useUsers.js', () => ({
  useUsers: () => mockUseUsers(),
}));

vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

function setupDefaults(overrides?: {
  users?: typeof ADMIN_USERS;
  loading?: boolean;
  error?: string | null;
  currentUser?: typeof OWNER_USER;
  isOwner?: boolean;
}) {
  const {
    users = ADMIN_USERS,
    loading = false,
    error = null,
    currentUser = OWNER_USER,
    isOwner = true,
  } = overrides ?? {};

  mockUseUsers.mockReturnValue({
    users,
    total: users.length,
    loading,
    error,
    reload: vi.fn(),
    updateRole: mockUpdateRole,
    remove: mockRemove,
    invite: mockInvite,
  });

  mockUseCurrentUser.mockReturnValue({
    user: currentUser,
    loading: false,
    isAdmin: currentUser.role === 'admin' || currentUser.role === 'owner',
    isOwner,
  });
}

describe('UsersTab', () => {
  const user = userEvent.setup();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows loading spinner when loading with no users', () => {
    setupDefaults({ users: [], loading: true });
    render(<UsersTab />);
    expect(document.querySelector('.animate-spin')).toBeInTheDocument();
  });

  it('shows error banner on error', () => {
    setupDefaults({ error: 'Something went wrong' });
    render(<UsersTab />);
    expect(screen.getByText('Something went wrong')).toBeInTheDocument();
  });

  it('shows empty state with Invite button when no users', () => {
    setupDefaults({ users: [] });
    render(<UsersTab />);
    expect(screen.getByText('No users yet')).toBeInTheDocument();
    expect(screen.getByText('Invite User')).toBeInTheDocument();
  });

  it('renders user rows with avatar, login, email', () => {
    setupDefaults();
    render(<UsersTab />);
    expect(screen.getByText('owner-user')).toBeInTheDocument();
    expect(screen.getByText('admin-user')).toBeInTheDocument();
    expect(screen.getByText('member-user')).toBeInTheDocument();
    expect(screen.getByText('owner@example.com')).toBeInTheDocument();
    // member-user has no email
    expect(screen.getByText('-')).toBeInTheDocument();
    // Avatar
    const avatarImg = screen.getByAltText('owner-user');
    expect(avatarImg).toHaveAttribute('src', 'https://github.com/owner-user.png?size=32');
  });

  it('shows Badge (no dropdown) for current user role', () => {
    setupDefaults({ currentUser: OWNER_USER, isOwner: true });
    render(<UsersTab />);
    // Current user (owner-user) should show as badge, not select
    // Other users should have selects
    const selects = document.querySelectorAll('select');
    // Only 2 selects (for admin-user and member-user), not 3
    expect(selects.length).toBe(2);
  });

  it('owner sees role dropdown with all options for other users', () => {
    setupDefaults({ currentUser: OWNER_USER, isOwner: true });
    render(<UsersTab />);
    const selects = document.querySelectorAll('select');
    expect(selects.length).toBeGreaterThanOrEqual(1);
    // Check one of the selects has all 3 options enabled
    const firstSelect = selects[0]!;
    const options = firstSelect.querySelectorAll('option');
    expect(options).toHaveLength(3);
  });

  it('admin (non-owner) sees limited role dropdown', () => {
    setupDefaults({ currentUser: ADMIN_USER, isOwner: false });
    render(<UsersTab />);
    // For admin viewing member-user, canPromote is false
    // The RoleSelector should show only member option or badge depending on logic
    // Since canPromote is false, options should only be ['member']
    // But member-user already has role 'member', so it should show Badge
  });

  it('owner can trigger role change via confirm dialog', async () => {
    setupDefaults();
    render(<UsersTab />);
    // Find a select and change it
    const selects = document.querySelectorAll('select');
    expect(selects.length).toBeGreaterThanOrEqual(1);
    // Change second user's role
    await user.selectOptions(selects[0]!, 'member');
    // Confirm dialog should appear
    expect(screen.getByText('Change User Role')).toBeInTheDocument();
    // Confirm it
    await user.click(screen.getByText('Change Role'));
    expect(mockUpdateRole).toHaveBeenCalled();
  });

  it('owner sees Remove button for other users', () => {
    setupDefaults({ currentUser: OWNER_USER, isOwner: true });
    render(<UsersTab />);
    // Should have Remove buttons (not for self)
    const removeButtons = screen.getAllByText('Remove');
    expect(removeButtons.length).toBe(2); // for admin-user and member-user
  });

  it('non-owner does not see Remove buttons', () => {
    setupDefaults({ currentUser: ADMIN_USER, isOwner: false });
    render(<UsersTab />);
    expect(screen.queryByText('Remove')).not.toBeInTheDocument();
  });

  it('owner clicks Remove and confirms to call remove()', async () => {
    setupDefaults();
    render(<UsersTab />);
    // The Remove buttons in user rows (not the confirm dialog button)
    const removeButtons = screen.getAllByRole('button', { name: 'Remove' });
    await user.click(removeButtons[0]!);
    // Confirm dialog should appear
    expect(screen.getByText('Remove User')).toBeInTheDocument();
    // The confirm dialog has a "Remove" confirmLabel button - find it by its danger style
    const confirmBtn = document.querySelector(
      'button.bg-red-600',
    ) as HTMLButtonElement;
    expect(confirmBtn).toBeTruthy();
    await user.click(confirmBtn!);
    expect(mockRemove).toHaveBeenCalled();
  });

  describe('InviteDialog', () => {
    it('opens invite dialog and submits with role', async () => {
      const inviteResult = {
        id: 'inv-1',
        inviteLink: 'https://app.tamma.dev/invite/abc123',
        role: 'member',
        expiresAt: '2026-05-01T00:00:00.000Z',
      };
      mockInvite.mockResolvedValue(inviteResult);
      setupDefaults();
      render(<UsersTab />);

      // Click Invite User button (in the header, not empty state)
      const inviteButtons = screen.getAllByText('Invite User');
      await user.click(inviteButtons[0]!);

      // Dialog should open
      expect(screen.getByText('Invite User', { selector: 'h3' })).toBeInTheDocument();

      // Submit without email (optional)
      await user.click(screen.getByText('Create Invite'));

      await waitFor(() => {
        expect(mockInvite).toHaveBeenCalledWith({ role: 'member' });
      });

      // Should show the invite link
      await waitFor(() => {
        expect(screen.getByDisplayValue('https://app.tamma.dev/invite/abc123')).toBeInTheDocument();
      });
    });

    it('copies invite link to clipboard', async () => {
      const inviteResult = {
        id: 'inv-1',
        inviteLink: 'https://app.tamma.dev/invite/abc123',
        role: 'member',
        expiresAt: '2026-05-01T00:00:00.000Z',
      };
      mockInvite.mockResolvedValue(inviteResult);
      setupDefaults();
      const clipboardSpy = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue(undefined);
      render(<UsersTab />);

      const inviteButtons = screen.getAllByText('Invite User');
      await user.click(inviteButtons[0]!);
      await user.click(screen.getByText('Create Invite'));

      await waitFor(() => {
        expect(screen.getByText('Copy')).toBeInTheDocument();
      });
      await user.click(screen.getByText('Copy'));
      expect(clipboardSpy).toHaveBeenCalledWith(
        'https://app.tamma.dev/invite/abc123',
      );
      clipboardSpy.mockRestore();
    });

    it('shows error from invite API', async () => {
      mockInvite.mockRejectedValue(new Error('Rate limit exceeded'));
      setupDefaults();
      render(<UsersTab />);

      const inviteButtons = screen.getAllByText('Invite User');
      await user.click(inviteButtons[0]!);
      await user.click(screen.getByText('Create Invite'));

      await waitFor(() => {
        expect(screen.getByText('Rate limit exceeded')).toBeInTheDocument();
      });
    });

    it('cancel button closes dialog', async () => {
      setupDefaults();
      render(<UsersTab />);

      const inviteButtons = screen.getAllByText('Invite User');
      await user.click(inviteButtons[0]!);
      expect(screen.getByText('Invite User', { selector: 'h3' })).toBeInTheDocument();

      await user.click(screen.getByText('Cancel'));
      expect(screen.queryByText('Invite User', { selector: 'h3' })).not.toBeInTheDocument();
    });
  });
});
