// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { AdminGuard } from '../../../guards/AdminGuard.js';
import { ConfigAuditPage } from '../ConfigAuditPage.js';

const mockUseCurrentUser = vi.fn();
vi.mock('../../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

function okResponse(body: unknown): Response {
  return { ok: true, status: 200, json: async () => body } as unknown as Response;
}

function errResponse(status: number, error = 'nope'): Response {
  return { ok: false, status, json: async () => ({ error }) } as unknown as Response;
}

// A curated audit record whose (redacted) payload deliberately contains a
// secret-looking value — the page must NEVER render the payload.
const LEAKED_SECRET = 'sk-LEAKED-KEY-should-never-render';

const me = { user: { id: 'u1', tenantId: 't1', role: 'owner' } };

const health = {
  providers: [
    { providerKey: 'anthropic-claude', status: 'healthy' },
    { providerKey: 'openai', status: 'down' },
  ],
};

const prompts = [
  { role: 'developer', action: 'code', source: 'tenant' },
  { role: 'reviewer', action: 'review', source: 'system' }, // system → not an override
];

const conventions = [
  { id: 'c1', role: 'developer', action: 'code', source: 'tenant', isOverride: true },
  { id: 'c2', role: 'reviewer', action: 'review', source: 'system', isOverride: false },
];

const entitlements = {
  planId: 'plan-guid',
  planVersion: 3,
  isCustom: false,
  limits: [
    {
      metricKey: 'agents',
      limitValue: 5,
      period: 'month',
      currentUsage: 2,
      remaining: 3,
      isOver: false,
    },
  ],
};

const audit = {
  records: [
    {
      id: 'a1',
      actionCategory: 'config',
      actionCode: 'CONVENTION.UPDATED.SUCCESS',
      actorLabel: 'admin@example.com',
      targetType: 'convention',
      targetId: 'developer/code',
      severity: 'notice',
      outcome: 'success',
      occurredAt: '2026-07-05T10:00:00.000Z',
      payload: '{}',
    },
    {
      id: 'a2',
      actionCategory: 'byok',
      actionCode: 'PROVIDER_KEY.CHANGED.SUCCESS',
      actorLabel: 'owner@example.com',
      targetType: 'provider',
      targetId: 'anthropic',
      severity: 'warning',
      outcome: 'success',
      occurredAt: '2026-07-05T09:00:00.000Z',
      payload: `{"secret":"${LEAKED_SECRET}"}`,
    },
    {
      id: 'a3',
      actionCategory: 'persona',
      actionCode: 'PROMPT.UPDATED.SUCCESS',
      actorLabel: null,
      targetType: 'prompt',
      targetId: 'developer/code',
      severity: 'notice',
      outcome: 'success',
      occurredAt: '2026-07-05T08:00:00.000Z',
      payload: '{}',
    },
    {
      // NOT a configuration category → must be filtered out of this page.
      id: 'a4',
      actionCategory: 'auth',
      actionCode: 'AUTH.LOGIN.SUCCESS',
      actorLabel: 'admin@example.com',
      targetType: 'user',
      targetId: 'u1',
      severity: 'info',
      outcome: 'success',
      occurredAt: '2026-07-05T07:00:00.000Z',
      payload: '{}',
    },
  ],
  nextCursor: null,
  total: 4,
  totalIsCapped: false,
};

const fetchMock = vi.fn();

function routeFetch(overrides: Partial<Record<string, unknown>> = {}): void {
  fetchMock.mockImplementation((url: string) => {
    const u = String(url);
    if (u.includes('/api/auth/me')) return Promise.resolve(okResponse(overrides.me ?? me));
    if (u.includes('/providers/health')) return Promise.resolve(okResponse(overrides.health ?? health));
    if (u.includes('/api/prompts')) return Promise.resolve(okResponse(overrides.prompts ?? prompts));
    if (u.includes('/api/conventions'))
      return Promise.resolve(okResponse(overrides.conventions ?? conventions));
    if (u.includes('/pricing/entitlements'))
      return Promise.resolve(okResponse(overrides.entitlements ?? entitlements));
    if (u.includes('/audit')) {
      if (overrides.auditStatus) return Promise.resolve(errResponse(overrides.auditStatus as number));
      return Promise.resolve(okResponse(overrides.audit ?? audit));
    }
    return Promise.resolve(okResponse({}));
  });
}

beforeEach(() => {
  fetchMock.mockReset();
  routeFetch();
  vi.stubGlobal('fetch', fetchMock);
  mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'owner' }, loading: false, isAdmin: true });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/monitoring/config']}>
      <ConfigAuditPage />
    </MemoryRouter>,
  );
}

describe('ConfigAuditPage', () => {
  it('renders the effective-config summary + detail tables from composed sources', async () => {
    renderPage();

    // Summary cards.
    expect(await screen.findByText('Providers configured')).toBeInTheDocument();
    expect(screen.getByText('Prompt overrides')).toBeInTheDocument();
    expect(screen.getByText('Convention overrides')).toBeInTheDocument();

    // Providers table (both configured providers appear).
    expect(await screen.findByText('anthropic-claude')).toBeInTheDocument();
    expect(screen.getByText('openai')).toBeInTheDocument();

    // Entitlement limits table.
    expect(screen.getByText('agents')).toBeInTheDocument();

    // Overrides table shows the tenant-overridden prompt + convention only.
    expect(screen.getAllByText('developer').length).toBeGreaterThan(0);

    // All the composed sources were queried.
    await waitFor(() => {
      const urls = fetchMock.mock.calls.map((c) => String(c[0]));
      expect(urls.some((u) => u.includes('/api/auth/me'))).toBe(true);
      expect(urls.some((u) => u.includes('/providers/health'))).toBe(true);
      expect(urls.some((u) => u.includes('/api/prompts'))).toBe(true);
      expect(urls.some((u) => u.includes('/api/conventions'))).toBe(true);
      expect(urls.some((u) => u.includes('/pricing/entitlements'))).toBe(true);
      expect(urls.some((u) => u.includes('/api/v1/orgs/t1/audit'))).toBe(true);
    });
  });

  it('renders config-change history (who/what/when) and filters out non-config categories', async () => {
    renderPage();

    // Config-relevant change rows appear.
    expect(await screen.findByText('CONVENTION.UPDATED.SUCCESS')).toBeInTheDocument();
    expect(screen.getByText('PROVIDER_KEY.CHANGED.SUCCESS')).toBeInTheDocument();
    expect(screen.getByText('admin@example.com')).toBeInTheDocument();

    // A non-config (auth) category is excluded from the Configuration Audit page.
    expect(screen.queryByText('AUTH.LOGIN.SUCCESS')).not.toBeInTheDocument();
  });

  it('NEVER renders a secret value and never reads the secret-bearing settings endpoint', async () => {
    renderPage();
    await screen.findByText('CONVENTION.UPDATED.SUCCESS');

    // The (redacted) audit payload is never surfaced — no secret leaks into the DOM.
    expect(screen.queryByText(new RegExp(LEAKED_SECRET))).not.toBeInTheDocument();
    expect(document.body.textContent ?? '').not.toContain(LEAKED_SECRET);

    // The raw-settings endpoint that can hold plaintext keys is never called.
    const urls = fetchMock.mock.calls.map((c) => String(c[0]));
    expect(urls.some((u) => u.includes('/api/config/providers'))).toBe(false);
  });

  it('filters change history by config category', async () => {
    renderPage();
    await screen.findByText('CONVENTION.UPDATED.SUCCESS');

    await userEvent.selectOptions(screen.getByLabelText('Change category filter'), 'byok');

    expect(screen.getByText('PROVIDER_KEY.CHANGED.SUCCESS')).toBeInTheDocument();
    expect(screen.queryByText('CONVENTION.UPDATED.SUCCESS')).not.toBeInTheDocument();
  });

  it('shows an empty state when nothing is configured', async () => {
    routeFetch({
      health: { providers: [] },
      prompts: [],
      conventions: [],
      entitlements: { planId: null, planVersion: null, isCustom: false, limits: [] },
      audit: { records: [], nextCursor: null, total: 0, totalIsCapped: false },
    });
    renderPage();

    expect(await screen.findByText('No providers configured')).toBeInTheDocument();
    expect(screen.getByText('No overrides')).toBeInTheDocument();
    expect(screen.getByText('No configuration changes')).toBeInTheDocument();
  });

  it('shows a restricted note when the audit read is forbidden (non tenant-admin)', async () => {
    routeFetch({ auditStatus: 403 });
    renderPage();

    expect(await screen.findByText('Change history restricted')).toBeInTheDocument();
    // The effective-config summary still renders (per-source degradation).
    expect(screen.getByText('anthropic-claude')).toBeInTheDocument();
  });

  it('skips the audit read and notes no active tenant when none is set (fail-closed)', async () => {
    routeFetch({ me: { user: { id: 'u1', tenantId: null, role: 'owner' } } });
    renderPage();

    expect(await screen.findByText('No active tenant')).toBeInTheDocument();
    // No cross-tenant fan-out: the audit endpoint is never called.
    await waitFor(() => {
      const urls = fetchMock.mock.calls.map((c) => String(c[0]));
      expect(urls.some((u) => u.includes('/audit'))).toBe(false);
    });
  });

  it('surfaces an error banner when every configuration source fails', async () => {
    fetchMock.mockImplementation((url: string) => {
      const u = String(url);
      if (u.includes('/api/auth/me')) return Promise.resolve(okResponse(me));
      return Promise.resolve(errResponse(500, 'boom'));
    });
    renderPage();
    expect(await screen.findByTestId('error-banner')).toHaveTextContent('boom');
  });
});

describe('ConfigAuditPage RBAC (inherited from the route AdminGuard)', () => {
  it('renders for an admin', async () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u1', role: 'admin' }, loading: false, isAdmin: true });
    render(
      <MemoryRouter initialEntries={['/monitoring/config']}>
        <AdminGuard>
          <ConfigAuditPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(await screen.findByRole('heading', { name: 'Config Audit' })).toBeInTheDocument();
  });

  it('does NOT render for a non-admin member', () => {
    mockUseCurrentUser.mockReturnValue({ user: { id: 'u2', role: 'member' }, loading: false, isAdmin: false });
    render(
      <MemoryRouter initialEntries={['/monitoring/config']}>
        <AdminGuard>
          <ConfigAuditPage />
        </AdminGuard>
      </MemoryRouter>,
    );
    expect(screen.queryByRole('heading', { name: 'Config Audit' })).not.toBeInTheDocument();
  });
});
