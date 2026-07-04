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

  // Fix 3: a non-numeric limit must block submit with an inline error, NOT be
  // coerced to null/unlimited via NaN.
  it('blocks save when an entitlement limit is a non-numeric typo (NaN)', async () => {
    render(<PlanVersionEditor />);
    await waitFor(() => expect(screen.getByText('Pro')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'New plan' }));
    fireEvent.change(screen.getByLabelText('Slug'), { target: { value: 'starter' } });
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Starter' } });
    // "10O0" (letter O) → Number(...) === NaN.
    fireEvent.change(screen.getByLabelText('Entitlement limit 0'), { target: { value: '10O0' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText(/is not a number/i)).toBeInTheDocument();
    expect(mockApi.createPlan).not.toHaveBeenCalled();
  });

  it('allows save with a blank limit (unlimited → null) and a valid number', async () => {
    render(<PlanVersionEditor />);
    await waitFor(() => expect(screen.getByText('Pro')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'New plan' }));
    fireEvent.change(screen.getByLabelText('Slug'), { target: { value: 'starter' } });
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Starter' } });
    // First row: blank → unlimited (null). Add a second row with a valid number.
    // Three collections each render a "+ Add"; Entitlements is the first.
    fireEvent.click(screen.getAllByRole('button', { name: '+ Add' })[0]!);
    fireEvent.change(screen.getByLabelText('Entitlement limit 1'), { target: { value: '250' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(mockApi.createPlan).toHaveBeenCalled());
    const body = mockApi.createPlan.mock.calls[0]![0];
    expect(body.entitlements[0].limitValue).toBeNull();
    expect(body.entitlements[1].limitValue).toBe(250);
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
