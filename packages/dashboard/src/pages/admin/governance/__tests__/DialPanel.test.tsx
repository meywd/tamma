// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import { DialPanel } from '../DialPanel.js';
import type {
  ActionPolicyResponse,
  AutonomyDialInfo,
  CatalogAction,
  PolicyAction,
} from '../../../../services/admin/actions-policy-api-client.js';

const mockGetDial = vi.fn();
const mockGetCatalog = vi.fn();
const mockGetPolicy = vi.fn();

vi.mock('../../../../services/admin/actions-policy-api-client.js', () => ({
  actionsPolicyApi: {
    getDial: (...args: unknown[]) => mockGetDial(...args),
    getCatalog: (...args: unknown[]) => mockGetCatalog(...args),
    getPolicy: (...args: unknown[]) => mockGetPolicy(...args),
  },
}));

const DIAL: AutonomyDialInfo = { min: 1, max: 100, alwaysHuman: 101, default: 70 };

function makeCatalogAction(overrides?: Partial<CatalogAction>): CatalogAction {
  return {
    key: 'scw:pr.merge',
    ns: 'scw',
    group: 'source-control-write',
    risk: 'high',
    title: 'Merge a pull request',
    summary: 'Merges a PR into its base branch.',
    reversible: false,
    defaultMinAutonomy: 90,
    escalatableToHuman: true,
    enforceable: true,
    siteKey: 'pr.merge',
    enforcementSites: ['route: POST /api/engine/pr-merge'],
    ...overrides,
  };
}

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

function makePolicy(actions: PolicyAction[]): ActionPolicyResponse {
  return {
    dial: { ...DIAL, current: 70, viewLevel: 70 },
    groups: [
      {
        group: 'source-control-write',
        description: 'Writing to source control.',
        members: 1,
        principalRow: null,
        platformRow: null,
      },
      {
        group: 'platform-automation',
        description: 'Deterministic platform plumbing.',
        members: 1,
        principalRow: null,
        platformRow: null,
      },
    ],
    actions,
  };
}

function setupSuccess() {
  mockGetDial.mockResolvedValue(DIAL);
  mockGetCatalog.mockResolvedValue([
    makeCatalogAction(),
    makeCatalogAction({
      key: 'automation:snapshot.write',
      ns: 'automation',
      group: 'platform-automation',
      title: 'Write a debug snapshot',
      defaultMinAutonomy: 1,
    }),
  ]);
  mockGetPolicy.mockResolvedValue(
    makePolicy([
      makePolicyAction(),
      makePolicyAction({
        key: 'automation:snapshot.write',
        group: 'platform-automation',
        title: 'Write a debug snapshot',
        isMachinery: true,
        editable: false,
        reason: 'machinery-not-dial-governed',
      }),
    ]),
  );
}

describe('DialPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows a spinner while loading', () => {
    mockGetDial.mockReturnValue(new Promise(() => {}));
    mockGetCatalog.mockReturnValue(new Promise(() => {}));
    mockGetPolicy.mockReturnValue(new Promise(() => {}));
    render(<DialPanel />);
    expect(screen.queryByTestId('dial-current')).not.toBeInTheDocument();
  });

  it('shows an error banner with retry when a read fails', async () => {
    mockGetDial.mockRejectedValue(new Error('boom'));
    mockGetCatalog.mockResolvedValue([]);
    mockGetPolicy.mockResolvedValue(makePolicy([]));
    render(<DialPanel />);
    expect(await screen.findByText('Failed to load the autonomy dial')).toBeInTheDocument();
    expect(screen.getByText('boom')).toBeInTheDocument();
    expect(screen.getByText('Retry')).toBeInTheDocument();
  });

  it('renders the current dial and the catalog grouped by group', async () => {
    setupSuccess();
    render(<DialPanel />);

    expect(await screen.findByTestId('dial-current')).toHaveTextContent('70');
    expect(screen.getByTestId('dial-row-scw:pr.merge')).toBeInTheDocument();
    expect(screen.getByText('Merge a pull request')).toBeInTheDocument();
    // Effective min-autonomy from the resolved policy view.
    expect(screen.getByTestId('dial-row-scw:pr.merge')).toHaveTextContent('90');
    // Below the dial → needs a person.
    expect(screen.getByText('Needs a person')).toBeInTheDocument();
  });

  it('shows machinery rows but marks them not dial-governed', async () => {
    setupSuccess();
    render(<DialPanel />);

    const badge = await screen.findByTestId('dial-machinery-automation:snapshot.write');
    expect(badge).toHaveTextContent('Not dial-governed');
  });
});
