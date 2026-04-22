// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AuthGuard } from '../AuthGuard.js';

// Mock useAuth hook
const mockUseAuth = vi.fn();
vi.mock('../../hooks/useAuth.js', () => ({
  useAuth: () => mockUseAuth(),
}));

function renderGuard() {
  return render(
    <MemoryRouter initialEntries={['/admin']}>
      <AuthGuard>
        <div>Authenticated Content</div>
      </AuthGuard>
    </MemoryRouter>,
  );
}

describe('AuthGuard', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('shows loading spinner while fetching auth', () => {
    mockUseAuth.mockReturnValue({ user: null, loading: true, error: null, logout: vi.fn() });
    renderGuard();
    expect(document.querySelector('.animate-spin')).toBeInTheDocument();
    expect(screen.queryByText('Authenticated Content')).not.toBeInTheDocument();
  });

  it('redirects to /login when no user', () => {
    mockUseAuth.mockReturnValue({ user: null, loading: false, error: null, logout: vi.fn() });
    renderGuard();
    expect(screen.queryByText('Authenticated Content')).not.toBeInTheDocument();
  });

  it('renders children when user is authenticated', () => {
    mockUseAuth.mockReturnValue({
      user: { id: 'u1', username: 'test', githubId: 1, role: 'member' },
      loading: false,
      error: null,
      logout: vi.fn(),
    });
    renderGuard();
    expect(screen.getByText('Authenticated Content')).toBeInTheDocument();
  });
});
