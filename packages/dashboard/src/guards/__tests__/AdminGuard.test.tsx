import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AdminGuard, ForbiddenPage } from '../AdminGuard.js';

// Mock useCurrentUser hook
const mockUseCurrentUser = vi.fn();
vi.mock('../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

function renderGuard(children: React.ReactNode = <div>Protected Content</div>) {
  return render(
    <MemoryRouter initialEntries={['/admin']}>
      <AdminGuard>{children}</AdminGuard>
    </MemoryRouter>,
  );
}

describe('AdminGuard', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('shows loading spinner while fetching user', () => {
    mockUseCurrentUser.mockReturnValue({
      user: null,
      loading: true,
      isAdmin: false,
    });
    renderGuard();
    // LoadingSpinner renders as a div with animate-spin
    expect(document.querySelector('.animate-spin')).toBeInTheDocument();
    expect(screen.queryByText('Protected Content')).not.toBeInTheDocument();
  });

  it('redirects when user is null and not loading', () => {
    mockUseCurrentUser.mockReturnValue({
      user: null,
      loading: false,
      isAdmin: false,
    });
    renderGuard();
    expect(screen.queryByText('Protected Content')).not.toBeInTheDocument();
  });

  it('redirects when user has member role', () => {
    mockUseCurrentUser.mockReturnValue({
      user: { id: 'u1', role: 'member', username: 'test', githubId: 1 },
      loading: false,
      isAdmin: false,
    });
    renderGuard();
    expect(screen.queryByText('Protected Content')).not.toBeInTheDocument();
  });

  it('renders children for admin user', () => {
    mockUseCurrentUser.mockReturnValue({
      user: { id: 'u1', role: 'admin', username: 'test', githubId: 1 },
      loading: false,
      isAdmin: true,
    });
    renderGuard();
    expect(screen.getByText('Protected Content')).toBeInTheDocument();
  });

  it('renders children for owner user', () => {
    mockUseCurrentUser.mockReturnValue({
      user: { id: 'u1', role: 'owner', username: 'test', githubId: 1 },
      loading: false,
      isAdmin: true,
    });
    renderGuard();
    expect(screen.getByText('Protected Content')).toBeInTheDocument();
  });
});

describe('ForbiddenPage', () => {
  it('renders 403 message with link to dashboard', () => {
    render(<ForbiddenPage />);
    expect(screen.getByText('403')).toBeInTheDocument();
    expect(screen.getByText('Access Denied')).toBeInTheDocument();
    expect(screen.getByText('Go to Dashboard')).toBeInTheDocument();
  });
});
