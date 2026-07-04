import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { CostEstimateWidget } from './CostEstimateWidget';

const { mockApi } = vi.hoisted(() => ({
  mockApi: { estimate: vi.fn() },
}));

vi.mock('../../api/pricing', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/pricing')>();
  return { ...actual, tenantPricingApi: mockApi };
});

describe('CostEstimateWidget', () => {
  beforeEach(() => vi.clearAllMocks());

  it('renders the sell price + pricing mode and NEVER a cost/margin figure', async () => {
    mockApi.estimate.mockResolvedValueOnce({
      provider: 'anthropic',
      model: 'claude-3-5-sonnet',
      inputTokens: 1000,
      outputTokens: 1000,
      pricingMode: 'platform_provided',
      sellPriceUsd: 0.0123,
      invoice: { sellPriceUsd: 0.01 },
    });

    render(<CostEstimateWidget />);
    fireEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await waitFor(() => expect(screen.getByText('$0.012300')).toBeInTheDocument());
    expect(screen.getByText('platform_provided')).toBeInTheDocument();
    // Security: the tenant surface must never render platform cost/margin.
    expect(screen.queryByText(/margin/i)).toBeNull();
    expect(screen.queryByText(/cost basis/i)).toBeNull();
    expect(screen.queryByText(/costBasis/i)).toBeNull();
  });

  it('shows zero markup for BYOK mode', async () => {
    mockApi.estimate.mockResolvedValueOnce({
      provider: 'anthropic',
      model: 'x',
      inputTokens: 1,
      outputTokens: 1,
      pricingMode: 'byok',
      sellPriceUsd: 0,
      invoice: { sellPriceUsd: 0 },
    });

    render(<CostEstimateWidget />);
    fireEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await waitFor(() => expect(screen.getByText('byok')).toBeInTheDocument());
    expect(screen.getByText('$0.000000')).toBeInTheDocument();
  });

  it('surfaces an unknown-model server error inline (never a silent $0)', async () => {
    const { ApiError } = await import('../../api/client');
    mockApi.estimate.mockRejectedValueOnce(
      new ApiError(400, 'API error', {
        error: 'PRICING.UNKNOWN_MODEL',
        message: 'No price sheet for provider/model.',
      }),
    );

    render(<CostEstimateWidget />);
    fireEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await waitFor(() =>
      expect(screen.getByText(/No price sheet for provider\/model/i)).toBeInTheDocument(),
    );
    // No result row rendered on error (the "Pricing mode" label only appears on success).
    expect(screen.queryByText('Pricing mode')).toBeNull();
  });
});
