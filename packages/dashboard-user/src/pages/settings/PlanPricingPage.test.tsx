import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { PlanPricingPage } from './PlanPricingPage';
import { ApiError } from '../../api/client';

const { mockAuth, mockApi } = vi.hoisted(() => ({
  mockAuth: vi.fn(),
  mockApi: {
    getEntitlements: vi.fn(),
    listPublicPlans: vi.fn(),
    estimate: vi.fn(),
    subscribe: vi.fn(),
    getPublicPlan: vi.fn(),
  },
}));

vi.mock('../../hooks/useAuth', () => ({ useAuth: () => mockAuth() }));

vi.mock('../../api/pricing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/pricing')>();
  return { ...actual, tenantPricingApi: mockApi };
});

const ENTITLEMENTS = {
  tenantId: 't1',
  planId: 'p1',
  planVersion: 2,
  isCustom: false,
  limits: [
    {
      metricKey: 'seats',
      limitValue: 10,
      period: 'monthly',
      overageMode: 'block',
      currentUsage: 3,
      remaining: 7,
      isOver: false,
      overagePercent: 30,
    },
    {
      metricKey: 'llm_tokens',
      limitValue: null,
      period: 'monthly',
      overageMode: 'meter',
      currentUsage: 5000,
      remaining: null,
      isOver: false,
      overagePercent: null,
    },
  ],
};

const PLANS = {
  plans: [
    {
      planId: 'p1',
      slug: 'pro',
      displayName: 'Pro',
      version: 2,
      status: 'active',
      isCustom: false,
      billingInterval: 'monthly',
      supersedesPlanId: null,
      features: [],
      entitlements: [],
      prices: [],
    },
  ],
};

function renderPage() {
  return render(
    <MemoryRouter>
      <PlanPricingPage />
    </MemoryRouter>,
  );
}

describe('PlanPricingPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockAuth.mockReturnValue({ user: { id: 'u1', email: 'a@b', role: 'owner', tenantId: 't1' } });
    mockApi.getEntitlements.mockResolvedValue(ENTITLEMENTS);
    mockApi.listPublicPlans.mockResolvedValue(PLANS);
  });

  it('renders the current plan, entitlement bars, and the change-plan control for an owner', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText('Pro')).toBeInTheDocument());
    expect(screen.getByText('Seats')).toBeInTheDocument();
    expect(screen.getByText('3 / 10')).toBeInTheDocument();
    // Unlimited entitlement renders as "Unlimited" with no bar.
    expect(screen.getByText('Unlimited')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Change plan' })).toBeInTheDocument();
  });

  it('NEVER renders platform cost or margin (tenant sell-price-only surface)', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText('Pro')).toBeInTheDocument());
    expect(screen.queryByText(/margin/i)).toBeNull();
    expect(screen.queryByText(/cost basis/i)).toBeNull();
    expect(screen.queryByText(/costBasis/i)).toBeNull();
  });

  it('renders member as read-only (no change-plan control)', async () => {
    mockAuth.mockReturnValue({ user: { id: 'u2', email: 'm@b', role: 'member', tenantId: 't1' } });
    renderPage();
    await waitFor(() => expect(screen.getByText('Pro')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Change plan' })).toBeNull();
    expect(screen.getByText(/read-only/i)).toBeInTheDocument();
  });

  it('renders an empty state when the tenant has no active plan (404)', async () => {
    mockApi.getEntitlements.mockRejectedValue(new ApiError(404, 'API error', { error: 'no_active_assignment' }));
    renderPage();
    await waitFor(() => expect(screen.getByText('No active plan')).toBeInTheDocument());
  });

  it('surfaces a non-404 error without white-screening', async () => {
    mockApi.getEntitlements.mockRejectedValue(new ApiError(500, 'boom', {}));
    renderPage();
    await waitFor(() =>
      expect(screen.getByText(/Failed to load plan & pricing|boom/i)).toBeInTheDocument(),
    );
  });
});
