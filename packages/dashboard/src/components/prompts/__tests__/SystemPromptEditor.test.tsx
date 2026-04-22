// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SystemPromptEditor } from '../SystemPromptEditor.js';

const SYSTEM_PROMPTS: Record<string, string> = {
  developer: 'You are an expert software developer working on Tamma.',
  tester: 'You are an expert tester focused on coverage.',
  security: 'You are a security expert following OWASP.',
  devops: 'You are a DevOps engineer focused on CI/CD.',
  architect: 'You are a senior architect.',
  product_owner: 'You are a product owner.',
  senior_developer: 'You are a senior developer and tech lead.',
  tech_writer: 'You are a tech writer.',
};

describe('SystemPromptEditor', () => {
  const user = userEvent.setup();

  it('renders a card per role with the shipped preamble preview', () => {
    render(
      <SystemPromptEditor
        systemPrompts={SYSTEM_PROMPTS}
        upsertSystemPromptOverride={vi.fn()}
        resetSystemPromptOverride={vi.fn()}
      />,
    );
    expect(screen.getByText('Developer')).toBeInTheDocument();
    expect(screen.getByText('Tech Writer')).toBeInTheDocument();
    // 8 Edit buttons (one per role)
    expect(screen.getAllByRole('button', { name: /^edit$/i })).toHaveLength(8);
  });

  it('opens an inline editor on Edit and saves the override', async () => {
    const upsert = vi.fn().mockResolvedValue(undefined);
    render(
      <SystemPromptEditor
        systemPrompts={SYSTEM_PROMPTS}
        upsertSystemPromptOverride={upsert}
        resetSystemPromptOverride={vi.fn()}
      />,
    );

    const editButtons = screen.getAllByRole('button', { name: /^edit$/i });
    await user.click(editButtons[0]!); // developer card

    const textarea = await screen.findByLabelText(/system prompt for developer/i);
    await user.type(textarea, ' UPDATED');

    await user.click(screen.getByRole('button', { name: /save override/i }));
    await waitFor(() => expect(upsert).toHaveBeenCalledTimes(1));
    expect(upsert).toHaveBeenCalledWith(
      'developer',
      expect.objectContaining({
        template: expect.stringContaining('UPDATED'),
      }),
    );
  });

  it('confirms before resetting an override', async () => {
    const reset = vi.fn().mockResolvedValue(undefined);
    render(
      <SystemPromptEditor
        systemPrompts={SYSTEM_PROMPTS}
        upsertSystemPromptOverride={vi.fn()}
        resetSystemPromptOverride={reset}
      />,
    );

    const resetButtons = screen.getAllByRole('button', { name: /reset override/i });
    await user.click(resetButtons[0]!);
    // Confirm dialog
    await user.click(screen.getByRole('button', { name: /^reset$/i }));
    await waitFor(() => expect(reset).toHaveBeenCalledWith('developer'));
  });
});
