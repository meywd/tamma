// @vitest-environment jsdom
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MarginPolicyPanel } from '../MarginPolicyPanel.js';

const { mockApi } = vi.hoisted(() => ({
  mockApi: {
    listMargins: vi.fn(),
    versionMargin: vi.fn(),
  },
}));

vi.mock('../../../../services/admin/admin-pricing-client.js', async (importOriginal) => {
  const actual = await importOriginal<
    typeof import('../../../../services/admin/admin-pricing-client.js')
  >();
  return { ...actual, adminPricingApi: mockApi };
});

const POLICY = {
  id: 'm1',
  scope: 'global',
  refKey: null,
  markupMultiplier: 1.5,
  fixedUsdPer1M: null,
  effectiveFrom: '2026-01-01T00:00:00.000Z',
  status: 'active',
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-01T00:00:00.000Z',
};

describe('MarginPolicyPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockApi.listMargins.mockResolvedValue({ policies: [POLICY] });
    mockApi.versionMargin.mockResolvedValue({ policy: POLICY, supersededPolicyId: 'old-123456' });
  });

  it('renders the policy list', async () => {
    render(<MarginPolicyPanel />);
    await waitFor(() => expect(screen.getByText('global')).toBeInTheDocument());
    expect(screen.getByText('1.5')).toBeInTheDocument();
  });

  it('validates that at least one knob is set before saving', async () => {
    render(<MarginPolicyPanel />);
    await waitFor(() => expect(screen.getByText('global')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Save policy' }));
    expect(
      await screen.findByText(/at least one of markup multiplier or fixed/i),
    ).toBeInTheDocument();
    expect(mockApi.versionMargin).not.toHaveBeenCalled();
  });

  it('calls versionMargin and surfaces the supersede result', async () => {
    render(<MarginPolicyPanel />);
    await waitFor(() => expect(screen.getByText('global')).toBeInTheDocument());

    fireEvent.change(screen.getByLabelText('Markup multiplier'), { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save policy' }));

    await waitFor(() => expect(mockApi.versionMargin).toHaveBeenCalled());
    const body = mockApi.versionMargin.mock.calls[0]![0];
    expect(body.scope).toBe('global');
    expect(body.markupMultiplier).toBe(2);
    expect(body.refKey).toBeNull();
    await waitFor(() => expect(screen.getByText(/superseded prior/i)).toBeInTheDocument());
  });

  it('requires a ref key for a provider-scoped policy', async () => {
    render(<MarginPolicyPanel />);
    await waitFor(() => expect(screen.getByText('global')).toBeInTheDocument());

    fireEvent.change(screen.getByLabelText('Margin scope'), { target: { value: 'provider' } });
    fireEvent.change(screen.getByLabelText('Markup multiplier'), { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save policy' }));

    expect(await screen.findByText(/requires a ref key/i)).toBeInTheDocument();
    expect(mockApi.versionMargin).not.toHaveBeenCalled();
  });
});
