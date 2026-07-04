// @vitest-environment jsdom
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { PricingTab } from '../PricingTab.js';

const { mockApi } = vi.hoisted(() => ({
  mockApi: {
    getOverview: vi.fn(),
    listPlans: vi.fn(),
    listMargins: vi.fn(),
  },
}));

vi.mock('../../../../services/admin/admin-pricing-client.js', async (importOriginal) => {
  const actual = await importOriginal<
    typeof import('../../../../services/admin/admin-pricing-client.js')
  >();
  return { ...actual, adminPricingApi: mockApi };
});

const OVERVIEW = {
  plans: [
    {
      planId: 'p1',
      slug: 'pro',
      displayName: 'Pro',
      version: 1,
      status: 'active',
      isCustom: false,
      billingInterval: 'monthly',
      recurringUsd: 49,
      activeTenantCount: 7,
    },
  ],
  margins: {
    activePolicyCount: 1,
    globalPolicyCount: 1,
    planScopedPolicyCount: 0,
    providerScopedPolicyCount: 0,
    globalMarkupMultiplier: 1.5,
    globalFixedUsdPer1M: 0.5,
  },
  totals: {
    activePlanCount: 1,
    customPlanCount: 0,
    deprecatedPlanCount: 0,
    totalActiveAssignments: 7,
    plansWithActiveAssignments: 1,
  },
};

describe('PricingTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockApi.getOverview.mockResolvedValue(OVERVIEW);
    mockApi.listPlans.mockResolvedValue({ plans: [] });
    mockApi.listMargins.mockResolvedValue({ policies: [] });
  });

  it('renders the overview panel with plan catalog + margin summary by default', async () => {
    render(<PricingTab />);
    await waitFor(() => expect(screen.getByText('Pro')).toBeInTheDocument());
    expect(screen.getByText('Plan catalog')).toBeInTheDocument();
    // Margin summary knob is shown (platform-owner economics).
    expect(screen.getByText('1.5')).toBeInTheDocument();
    // The plan's recurring list price (admin-only surface).
    expect(screen.getByText('$49.00')).toBeInTheDocument();
  });

  it('renders all four sub-tabs and switches to Plans', async () => {
    render(<PricingTab />);
    expect(screen.getByRole('button', { name: 'Overview' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Plans' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Margins' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Custom Plans' })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Plans' }));
    await waitFor(() => expect(mockApi.listPlans).toHaveBeenCalled());
    expect(screen.getByRole('button', { name: 'New plan' })).toBeInTheDocument();
  });

  it('renders an empty state when the catalog is empty', async () => {
    mockApi.getOverview.mockResolvedValue({
      ...OVERVIEW,
      plans: [],
      totals: { ...OVERVIEW.totals, activePlanCount: 0 },
    });
    render(<PricingTab />);
    await waitFor(() =>
      expect(screen.getByText(/No plans in the catalog yet/i)).toBeInTheDocument(),
    );
  });
});
