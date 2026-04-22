// @vitest-environment jsdom
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PromptTable } from '../PromptTable.js';
import type { PromptResponse } from '../../../services/admin/prompts-api-client.js';

function makePrompt(role: string, action: string, overrides?: Partial<PromptResponse>): PromptResponse {
  return {
    role,
    action,
    template: `${role}/${action} template body — implement the change`,
    systemPrompt: null,
    variables: ['issue_body'],
    enableTools: false,
    maxTokens: 4096,
    source: 'system',
    ...overrides,
  };
}

const PROMPTS: PromptResponse[] = [
  makePrompt('developer', 'implement', { enableTools: true, maxTokens: 16384 }),
  makePrompt('developer', 'plan'),
  makePrompt('developer', 'code-review', { source: 'user' }),
  makePrompt('tester', 'implement', { template: 'tester implement — focus on coverage' }),
  makePrompt('security', 'code-review'),
];

describe('PromptTable', () => {
  const user = userEvent.setup();

  it('renders all rows from the prompts prop', () => {
    render(<PromptTable prompts={PROMPTS} onRowClick={() => {}} />);
    // 5 data rows + header
    expect(screen.getAllByRole('row')).toHaveLength(PROMPTS.length + 1);
    // Counter shows full count
    expect(screen.getByText(/5 of 5 templates/i)).toBeInTheDocument();
  });

  it('filters rows by role dropdown', async () => {
    render(<PromptTable prompts={PROMPTS} onRowClick={() => {}} />);
    const roleSelect = screen.getByLabelText(/filter by role/i);
    await user.selectOptions(roleSelect, 'developer');
    // 3 developer rows + header
    expect(screen.getAllByRole('row')).toHaveLength(4);
    expect(screen.getByText(/3 of 5 templates/i)).toBeInTheDocument();
  });

  it('filters rows by action dropdown', async () => {
    render(<PromptTable prompts={PROMPTS} onRowClick={() => {}} />);
    const actionSelect = screen.getByLabelText(/filter by action/i);
    await user.selectOptions(actionSelect, 'code-review');
    // 2 code-review rows + header
    expect(screen.getAllByRole('row')).toHaveLength(3);
  });

  it('filters by template content via search', async () => {
    render(<PromptTable prompts={PROMPTS} onRowClick={() => {}} />);
    const search = screen.getByLabelText(/search template content/i);
    await user.type(search, 'coverage');
    // Only the tester/implement row mentions "coverage"
    expect(screen.getAllByRole('row')).toHaveLength(2);
    expect(screen.getByText(/1 of 5 templates/i)).toBeInTheDocument();
  });

  it('shows the override badge for user-source rows', () => {
    render(<PromptTable prompts={PROMPTS} onRowClick={() => {}} />);
    const overrideBadges = screen.getAllByText('override');
    expect(overrideBadges).toHaveLength(1);
  });

  it('invokes onRowClick with role + action on row click', async () => {
    const onRowClick = vi.fn();
    render(<PromptTable prompts={PROMPTS} onRowClick={onRowClick} />);
    const rows = screen.getAllByRole('row').slice(1); // skip header
    const firstRow = rows[0];
    if (!firstRow) throw new Error('row missing');
    await user.click(within(firstRow).getAllByRole('cell')[0]!);
    expect(onRowClick).toHaveBeenCalledWith('developer', 'implement');
  });

  it('renders an empty-state row when no rows match', async () => {
    render(<PromptTable prompts={PROMPTS} onRowClick={() => {}} />);
    const search = screen.getByLabelText(/search template content/i);
    await user.type(search, 'zzznevermatchzzz');
    expect(
      screen.getByText(/no templates match the current filters/i),
    ).toBeInTheDocument();
  });
});
