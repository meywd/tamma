// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PromptPreview } from './PromptPreview.js';

describe('PromptPreview', () => {
  const user = userEvent.setup();

  it('renders an input field per variable', () => {
    render(
      <PromptPreview
        role="developer"
        action="implement"
        variables={['role', 'task']}
        renderPreview={vi.fn()}
      />,
    );
    // Details element needs to be expanded
    const summary = screen.getByText(/Preview \/ Test/i);
    summary.click();
    expect(screen.getByLabelText('{{role}}')).toBeInTheDocument();
    expect(screen.getByLabelText('{{task}}')).toBeInTheDocument();
  });

  it('calls renderPreview with variable values on Render click', async () => {
    const renderPreview = vi.fn().mockResolvedValue({
      renderedTemplate: 'Hello world',
      renderedSystemPrompt: 'system',
      unresolvedVariables: [],
      enableTools: false,
      maxTokens: 4096,
    });
    render(
      <PromptPreview
        role="developer"
        action="implement"
        variables={['name']}
        renderPreview={renderPreview}
      />,
    );
    const summary = screen.getByText(/Preview \/ Test/i);
    await user.click(summary);
    const input = screen.getByLabelText('{{name}}');
    await user.type(input, 'world');
    await user.click(screen.getByRole('button', { name: /Render Preview/i }));
    await waitFor(() =>
      expect(renderPreview).toHaveBeenCalledWith('developer', 'implement', { name: 'world' }),
    );
  });

  it('displays rendered template text', async () => {
    const renderPreview = vi.fn().mockResolvedValue({
      renderedTemplate: 'Hello, Alice',
      renderedSystemPrompt: '',
      unresolvedVariables: [],
      enableTools: false,
      maxTokens: 4096,
    });
    render(
      <PromptPreview
        role="developer"
        action="implement"
        variables={['name']}
        renderPreview={renderPreview}
      />,
    );
    await user.click(screen.getByText(/Preview \/ Test/i));
    await user.click(screen.getByRole('button', { name: /Render Preview/i }));
    expect(await screen.findByText('Hello, Alice')).toBeInTheDocument();
  });

  it('highlights unresolved variables in red', async () => {
    const renderPreview = vi.fn().mockResolvedValue({
      renderedTemplate: 'Hello {{name}}',
      renderedSystemPrompt: '',
      unresolvedVariables: ['name'],
      enableTools: false,
      maxTokens: 4096,
    });
    render(
      <PromptPreview
        role="developer"
        action="implement"
        variables={['name']}
        renderPreview={renderPreview}
      />,
    );
    await user.click(screen.getByText(/Preview \/ Test/i));
    await user.click(screen.getByRole('button', { name: /Render Preview/i }));
    const unresolved = await screen.findByTestId('unresolved-variables');
    expect(unresolved.className).toMatch(/text-red-600/);
    expect(unresolved.textContent).toContain('name');
  });
});
