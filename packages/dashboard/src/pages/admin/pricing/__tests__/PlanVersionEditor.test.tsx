// @vitest-environment jsdom
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { PlanVersionEditor } from '../PlanVersionEditor.js';
import { AdminPricingApiError } from '../../../../services/admin/admin-pricing-client.js';

const { mockApi } = vi.hoisted(() => ({
  mockApi: {
    listPlans: vi.fn(),
    createPlan: vi.fn(),
    versionPlan: vi.fn(),
    deprecateVersion: vi.fn(),
  },
}));

vi.mock('../../../../services/admin/admin-pricing-client.js', async (importOriginal) => {
  const actual = await importOriginal<
    typeof import('../../../../services/admin/admin-pricing-client.js')
  >();
  return { ...actual, adminPricingApi: mockApi };
});

const PLAN = {
  planId: 'p1',
  slug: 'pro',
  displayName: 'Pro',
  version: 2,
  status: 'active',
  isCustom: false,
  billingInterval: 'monthly',
  supersedesPlanId: null,
  features: [],
  entitlements: [{ metricKey: 3, limitValue: 10, period: 'monthly', overageMode: 'block' }],
  prices: [],
};

describe('PlanVersionEditor', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockApi.listPlans.mockResolvedValue({ plans: [PLAN] });
    mockApi.createPlan.mockResolvedValue({ ...PLAN, slug: 'starter', version: 1 });
    mockApi.deprecateVersion.mockResolvedValue({ deprecated: true });
  });

  it('lists non-custom plans with a numeric-ordinal entitlement rendered as a label', async () => {
    render(<PlanVersionEditor />);
    await waitFor(() => expect(screen.getByText('Pro')).toBeInTheDocument());
    expect(mockApi.listPlans).toHaveBeenCalledWith({ isCustom: false });
    // metricKey ordinal 3 → "seats" label.
    expect(screen.getByText(/seats=10/)).toBeInTheDocument();
  });

  it('creates a new plan from the editor form', async () => {
    render(<PlanVersionEditor />);
    await waitFor(() => expect(screen.getByText('Pro')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'New plan' }));
    fireEvent.change(screen.getByLabelText('Slug'), { target: { value: 'starter' } });
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Starter' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(mockApi.createPlan).toHaveBeenCalled());
    const body = mockApi.createPlan.mock.calls[0]![0];
    expect(body.slug).toBe('starter');
    expect(body.displayName).toBe('Starter');
    expect(Array.isArray(body.entitlements)).toBe(true);
    await waitFor(() => expect(screen.getByText(/Created starter v1/)).toBeInTheDocument());
  });

  it('blocks save when required fields are missing', async () => {
    render(<PlanVersionEditor />);
    await waitFor(() => expect(screen.getByText('Pro')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'New plan' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText(/Slug is required/i)).toBeInTheDocument();
    expect(mockApi.createPlan).not.toHaveBeenCalled();
  });

  it('surfaces the 409 affected-tenant count and forces deprecate on confirm', async () => {
    mockApi.deprecateVersion
      .mockRejectedValueOnce(
        new AdminPricingApiError(409, 'has assignments', { affectedTenantCount: 4 }),
      )
      .mockResolvedValueOnce({ deprecated: true });

    render(<PlanVersionEditor />);
    await waitFor(() => expect(screen.getByText('Pro')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Deprecate' }));
    await waitFor(() => expect(screen.getByText(/4 tenants on this version/i)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Deprecate anyway \(force\)/i }));
    await waitFor(() =>
      expect(mockApi.deprecateVersion).toHaveBeenLastCalledWith('pro', 2, true),
    );
  });
});
