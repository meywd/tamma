// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { OverridesPanel } from '../OverridesPanel.js';
import type {
  ActionPolicyResponse,
  PolicyAction,
} from '../../../../services/admin/actions-policy-api-client.js';

const mockGetPolicy = vi.fn();
const mockSetActionThreshold = vi.fn();
const mockSetActionEnforce = vi.fn();
const mockSetActionEnabled = vi.fn();
const mockDeleteActionOverride = vi.fn();
const mockSetGroupThreshold = vi.fn();
const mockDeleteGroupOverride = vi.fn();
const mockResetPolicy = vi.fn();

vi.mock('../../../../services/admin/actions-policy-api-client.js', () => ({
  actionsPolicyApi: {
    getPolicy: (...args: unknown[]) => mockGetPolicy(...args),
    setActionThreshold: (...args: unknown[]) => mockSetActionThreshold(...args),
    setActionEnforce: (...args: unknown[]) => mockSetActionEnforce(...args),
    setActionEnabled: (...args: unknown[]) => mockSetActionEnabled(...args),
    deleteActionOverride: (...args: unknown[]) => mockDeleteActionOverride(...args),
    setGroupThreshold: (...args: unknown[]) => mockSetGroupThreshold(...args),
    deleteGroupOverride: (...args: unknown[]) => mockDeleteGroupOverride(...args),
    resetPolicy: (...args: unknown[]) => mockResetPolicy(...args),
  },
}));

function makePolicyAction(overrides?: Partial<PolicyAction>): PolicyAction {
  return {
    key: 'scw:pr.merge',
    group: 'source-control-write',
    risk: 'high',
    title: 'Merge a pull request',
    summary: 'Merges a PR into its base branch.',
    siteKey: 'pr.merge',
    minAutonomy: 90,
    source: 'system-default',
    enforce: true,
    enabled: true,
    allowedRoles: null,
    escalatableToHuman: true,
    enforceable: true,
    isMachinery: false,
    shippedLevel: 90,
    ladderWithoutRow: 90,
    automatedAtLevel: false,
    levelOwned: false,
    editable: true,
    reason: 'editable',
    toggleAboveDial: false,
    enforcementSites: ['route: POST /api/engine/pr-merge'],
    ...overrides,
  };
}

function makePolicy(): ActionPolicyResponse {
  return {
    dial: { min: 1, max: 100, alwaysHuman: 101, default: 70, current: 70, viewLevel: 70 },
    groups: [
      {
        group: 'source-control-write',
        description: 'Writing to source control.',
        members: 2,
        principalRow: null,
        platformRow: null,
      },
      {
        group: 'docs',
        description: 'Human-readable prose.',
        members: 1,
        principalRow: { minAutonomy: 60, enforce: null, enabled: null, allowedRoles: null },
        platformRow: null,
      },
    ],
    actions: [
      makePolicyAction(),
      makePolicyAction({
        key: 'scw:branch.push',
        title: 'Push a branch',
        source: 'action-override',
        minAutonomy: 1,
        automatedAtLevel: true,
      }),
    ],
  };
}

function setup() {
  mockGetPolicy.mockResolvedValue(makePolicy());
  mockSetGroupThreshold.mockResolvedValue({ group: 'source-control-write', minAutonomy: 95 });
  mockDeleteGroupOverride.mockResolvedValue({ message: 'gone' });
  mockSetActionThreshold.mockResolvedValue({ key: 'scw:pr.merge', minAutonomy: 1, dialAtMint: 70 });
  mockSetActionEnabled.mockResolvedValue({ key: 'scw:pr.merge', field: 'enabled', value: false });
  mockSetActionEnforce.mockResolvedValue({ key: 'scw:pr.merge', field: 'enforce', value: false });
  mockDeleteActionOverride.mockResolvedValue({
    message: 'gone',
    nowResolvesTo: 90,
    source: 'shipped',
    reason: 'the next tier applies',
  });
  mockResetPolicy.mockResolvedValue({ removed: 2 });
}

describe('OverridesPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows an error banner with retry when the policy read fails', async () => {
    mockGetPolicy.mockRejectedValue(new Error('boom'));
    render(<OverridesPanel />);
    expect(await screen.findByText('Failed to load the policy')).toBeInTheDocument();
    expect(screen.getByText('boom')).toBeInTheDocument();
    expect(screen.getByText('Retry')).toBeInTheDocument();
  });

  it('renders groups and actions with provenance badges', async () => {
    setup();
    render(<OverridesPanel />);

    expect(await screen.findByTestId('group-row-source-control-write')).toBeInTheDocument();
    // docs carries a stored group row → its override value shows.
    expect(screen.getByTestId('group-row-docs')).toHaveTextContent('60');
    expect(screen.getByTestId('override-source-scw:pr.merge')).toHaveTextContent('Default');
    expect(screen.getByTestId('override-source-scw:branch.push')).toHaveTextContent(
      'Action override',
    );
  });

  it('sets a group threshold through PUT and reloads', async () => {
    setup();
    render(<OverridesPanel />);

    const input = await screen.findByLabelText('Threshold for source-control-write');
    await userEvent.type(input, '95');
    const row = screen.getByTestId('group-row-source-control-write');
    await userEvent.click(row.querySelector('button')!);

    await waitFor(() =>
      expect(mockSetGroupThreshold).toHaveBeenCalledWith('source-control-write', 95),
    );
    // One load on mount + one reload after the write.
    await waitFor(() => expect(mockGetPolicy).toHaveBeenCalledTimes(2));
  });

  it('clears a stored group row through DELETE', async () => {
    setup();
    render(<OverridesPanel />);

    await screen.findByTestId('group-row-docs');
    await userEvent.click(screen.getByText('Clear'));

    await waitFor(() => expect(mockDeleteGroupOverride).toHaveBeenCalledWith('docs'));
  });

  it('forces an action on with the server-reported minimum (no client arithmetic)', async () => {
    setup();
    render(<OverridesPanel />);

    await userEvent.click(await screen.findByLabelText('Force scw:pr.merge on'));

    // The value sent is the /policy dial minimum the server returned (1 here).
    await waitFor(() =>
      expect(mockSetActionThreshold).toHaveBeenCalledWith('scw:pr.merge', 1),
    );
  });

  it('toggles enabled through PUT', async () => {
    setup();
    render(<OverridesPanel />);

    await userEvent.click(await screen.findByLabelText('Disable scw:pr.merge'));

    await waitFor(() => expect(mockSetActionEnabled).toHaveBeenCalledWith('scw:pr.merge', false));
  });

  it('removes an action override through DELETE', async () => {
    setup();
    render(<OverridesPanel />);

    await userEvent.click(await screen.findByLabelText('Remove override for scw:branch.push'));

    await waitFor(() =>
      expect(mockDeleteActionOverride).toHaveBeenCalledWith('scw:branch.push'),
    );
  });

  it('reset-all requires a confirm and then POSTs the reset', async () => {
    setup();
    render(<OverridesPanel />);

    await userEvent.click(await screen.findByText('Reset all overrides'));
    // Nothing sent yet — the confirm step is in the way.
    expect(mockResetPolicy).not.toHaveBeenCalled();

    await userEvent.click(screen.getByText('Yes, remove all'));
    await waitFor(() => expect(mockResetPolicy).toHaveBeenCalledTimes(1));
  });

  it('cancelling the reset confirm sends nothing', async () => {
    setup();
    render(<OverridesPanel />);

    await userEvent.click(await screen.findByText('Reset all overrides'));
    await userEvent.click(screen.getByText('Cancel'));

    expect(mockResetPolicy).not.toHaveBeenCalled();
    expect(screen.getByText('Reset all overrides')).toBeInTheDocument();
  });

  it('surfaces a failed write as an alert without losing the table', async () => {
    setup();
    mockSetActionEnabled.mockRejectedValue(new Error('member role may not edit policy'));
    render(<OverridesPanel />);

    await userEvent.click(await screen.findByLabelText('Disable scw:pr.merge'));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'member role may not edit policy',
    );
    expect(screen.getByTestId('override-row-scw:pr.merge')).toBeInTheDocument();
  });
});
