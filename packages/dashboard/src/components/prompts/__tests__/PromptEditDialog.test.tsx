// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PromptEditDialog } from '../PromptEditDialog.js';
import type { PromptResponse } from '../../../services/admin/prompts-api-client.js';

function makeResolved(overrides?: Partial<PromptResponse>): PromptResponse {
  return {
    role: 'developer',
    action: 'implement',
    template: 'You are {{role}}. Plan: {{plan}}. Files: {{file_list}}.',
    systemPrompt: null,
    variables: ['role', 'plan', 'file_list'],
    enableTools: true,
    maxTokens: 8192,
    source: 'system',
    ...overrides,
  };
}

describe('PromptEditDialog', () => {
  const user = userEvent.setup();

  it('loads the resolved prompt and shows extracted variables', async () => {
    const loadResolved = vi.fn().mockResolvedValue(makeResolved());
    const saveOverride = vi.fn();
    const resetOverride = vi.fn();

    render(
      <PromptEditDialog
        role="developer"
        action="implement"
        onClose={() => {}}
        onChanged={() => {}}
        loadResolved={loadResolved}
        saveOverride={saveOverride}
        resetOverride={resetOverride}
      />,
    );

    await waitFor(() => expect(loadResolved).toHaveBeenCalledWith('developer', 'implement'));
    // Variables count + chips
    await screen.findByText(/Variables \(3\)/);
    expect(screen.getByRole('button', { name: '{{role}}' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '{{plan}}' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '{{file_list}}' })).toBeInTheDocument();
  });

  it('extracts variables in real-time as the template is edited', async () => {
    const loadResolved = vi.fn().mockResolvedValue(makeResolved({ template: 'no vars' }));
    render(
      <PromptEditDialog
        role="developer"
        action="implement"
        onClose={() => {}}
        onChanged={() => {}}
        loadResolved={loadResolved}
        saveOverride={vi.fn()}
        resetOverride={vi.fn()}
      />,
    );

    await screen.findByText(/Variables \(0\)/);
    const textarea = screen.getByLabelText(/^Template$/i);
    // user-event treats '{' as a key-spec sentinel; escape with '{{'
    // → literal '{', and '}}' → literal '}'.
    await user.type(textarea, ' {{{{newVar}}');
    await screen.findByText(/Variables \(1\)/);
    expect(screen.getByRole('button', { name: '{{newVar}}' })).toBeInTheDocument();
  });

  it('save sends an UpsertPromptRequest with the current state', async () => {
    const loadResolved = vi.fn().mockResolvedValue(makeResolved());
    const saveOverride = vi.fn().mockResolvedValue(makeResolved({ source: 'user' }));
    const onChanged = vi.fn();
    const onClose = vi.fn();

    render(
      <PromptEditDialog
        role="developer"
        action="implement"
        onClose={onClose}
        onChanged={onChanged}
        loadResolved={loadResolved}
        saveOverride={saveOverride}
        resetOverride={vi.fn()}
      />,
    );

    await screen.findByText(/Variables \(3\)/);
    await user.click(screen.getByRole('button', { name: /save override/i }));

    await waitFor(() => expect(saveOverride).toHaveBeenCalledTimes(1));
    expect(saveOverride).toHaveBeenCalledWith(
      'developer',
      'implement',
      expect.objectContaining({
        template: expect.stringContaining('{{role}}'),
        enableTools: true,
        maxTokens: 8192,
        variables: ['role', 'plan', 'file_list'],
      }),
    );
    expect(onChanged).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it('reset asks for confirmation, then calls resetOverride', async () => {
    const loadResolved = vi.fn().mockResolvedValue(makeResolved({ source: 'user' }));
    const resetOverride = vi.fn().mockResolvedValue(undefined);

    render(
      <PromptEditDialog
        role="developer"
        action="implement"
        onClose={() => {}}
        onChanged={() => {}}
        loadResolved={loadResolved}
        saveOverride={vi.fn()}
        resetOverride={resetOverride}
      />,
    );

    await screen.findByText(/user override/i);
    await user.click(screen.getByRole('button', { name: /reset to default/i }));
    // Confirm dialog appears — confirm it.
    await user.click(screen.getByRole('button', { name: /^reset$/i }));
    await waitFor(() =>
      expect(resetOverride).toHaveBeenCalledWith('developer', 'implement'),
    );
  });

  it('does NOT show the reset button when current source is system', async () => {
    const loadResolved = vi.fn().mockResolvedValue(makeResolved({ source: 'system' }));
    render(
      <PromptEditDialog
        role="developer"
        action="implement"
        onClose={() => {}}
        onChanged={() => {}}
        loadResolved={loadResolved}
        saveOverride={vi.fn()}
        resetOverride={vi.fn()}
      />,
    );
    await screen.findByText(/system default/i);
    expect(
      screen.queryByRole('button', { name: /reset to default/i }),
    ).not.toBeInTheDocument();
  });

  it('renders a load error if loadResolved throws', async () => {
    const loadResolved = vi.fn().mockRejectedValue(new Error('boom'));
    render(
      <PromptEditDialog
        role="developer"
        action="implement"
        onClose={() => {}}
        onChanged={() => {}}
        loadResolved={loadResolved}
        saveOverride={vi.fn()}
        resetOverride={vi.fn()}
      />,
    );
    await screen.findByText('boom');
  });
});
