// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import { TenantSecretsPage } from '../TenantSecretsPage.js';

const mockUseCurrentTenant = vi.fn();
vi.mock('../../../hooks/orgs/useCurrentTenant.js', () => ({
  useCurrentTenant: () => mockUseCurrentTenant(),
}));

const mockList = vi.fn();
const mockCreate = vi.fn();
vi.mock('../../../services/secrets/secrets-api-client.js', async () => {
  const actual = await vi.importActual<
    typeof import('../../../services/secrets/secrets-api-client.js')
  >('../../../services/secrets/secrets-api-client.js');
  return {
    ...actual,
    tenantSecretsApi: (_tenantId: string) => ({
      list: () => mockList(),
      create: (b: unknown) => mockCreate(b),
      get: vi.fn(),
      listVersions: vi.fn(),
      rotate: vi.fn(),
      retireVersion: vi.fn(),
    }),
  };
});

describe('TenantSecretsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows a loading state while useCurrentTenant resolves', () => {
    mockUseCurrentTenant.mockReturnValue({
      loading: true,
      tenantId: null,
      role: null,
      me: null,
      error: null,
      reload: vi.fn(),
    });

    render(<TenantSecretsPage />);
    expect(screen.getByText(/Loading/i)).toBeInTheDocument();
  });

  it('renders the org-picker hint when no active tenant is resolved', () => {
    mockUseCurrentTenant.mockReturnValue({
      loading: false,
      tenantId: null,
      role: null,
      me: null,
      error: null,
      reload: vi.fn(),
    });

    render(<TenantSecretsPage />);
    expect(
      screen.getByText(/No active tenant selected/i),
    ).toBeInTheDocument();
  });

  it('renders the list view scoped to the active tenant', async () => {
    mockUseCurrentTenant.mockReturnValue({
      loading: false,
      tenantId: '22222222-2222-2222-2222-222222222222',
      role: 'admin',
      me: null,
      error: null,
      reload: vi.fn(),
    });
    mockList.mockResolvedValueOnce({ secrets: [] });

    render(<TenantSecretsPage />);

    await waitFor(() => {
      expect(
        screen.getByRole('heading', { level: 1, name: /Organization secrets/i }),
      ).toBeInTheDocument();
    });
    // Empty-state copy from the brief AC9.
    expect(
      await screen.findByText(/When you create a tenant-scoped DB user/),
    ).toBeInTheDocument();
    expect(mockList).toHaveBeenCalled();
  });
});
