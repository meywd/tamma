/**
 * Story 46-3 AC1/AC3/AC4/AC6 — ModelSettingsPage: roster rendering, the
 * two-state provenance mapping (table-driven over the exported map), BYOK
 * indicator, picker laziness (fetch only on expand), save → provenance flip,
 * reset → fall back to platform default, page-wide 403 downgrade, member
 * read-only, error-keeps-frame + retry, empty roster.
 *
 * Roster fixture mirrors TenantProviderRosterRow — apps/tamma-elsa/src/
 * Tamma.Api/Endpoints/ProviderCredentialEndpoints.cs (note the field is
 * `provider`, NOT `key`; `fallbackModel` is the server-computed
 * skip-principal resolution). Do not invent fields (the 45-1 lesson).
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ModelSettingsPage } from './ModelSettingsPage';
import { provenanceLabel, TENANT_PROVENANCE_LABELS } from './provenance';
import { ApiError } from '../../api/client';
import type {
  ProviderModelsResponse,
  TenantProviderRosterResponse,
} from '../../api/provider-models';

const { mockAuth, mockApi } = vi.hoisted(() => ({
  mockAuth: vi.fn(),
  mockApi: {
    listProviderModelSettings: vi.fn(),
    listProviderModels: vi.fn(),
    getProviderModel: vi.fn(),
    putProviderModel: vi.fn(),
    deleteProviderModel: vi.fn(),
  },
}));

vi.mock('../../hooks/useAuth', () => ({ useAuth: () => mockAuth() }));

vi.mock('../../api/provider-models', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/provider-models')>();
  return { ...actual, providerModelsApi: mockApi };
});

// GET /api/v1/agents/providers/models — enabled-only is the SERVER's concern;
// this fixture reflects it (a disabled provider is simply absent).
const ROSTER: TenantProviderRosterResponse = {
  providers: [
    {
      provider: 'anthropic',
      displayName: 'Anthropic',
      modelsSupported: true,
      model: 'claude-sonnet-4-5',
      source: 'platform-db',
      hasOverride: false,
      byokKeyPresent: true,
      // No override → the fallback IS the resolved model.
      fallbackModel: 'claude-sonnet-4-5',
    },
    {
      provider: 'openai',
      displayName: 'OpenAI',
      modelsSupported: true,
      model: 'gpt-5.2',
      source: 'tenant-override',
      hasOverride: true,
      byokKeyPresent: false,
      // Override active at page load — the server still names what a reset
      // would land on (the bug the field exists to fix).
      fallbackModel: 'gpt-5-default',
    },
    {
      provider: 'z-ai',
      displayName: 'Z.ai',
      modelsSupported: false,
      model: 'glm-4.7',
      source: 'config',
      hasOverride: false,
      byokKeyPresent: false,
      fallbackModel: 'glm-4.7',
    },
    {
      provider: 'deepseek',
      displayName: 'DeepSeek',
      modelsSupported: true,
      model: null,
      source: 'descriptor',
      hasOverride: false,
      byokKeyPresent: false,
      // Nothing anywhere names a model (descriptor default "" → null).
      fallbackModel: null,
    },
  ],
};

const ANTHROPIC_MODELS: ProviderModelsResponse = {
  provider: 'anthropic',
  models: [
    { id: 'claude-sonnet-4-5', displayName: 'Claude Sonnet 4.5', deprecated: false, current: true },
    { id: 'claude-opus-4-6', displayName: 'Claude Opus 4.6', deprecated: false, current: false },
  ],
  fetchedAt: '2026-07-27T12:00:00.000Z',
  stale: false,
  errorCode: null,
};

function renderPage() {
  return render(<ModelSettingsPage />);
}

beforeEach(() => {
  vi.clearAllMocks();
  mockAuth.mockReturnValue({
    user: { id: 'u1', email: 'a@b.dev', displayName: 'A', tenantId: 't1', role: 'admin' },
  });
  mockApi.listProviderModelSettings.mockResolvedValue(ROSTER);
  mockApi.listProviderModels.mockResolvedValue(ANTHROPIC_MODELS);
});

describe('provenance mapping (D3 — tenants see two states, not four)', () => {
  // Table-driven over the exported map: 'tenant-override' is the ONLY source
  // rendered as an override; platform-db/config/descriptor (and anything the
  // server adds later) all collapse to "Platform default".
  it.each([
    ['tenant-override', TENANT_PROVENANCE_LABELS['tenant-override']],
    ['platform-db', TENANT_PROVENANCE_LABELS['platform-default']],
    ['config', TENANT_PROVENANCE_LABELS['platform-default']],
    ['descriptor', TENANT_PROVENANCE_LABELS['platform-default']],
  ])('%s → %s', (source, label) => {
    expect(provenanceLabel(source)).toBe(label);
  });
});

describe('ModelSettingsPage — roster', () => {
  it('renders one card per enabled provider with model + provenance', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText('Anthropic')).toBeInTheDocument());
    expect(screen.getByText('OpenAI')).toBeInTheDocument();
    expect(screen.getByText('Z.ai')).toBeInTheDocument();
    expect(screen.getByText('DeepSeek')).toBeInTheDocument();

    expect(screen.getByText('claude-sonnet-4-5')).toBeInTheDocument();
    expect(screen.getByText('gpt-5.2')).toBeInTheDocument();
    expect(screen.getByText('No model set')).toBeInTheDocument();

    // Provenance wording: one override row, three platform-default rows.
    expect(screen.getAllByText('Your override')).toHaveLength(1);
    expect(screen.getAllByText('Platform default')).toHaveLength(3);
  });

  it('shows the BYOK indicator iff byokKeyPresent', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText('Anthropic')).toBeInTheDocument());
    expect(screen.getAllByText('Your key')).toHaveLength(1);
  });

  it('keeps the frame on load error and retries', async () => {
    mockApi.listProviderModelSettings
      .mockRejectedValueOnce(new Error('boom'))
      .mockResolvedValueOnce(ROSTER);
    renderPage();

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());
    expect(screen.getByText('boom')).toBeInTheDocument();
    // The frame (header) stays.
    expect(screen.getByText('Model settings')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(screen.getByText('Anthropic')).toBeInTheDocument());
    expect(mockApi.listProviderModelSettings).toHaveBeenCalledTimes(2);
  });

  it('renders the empty state when no providers are enabled', async () => {
    mockApi.listProviderModelSettings.mockResolvedValue({ providers: [] });
    renderPage();
    await waitFor(() =>
      expect(screen.getByText(/No providers are enabled/)).toBeInTheDocument(),
    );
  });

  it('does NOT fetch any model list until a row is expanded (fetch-on-open)', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText('Anthropic')).toBeInTheDocument());
    expect(mockApi.listProviderModels).not.toHaveBeenCalled();

    const changeButtons = screen.getAllByRole('button', { name: 'Change model' });
    const first = changeButtons[0];
    expect(first).toBeDefined();
    await userEvent.click(first as HTMLElement);

    await waitFor(() => expect(mockApi.listProviderModels).toHaveBeenCalledTimes(1));
    expect(mockApi.listProviderModels).toHaveBeenCalledWith('anthropic');
  });
});

describe('ModelSettingsPage — override lifecycle on the roster', () => {
  it('save flips the row provenance to "Your override" and shows the pricing warning', async () => {
    mockApi.putProviderModel.mockResolvedValue({
      provider: 'anthropic',
      model: 'claude-opus-4-6',
      source: 'tenant-override',
      pricingKnown: false,
      warning: 'No cost pricing row exists for anthropic/claude-opus-4-6 — …',
    });
    renderPage();
    await waitFor(() => expect(screen.getByText('Anthropic')).toBeInTheDocument());

    const anthButton = screen.getAllByRole('button', { name: 'Change model' })[0];
    await userEvent.click(anthButton as HTMLElement);
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    await userEvent.click(screen.getByRole('button', { name: /Claude Opus 4.6/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Save override' }));

    await waitFor(() => expect(screen.getAllByText('Your override')).toHaveLength(2));
    expect(screen.getAllByText('claude-opus-4-6').length).toBeGreaterThan(0);
    // Non-blocking warning surfaced.
    expect(screen.getByText(/No cost pricing row exists/)).toBeInTheDocument();
  });

  it('reset confirms naming the server-reported fallback (override active since load), DELETEs, and flips the row back to platform default', async () => {
    // OpenAI row carries the override; its model list is irrelevant here.
    mockApi.listProviderModels.mockResolvedValue({
      provider: 'openai',
      models: [],
      fetchedAt: null,
      stale: false,
      errorCode: 'no_provider_key',
    });
    mockApi.deleteProviderModel.mockResolvedValue(undefined);
    // Re-resolution after DELETE — TenantProviderModelResponse
    // (ProviderCredentialEndpoints.cs).
    mockApi.getProviderModel.mockResolvedValue({
      provider: 'openai',
      model: 'gpt-5-default',
      source: 'platform-db',
      override: null,
      fallbackModel: 'gpt-5-default',
    });
    renderPage();
    await waitFor(() => expect(screen.getByText('OpenAI')).toBeInTheDocument());

    // Expand the OpenAI row (second "Change model" button).
    const openaiButton = screen.getAllByRole('button', { name: 'Change model' })[1];
    await userEvent.click(openaiButton as HTMLElement);

    await userEvent.click(
      await screen.findByRole('button', { name: 'Use platform default' }),
    );
    // The confirm NAMES the fallback even though the override was already
    // active when the page loaded — the roster row's fallbackModel, not a
    // client-side capture (the pre-fix behaviour stayed generic here).
    const confirm = screen.getByText(/Remove your override/);
    expect(confirm.textContent).toContain('fall back to the platform default');
    expect(confirm.textContent).toContain('gpt-5-default');

    await userEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    await waitFor(() => expect(mockApi.deleteProviderModel).toHaveBeenCalledWith('openai'));

    // The row re-resolves to the platform default (the model id shows in the
    // roster row and the picker's pinned row — assert at-least-one).
    await waitFor(() =>
      expect(screen.getAllByText('gpt-5-default').length).toBeGreaterThan(0),
    );
    expect(screen.queryByText('Your override')).toBeNull();
    expect(screen.getAllByText('Platform default')).toHaveLength(4);
  });

  it('a PUT 403 downgrades the WHOLE page to read-only with the role message (no retry loop)', async () => {
    mockApi.putProviderModel.mockRejectedValue(new ApiError(403, 'API error: 403', {}));
    renderPage();
    await waitFor(() => expect(screen.getByText('Anthropic')).toBeInTheDocument());

    const anthButton = screen.getAllByRole('button', { name: 'Change model' })[0];
    await userEvent.click(anthButton as HTMLElement);
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    await userEvent.click(screen.getByRole('button', { name: /Claude Opus 4.6/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Save override' }));

    // Page-level downgrade: banner + every edit affordance gone.
    await waitFor(() =>
      expect(
        screen.getAllByText('Your role can view models but not change them.').length,
      ).toBeGreaterThan(0),
    );
    expect(screen.queryByRole('button', { name: 'Save override' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Change model' })).toBeNull();
    expect(screen.getAllByRole('button', { name: 'View models' }).length).toBeGreaterThan(0);
    expect(mockApi.putProviderModel).toHaveBeenCalledTimes(1);
  });
});

describe('ModelSettingsPage — roles (client-side canEdit is cosmetic, D2)', () => {
  it('member renders read-only: note shown, "View models" disclosure, no editor', async () => {
    mockAuth.mockReturnValue({
      user: { id: 'u2', email: 'm@b.dev', displayName: 'M', tenantId: 't1', role: 'member' },
    });
    renderPage();
    await waitFor(() => expect(screen.getByText('Anthropic')).toBeInTheDocument());

    expect(screen.getByText(/read-only access/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Change model' })).toBeNull();

    const viewButtons = screen.getAllByRole('button', { name: 'View models' });
    expect(viewButtons).toHaveLength(4);
    await userEvent.click(viewButtons[0] as HTMLElement);

    // Disclosure: the live list is viewable…
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());
    // …but there are no save/reset affordances.
    expect(screen.queryByRole('button', { name: 'Save override' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Use platform default' })).toBeNull();
  });

  it('the single-user sole user (no membership role) gets the editor optimistically', async () => {
    mockAuth.mockReturnValue({
      user: { id: 'u3', email: 's@b.dev', displayName: 'S', tenantId: null, role: null },
    });
    renderPage();
    await waitFor(() => expect(screen.getByText('Anthropic')).toBeInTheDocument());
    expect(screen.getAllByRole('button', { name: 'Change model' }).length).toBeGreaterThan(0);
  });
});
