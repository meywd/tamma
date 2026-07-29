// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RulesEditDialog } from '../RulesEditDialog.js';
import type {
  AcceptanceRules,
  ResolvedAcceptanceRules,
} from '../../../services/admin/acceptance-rules-api-client.js';

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

function makeResolved(rules?: Partial<AcceptanceRules>): ResolvedAcceptanceRules {
  return {
    rules: makeRules(rules),
    source: 'system-default',
    version: 1,
    documentTypeKey: 'design',
    resolvedAt: '2026-07-29T00:00:00.000Z',
  };
}

describe('RulesEditDialog', () => {
  const user = userEvent.setup();

  /**
   * Story 43-0's client-side regression pin — the test that would have caught the
   * bug. `design` resolves with `acceptorRequirement: 'human'`; an admin edits an
   * UNRELATED field and saves; the submitted body must still say `human`.
   * Pre-fix, the body memo built eight fields and the API defaulted the ninth to
   * `any`, silently removing the human-acceptance requirement.
   */
  it('preserves acceptorRequirement when an unrelated field is edited', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);

    render(
      <RulesEditDialog
        resolved={makeResolved({ acceptorRequirement: 'human' })}
        onSave={onSave}
        onReset={vi.fn()}
        onClose={vi.fn()}
      />,
    );

    const rounds = screen.getByLabelText('Max revision rounds');
    await user.clear(rounds);
    await user.type(rounds, '5');
    await user.click(screen.getByRole('button', { name: /Save override/ }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    const [documentTypeKey, body] = onSave.mock.calls[0] as [string, AcceptanceRules];
    expect(documentTypeKey).toBe('design');
    expect(body.maxRevisionRounds).toBe(5);
    expect(body.acceptorRequirement).toBe('human');
  });

  /** AC2 — the field is editable, not merely echoed back. */
  it('submits the acceptorRequirement chosen in the control', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);

    render(
      <RulesEditDialog
        resolved={makeResolved({ acceptorRequirement: 'any' })}
        onSave={onSave}
        onReset={vi.fn()}
        onClose={vi.fn()}
      />,
    );

    await user.selectOptions(screen.getByLabelText('Acceptor requirement'), 'human');
    await user.click(screen.getByRole('button', { name: /Save override/ }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    const [, body] = onSave.mock.calls[0] as [string, AcceptanceRules];
    expect(body.acceptorRequirement).toBe('human');
  });

  /** The control reflects the resolved value rather than a hardcoded default. */
  it('seeds the control from the resolved payload', () => {
    render(
      <RulesEditDialog
        resolved={makeResolved({ acceptorRequirement: 'human' })}
        onSave={vi.fn()}
        onReset={vi.fn()}
        onClose={vi.fn()}
      />,
    );

    expect(screen.getByLabelText<HTMLSelectElement>('Acceptor requirement').value).toBe(
      'human',
    );
  });

  /**
   * The whole-body pin: the PUT body must carry every field of `AcceptanceRules`
   * AS THE INTERFACE DECLARES IT TODAY.
   *
   * Be precise about what this catches (review MINOR-4, 2026-07-29). The earlier
   * comment claimed "a future tenth field added to the interface and forgotten in
   * the memo fails here" — it cannot. The expected key list below is a hardcoded
   * literal, so adding a tenth field to `AcceptanceRules` and forgetting it in the
   * memo leaves both the memo AND this literal at nine keys, and the assertion
   * still passes. What actually catches that omission is (a) `tsc` — the memo is
   * typed `useMemo<AcceptanceRules>`, so a missing required property is a compile
   * error — and (b) the C# `AcceptanceRulesUpsertRequestFieldSetTests`, which
   * derives the field set by reflection over the DTO rather than restating it.
   *
   * What THIS test does catch is the 43-0 bug shape directly: the memo silently
   * DROPPING a field that the interface still declares (the original defect —
   * `acceptorRequirement` absent from a body typed against an interface that also
   * omitted it), and any stray extra key. It is a body-shape pin, not a
   * contract-drift pin.
   */
  it('submits every field of the acceptance-rules contract', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);

    render(
      <RulesEditDialog
        resolved={makeResolved({ acceptorRequirement: 'human' })}
        onSave={onSave}
        onReset={vi.fn()}
        onClose={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: /Save override/ }));
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));

    const [, body] = onSave.mock.calls[0] as [string, AcceptanceRules];
    expect(Object.keys(body).sort()).toEqual(
      [
        'acceptorRequirement',
        'alwaysEscalate',
        'ambiguityEscalationThreshold',
        'autonomyLevel',
        'decisionGuidance',
        'maxRevisionRounds',
        'maxValidationRepairAttempts',
        'reviewerSelection',
        'routingGuidance',
      ].sort(),
    );
  });
});
