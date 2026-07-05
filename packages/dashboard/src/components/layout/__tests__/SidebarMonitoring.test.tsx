// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { Sidebar } from '../Sidebar.js';

const mockUseCurrentUser = vi.fn();
vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

function renderSidebar(initialPath = '/monitoring') {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <Sidebar />
    </MemoryRouter>,
  );
}

describe('Sidebar — Monitoring section (Story 23-12)', () => {
  afterEach(() => vi.clearAllMocks());

  it('shows the Monitoring group and all links for admin/owner users', () => {
    mockUseCurrentUser.mockReturnValue({ isAdmin: true });
    renderSidebar();

    expect(screen.getByText('Monitoring')).toBeInTheDocument();
    // A representative sample of the ten sections plus the overview link.
    for (const label of [
      'Overview',
      'System Health',
      'Agent Monitor',
      'Event Explorer',
      'Workflows',
      'Providers',
      'Logs',
      'Infrastructure',
      'Knowledge Base',
      'Config Audit',
      'Security Audit',
    ]) {
      expect(screen.getByRole('link', { name: label })).toBeInTheDocument();
    }
  });

  it('hides the Monitoring group from non-admin members', () => {
    mockUseCurrentUser.mockReturnValue({ isAdmin: false });
    renderSidebar();

    expect(screen.queryByText('Monitoring')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'System Health' })).not.toBeInTheDocument();
  });

  it('highlights the active monitoring route', () => {
    mockUseCurrentUser.mockReturnValue({ isAdmin: true });
    renderSidebar('/monitoring/health');

    const active = screen.getByRole('link', { name: 'System Health' });
    expect(active.className).toContain('font-semibold');
    expect(active.className).toContain('bg-gray-700');

    // The Overview index link uses `end`, so it is not active on a sub-route.
    const overview = screen.getByRole('link', { name: 'Overview' });
    expect(overview.className).not.toContain('font-semibold');
  });
});
