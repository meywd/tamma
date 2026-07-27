import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { UpgradePlanModal, computeDelta, formatWarning } from './UpgradePlanModal';
import type {
  PlanSnapshotDto,
  ResolvedEntitlementLine,
  SubscribeResponse,
} from '../../api/pricing';

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

  // Fix 2: an ABSENT metric means "not granted", NOT "unlimited". Metric ordinal
  // 5 = rag_storage_mb (present on target only) is an ADDED entitlement → a GAIN
  // labelled with its new finite limit, never "Unlimited".
  it('labels a metric the target ADDS as a gain with the new limit, not "Unlimited"', () => {
    const current = [ent({ metricKey: 'seats', limitValue: 5 })];
    const target = plan([
      { metricKey: 3, limitValue: 5, period: 'monthly', overageMode: 'block' }, // seats unchanged
      { metricKey: 5, limitValue: 500, period: 'monthly', overageMode: 'meter' }, // rag_storage_mb ADDED
    ]);

    const delta = computeDelta(current, target);
    const rag = delta.find((d) => d.metric === 'Rag Storage Mb');
    expect(rag?.kind).toBe('gain');
    expect(rag?.change).toBe('added');
    expect(rag?.detail).toMatch(/500/);
    expect(rag?.detail).not.toMatch(/Unlimited/i);
    expect(rag?.violation).toBe(false);
    // seats unchanged (5→5) is filtered out.
    expect(delta.find((d) => d.metric === 'Seats')).toBeUndefined();
  });

  // A downgrade that DROPS a metric the tenant holds today is a LOSS of that
  // capability — it must NOT read as "500 → Unlimited" (the old Infinity bug).
  it('labels a metric the target DROPS as a loss ("Removed"), not "→ Unlimited"', () => {
    const current = [
      ent({ metricKey: 'seats', limitValue: 5 }),
      ent({ metricKey: 'rag_storage_mb', limitValue: 500, currentUsage: 100 }),
    ];
    // Target has only seats (unchanged); rag_storage_mb is absent → dropped.
    const target = plan([{ metricKey: 3, limitValue: 5, period: 'monthly', overageMode: 'block' }]);

    const delta = computeDelta(current, target);
    const rag = delta.find((d) => d.metric === 'Rag Storage Mb');
    expect(rag?.kind).toBe('loss');
    expect(rag?.change).toBe('removed');
    expect(rag?.detail).not.toMatch(/Unlimited/i);
    expect(rag?.violation).toBe(false);
  });

  // Present-on-both with the target dropping to unlimited stays correct: a
  // present metric whose target limit is null IS genuinely unlimited (a gain).
  it('treats a present metric with null target limit as unlimited (gain)', () => {
    const current = [ent({ metricKey: 'seats', limitValue: 5, currentUsage: 3 })];
    const target = plan([{ metricKey: 3, limitValue: null, period: 'total', overageMode: 'allow' }]);

    const delta = computeDelta(current, target);
    const seats = delta.find((d) => d.metric === 'Seats');
    expect(seats?.kind).toBe('gain');
    expect(seats?.change).toBe('compare');
    expect(seats?.detail).toMatch(/Unlimited/i);
  });
});

// The subscribe fixture is copied from the C# wire DTO, NOT invented:
// PlanAssignmentResponse / PlanAssignmentWarningItem —
// apps/tamma-elsa/src/Tamma.Api/Dtos/Admin/AdminTenantDtos.cs:135-148,
// projected by AdminTenantsEndpoints.ToPlanAssignmentResponse (:884-896).
// `direction` is PlanChangeDirection lowercased ("upgrade"|"downgrade"|"lateral");
// `warnings[].metricKey` is the PascalCase enum member name (`.ToString()`),
// e.g. "Seats" — the ordinal form is also covered below because
// metricKeyLabel() accepts both. The previous fixture mocked a `violations:
// string[]` field the server has never sent, and the suite certified it.
function subscribeResponse(overrides: Partial<SubscribeResponse> = {}): SubscribeResponse {
  return {
    tenantId: 't',
    planId: 'p2',
    planVersion: 1,
    status: 'active',
    direction: 'upgrade',
    warnings: [],
    scheduledEffectiveAt: null,
    ...overrides,
  };
}

describe('formatWarning', () => {
  it('renders metric label, usage and new limit', () => {
    expect(formatWarning({ metricKey: 'Seats', currentUsage: 12, newLimit: 5 })).toBe(
      'Seats: you are using 12, the new plan allows 5',
    );
  });

  it('labels an ordinal metricKey via metricKeyLabel (pins D3)', () => {
    // seats = ordinal 3 in EntitlementMetricKey declaration order.
    expect(formatWarning({ metricKey: 3, currentUsage: 12, newLimit: 5 })).toBe(
      'Seats: you are using 12, the new plan allows 5',
    );
  });

  it('normalizes a multi-word PascalCase enum name', () => {
    expect(formatWarning({ metricKey: 'WorkflowRuns', currentUsage: 900, newLimit: 500 })).toBe(
      'Workflow Runs: you are using 900, the new plan allows 500',
    );
  });

  it('omits a null half instead of printing null', () => {
    const line = formatWarning({ metricKey: 'Seats', currentUsage: null, newLimit: 5 });
    expect(line).toBe('Seats: the new plan allows 5');
    expect(line).not.toMatch(/null/);
  });
});

describe('UpgradePlanModal', () => {
  beforeEach(() => vi.clearAllMocks());

  const current = [ent({ metricKey: 'seats', limitValue: 5, currentUsage: 4 })];
  const plans = [plan([{ metricKey: 3, limitValue: 10, period: 'monthly', overageMode: 'block' }])];

  function renderAndSubscribe(onSubscribed = vi.fn(), onClose = vi.fn()) {
    render(
      <UpgradePlanModal
        plans={plans}
        currentEntitlements={current}
        currentPlanId="p1"
        canMutate
        onClose={onClose}
        onSubscribed={onSubscribed}
      />,
    );
    fireEvent.change(screen.getByLabelText('Choose a plan'), { target: { value: 'pro' } });
    fireEvent.click(screen.getByRole('button', { name: 'Confirm change' }));
    return { onSubscribed, onClose };
  }

  it('shows the delta, subscribes on confirm, and notifies the page on Done', async () => {
    mockApi.subscribe.mockResolvedValueOnce(subscribeResponse());
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

    // The confirmation renders BEFORE onSubscribed fires — firing it
    // immediately would unmount the modal and hide the warnings (the old bug).
    await waitFor(() => expect(screen.getByRole('status')).toBeInTheDocument());
    expect(onSubscribed).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Done' }));
    expect(onSubscribed).toHaveBeenCalledTimes(1);
  });

  it('renders no warning banner when warnings is empty', async () => {
    mockApi.subscribe.mockResolvedValueOnce(subscribeResponse({ warnings: [] }));
    renderAndSubscribe();

    await waitFor(() => expect(screen.getByRole('status')).toBeInTheDocument());
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByText(/over-limit/i)).toBeNull();
  });

  it('renders one line per warning with metric label, usage and new limit', async () => {
    mockApi.subscribe.mockResolvedValueOnce(
      subscribeResponse({
        direction: 'downgrade',
        warnings: [{ metricKey: 'Seats', currentUsage: 12, newLimit: 5 }],
      }),
    );
    renderAndSubscribe();

    await waitFor(() =>
      expect(
        screen.getByText('Seats: you are using 12, the new plan allows 5'),
      ).toBeInTheDocument(),
    );
  });

  it('renders a warning with null currentUsage without printing null', async () => {
    mockApi.subscribe.mockResolvedValueOnce(
      subscribeResponse({
        direction: 'downgrade',
        warnings: [{ metricKey: 'Seats', currentUsage: null, newLimit: 5 }],
      }),
    );
    renderAndSubscribe();

    await waitFor(() =>
      expect(screen.getByText('Seats: the new plan allows 5')).toBeInTheDocument(),
    );
    expect(screen.queryByText(/null/)).toBeNull();
  });

  it('surfaces the server-computed direction, not a client-side guess', async () => {
    mockApi.subscribe.mockResolvedValueOnce(subscribeResponse({ direction: 'downgrade' }));
    renderAndSubscribe();

    await waitFor(() =>
      expect(screen.getByText(/your plan has been downgraded/i)).toBeInTheDocument(),
    );
  });

  it('shows the scheduled effective date when present, and not when null', async () => {
    mockApi.subscribe.mockResolvedValueOnce(
      subscribeResponse({ scheduledEffectiveAt: '2026-08-01T00:00:00Z' }),
    );
    renderAndSubscribe();

    await waitFor(() => expect(screen.getByText(/takes effect/i)).toBeInTheDocument());
  });

  it('does not show an effective date when scheduledEffectiveAt is null', async () => {
    mockApi.subscribe.mockResolvedValueOnce(subscribeResponse({ scheduledEffectiveAt: null }));
    renderAndSubscribe();

    await waitFor(() => expect(screen.getByRole('status')).toBeInTheDocument());
    expect(screen.queryByText(/takes effect/i)).toBeNull();
  });

  it('notifies the page even when the confirmation is dismissed via the X', async () => {
    mockApi.subscribe.mockResolvedValueOnce(subscribeResponse());
    const { onSubscribed, onClose } = renderAndSubscribe();

    await waitFor(() => expect(screen.getByRole('status')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(onSubscribed).toHaveBeenCalledTimes(1);
    expect(onClose).not.toHaveBeenCalled();
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
