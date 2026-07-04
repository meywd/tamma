import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { UpgradePlanModal, computeDelta } from './UpgradePlanModal';
import type { PlanSnapshotDto, ResolvedEntitlementLine } from '../../api/pricing';

const { mockApi } = vi.hoisted(() => ({
  mockApi: { subscribe: vi.fn() },
}));

vi.mock('../../api/pricing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/pricing')>();
  return { ...actual, tenantPricingApi: mockApi };
});

function ent(overrides: Partial<ResolvedEntitlementLine>): ResolvedEntitlementLine {
  return {
    metricKey: 'seats',
    limitValue: 5,
    period: 'monthly',
    overageMode: 'block',
    currentUsage: 4,
    remaining: 1,
    isOver: false,
    overagePercent: 80,
    ...overrides,
  };
}

// metricKey uses the NUMERIC ordinal on a PlanSnapshot (no string-enum converter):
// seats = 3, llm_tokens = 2.
function plan(entitlements: PlanSnapshotDto['entitlements']): PlanSnapshotDto {
  return {
    planId: 'p2',
    slug: 'pro',
    displayName: 'Pro',
    version: 1,
    status: 'active',
    isCustom: false,
    billingInterval: 'monthly',
    supersedesPlanId: null,
    features: [],
    entitlements,
    prices: [],
  };
}

describe('computeDelta', () => {
  it('reports gains and losses vs the current resolved set', () => {
    const current = [ent({ metricKey: 'seats', limitValue: 5 }), ent({ metricKey: 'llm_tokens', limitValue: null, currentUsage: 0 })];
    const target = plan([
      { metricKey: 3, limitValue: 10, period: 'monthly', overageMode: 'block' }, // seats 5→10 gain
      { metricKey: 2, limitValue: 1000000, period: 'monthly', overageMode: 'meter' }, // llm ∞→1M loss
    ]);

    const delta = computeDelta(current, target);
    const seats = delta.find((d) => d.metric === 'Seats');
    const llm = delta.find((d) => d.metric === 'Llm Tokens');
    expect(seats?.kind).toBe('gain');
    expect(llm?.kind).toBe('loss');
    expect(seats?.violation).toBe(false);
  });

  it('flags a downgrade that puts current usage over the new limit', () => {
    const current = [ent({ metricKey: 'seats', limitValue: 5, currentUsage: 4 })];
    const target = plan([{ metricKey: 3, limitValue: 2, period: 'monthly', overageMode: 'block' }]);

    const delta = computeDelta(current, target);
    const seats = delta.find((d) => d.metric === 'Seats');
    expect(seats?.kind).toBe('loss');
    expect(seats?.violation).toBe(true);
  });
});

describe('UpgradePlanModal', () => {
  beforeEach(() => vi.clearAllMocks());

  const current = [ent({ metricKey: 'seats', limitValue: 5, currentUsage: 4 })];
  const plans = [plan([{ metricKey: 3, limitValue: 10, period: 'monthly', overageMode: 'block' }])];

  it('shows the delta and subscribes on confirm', async () => {
    mockApi.subscribe.mockResolvedValueOnce({ tenantId: 't', status: 'active' });
    const onSubscribed = vi.fn();

    render(
      <UpgradePlanModal
        plans={plans}
        currentEntitlements={current}
        currentPlanId="p1"
        canMutate
        onClose={vi.fn()}
        onSubscribed={onSubscribed}
      />,
    );

    fireEvent.change(screen.getByLabelText('Choose a plan'), { target: { value: 'pro' } });
    expect(screen.getByText('Entitlement changes')).toBeInTheDocument();
    expect(screen.getByText('Seats')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Confirm change' }));
    await waitFor(() => expect(mockApi.subscribe).toHaveBeenCalledWith({ planSlug: 'pro' }));
    await waitFor(() => expect(onSubscribed).toHaveBeenCalled());
  });

  it('surfaces the flagged-violation list from the response as a non-blocking warning', async () => {
    mockApi.subscribe.mockResolvedValueOnce({
      tenantId: 't',
      status: 'active',
      violations: ['seats over limit'],
    });

    render(
      <UpgradePlanModal
        plans={plans}
        currentEntitlements={current}
        currentPlanId="p1"
        canMutate
        onClose={vi.fn()}
        onSubscribed={vi.fn()}
      />,
    );

    fireEvent.change(screen.getByLabelText('Choose a plan'), { target: { value: 'pro' } });
    fireEvent.click(screen.getByRole('button', { name: 'Confirm change' }));

    await waitFor(() =>
      expect(screen.getByText(/Subscribed with warnings: seats over limit/i)).toBeInTheDocument(),
    );
  });

  it('hides the confirm control for a read-only member', () => {
    render(
      <UpgradePlanModal
        plans={plans}
        currentEntitlements={current}
        currentPlanId="p1"
        canMutate={false}
        onClose={vi.fn()}
        onSubscribed={vi.fn()}
      />,
    );

    fireEvent.change(screen.getByLabelText('Choose a plan'), { target: { value: 'pro' } });
    expect(screen.queryByRole('button', { name: 'Confirm change' })).toBeNull();
  });
});
