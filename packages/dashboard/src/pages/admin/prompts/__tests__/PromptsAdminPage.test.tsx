// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PromptsAdminPage } from '../PromptsAdminPage.js';
import type {
  PromptResponse,
  SystemDefaultsResponse,
} from '../../../../services/admin/prompts-api-client.js';

const mockUseSystemPrompts = vi.fn();

vi.mock('../../../../hooks/admin/useSystemPrompts.js', () => ({
  useSystemPrompts: () => mockUseSystemPrompts(),
}));

// ConventionPreview hits the network — stub it out so the page tests
// stay focused on tab-switching + table rendering, and don't require
// a fetch mock on every test.
vi.mock('../../../../components/prompts/ConventionPreview.js', () => ({
  ConventionPreview: () => <div data-testid="convention-preview-stub">conventions</div>,
}));

function makePrompt(role: string, action: string): PromptResponse {
  return {
    role,
    action,
    template: `${role}/${action} template body`,
    systemPrompt: null,
    variables: [],
    enableTools: false,
    maxTokens: 4096,
    source: 'system',
  };
}

function makeData(): SystemDefaultsResponse {
  return {
    roleActionTemplates: [
      makePrompt('developer', 'implement'),
      makePrompt('developer', 'plan'),
      makePrompt('tester', 'write-tests'),
    ],
    systemPrompts: {
      developer: 'You are a developer.',
      tester: 'You are a tester.',
      security: 'You are security.',
      devops: 'You are devops.',
      architect: 'You are architect.',
      product_owner: 'You are PO.',
      senior_developer: 'You are senior dev.',
      tech_writer: 'You are tech writer.',
    },
    actionDefaults: {
      implement: makePrompt('', 'implement'),
      plan: makePrompt('', 'plan'),
    },
  };
}

function setup(overrides?: Partial<ReturnType<typeof mockUseSystemPrompts>>) {
  const defaults = {
    data: makeData(),
    loading: false,
    error: null,
    reload: vi.fn(),
    getResolved: vi.fn(),
    upsertOverride: vi.fn(),
    resetOverride: vi.fn(),
    upsertSystemPromptOverride: vi.fn(),
    resetSystemPromptOverride: vi.fn(),
    ...overrides,
  };
  mockUseSystemPrompts.mockReturnValue(defaults);
  return defaults;
}

describe('PromptsAdminPage', () => {
  const user = userEvent.setup();

  beforeEach(() => {
    mockUseSystemPrompts.mockReset();
  });

  it('shows a spinner while initial load is in flight', () => {
    setup({ data: null, loading: true });
    render(<PromptsAdminPage />);
    expect(document.querySelector('.animate-spin')).not.toBeNull();
  });

  it('shows an error banner with retry on load failure', async () => {
    const reload = vi.fn();
    setup({ data: null, loading: false, error: 'Boom', reload });
    render(<PromptsAdminPage />);
    expect(screen.getByText('Boom')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /retry/i }));
    expect(reload).toHaveBeenCalled();
  });

  it('renders the templates tab by default with rows from the snapshot', () => {
    setup();
    render(<PromptsAdminPage />);
    // Three rows from the mock snapshot.
    expect(screen.getByText(/3 of 3 templates/i)).toBeInTheDocument();
  });

  it('switches to the System Prompts tab and renders 8 cards', async () => {
    setup();
    render(<PromptsAdminPage />);
    await user.click(screen.getByRole('button', { name: /^System Prompts$/i }));
    expect(screen.getAllByRole('button', { name: /^edit$/i })).toHaveLength(8);
  });

  it('switches to the Action Defaults tab', async () => {
    setup();
    render(<PromptsAdminPage />);
    await user.click(screen.getByRole('button', { name: /^Action Defaults$/i }));
    expect(
      screen.getByText(/Layer-4 safety-net templates/i),
    ).toBeInTheDocument();
  });

  it('switches to the Conventions tab and mounts the conventions stub', async () => {
    setup();
    render(<PromptsAdminPage />);
    await user.click(screen.getByRole('button', { name: /^Conventions$/i }));
    expect(screen.getByTestId('convention-preview-stub')).toBeInTheDocument();
  });

  it('opens the edit drawer when a template row is clicked', async () => {
    const getResolved = vi.fn().mockResolvedValue({
      role: 'developer',
      action: 'implement',
      template: 'You are {{role}}.',
      systemPrompt: null,
      variables: ['role'],
      enableTools: false,
      maxTokens: 4096,
      source: 'system',
    });
    setup({ getResolved });
    render(<PromptsAdminPage />);
    // Click the first data row (skip header)
    const rows = screen.getAllByRole('row').slice(1);
    const firstRow = rows[0];
    if (!firstRow) throw new Error('no rows');
    await user.click(firstRow);
    await waitFor(() =>
      expect(getResolved).toHaveBeenCalledWith('developer', 'implement'),
    );
  });
});
