// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { StatusBadge } from '../StatusBadge.js';
import { MetricCard } from '../MetricCard.js';
import { MetricGrid } from '../MetricGrid.js';
import { EmptyState } from '../EmptyState.js';
import { ErrorBanner } from '../ErrorBanner.js';
import { ProgressRing } from '../ProgressRing.js';
import { LatencyBar } from '../LatencyBar.js';
import { TimeSeriesChart } from '../TimeSeriesChart.js';

describe('StatusBadge', () => {
  it('maps a semantic status to the right tone and renders the label', () => {
    render(<StatusBadge status="healthy" label="Healthy" />);
    const badge = screen.getByTestId('status-badge');
    expect(badge).toHaveAttribute('data-tone', 'green');
    expect(badge).toHaveTextContent('Healthy');
  });

  it('supports raw tones and children override', () => {
    render(<StatusBadge status="red">Down</StatusBadge>);
    const badge = screen.getByTestId('status-badge');
    expect(badge).toHaveAttribute('data-tone', 'red');
    expect(badge).toHaveTextContent('Down');
  });
});

describe('MetricCard', () => {
  it('renders label, value, unit and a trend arrow', () => {
    render(<MetricCard label="Requests" value={1234} unit="req/s" trend="up" trendLabel="+12%" />);
    expect(screen.getByText('Requests')).toBeInTheDocument();
    expect(screen.getByText('1234')).toBeInTheDocument();
    expect(screen.getByText('req/s')).toBeInTheDocument();
    expect(screen.getByTestId('metric-trend')).toHaveTextContent('▲');
    expect(screen.getByText('+12%')).toBeInTheDocument();
  });

  it('renders a sparkline only when at least two points are supplied', () => {
    const { rerender } = render(<MetricCard label="A" value={1} sparkline={[1, 2, 3]} />);
    expect(screen.getByTestId('metric-sparkline')).toBeInTheDocument();
    rerender(<MetricCard label="A" value={1} sparkline={[1]} />);
    expect(screen.queryByTestId('metric-sparkline')).not.toBeInTheDocument();
  });
});

describe('MetricGrid', () => {
  it('renders children in a responsive grid', () => {
    render(
      <MetricGrid columns={3}>
        <div>child-a</div>
        <div>child-b</div>
      </MetricGrid>,
    );
    const grid = screen.getByTestId('metric-grid');
    expect(grid.className).toContain('lg:grid-cols-3');
    expect(screen.getByText('child-a')).toBeInTheDocument();
    expect(screen.getByText('child-b')).toBeInTheDocument();
  });
});

describe('EmptyState', () => {
  it('renders title, description and an optional action', async () => {
    const onClick = vi.fn();
    render(
      <EmptyState
        title="Nothing here"
        description="No events yet"
        action={{ label: 'Reload', onClick }}
      />,
    );
    expect(screen.getByText('Nothing here')).toBeInTheDocument();
    expect(screen.getByText('No events yet')).toBeInTheDocument();
    await userEvent.click(screen.getByText('Reload'));
    expect(onClick).toHaveBeenCalledOnce();
  });
});

describe('ErrorBanner', () => {
  it('shows the message and calls onRetry', async () => {
    const onRetry = vi.fn();
    render(<ErrorBanner message="Boom" onRetry={onRetry} />);
    expect(screen.getByText('Boom')).toBeInTheDocument();
    await userEvent.click(screen.getByText('Retry'));
    expect(onRetry).toHaveBeenCalledOnce();
  });

  it('dismisses itself and fires onDismiss', async () => {
    const onDismiss = vi.fn();
    render(<ErrorBanner message="Boom" onDismiss={onDismiss} />);
    expect(screen.getByRole('alert')).toBeInTheDocument();
    await userEvent.click(screen.getByLabelText('Dismiss'));
    expect(onDismiss).toHaveBeenCalledOnce();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});

describe('ProgressRing', () => {
  it('renders the clamped percentage with an accessible label', () => {
    render(<ProgressRing value={142} label="Disk" />);
    expect(screen.getByText('100%')).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'Disk: 100%' })).toBeInTheDocument();
  });
});

describe('LatencyBar', () => {
  it('renders the p50/p95/p99 percentile values', () => {
    render(<LatencyBar p50={10} p95={50} p99={120} />);
    expect(screen.getByText(/10ms/)).toBeInTheDocument();
    expect(screen.getByText(/50ms/)).toBeInTheDocument();
    expect(screen.getByText(/120ms/)).toBeInTheDocument();
  });
});

describe('TimeSeriesChart', () => {
  it('renders a chart path when data is present', () => {
    render(
      <TimeSeriesChart
        data={[
          { timestamp: 1, value: 5 },
          { timestamp: 2, value: 8 },
          { timestamp: 3, value: 3 },
        ]}
      />,
    );
    expect(screen.getByTestId('time-series-chart')).toBeInTheDocument();
  });

  it('shows an empty state when there is no data', () => {
    render(<TimeSeriesChart data={[]} emptyMessage="Nothing to plot" />);
    expect(screen.getByTestId('empty-state')).toBeInTheDocument();
    expect(screen.getByText('Nothing to plot')).toBeInTheDocument();
  });
});
