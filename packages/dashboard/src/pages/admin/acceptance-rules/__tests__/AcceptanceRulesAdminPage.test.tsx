// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AcceptanceRulesAdminPage } from '../AcceptanceRulesAdminPage.js';
import type {
  AcceptanceRules,
  AcceptanceRulesSource,
  ResolvedAcceptanceRules,
} from '../../../../services/admin/acceptance-rules-api-client.js';

const mockUseAcceptanceRules = vi.fn();

vi.mock('../../../../hooks/admin/useAcceptanceRules.js', () => ({
  useAcceptanceRules: () => mockUseAcceptanceRules(),
}));

const DOCUMENT_TYPES = [
  'findings',
  'ambiguity-assessment',
  'clarification',
  'decomposition',
  'plan',
  'design',
  'review',
  'triage-decision',
  'diagnosis',
  'test-spec',
];

function makeRules(overrides?: Partial<AcceptanceRules>): AcceptanceRules {
  return {
    autonomyLevel: 70,
    maxRevisionRounds: 2,
    maxValidationRepairAttempts: 2,
    ambiguityEscalationThreshold: 0.7,
    alwaysEscalate: [],
    reviewerSelection: {
      mode: 'single-reviewer',
      reviewerRole: 'architect',
      panelRoles: [],
      quorum: null,
      decisionRule: 'unanimous',
    },
    acceptorRequirement: 'any',
    decisionGuidance: 'decide guidance',
    routingGuidance: 'route guidance',
    ...overrides,
  };
}

function makeRow(
  documentTypeKey: string,
  source: AcceptanceRulesSource = 'system-default',
  rules?: Partial<AcceptanceRules>,
): ResolvedAcceptanceRules {
  return {
    rules: makeRules(rules),
    source,
    version: 1,
    documentTypeKey,
    resolvedAt: '2026-07-22T00:00:00.000Z',
  };
}

function makeRows(): ResolvedAcceptanceRules[] {
  return DOCUMENT_TYPES.map((t) =>
    t === 'plan' || t === 'review'
      ? makeRow(t, 'type-override', {
          reviewerSelection: {
            mode: 'panel',
            reviewerRole: null,
            panelRoles: ['architect', 'developer', 'tester'],
            quorum: null,
            decisionRule: 'majority',
          },
        })
      : makeRow(t),
  );
}

function setup(overrides?: Partial<ReturnType<typeof mockUseAcceptanceRules>>) {
  const upsert = vi.fn().mockResolvedValue(makeRow('design'));
  const reset = vi.fn().mockResolvedValue(undefined);
  const reload = vi.fn().mockResolvedValue(undefined);
  const value = {
    rows: makeRows(),
    defaults: makeRules(),
    loading: false,
    error: null,
    reload,
    upsert,
    reset,
    ...overrides,
  };
  mockUseAcceptanceRules.mockReturnValue(value);
  return { upsert, reset, reload };
}

describe('AcceptanceRulesAdminPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders a row for every document type with a provenance badge', () => {
    setup();
    render(<AcceptanceRulesAdminPage />);
    for (const t of DOCUMENT_TYPES) {
      expect(screen.getByTestId(`rules-row-${t}`)).toBeTruthy();
      expect(screen.getByTestId(`rules-source-${t}`)).toBeTruthy();
    }
    // plan/review resolve from a type override; others from the default.
    expect(screen.getByTestId('rules-source-plan').textContent).toContain('Type override');
    expect(screen.getByTestId('rules-source-findings').textContent).toContain('Default');
  });

  it('constrains the autonomy dial to 70–100', async () => {
    setup();
    render(<AcceptanceRulesAdminPage />);
    await userEvent.click(screen.getByTestId('rules-row-design'));
    const slider = (await screen.findByLabelText('Autonomy level')) as HTMLInputElement;
    expect(slider.min).toBe('70');
    expect(slider.max).toBe('100');
  });

  it('saves the edited payload via upsert (PUT)', async () => {
    const { upsert } = setup();
    render(<AcceptanceRulesAdminPage />);
    await userEvent.click(screen.getByTestId('rules-row-design'));

    const rounds = (await screen.findByLabelText('Max revision rounds')) as HTMLInputElement;
    await userEvent.clear(rounds);
    await userEvent.type(rounds, '4');

    await userEvent.click(screen.getByText('Save override'));

    await waitFor(() => expect(upsert).toHaveBeenCalledTimes(1));
    const [key, body] = upsert.mock.calls[0]!;
    expect(key).toBe('design');
    expect(body.maxRevisionRounds).toBe(4);
    expect(body.autonomyLevel).toBe(70);
  });

  it('resets an override via reset (DELETE)', async () => {
    const { reset } = setup();
    render(<AcceptanceRulesAdminPage />);
    // plan is a type-override, so the Reset button is enabled.
    await userEvent.click(screen.getByTestId('rules-row-plan'));
    await userEvent.click(await screen.findByText('Reset to default'));
    await waitFor(() => expect(reset).toHaveBeenCalledWith('plan'));
  });

  it('shows an error banner when the hook reports an error', () => {
    setup({ rows: [], error: 'boom', loading: false });
    render(<AcceptanceRulesAdminPage />);
    expect(screen.getByText('Failed to load acceptance rules')).toBeTruthy();
  });
});
