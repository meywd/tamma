// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import { PromptsPage } from './PromptsPage.js';
import type { ResolvedPrompt, UseTenantPromptsReturn } from '../../hooks/useTenantPrompts.js';

const mockUseTenantPrompts = vi.fn();
const mockUseCurrentUser = vi.fn();

vi.mock('../../hooks/useTenantPrompts.js', () => ({
  useTenantPrompts: () => mockUseTenantPrompts(),
}));

vi.mock('../../hooks/admin/useCurrentUser.js', () => ({
  useCurrentUser: () => mockUseCurrentUser(),
}));

const SAMPLE_PROMPTS: ResolvedPrompt[] = [
  {
    role: 'developer',
    action: 'implement',
    template: 'sys impl',
    systemPrompt: 'sys',
    variables: ['role'],
    enableTools: true,
    maxTokens: 4096,
    source: 'user',
  },
  {
    role: 'tester',
    action: 'write-tests',
    template: 'sys tests',
    systemPrompt: 'sys',
    variables: [],
    enableTools: false,
    maxTokens: 4096,
    source: 'system',
  },
];

function setup(opts?: {
  role?: 'owner' | 'admin' | 'member';
  prompts?: ResolvedPrompt[];
  loading?: boolean;
  error?: string | null;
}) {
  const prompts = opts?.prompts ?? SAMPLE_PROMPTS;
  const overrideCount = prompts.filter((p) => p.source === 'user').length;
  const hookValue: UseTenantPromptsReturn = {
    prompts,
    loading: opts?.loading ?? false,
    error: opts?.error ?? null,
    overrideCount,
    fetchPrompts: vi.fn().mockResolvedValue(undefined),
    getPrompt: vi.fn().mockResolvedValue(null),
    upsertOverride: vi.fn().mockResolvedValue(prompts[0]!),
    deleteOverride: vi.fn().mockResolvedValue(true),
    renderPreview: vi.fn().mockResolvedValue(null),
  };
  mockUseTenantPrompts.mockReturnValue(hookValue);
  mockUseCurrentUser.mockReturnValue({
    user: { id: 'u1', username: 'u', githubId: 1, role: opts?.role ?? 'owner' },
    loading: false,
    isAdmin: opts?.role !== 'member',
    isOwner: opts?.role === 'owner',
  });
  return hookValue;
}

describe('PromptsPage', () => {
  beforeEach(() => vi.clearAllMocks());

  it('renders the "AI Prompts" heading', () => {
    setup();
    render(<PromptsPage />);
    expect(screen.getByRole('heading', { name: /AI Prompts/i })).toBeInTheDocument();
  });

  it('renders the override count', () => {
    setup();
    const { container } = render(<PromptsPage />);
    const text = container.textContent ?? '';
    expect(text).toMatch(/1\s*of 2 prompts overridden/i);
  });

  it('shows loading indicator when loading', () => {
    setup({ loading: true, prompts: [] });
    render(<PromptsPage />);
    expect(document.querySelector('.animate-spin')).toBeInTheDocument();
  });

  it('shows error banner on error', async () => {
    setup({ error: 'Network down' });
    render(<PromptsPage />);
    await waitFor(() => expect(screen.getByText('Network down')).toBeInTheDocument());
  });

  it('shows read-only banner for members', () => {
    setup({ role: 'member' });
    render(<PromptsPage />);
    expect(screen.getByText(/read-only/i)).toBeInTheDocument();
  });

  it('does not show read-only banner for admins', () => {
    setup({ role: 'admin' });
    render(<PromptsPage />);
    expect(screen.queryByText(/read-only/i)).not.toBeInTheDocument();
  });
});
