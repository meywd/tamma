/**
 * Story 46-3 AC2/AC3 — TenantModelPicker behaviours: search, current-pinned +
 * delisted marker (the server's `delisted` flag — no heuristic), deprecated
 * ordering, stale/unavailable banners, free-text path, save + pricing
 * warning, reset confirm naming the platform default from the roster row's
 * server-computed fallback, 403 → forbidden callback (no retry loop), member
 * read-only disclosure.
 *
 * Fixtures mirror the C# DTOs — ProviderModelEntry / ProviderModelsResponse
 * (apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderAdminEndpoints.cs)
 * and PutTenantProviderModelResponse
 * (apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderCredentialEndpoints.cs).
 * Do not invent fields (the 45-1 lesson).
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TenantModelPicker, type TenantModelPickerProps } from './TenantModelPicker';
import { ApiError } from '../../api/client';
import type { ProviderModelsResponse } from '../../api/provider-models';

const { mockApi } = vi.hoisted(() => ({
  mockApi: {
    listProviderModelSettings: vi.fn(),
    listProviderModels: vi.fn(),
    getProviderModel: vi.fn(),
    putProviderModel: vi.fn(),
    deleteProviderModel: vi.fn(),
  },
}));

vi.mock('../../api/provider-models', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/provider-models')>();
  return { ...actual, providerModelsApi: mockApi };
});

// ProviderModelsResponse fixture — fresh list; the current model is flagged
// IN PLACE (not synthesized); one deprecated entry sits mid-list so ordering
// is observable.
const FRESH: ProviderModelsResponse = {
  provider: 'anthropic',
  models: [
    { id: 'claude-sonnet-4-5', displayName: 'Claude Sonnet 4.5', deprecated: false, current: true },
    { id: 'claude-opus-4-6', displayName: 'Claude Opus 4.6', deprecated: false, current: false },
    { id: 'claude-3-opus', displayName: 'Claude 3 Opus', deprecated: true, current: false },
    { id: 'claude-haiku-4-5', displayName: 'Claude Haiku 4.5', deprecated: false, current: false },
  ],
  fetchedAt: '2026-07-27T12:00:00.000Z',
  stale: false,
  errorCode: null,
};

// Delisted current: BuildModelsResponse synthesized the pin (index 0,
// displayName null, `delisted: true`) because the live list no longer
// carries the model. The flag is the contract — position/display name are
// incidental.
const DELISTED: ProviderModelsResponse = {
  provider: 'anthropic',
  models: [
    {
      id: 'claude-retired-1',
      displayName: null,
      deprecated: false,
      current: true,
      delisted: true,
    },
    { id: 'claude-opus-4-6', displayName: 'Claude Opus 4.6', deprecated: false, current: false },
  ],
  fetchedAt: '2026-07-27T12:00:00.000Z',
  stale: false,
  errorCode: null,
};

// Stale cache served after a failed live fetch (epic D6).
const STALE: ProviderModelsResponse = {
  ...FRESH,
  stale: true,
  errorCode: 'provider_unreachable',
};

// Nothing listable at all: empty models + errorCode, no current model set.
const EMPTY: ProviderModelsResponse = {
  provider: 'deepseek',
  models: [],
  fetchedAt: null,
  stale: false,
  errorCode: 'no_provider_key',
};

function makeProps(over: Partial<TenantModelPickerProps> = {}): TenantModelPickerProps {
  return {
    provider: 'anthropic',
    displayName: 'Anthropic',
    modelsSupported: true,
    effectiveModel: 'claude-sonnet-4-5',
    hasOverride: false,
    platformDefaultModel: null,
    canEdit: true,
    onSaved: vi.fn(),
    onResetDone: vi.fn(),
    onForbidden: vi.fn(),
    ...over,
  };
}

function renderPicker(over: Partial<TenantModelPickerProps> = {}) {
  const props = makeProps(over);
  return { ...render(<TenantModelPicker {...props} />), props };
}

beforeEach(() => {
  vi.clearAllMocks();
  mockApi.listProviderModels.mockResolvedValue(FRESH);
});

describe('delisted marker — a plain read of the envelope flag (no heuristic)', () => {
  it('OpenAI-style list (no display names anywhere) WITH the flag shows the marker — the heuristic-era false negative', async () => {
    mockApi.listProviderModels.mockResolvedValue({
      provider: 'openai',
      models: [
        { id: 'gpt-old', displayName: null, deprecated: false, current: true, delisted: true },
        { id: 'gpt-5', displayName: null, deprecated: false, current: false },
      ],
      fetchedAt: '2026-07-27T12:00:00.000Z',
      stale: false,
      errorCode: null,
    });
    renderPicker({ provider: 'openai', displayName: 'OpenAI', effectiveModel: 'gpt-old' });
    await waitFor(() =>
      expect(screen.getByText('no longer listed by the provider')).toBeInTheDocument(),
    );
  });

  it('a genuinely-listed nameless first-position current WITHOUT the flag shows no marker — the heuristic-era false positive', async () => {
    mockApi.listProviderModels.mockResolvedValue({
      provider: 'openai',
      models: [
        { id: 'gpt-5', displayName: null, deprecated: false, current: true },
        { id: 'gpt-5-mini', displayName: null, deprecated: false, current: false },
      ],
      fetchedAt: '2026-07-27T12:00:00.000Z',
      stale: false,
      errorCode: null,
    });
    renderPicker({ provider: 'openai', displayName: 'OpenAI', effectiveModel: 'gpt-5' });
    await waitFor(() => expect(screen.getByText('current')).toBeInTheDocument());
    expect(screen.queryByText('no longer listed by the provider')).toBeNull();
  });

  it('a sole synthesized pin after a failed fetch is flagged by the server, not inferred from stale/errorCode', async () => {
    mockApi.listProviderModels.mockResolvedValue({
      ...EMPTY,
      models: [
        { id: 'm-old', displayName: null, deprecated: false, current: true, delisted: true },
      ],
    });
    renderPicker({ provider: 'deepseek', displayName: 'DeepSeek', effectiveModel: 'm-old' });
    await waitFor(() =>
      expect(screen.getByText('no longer listed by the provider')).toBeInTheDocument(),
    );
  });
});

describe('TenantModelPicker — list rendering', () => {
  it('fetches on mount (the page mounts it on expand) and renders the list', async () => {
    renderPicker();
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());
    expect(mockApi.listProviderModels).toHaveBeenCalledTimes(1);
    expect(mockApi.listProviderModels).toHaveBeenCalledWith('anthropic');
  });

  it('pins the current model first with a badge, not duplicated in the list', async () => {
    renderPicker();
    await waitFor(() => expect(screen.getByText('current')).toBeInTheDocument());
    expect(screen.getByText('claude-sonnet-4-5')).toBeInTheDocument();
    // The current entry is NOT offered again as a list option.
    expect(
      screen.queryByRole('button', { name: /Claude Sonnet 4.5/ }),
    ).toBeNull();
  });

  it('search filters over id and displayName', async () => {
    renderPicker();
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    await userEvent.type(screen.getByLabelText('Search Anthropic models'), 'haiku');
    expect(screen.getByText(/Claude Haiku 4.5/)).toBeInTheDocument();
    expect(screen.queryByText(/Claude Opus 4.6/)).toBeNull();

    await userEvent.clear(screen.getByLabelText('Search Anthropic models'));
    await userEvent.type(screen.getByLabelText('Search Anthropic models'), 'claude-3-opus');
    expect(screen.getByText(/Claude 3 Opus/)).toBeInTheDocument();
    expect(screen.queryByText(/Claude Haiku 4.5/)).toBeNull();
  });

  it('marks deprecated entries and sorts them last', async () => {
    renderPicker();
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    const options = screen.getAllByRole('listitem').map((li) => li.textContent ?? '');
    expect(options).toHaveLength(3);
    // Server order was opus, DEPRECATED legacy, haiku — legacy sorts last.
    expect(options[0]).toContain('Claude Opus 4.6');
    expect(options[1]).toContain('Claude Haiku 4.5');
    expect(options[2]).toContain('Claude 3 Opus');
    expect(options[2]).toContain('deprecated');
  });

  it('shows the "no longer listed" marker for a synthesized current pin', async () => {
    mockApi.listProviderModels.mockResolvedValue(DELISTED);
    renderPicker({ effectiveModel: 'claude-retired-1' });
    await waitFor(() =>
      expect(screen.getByText('no longer listed by the provider')).toBeInTheDocument(),
    );
    expect(screen.getByText('claude-retired-1')).toBeInTheDocument();
  });

  // U3 — the pin derives from the LIVE effectiveModel prop (the page
  // re-resolves the row after save/reset and passes it back down); the
  // mount-time envelope snapshot must never win over it.
  it('the current pin follows the effectiveModel prop after a save/reset — including clearing a stale delisted badge', async () => {
    mockApi.listProviderModels.mockResolvedValue(DELISTED);
    const props = makeProps({ effectiveModel: 'claude-retired-1' });
    const view = render(<TenantModelPicker {...props} />);
    await waitFor(() =>
      expect(screen.getByText('no longer listed by the provider')).toBeInTheDocument(),
    );

    // The page saved/reset and re-resolved the row; the picker stays mounted
    // (no list re-fetch) and only the prop changes.
    view.rerender(<TenantModelPicker {...props} effectiveModel="claude-opus-4-6" />);

    const pin = screen.getByText('current').parentElement as HTMLElement;
    expect(pin.textContent).toContain('claude-opus-4-6');
    // The old model is gone from the pin AND the stale delisted badge with it
    // (the badge belonged to the old model, not the new one).
    expect(screen.queryByText('claude-retired-1')).toBeNull();
    expect(screen.queryByText('no longer listed by the provider')).toBeNull();
    expect(mockApi.listProviderModels).toHaveBeenCalledTimes(1);
  });

  it('renders the stale banner with the error code (cached list still usable)', async () => {
    mockApi.listProviderModels.mockResolvedValue(STALE);
    renderPicker();
    await waitFor(() =>
      expect(screen.getByText(/Showing a cached model list/)).toBeInTheDocument(),
    );
    expect(screen.getByText('provider_unreachable')).toBeInTheDocument();
    // The cached entries still render.
    expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument();
  });

  it('renders the unavailable banner + free-text path for an empty list', async () => {
    mockApi.listProviderModels.mockResolvedValue(EMPTY);
    mockApi.putProviderModel.mockResolvedValue({
      provider: 'deepseek',
      model: 'deepseek-chat',
      source: 'tenant-override',
      pricingKnown: true,
      warning: null,
    });
    const { props } = renderPicker({
      provider: 'deepseek',
      displayName: 'DeepSeek',
      effectiveModel: null,
    });
    await waitFor(() =>
      expect(screen.getByText(/model list is unavailable/)).toBeInTheDocument(),
    );
    expect(screen.getByText('no_provider_key')).toBeInTheDocument();

    // Free-text save still works — the page is never unusable (epic D6).
    await userEvent.type(screen.getByLabelText('DeepSeek model id'), 'deepseek-chat');
    await userEvent.click(screen.getByRole('button', { name: 'Save override' }));
    await waitFor(() =>
      expect(mockApi.putProviderModel).toHaveBeenCalledWith('deepseek', 'deepseek-chat'),
    );
    expect(props.onSaved).toHaveBeenCalled();
  });

  it('modelsSupported:false renders the free-text path WITHOUT fetching a list', async () => {
    renderPicker({
      provider: 'z-ai',
      displayName: 'Z.ai',
      modelsSupported: false,
      effectiveModel: 'glm-4.7',
    });
    expect(screen.getByText(/does not publish a model list/)).toBeInTheDocument();
    expect(screen.getByLabelText('Z.ai model id')).toBeInTheDocument();
    expect(mockApi.listProviderModels).not.toHaveBeenCalled();
  });
});

describe('TenantModelPicker — override lifecycle', () => {
  it('clicking an entry fills the model id; save PUTs it and surfaces the pricing warning non-blockingly', async () => {
    mockApi.putProviderModel.mockResolvedValue({
      // PutTenantProviderModelResponse — ProviderCredentialEndpoints.cs:689-690
      provider: 'anthropic',
      model: 'claude-opus-4-6',
      source: 'tenant-override',
      pricingKnown: false,
      warning:
        'No cost pricing row exists for anthropic/claude-opus-4-6 — calls will record cost 0 …',
    });
    const { props } = renderPicker();
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    await userEvent.click(screen.getByRole('button', { name: /Claude Opus 4.6/ }));
    expect(screen.getByLabelText('Anthropic model id')).toHaveValue('claude-opus-4-6');

    await userEvent.click(screen.getByRole('button', { name: 'Save override' }));
    await waitFor(() =>
      expect(mockApi.putProviderModel).toHaveBeenCalledWith('anthropic', 'claude-opus-4-6'),
    );
    expect(props.onSaved).toHaveBeenCalledWith(
      expect.objectContaining({ model: 'claude-opus-4-6', source: 'tenant-override' }),
    );
    // Non-blocking: the save succeeded AND the warning shows.
    expect(screen.getByText(/Saved/)).toBeInTheDocument();
    expect(screen.getByText(/No cost pricing row exists/)).toBeInTheDocument();
  });

  it('reset confirm names the platform default (the row\'s server-computed fallbackModel), DELETEs, and reports done', async () => {
    mockApi.deleteProviderModel.mockResolvedValue(undefined);
    const { props } = renderPicker({
      hasOverride: true,
      effectiveModel: 'claude-opus-4-6',
      platformDefaultModel: 'claude-sonnet-4-5',
    });
    // The effective model (claude-opus-4-6) is the pinned current — it is
    // NOT offered again as a list option (U3: pin follows the live prop), so
    // wait on the pin and a listed sibling instead.
    await waitFor(() => expect(screen.getByText('claude-opus-4-6')).toBeInTheDocument());
    expect(screen.getByText(/Claude Haiku 4.5/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Claude Opus 4.6/ })).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Use platform default' }));
    const confirm = screen.getByText(/Remove your override/);
    expect(confirm.textContent).toContain('Anthropic will fall back to the platform default');
    expect(confirm.textContent).toContain('claude-sonnet-4-5');

    await userEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    await waitFor(() => expect(mockApi.deleteProviderModel).toHaveBeenCalledWith('anthropic'));
    expect(props.onResetDone).toHaveBeenCalledTimes(1);
  });

  it('reset confirm stays generic when the server reports no fallback (fallbackModel null)', async () => {
    // fallbackModel is null only when nothing below the override names a
    // model (empty-string config / descriptor floor) — not "unknown".
    renderPicker({ hasOverride: true, platformDefaultModel: null });
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    await userEvent.click(screen.getByRole('button', { name: 'Use platform default' }));
    const confirm = screen.getByText(/Remove your override/);
    expect(confirm.textContent).toContain('fall back to the platform default.');
  });

  it('a 403 on save renders the role message, calls onForbidden, and does NOT retry', async () => {
    mockApi.putProviderModel.mockRejectedValue(new ApiError(403, 'API error: 403', {}));
    const { props } = renderPicker();
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    await userEvent.type(screen.getByLabelText('Anthropic model id'), 'claude-opus-4-6');
    await userEvent.click(screen.getByRole('button', { name: 'Save override' }));

    await waitFor(() =>
      expect(
        screen.getByText('Your role can view models but not change them.'),
      ).toBeInTheDocument(),
    );
    expect(props.onForbidden).toHaveBeenCalledTimes(1);
    expect(mockApi.putProviderModel).toHaveBeenCalledTimes(1);
  });

  it('a 409 (platform-disabled provider) renders a clear error without downgrading the page', async () => {
    mockApi.putProviderModel.mockRejectedValue(
      new ApiError(409, 'API error: 409', { error: 'provider_disabled' }),
    );
    const { props } = renderPicker();
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    await userEvent.type(screen.getByLabelText('Anthropic model id'), 'x-model');
    await userEvent.click(screen.getByRole('button', { name: 'Save override' }));

    await waitFor(() =>
      expect(screen.getByText('This provider is disabled by the platform.')).toBeInTheDocument(),
    );
    expect(props.onForbidden).not.toHaveBeenCalled();
  });

  // U4 — a 400 must not surface as the opaque "API error: 400": the server's
  // `detail` (ProviderAdminEndpoints.ValidateModel) is shown instead.
  it('a 400 invalid_model surfaces the server detail, not the opaque status message', async () => {
    mockApi.putProviderModel.mockRejectedValue(
      new ApiError(400, 'API error: 400', {
        error: 'invalid_model',
        detail: 'model must not contain control characters.',
      }),
    );
    renderPicker();
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    await userEvent.type(screen.getByLabelText('Anthropic model id'), 'bad-model');
    await userEvent.click(screen.getByRole('button', { name: 'Save override' }));

    await waitFor(() =>
      expect(
        screen.getByText('model must not contain control characters.'),
      ).toBeInTheDocument(),
    );
    expect(screen.queryByText('API error: 400')).toBeNull();
  });

  // U4 — the cheap client-side mirror of the server rule (non-empty, ≤256
  // chars, no control chars): the common invalid input never round-trips.
  it('an over-long model id is rejected client-side without a PUT', async () => {
    renderPicker();
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    const input = screen.getByLabelText('Anthropic model id');
    await userEvent.click(input);
    await userEvent.paste('m'.repeat(257));
    await userEvent.click(screen.getByRole('button', { name: 'Save override' }));

    await waitFor(() =>
      expect(screen.getByText(/at most 256 characters/)).toBeInTheDocument(),
    );
    expect(mockApi.putProviderModel).not.toHaveBeenCalled();
  });

  it('member read-only disclosure: list viewable, no save/reset affordances, no input', async () => {
    renderPicker({ canEdit: false, hasOverride: true });
    await waitFor(() => expect(screen.getByText(/Claude Opus 4.6/)).toBeInTheDocument());

    expect(screen.queryByRole('button', { name: 'Save override' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Use platform default' })).toBeNull();
    expect(screen.queryByLabelText('Anthropic model id')).toBeNull();
    expect(screen.getByText(/Read-only/)).toBeInTheDocument();
    // Entries render as plain rows, not buttons.
    expect(screen.queryByRole('button', { name: /Claude Opus 4.6/ })).toBeNull();
  });
});
