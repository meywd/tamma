// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConventionsPage } from '../ConventionsPage.js';
import type { UseTenantConventionsReturn } from '../../../hooks/useTenantConventions.js';
import type { ConventionResponse } from '../../../services/admin/conventions-api-client.js';

const mockUseTenantConventions = vi.fn();
const mockUseCurrentUser = vi.fn();

vi.mock('../../../hooks/useTenantConventions.js', () => ({
  useTenantConventions: () => mockUseTenantConventions(),
}));

vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

const SAMPLE_CONVENTIONS: ConventionResponse[] = [
  {
    id: '1',
    role: 'developer',
    action: 'implement',
    body: 'Implement conventions',
    enabled: true,
    version: 1,
    source: 'system',
    isOverride: false,
    updatedAt: '2026-01-01T00:00:00.000Z',
  },
  {
    id: '2',
    role: 'tester',
    action: 'write-tests',
    body: 'Test conventions',
    enabled: true,
    version: 2,
    source: 'tenant',
    isOverride: true,
    updatedAt: '2026-02-01T00:00:00.000Z',
  },
];

function setup(opts?: {
  role?: 'owner' | 'admin' | 'member';
  conventions?: ConventionResponse[];
  loading?: boolean;
  error?: string | null;
}) {
  const conventions = opts?.conventions ?? SAMPLE_CONVENTIONS;
  const overrideCount = conventions.filter((c) => c.isOverride === true).length;
  const hookValue: UseTenantConventionsReturn = {
    conventions,
    loading: opts?.loading ?? false,
    error: opts?.error ?? null,
    overrideCount,
    fetchConventions: vi.fn().mockResolvedValue(undefined),
    get: vi.fn().mockResolvedValue(null),
    upsertOverride: vi.fn().mockResolvedValue(conventions[0]!),
    deleteOverride: vi.fn().mockResolvedValue(true),
    getSystemDefault: vi.fn().mockResolvedValue(null),
  };
  mockUseTenantConventions.mockReturnValue(hookValue);
  mockUseCurrentUser.mockReturnValue({
    user: { id: 'u1', username: 'u', githubId: 1, role: opts?.role ?? 'owner' },
    loading: false,
    isAdmin: opts?.role !== 'member',
    isOwner: opts?.role === 'owner',
  });
  return hookValue;
}

describe('ConventionsPage', () => {
  beforeEach(() => vi.clearAllMocks());

  it('renders the "Conventions" heading', () => {
    setup();
    render(<ConventionsPage />);
    expect(screen.getByRole('heading', { name: /conventions/i })).toBeInTheDocument();
  });

  it('renders the override count', () => {
    setup();
    const { container } = render(<ConventionsPage />);
    const text = container.textContent ?? '';
    expect(text).toMatch(/1\s*of 2 conventions overridden/i);
  });

  it('shows loading indicator when loading', () => {
    setup({ loading: true, conventions: [] });
    render(<ConventionsPage />);
    expect(document.querySelector('.animate-spin')).toBeInTheDocument();
  });

  it('shows error banner on error', async () => {
    setup({ error: 'Network down' });
    render(<ConventionsPage />);
    await waitFor(() => expect(screen.getByText('Network down')).toBeInTheDocument());
  });

  it('shows read-only banner for members', () => {
    setup({ role: 'member' });
    render(<ConventionsPage />);
    expect(screen.getByText(/read-only access/i)).toBeInTheDocument();
  });

  it('does not show read-only banner for admins', () => {
    setup({ role: 'admin' });
    render(<ConventionsPage />);
    expect(screen.queryByText(/read-only/i)).not.toBeInTheDocument();
  });

  it('renders override badge for overridden rows', () => {
    setup();
    render(<ConventionsPage />);
    expect(screen.getAllByText('Override')).toHaveLength(1);
  });

  it('opens editor on row click', async () => {
    const user = userEvent.setup();
    setup();
    render(<ConventionsPage />);
    const row = screen.getByTestId('tenant-conv-row-developer-implement');
    await user.click(row);
    await waitFor(() =>
      expect(screen.getByRole('dialog')).toBeInTheDocument(),
    );
  });
});
