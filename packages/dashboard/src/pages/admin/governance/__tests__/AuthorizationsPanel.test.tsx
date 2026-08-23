// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthorizationsPanel } from '../AuthorizationsPanel.js';
import type { ActionAuthorization } from '../../../../services/admin/actions-policy-api-client.js';

const mockListAuthorizations = vi.fn();
const mockDecideAuthorization = vi.fn();

vi.mock('../../../../services/admin/actions-policy-api-client.js', () => ({
  actionsPolicyApi: {
    listAuthorizations: (...args: unknown[]) => mockListAuthorizations(...args),
    decideAuthorization: (...args: unknown[]) => mockDecideAuthorization(...args),
  },
}));

function makeRow(overrides?: Partial<ActionAuthorization>): ActionAuthorization {
  return {
    id: 'aaaaaaaa-0000-0000-0000-000000000001',
    correlationId: 'run-42',
    targetKind: 'action',
    targetKey: 'effect:deploy.production',
    state: 'pending',
    requestedAtUtc: '2026-08-21T09:00:00Z',
    decidedAtUtc: null,
    decidedByUserId: null,
    expiresAtUtc: '2026-08-22T09:00:00Z',
    consumedAtUtc: null,
    autonomyLevelAtRequest: 70,
    reason: 'production deploy below the dial',
    expired: false,
    ...overrides,
  };
}

function setup(rows: ActionAuthorization[]) {
  mockListAuthorizations.mockResolvedValue({
    state: 'pending',
    count: rows.length,
    authorizations: rows,
  });
  mockDecideAuthorization.mockResolvedValue({
    id: rows[0]?.id ?? 'x',
    state: 'granted',
    correlationId: 'run-42',
    targetKind: 'action',
    targetKey: 'effect:deploy.production',
    decidedAtUtc: '2026-08-21T10:00:00Z',
    decidedByUserId: 'u-1',
    expiresAtUtc: null,
    reason: null,
  });
}

describe('AuthorizationsPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows an error banner with retry when the list fails', async () => {
    mockListAuthorizations.mockRejectedValue(new Error('boom'));
    render(<AuthorizationsPanel />);
    expect(
      await screen.findByText('Failed to load pending authorizations'),
    ).toBeInTheDocument();
    expect(screen.getByText('boom')).toBeInTheDocument();
    expect(screen.getByText('Retry')).toBeInTheDocument();
  });

  it('explains what the list is for when it is empty', async () => {
    setup([]);
    render(<AuthorizationsPanel />);
    const empty = await screen.findByTestId('authorizations-empty');
    expect(empty).toHaveTextContent('Nothing is waiting on a person.');
    expect(empty).toHaveTextContent('production deploy below the autonomy dial');
  });

  it('lists pending rows with the action, run and dial-at-request', async () => {
    setup([makeRow()]);
    render(<AuthorizationsPanel />);

    const row = await screen.findByTestId(
      'authorization-row-aaaaaaaa-0000-0000-0000-000000000001',
    );
    expect(row).toHaveTextContent('effect:deploy.production');
    expect(row).toHaveTextContent('run run-42');
    expect(row).toHaveTextContent('70');
    expect(row).toHaveTextContent('production deploy below the dial');
  });

  it('approves through POST …/decide with granted and reloads', async () => {
    setup([makeRow()]);
    render(<AuthorizationsPanel />);

    await userEvent.click(await screen.findByLabelText('Approve effect:deploy.production'));

    await waitFor(() =>
      expect(mockDecideAuthorization).toHaveBeenCalledWith(
        'aaaaaaaa-0000-0000-0000-000000000001',
        'granted',
      ),
    );
    // One load on mount + one reload after the decision.
    await waitFor(() => expect(mockListAuthorizations).toHaveBeenCalledTimes(2));
  });

  it('denies through POST …/decide with denied', async () => {
    setup([makeRow()]);
    render(<AuthorizationsPanel />);

    await userEvent.click(await screen.findByLabelText('Deny effect:deploy.production'));

    await waitFor(() =>
      expect(mockDecideAuthorization).toHaveBeenCalledWith(
        'aaaaaaaa-0000-0000-0000-000000000001',
        'denied',
      ),
    );
  });

  it('hides the decision buttons on an expired row', async () => {
    setup([makeRow({ expired: true })]);
    render(<AuthorizationsPanel />);

    await screen.findByTestId('authorization-row-aaaaaaaa-0000-0000-0000-000000000001');
    expect(screen.queryByLabelText('Approve effect:deploy.production')).not.toBeInTheDocument();
    expect(
      screen.getByTestId('authorization-expired-aaaaaaaa-0000-0000-0000-000000000001'),
    ).toHaveTextContent('Expired');
  });

  it('surfaces a failed decision (e.g. already decided → 409) as an alert', async () => {
    setup([makeRow()]);
    mockDecideAuthorization.mockRejectedValue(
      new Error('no pending, unexpired authorization with that id could be decided'),
    );
    render(<AuthorizationsPanel />);

    await userEvent.click(await screen.findByLabelText('Approve effect:deploy.production'));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'no pending, unexpired authorization',
    );
  });
});
