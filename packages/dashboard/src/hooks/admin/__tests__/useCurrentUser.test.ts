import { renderHook } from '@testing-library/react';
import { useCurrentUser } from '../useCurrentUser.js';
import { useAdminStore } from '../../../stores/admin/store.js';
import { OWNER_USER, ADMIN_USER, MEMBER_USER } from '../../../test/fixtures.js';

// Mock the store
const mockLoadCurrentUser = vi.fn();

describe('useCurrentUser', () => {
  beforeEach(() => {
    // Reset Zustand store
    useAdminStore.setState({
      currentUser: null,
      currentUserLoading: false,
      currentUserError: null,
      loadCurrentUser: mockLoadCurrentUser,
    });
    vi.clearAllMocks();
  });

  it('calls loadCurrentUser on mount when no user loaded', () => {
    renderHook(() => useCurrentUser());
    expect(mockLoadCurrentUser).toHaveBeenCalledOnce();
  });

  it('does not call loadCurrentUser when user already exists', () => {
    useAdminStore.setState({ currentUser: OWNER_USER });
    renderHook(() => useCurrentUser());
    expect(mockLoadCurrentUser).not.toHaveBeenCalled();
  });

  it('does not call loadCurrentUser while loading', () => {
    useAdminStore.setState({ currentUserLoading: true });
    renderHook(() => useCurrentUser());
    expect(mockLoadCurrentUser).not.toHaveBeenCalled();
  });

  it('returns isOwner=true, isAdmin=true for owner role', () => {
    useAdminStore.setState({ currentUser: OWNER_USER });
    const { result } = renderHook(() => useCurrentUser());
    expect(result.current.isOwner).toBe(true);
    expect(result.current.isAdmin).toBe(true);
  });

  it('returns isOwner=false, isAdmin=true for admin role', () => {
    useAdminStore.setState({ currentUser: ADMIN_USER });
    const { result } = renderHook(() => useCurrentUser());
    expect(result.current.isOwner).toBe(false);
    expect(result.current.isAdmin).toBe(true);
  });

  it('returns isOwner=false, isAdmin=false for member role', () => {
    useAdminStore.setState({ currentUser: MEMBER_USER });
    const { result } = renderHook(() => useCurrentUser());
    expect(result.current.isOwner).toBe(false);
    expect(result.current.isAdmin).toBe(false);
  });
});
