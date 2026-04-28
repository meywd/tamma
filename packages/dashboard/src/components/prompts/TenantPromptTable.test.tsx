// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TenantPromptTable } from './TenantPromptTable.js';
import type { ResolvedPrompt } from '../../hooks/useTenantPrompts.js';

const PROMPTS: ResolvedPrompt[] = [
  {
    role: 'developer',
    action: 'implement',
    template: 'You are a {{role}} implementing {{task}}',
    systemPrompt: 'dev system',
    variables: ['role', 'task'],
    enableTools: true,
    maxTokens: 4096,
    source: 'user',
  },
  {
    role: 'developer',
    action: 'plan',
    template: 'Plan for {{workItemJson}}',
    systemPrompt: 'dev system',
    variables: ['workItemJson'],
    enableTools: false,
    maxTokens: 8192,
    source: 'system',
  },
  {
    role: 'tester',
    action: 'write-tests',
    template: 'Write tests for {{code}}',
    systemPrompt: 'tester system',
    variables: ['code'],
    enableTools: true,
    maxTokens: 4096,
    source: 'system',
  },
];

describe('TenantPromptTable', () => {
  const user = userEvent.setup();

  it('renders a row per prompt', () => {
    render(<TenantPromptTable prompts={PROMPTS} overrideCount={1} onRowClick={vi.fn()} />);
    expect(screen.getByTestId('prompt-row-developer-implement')).toBeInTheDocument();
    expect(screen.getByTestId('prompt-row-developer-plan')).toBeInTheDocument();
    expect(screen.getByTestId('prompt-row-tester-write-tests')).toBeInTheDocument();
  });

  it('shows "X of N prompts overridden" count', () => {
    const { container } = render(
      <TenantPromptTable prompts={PROMPTS} overrideCount={1} onRowClick={vi.fn()} />,
    );
    const counter = container.querySelector('.text-gray-600');
    expect(counter?.textContent).toMatch(/1\s+of 3 prompts overridden/i);
  });

  it('renders Override badge for overridden rows and Default for others', () => {
    render(<TenantPromptTable prompts={PROMPTS} overrideCount={1} onRowClick={vi.fn()} />);
    expect(screen.getAllByText('Override')).toHaveLength(1);
    expect(screen.getAllByText('Default')).toHaveLength(2);
  });

  it('highlights overridden rows with blue background', () => {
    const { container } = render(
      <TenantPromptTable prompts={PROMPTS} overrideCount={1} onRowClick={vi.fn()} />,
    );
    const overrideRow = container.querySelector('[data-testid="prompt-row-developer-implement"]');
    expect(overrideRow).not.toBeNull();
    expect(overrideRow!.className).toMatch(/bg-blue-50/);
  });

  it('filters rows by role', async () => {
    render(<TenantPromptTable prompts={PROMPTS} overrideCount={1} onRowClick={vi.fn()} />);
    const roleSelect = screen.getByLabelText('Filter by role');
    await user.selectOptions(roleSelect, 'tester');
    expect(screen.queryByTestId('prompt-row-developer-implement')).not.toBeInTheDocument();
    expect(screen.getByTestId('prompt-row-tester-write-tests')).toBeInTheDocument();
  });

  it('filters rows by action', async () => {
    render(<TenantPromptTable prompts={PROMPTS} overrideCount={1} onRowClick={vi.fn()} />);
    const actionSelect = screen.getByLabelText('Filter by action');
    await user.selectOptions(actionSelect, 'plan');
    expect(screen.getByTestId('prompt-row-developer-plan')).toBeInTheDocument();
    expect(screen.queryByTestId('prompt-row-developer-implement')).not.toBeInTheDocument();
    expect(screen.queryByTestId('prompt-row-tester-write-tests')).not.toBeInTheDocument();
  });

  it('calls onRowClick with role and action on row click', async () => {
    const onRowClick = vi.fn();
    render(<TenantPromptTable prompts={PROMPTS} overrideCount={1} onRowClick={onRowClick} />);
    const row = screen.getByTestId('prompt-row-developer-plan');
    await user.click(row);
    expect(onRowClick).toHaveBeenCalledWith('developer', 'plan');
  });
});
