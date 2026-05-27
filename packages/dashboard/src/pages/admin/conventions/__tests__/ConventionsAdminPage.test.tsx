// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConventionsAdminPage } from '../ConventionsAdminPage.js';
import type { ConventionResponse } from '../../../../services/admin/conventions-api-client.js';

const mockUseAdminConventions = vi.fn();

vi.mock('../../../../hooks/admin/useAdminConventions.js', () => ({
  useAdminConventions: () => mockUseAdminConventions(),
}));

// Stub child components that make their own network calls so we can stay
// focused on the page-level rendering and interaction.
vi.mock('../../../../components/conventions/ConventionEditor.js', () => ({
  ConventionEditor: ({
    convention,
    isNew,
    onClose,
  }: {
    convention: { role: string; action: string } | null;
    isNew: boolean;
    onClose: () => void;
  }) => (
    <div data-testid="convention-editor-stub">
      {isNew ? 'new-convention' : `${convention?.role}/${convention?.action}`}
      <button type="button" onClick={onClose}>
        close
      </button>
    </div>
  ),
}));

function makeConvention(role: string, action: string): ConventionResponse {
  return {
    id: `${role}-${action}`,
    role,
    action,
    body: `Convention body for ${role}/${action}`,
    enabled: true,
    version: 1,
    source: 'system',
    updatedAt: '2026-01-01T00:00:00.000Z',
  };
}

function setup(overrides?: Partial<ReturnType<typeof mockUseAdminConventions>>) {
  const defaults = {
    conventions: [
      makeConvention('developer', 'implement'),
      makeConvention('developer', 'plan'),
      makeConvention('tester', 'write-tests'),
    ],
    roles: ['developer', 'tester'],
    actions: ['implement', 'plan', 'write-tests'],
    eligiblePairs: [
      { role: 'developer', action: 'implement' },
      { role: 'developer', action: 'plan' },
      { role: 'tester', action: 'write-tests' },
    ],
    loading: false,
    error: null,
    reload: vi.fn(),
    getDefault: vi.fn().mockResolvedValue(null),
    upsert: vi.fn(),
    reset: vi.fn(),
    remove: vi.fn(),
    ...overrides,
  };
  mockUseAdminConventions.mockReturnValue(defaults);
  return defaults;
}

describe('ConventionsAdminPage', () => {
  const user = userEvent.setup();

  beforeEach(() => {
    mockUseAdminConventions.mockReset();
  });

  it('shows a spinner while initial load is in flight', () => {
    setup({ conventions: [], loading: true });
    render(<ConventionsAdminPage />);
    expect(document.querySelector('.animate-spin')).not.toBeNull();
  });

  it('shows an error banner with retry on load failure', async () => {
    const reload = vi.fn();
    setup({ conventions: [], loading: false, error: 'Network error', reload });
    render(<ConventionsAdminPage />);
    expect(screen.getByText('Network error')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /retry/i }));
    expect(reload).toHaveBeenCalled();
  });

  it('renders the conventions table with rows from the snapshot', () => {
    setup();
    render(<ConventionsAdminPage />);
    expect(screen.getByTestId('convention-row-developer-implement')).toBeInTheDocument();
    expect(screen.getByTestId('convention-row-developer-plan')).toBeInTheDocument();
    expect(screen.getByTestId('convention-row-tester-write-tests')).toBeInTheDocument();
  });

  it('shows System Seed badges for every row', () => {
    setup();
    render(<ConventionsAdminPage />);
    const badges = screen.getAllByText('System Seed');
    expect(badges.length).toBe(3);
  });

  it('opens the editor when a row is clicked', async () => {
    setup();
    render(<ConventionsAdminPage />);
    await user.click(screen.getByTestId('convention-row-developer-implement'));
    await waitFor(() =>
      expect(screen.getByTestId('convention-editor-stub')).toBeInTheDocument(),
    );
    expect(screen.getByTestId('convention-editor-stub').textContent).toContain(
      'developer/implement',
    );
  });

  it('opens a blank editor for a new convention', async () => {
    setup();
    render(<ConventionsAdminPage />);
    await user.click(screen.getByRole('button', { name: /new convention/i }));
    await waitFor(() =>
      expect(screen.getByTestId('convention-editor-stub')).toBeInTheDocument(),
    );
    expect(screen.getByTestId('convention-editor-stub').textContent).toContain('new-convention');
  });

  it('closes the editor when onClose is called', async () => {
    setup();
    render(<ConventionsAdminPage />);
    await user.click(screen.getByTestId('convention-row-developer-implement'));
    await waitFor(() =>
      expect(screen.getByTestId('convention-editor-stub')).toBeInTheDocument(),
    );
    await user.click(screen.getByRole('button', { name: /close/i }));
    await waitFor(() =>
      expect(screen.queryByTestId('convention-editor-stub')).not.toBeInTheDocument(),
    );
  });

  it('shows the page heading', () => {
    setup();
    render(<ConventionsAdminPage />);
    expect(screen.getByRole('heading', { name: /system conventions/i })).toBeInTheDocument();
  });
});
