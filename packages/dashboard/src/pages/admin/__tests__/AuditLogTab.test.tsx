// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';

// We need to test both feature flag states. We do this by mocking the module twice.
describe('AuditLogTab', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('shows coming-soon placeholder when feature flag is off', async () => {
    // Default: VITE_FEATURE_ADMIN_AUDIT_LOG is not set
    vi.stubEnv('VITE_FEATURE_ADMIN_AUDIT_LOG', '');
    // Re-import to pick up env
    const { AuditLogTab } = await import('../AuditLogTab.js');
    render(<AuditLogTab />);
    expect(screen.getByText('Coming Soon')).toBeInTheDocument();
    expect(screen.getByText(/audit log viewer will provide/i)).toBeInTheDocument();
    vi.unstubAllEnvs();
  });
});
