// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { TenantAdminGuard } from '../TenantAdminGuard.js';

const mockUseCurrentTenant = vi.fn();
vi.mock('../../hooks/orgs/useCurrentTenant.js', () => ({
  useCurrentTenant: () => mockUseCurrentTenant(),
}));

function renderGuard(children: React.ReactNode = <div>Protected Content</div>) {
  return render(
    <MemoryRouter initialEntries={['/settings/organization']}>
      <TenantAdminGuard>{children}</TenantAdminGuard>
    </MemoryRouter>,
  );
}

describe('TenantAdminGuard', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it('shows loading spinner while resolving identity', () => {
    mockUseCurrentTenant.mockReturnValue({
      loading: true,
      role: null,
      tenantId: null,
      error: null,
      me: null,
      reload: vi.fn(),
    });
    renderGuard();
    expect(document.querySelector('.animate-spin')).toBeInTheDocument();
    expect(screen.queryByText('Protected Content')).not.toBeInTheDocument();
  });

  it('renders error UI when /auth/me failed', () => {
    mockUseCurrentTenant.mockReturnValue({
      loading: false,
      role: null,
      tenantId: null,
      error: 'network down',
      me: null,
      reload: vi.fn(),
    });
    renderGuard();
    expect(screen.getByText(/Failed to verify your tenant role/i)).toBeInTheDocument();
  });

  it('renders no-active-tenant message when user has none', () => {
    mockUseCurrentTenant.mockReturnValue({
      loading: false,
      role: null,
      tenantId: null,
      error: null,
      me: { id: 'u1', tenantId: null, memberships: [] },
      reload: vi.fn(),
    });
    renderGuard();
    expect(screen.getByText('No active organization')).toBeInTheDocument();
    expect(screen.queryByText('Protected Content')).not.toBeInTheDocument();
  });

  it('renders 403 page when user is a tenant member (not admin)', () => {
    mockUseCurrentTenant.mockReturnValue({
      loading: false,
      role: 'member',
      tenantId: 't1',
      error: null,
      me: { id: 'u1', tenantId: 't1', memberships: [] },
      reload: vi.fn(),
    });
    renderGuard();
    expect(screen.getByText('403')).toBeInTheDocument();
    expect(screen.getByText('Admin access required')).toBeInTheDocument();
    expect(screen.queryByText('Protected Content')).not.toBeInTheDocument();
  });

  it('renders children when user is tenant admin', () => {
    mockUseCurrentTenant.mockReturnValue({
      loading: false,
      role: 'admin',
      tenantId: 't1',
      error: null,
      me: { id: 'u1', tenantId: 't1', memberships: [] },
      reload: vi.fn(),
    });
    renderGuard();
    expect(screen.getByText('Protected Content')).toBeInTheDocument();
  });

  it('renders children when user is tenant owner', () => {
    mockUseCurrentTenant.mockReturnValue({
      loading: false,
      role: 'owner',
      tenantId: 't1',
      error: null,
      me: { id: 'u1', tenantId: 't1', memberships: [] },
      reload: vi.fn(),
    });
    renderGuard();
    expect(screen.getByText('Protected Content')).toBeInTheDocument();
  });
});
