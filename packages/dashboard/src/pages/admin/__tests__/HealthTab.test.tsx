import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HealthTab } from '../HealthTab.js';
import { HEALTHY_SERVICES } from '../../../test/fixtures.js';

const mockReload = vi.fn();
const mockUseSystemHealth = vi.fn();

vi.mock('../../../hooks/admin/useSystemHealth.js', () => ({
  useSystemHealth: () => mockUseSystemHealth(),
}));

function setupDefaults(overrides?: {
  services?: typeof HEALTHY_SERVICES;
  loading?: boolean;
  error?: string | null;
}) {
  const { services = HEALTHY_SERVICES, loading = false, error = null } = overrides ?? {};
  mockUseSystemHealth.mockReturnValue({ services, loading, error, reload: mockReload });
}

describe('HealthTab', () => {
  const user = userEvent.setup();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows loading spinner when loading with no data', () => {
    setupDefaults({ services: [], loading: true });
    render(<HealthTab />);
    expect(document.querySelector('.animate-spin')).toBeInTheDocument();
  });

  it('shows error banner on error', () => {
    setupDefaults({ error: 'Health check failed' });
    render(<HealthTab />);
    expect(screen.getByText('Health check failed')).toBeInTheDocument();
  });

  it('shows empty state when no services', () => {
    setupDefaults({ services: [] });
    render(<HealthTab />);
    expect(screen.getByText('No health data')).toBeInTheDocument();
  });

  it('renders service cards with name, status, response time', () => {
    setupDefaults();
    render(<HealthTab />);
    expect(screen.getByText('Tamma API')).toBeInTheDocument();
    expect(screen.getByText('PostgreSQL')).toBeInTheDocument();
    expect(screen.getByText('ELSA Server')).toBeInTheDocument();
    expect(screen.getByText('OpenSearch')).toBeInTheDocument();
    expect(screen.getByText('12ms')).toBeInTheDocument();
    expect(screen.getByText('3ms')).toBeInTheDocument();
  });

  it('unhealthy service shows red dot and details', () => {
    setupDefaults();
    render(<HealthTab />);
    // ELSA Server is unhealthy
    expect(screen.getByText('Connection refused')).toBeInTheDocument();
    // Check for red dot
    const dots = document.querySelectorAll('.bg-red-500');
    expect(dots.length).toBeGreaterThanOrEqual(1);
  });

  it('unknown status shows grey dot', () => {
    setupDefaults();
    render(<HealthTab />);
    const greyDots = document.querySelectorAll('.bg-gray-400');
    expect(greyDots.length).toBeGreaterThanOrEqual(1);
  });

  it('refresh button calls reload()', async () => {
    setupDefaults();
    render(<HealthTab />);
    await user.click(screen.getByText('Refresh'));
    expect(mockReload).toHaveBeenCalled();
  });

  it('refresh button is disabled while loading', () => {
    setupDefaults({ loading: true });
    render(<HealthTab />);
    const refreshBtn = screen.getByText('Checking...');
    expect(refreshBtn).toBeDisabled();
  });
});
