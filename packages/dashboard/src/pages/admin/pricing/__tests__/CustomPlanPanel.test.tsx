// @vitest-environment jsdom
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { CustomPlanPanel } from '../CustomPlanPanel.js';

const { mockApi, mockTenantsApi } = vi.hoisted(() => ({
  mockApi: {
    listPlans: vi.fn(),
    mintCustomPlan: vi.fn(),
  },
  mockTenantsApi: {
    updatePlan: vi.fn(),
  },
}));

vi.mock('../../../../services/admin/admin-pricing-client.js', async (importOriginal) => {
  const actual = await importOriginal<
    typeof import('../../../../services/admin/admin-pricing-client.js')
  >();
  return { ...actual, adminPricingApi: mockApi };
});

vi.mock('../../../../services/admin/admin-tenants-client.js', () => ({
  adminTenantsApi: mockTenantsApi,
}));

const CUSTOM_PLAN = {
  planId: 'cp1',
  slug: 'custom-abc-1',
  displayName: 'Bespoke',
  version: 1,
  status: 'active',
  isCustom: true,
  billingInterval: 'monthly',
  supersedesPlanId: null,
  features: [],
  entitlements: [],
  prices: [],
};

describe('CustomPlanPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockApi.listPlans.mockResolvedValue({ plans: [] });
    mockApi.mintCustomPlan.mockResolvedValue(CUSTOM_PLAN);
    mockTenantsApi.updatePlan.mockResolvedValue({ tenantId: 'tnt-9', status: 'ok', message: '' });
  });

  it('lists existing custom plans (isCustom=true) and renders an empty state when none', async () => {
    render(<CustomPlanPanel />);
    await waitFor(() => expect(mockApi.listPlans).toHaveBeenCalledWith({ isCustom: true }));
    expect(screen.getByText(/No custom plans yet/i)).toBeInTheDocument();
  });

  it('mints a custom plan bound to a tenant and assigns it via adminTenantsApi.updatePlan', async () => {
    render(<CustomPlanPanel />);
    await waitFor(() => expect(mockApi.listPlans).toHaveBeenCalled());

    fireEvent.change(screen.getByLabelText('Tenant ID'), { target: { value: 'tnt-9' } });
    fireEvent.change(screen.getByLabelText('Custom plan display name'), {
      target: { value: 'Bespoke' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Mint custom plan' }));

    await waitFor(() => expect(mockApi.mintCustomPlan).toHaveBeenCalled());
    const body = mockApi.mintCustomPlan.mock.calls[0]![0];
    expect(body.tenantId).toBe('tnt-9');
    expect(body.makePublic).toBeUndefined();

    await waitFor(() =>
      expect(mockTenantsApi.updatePlan).toHaveBeenCalledWith('tnt-9', 'cp1'),
    );
    await waitFor(() => expect(screen.getByText(/Minted custom plan/i)).toBeInTheDocument());
  });

  it('does not assign when the assign checkbox is unchecked', async () => {
    render(<CustomPlanPanel />);
    await waitFor(() => expect(mockApi.listPlans).toHaveBeenCalled());

    fireEvent.change(screen.getByLabelText('Tenant ID'), { target: { value: 'tnt-9' } });
    fireEvent.change(screen.getByLabelText('Custom plan display name'), {
      target: { value: 'Bespoke' },
    });
    fireEvent.click(screen.getByLabelText(/Assign to the tenant immediately/i));
    fireEvent.click(screen.getByRole('button', { name: 'Mint custom plan' }));

    await waitFor(() => expect(mockApi.mintCustomPlan).toHaveBeenCalled());
    expect(mockTenantsApi.updatePlan).not.toHaveBeenCalled();
  });

  it('requires tenant id and display name', async () => {
    render(<CustomPlanPanel />);
    await waitFor(() => expect(mockApi.listPlans).toHaveBeenCalled());

    fireEvent.click(screen.getByRole('button', { name: 'Mint custom plan' }));
    expect(await screen.findByText(/Tenant ID is required/i)).toBeInTheDocument();
    expect(mockApi.mintCustomPlan).not.toHaveBeenCalled();
  });

  // Fix 3: a non-numeric limit must block the mint with an inline error, NOT be
  // coerced to null/unlimited via NaN.
  it('blocks mint when an entitlement limit is a non-numeric typo (NaN)', async () => {
    render(<CustomPlanPanel />);
    await waitFor(() => expect(mockApi.listPlans).toHaveBeenCalled());

    fireEvent.change(screen.getByLabelText('Tenant ID'), { target: { value: 'tnt-9' } });
    fireEvent.change(screen.getByLabelText('Custom plan display name'), {
      target: { value: 'Bespoke' },
    });
    // "10O0" (letter O) → Number(...) === NaN.
    fireEvent.change(screen.getByLabelText('Custom entitlement limit 0'), {
      target: { value: '10O0' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Mint custom plan' }));

    expect(await screen.findByText(/is not a number/i)).toBeInTheDocument();
    expect(mockApi.mintCustomPlan).not.toHaveBeenCalled();
  });

  it('mints with a blank limit (unlimited → null) and a valid number', async () => {
    render(<CustomPlanPanel />);
    await waitFor(() => expect(mockApi.listPlans).toHaveBeenCalled());

    fireEvent.change(screen.getByLabelText('Tenant ID'), { target: { value: 'tnt-9' } });
    fireEvent.change(screen.getByLabelText('Custom plan display name'), {
      target: { value: 'Bespoke' },
    });
    // First entitlement row: blank → unlimited (null). Add a second with a number.
    fireEvent.click(screen.getByRole('button', { name: '+ Add' }));
    fireEvent.change(screen.getByLabelText('Custom entitlement limit 1'), {
      target: { value: '42' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Mint custom plan' }));

    await waitFor(() => expect(mockApi.mintCustomPlan).toHaveBeenCalled());
    const body = mockApi.mintCustomPlan.mock.calls[0]![0];
    expect(body.entitlements[0].limitValue).toBeNull();
    expect(body.entitlements[1].limitValue).toBe(42);
  });
});
