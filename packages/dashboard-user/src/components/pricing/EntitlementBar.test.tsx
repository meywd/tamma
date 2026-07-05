import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { EntitlementBar } from './EntitlementBar';
import type { ResolvedEntitlementLine } from '../../api/pricing';

function line(overrides: Partial<ResolvedEntitlementLine>): ResolvedEntitlementLine {
  return {
    metricKey: 'seats',
    limitValue: 10,
    period: 'monthly',
    overageMode: 'block',
    currentUsage: 3,
    remaining: 7,
    isOver: false,
    overagePercent: 30,
    ...overrides,
  };
}

describe('EntitlementBar', () => {
  it('renders usage vs limit with a bar for a limited metric', () => {
    render(<EntitlementBar line={line({})} />);
    expect(screen.getByText('Seats')).toBeInTheDocument();
    expect(screen.getByText('3 / 10')).toBeInTheDocument();
    // Bar element present.
    expect(screen.getByTestId('entitlement-seats').querySelector('.bg-blue-500')).toBeTruthy();
  });

  it('renders "Unlimited" and NO bar for a null limit', () => {
    render(<EntitlementBar line={line({ metricKey: 'llm_tokens', limitValue: null, remaining: null })} />);
    expect(screen.getByText('Unlimited')).toBeInTheDocument();
    // No progress bar rendered for unlimited.
    const container = screen.getByTestId('entitlement-llm_tokens');
    expect(container.querySelector('.bg-blue-500')).toBeNull();
    expect(container.querySelector('.bg-red-500')).toBeNull();
  });

  it('renders over-limit styling when isOver', () => {
    render(<EntitlementBar line={line({ currentUsage: 12, remaining: -2, isOver: true })} />);
    expect(screen.getByText(/over limit/i)).toBeInTheDocument();
    expect(screen.getByTestId('entitlement-seats').querySelector('.bg-red-500')).toBeTruthy();
  });
});
