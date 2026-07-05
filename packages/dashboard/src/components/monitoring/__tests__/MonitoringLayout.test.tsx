// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MonitoringLayout } from '../MonitoringLayout.js';

describe('MonitoringLayout', () => {
  it('renders the title, description and children', () => {
    render(
      <MonitoringLayout title="System Health" description="Service health overview">
        <div>page-body</div>
      </MonitoringLayout>,
    );
    expect(screen.getByRole('heading', { name: 'System Health' })).toBeInTheDocument();
    expect(screen.getByText('Service health overview')).toBeInTheDocument();
    expect(screen.getByText('page-body')).toBeInTheDocument();
  });

  it('calls onRefresh when the refresh button is clicked', async () => {
    const onRefresh = vi.fn();
    render(
      <MonitoringLayout title="X" onRefresh={onRefresh}>
        <div />
      </MonitoringLayout>,
    );
    await userEvent.click(screen.getByRole('button', { name: /Refresh/ }));
    expect(onRefresh).toHaveBeenCalledOnce();
  });

  it('reports auto-refresh interval changes (off -> 10s)', async () => {
    const onAutoRefreshChange = vi.fn();
    render(
      <MonitoringLayout title="X" autoRefreshInterval={null} onAutoRefreshChange={onAutoRefreshChange}>
        <div />
      </MonitoringLayout>,
    );
    await userEvent.selectOptions(screen.getByLabelText('Auto-refresh interval'), '10000');
    expect(onAutoRefreshChange).toHaveBeenCalledWith(10000);
  });

  it('reports time-range changes', async () => {
    const onTimeRangeChange = vi.fn();
    render(
      <MonitoringLayout title="X" timeRange="24h" onTimeRangeChange={onTimeRangeChange}>
        <div />
      </MonitoringLayout>,
    );
    await userEvent.selectOptions(screen.getByLabelText('Time range'), '6h');
    expect(onTimeRangeChange).toHaveBeenCalledWith('6h');
  });

  it('shows the SSE connection status indicator', () => {
    render(
      <MonitoringLayout title="X" connectionStatus="connected">
        <div />
      </MonitoringLayout>,
    );
    expect(screen.getByText('Live')).toBeInTheDocument();
  });

  it('shows a last-updated timestamp when provided', () => {
    render(
      <MonitoringLayout title="X" lastUpdated={new Date('2026-01-01T10:00:00Z')}>
        <div />
      </MonitoringLayout>,
    );
    expect(screen.getByTestId('last-updated')).toHaveTextContent(/Last updated/);
  });

  it('hides the time-range selector when showTimeRange is false', () => {
    render(
      <MonitoringLayout title="X" showTimeRange={false} onTimeRangeChange={() => {}}>
        <div />
      </MonitoringLayout>,
    );
    expect(screen.queryByLabelText('Time range')).not.toBeInTheDocument();
  });
});
