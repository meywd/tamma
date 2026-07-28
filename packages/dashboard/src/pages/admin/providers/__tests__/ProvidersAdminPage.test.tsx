// @vitest-environment jsdom
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { ProvidersAdminPage } from '../ProvidersAdminPage.js';
import { SOURCE_BADGE_LABELS } from '../ProviderRow.js';
import { providersAdminApi } from '../../../../services/admin/providers-api-client.js';
import type {
  ProviderModelsResponse,
  ProviderStatusRow,
  PutProviderSettingsResponse,
} from '../../../../services/admin/providers-api-client.js';

vi.mock('../../../../services/admin/providers-api-client.js', () => ({
  providersAdminApi: {
    listProviders: vi.fn(),
    listProviderModels: vi.fn(),
    putProviderSettings: vi.fn(),
    deleteProviderSettings: vi.fn(),
  },
}));

const api = vi.mocked(providersAdminApi);

// ============================================================================
// Fixtures — shapes copied from the C# response DTOs in
// apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderAdminEndpoints.cs
// (records ProviderStatusRow, ProviderModelEntry, ProviderModelsResponse,
// PutProviderSettingsRequest, PutProviderSettingsResponse — serialized
// camelCase). Epic 45 lesson (45-1): fixtures FROM the DTOs, never invented.
// Provider keys/models are synthetic values in the real shapes.
// ============================================================================

function makeRow(overrides: Partial<ProviderStatusRow> & Pick<ProviderStatusRow, 'key'>): ProviderStatusRow {
  return {
    displayName: overrides.key,
    transport: 'http',
    dialect: 'OpenAiChat',
    effectiveBaseUrl: `https://api.${overrides.key}.example`,
    keyStatus: 'configured',
    modelsSupported: true,
    currentModel: null,
    source: 'descriptor',
    enabled: true,
    aliases: [],
    ...overrides,
  };
}

function makeRoster(): ProviderStatusRow[] {
  return [
    makeRow({
      key: 'alpha',
      displayName: 'Alpha AI',
      dialect: 'AnthropicMessages',
      keyStatus: 'configured',
      currentModel: 'alpha-large-2',
      source: 'platform-db',
      aliases: ['alpha-ai'],
    }),
    makeRow({
      key: 'beta',
      displayName: 'Beta Cloud',
      keyStatus: 'missing',
      currentModel: 'beta-chat-1',
      source: 'config',
    }),
    makeRow({
      key: 'gamma',
      displayName: 'Gamma Local',
      keyStatus: 'not_required',
      currentModel: 'gamma-free',
      source: 'descriptor',
    }),
    // A provider without a listable models endpoint (descriptor
    // ModelsEndpointPath null — epic D4 free-text path).
    makeRow({
      key: 'delta',
      displayName: 'Delta AI',
      modelsSupported: false,
      currentModel: 'delta-9',
      source: 'config',
    }),
    // A non-HTTP (CLI transport) provider row.
    makeRow({
      key: 'epsilon',
      displayName: 'Epsilon CLI',
      transport: 'cli',
      dialect: null,
      effectiveBaseUrl: null,
      modelsSupported: false,
      currentModel: 'epsilon-code',
      source: 'descriptor',
    }),
    // A disabled provider.
    makeRow({
      key: 'zeta',
      displayName: 'Zeta AI',
      currentModel: 'zeta-1',
      source: 'platform-db',
      enabled: false,
    }),
  ];
}

/** Fresh live list for `alpha` — current entry deliberately NOT first, so the
 * pin-to-top behaviour is the page's doing, not the fixture's. */
function makeFreshModels(): ProviderModelsResponse {
  return {
    provider: 'alpha',
    models: [
      { id: 'alpha-small-1', displayName: 'Alpha Small 1', deprecated: false, current: false },
      { id: 'alpha-large-2', displayName: 'Alpha Large 2', deprecated: false, current: true },
      { id: 'alpha-old-1', displayName: 'Alpha Old 1', deprecated: true, current: false },
      { id: 'alpha-mini', displayName: 'Alpha Mini', deprecated: false, current: false },
    ],
    fetchedAt: '2026-07-27T00:00:00.000Z',
    stale: false,
    errorCode: null,
  };
}

function makePutResponse(
  overrides?: Partial<PutProviderSettingsResponse>,
): PutProviderSettingsResponse {
  return {
    provider: 'alpha',
    defaultModel: 'alpha-small-1',
    enabled: true,
    pricingKnown: true,
    warning: null,
    ...overrides,
  };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <ProvidersAdminPage />
    </MemoryRouter>,
  );
}

async function openPicker(key: string): Promise<void> {
  await userEvent.click(await screen.findByTestId(`provider-edit-${key}`));
}

describe('ProvidersAdminPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.listProviders.mockResolvedValue({ providers: makeRoster() });
    api.listProviderModels.mockResolvedValue(makeFreshModels());
    api.putProviderSettings.mockResolvedValue(makePutResponse());
    api.deleteProviderSettings.mockResolvedValue(undefined);
  });

  it('renders one roster row per provider with name, muted key/aliases, and dialect or transport', async () => {
    renderPage();

    for (const row of makeRoster()) {
      const el = await screen.findByTestId(`provider-row-${row.key}`);
      expect(within(el).getByText(row.displayName)).toBeTruthy();
    }

    // Muted sub-line: key + aliases.
    const alpha = screen.getByTestId('provider-row-alpha');
    expect(alpha.textContent).toContain('alpha · alpha-ai');
    // Dialect for HTTP rows; transport for non-HTTP rows (no dialect).
    expect(alpha.textContent).toContain('AnthropicMessages');
    const epsilon = screen.getByTestId('provider-row-epsilon');
    expect(within(epsilon).getByText('cli')).toBeTruthy();
    // Source badges come from the exported D4 map, not restated strings.
    expect(screen.getByTestId('provider-source-alpha').textContent).toBe(
      SOURCE_BADGE_LABELS['platform-db'],
    );
    expect(screen.getByTestId('provider-source-beta').textContent).toBe(
      SOURCE_BADGE_LABELS.config,
    );
    expect(screen.getByTestId('provider-source-gamma').textContent).toBe(
      SOURCE_BADGE_LABELS.descriptor,
    );
  });

  it('renders the three key statuses distinctly — not_required is not shown as configured', async () => {
    renderPage();

    expect((await screen.findByTestId('provider-keystatus-alpha')).textContent).toBe(
      'Key configured',
    );
    expect(screen.getByTestId('provider-keystatus-beta').textContent).toBe('Key missing');
    expect(screen.getByTestId('provider-keystatus-gamma').textContent).toBe('No key required');
    expect(screen.getByTestId('provider-keystatus-gamma').textContent).not.toContain(
      'configured',
    );
  });

  it('fetches the model list only when a picker is opened, not on page load', async () => {
    renderPage();
    await screen.findByTestId('provider-row-alpha');

    expect(api.listProviderModels).not.toHaveBeenCalled();

    await openPicker('alpha');
    await screen.findByTestId('model-listbox');

    expect(api.listProviderModels).toHaveBeenCalledTimes(1);
    expect(api.listProviderModels).toHaveBeenCalledWith('alpha');
  });

  it('filters the model list over id and displayName', async () => {
    renderPage();
    await openPicker('alpha');
    const listbox = await screen.findByTestId('model-listbox');

    await userEvent.type(screen.getByTestId('model-search'), 'small');
    let options = within(listbox).getAllByRole('option');
    // Current stays pinned even though it does not match the filter.
    expect(options.map((o) => (o as HTMLOptionElement).value)).toEqual([
      'alpha-large-2',
      'alpha-small-1',
    ]);

    // displayName is searched too.
    await userEvent.clear(screen.getByTestId('model-search'));
    await userEvent.type(screen.getByTestId('model-search'), 'Alpha Mini');
    options = within(listbox).getAllByRole('option');
    expect(options.map((o) => (o as HTMLOptionElement).value)).toEqual([
      'alpha-large-2',
      'alpha-mini',
    ]);
  });

  it('pre-selects the current model and pins it at the top of the list', async () => {
    renderPage();
    await openPicker('alpha');
    const listbox = (await screen.findByTestId('model-listbox')) as HTMLSelectElement;

    expect(listbox.value).toBe('alpha-large-2');
    const options = within(listbox).getAllByRole('option');
    // Fixture order puts the current entry second; the page pins it first.
    expect((options[0] as HTMLOptionElement).value).toBe('alpha-large-2');
    expect(options[0]!.textContent).toContain('(current)');
  });

  it('marks a delisted current model as no longer listed by the provider', async () => {
    // The server synthesizes the current entry (displayName null,
    // `delisted: true`) at index 0 when the provider delisted it
    // (BuildModelsResponse in ProviderAdminEndpoints.cs). The marker is a
    // plain read of that flag — no positional heuristic.
    api.listProviderModels.mockResolvedValue({
      provider: 'alpha',
      models: [
        {
          id: 'alpha-large-2',
          displayName: null,
          deprecated: false,
          current: true,
          delisted: true,
        },
        { id: 'alpha-small-1', displayName: 'Alpha Small 1', deprecated: false, current: false },
      ],
      fetchedAt: '2026-07-27T00:00:00.000Z',
      stale: false,
      errorCode: null,
    });

    renderPage();
    await openPicker('alpha');
    const listbox = await screen.findByTestId('model-listbox');

    const options = within(listbox).getAllByRole('option');
    expect(options[0]!.textContent).toContain('no longer listed by the provider');
    expect(options[1]!.textContent).not.toContain('no longer listed');
  });

  it('does NOT mark a genuinely-listed first-position current model without display name', async () => {
    // The old heuristic's false positive (display-name-less providers whose
    // current model really is first in the provider's own order): without the
    // server's `delisted` flag, no marker renders.
    api.listProviderModels.mockResolvedValue({
      provider: 'alpha',
      models: [
        { id: 'alpha-large-2', displayName: null, deprecated: false, current: true },
        { id: 'alpha-small-1', displayName: null, deprecated: false, current: false },
      ],
      fetchedAt: '2026-07-27T00:00:00.000Z',
      stale: false,
      errorCode: null,
    });

    renderPage();
    await openPicker('alpha');
    const listbox = await screen.findByTestId('model-listbox');

    const options = within(listbox).getAllByRole('option');
    expect(options[0]!.textContent).toContain('(current)');
    expect(options[0]!.textContent).not.toContain('no longer listed');
  });

  it('marks deprecated models and orders them after non-deprecated ones', async () => {
    renderPage();
    await openPicker('alpha');
    const listbox = await screen.findByTestId('model-listbox');

    const options = within(listbox).getAllByRole('option');
    expect(options.map((o) => (o as HTMLOptionElement).value)).toEqual([
      'alpha-large-2', // current, pinned
      'alpha-small-1',
      'alpha-mini',
      'alpha-old-1', // deprecated → last
    ]);
    expect(options[3]!.textContent).toContain('(deprecated)');
    expect(options[1]!.textContent).not.toContain('(deprecated)');
  });

  it('renders the stale-cache banner with the error code', async () => {
    api.listProviderModels.mockResolvedValue({
      ...makeFreshModels(),
      stale: true,
      errorCode: 'provider_unreachable',
    });

    renderPage();
    await openPicker('alpha');

    const banner = await screen.findByTestId('models-stale-banner');
    expect(banner.textContent).toContain('shown from cache');
    expect(banner.textContent).toContain('(provider_unreachable)');
    // The cached list still renders — the page is never dead-ended.
    expect(screen.getByTestId('model-listbox')).toBeTruthy();
  });

  it('degrades an empty model list to a banner plus a usable free-text input', async () => {
    // Fail-soft envelope: only the synthesized current entry (flagged
    // `delisted` — it is absent from a list the server could not fetch), with
    // the error code.
    api.listProviderModels.mockResolvedValue({
      provider: 'beta',
      models: [
        { id: 'beta-chat-1', displayName: null, deprecated: false, current: true, delisted: true },
      ],
      fetchedAt: null,
      stale: false,
      errorCode: 'credential_unavailable',
    });

    renderPage();
    await openPicker('beta');

    const banner = await screen.findByTestId('models-empty-banner');
    expect(banner.textContent).toContain('(credential_unavailable)');
    const input = screen.getByTestId('model-free-text') as HTMLInputElement;
    expect(input.value).toBe('beta-chat-1');
    expect((screen.getByTestId('model-save') as HTMLButtonElement).disabled).toBe(false);
  });

  it('gives providers without a models endpoint the free-text path and fetches nothing', async () => {
    renderPage();
    await openPicker('delta');

    const input = (await screen.findByTestId('model-free-text')) as HTMLInputElement;
    expect(input.value).toBe('delta-9');
    expect(screen.getByText(/does not expose a model list/)).toBeTruthy();
    expect(screen.queryByTestId('model-listbox')).toBeNull();
    expect(api.listProviderModels).not.toHaveBeenCalled();
  });

  it('saves via PUT with the exact body, re-fetches the roster, and renders the pricing warning', async () => {
    api.putProviderSettings.mockResolvedValue(
      makePutResponse({
        pricingKnown: false,
        warning: 'No cost pricing row exists for alpha/alpha-small-1 — calls will record cost 0.',
      }),
    );

    renderPage();
    await openPicker('alpha');
    await screen.findByTestId('model-listbox');
    expect(api.listProviders).toHaveBeenCalledTimes(1);

    await userEvent.selectOptions(screen.getByTestId('model-listbox'), 'alpha-small-1');
    await userEvent.click(screen.getByTestId('model-save'));

    await waitFor(() => expect(api.putProviderSettings).toHaveBeenCalledTimes(1));
    expect(api.putProviderSettings).toHaveBeenCalledWith('alpha', {
      defaultModel: 'alpha-small-1',
    });
    // AC4 — the roster is re-fetched after a save.
    await waitFor(() => expect(api.listProviders).toHaveBeenCalledTimes(2));
    // D3b — the pricingKnown:false warning is surfaced, non-blockingly.
    const warning = await screen.findByTestId('pricing-warning');
    expect(warning.textContent).toContain('No cost pricing row exists for alpha/alpha-small-1');
  });

  it('resets behind a confirm step naming the fallback tiers, then DELETEs and re-fetches', async () => {
    renderPage();
    await openPicker('alpha');
    await screen.findByTestId('model-listbox');

    await userEvent.click(screen.getByTestId('model-reset'));
    const confirm = await screen.findByTestId('reset-confirm');
    // The confirm copy states the fallback using the D4 badge map.
    expect(confirm.textContent).toContain(SOURCE_BADGE_LABELS.config!);
    expect(confirm.textContent).toContain(SOURCE_BADGE_LABELS.descriptor!);
    expect(api.deleteProviderSettings).not.toHaveBeenCalled();

    await userEvent.click(screen.getByTestId('reset-confirm-button'));
    await waitFor(() => expect(api.deleteProviderSettings).toHaveBeenCalledWith('alpha'));
    await waitFor(() => expect(api.listProviders).toHaveBeenCalledTimes(2));
  });

  it('renders a disabled provider inert except for re-enable', async () => {
    api.putProviderSettings.mockResolvedValue(
      makePutResponse({ provider: 'zeta', defaultModel: null, enabled: true }),
    );

    renderPage();
    const row = await screen.findByTestId('provider-row-zeta');
    expect(row.className).toContain('opacity-60');

    // Edit is inert…
    expect((screen.getByTestId('provider-edit-zeta') as HTMLButtonElement).disabled).toBe(true);
    // …the re-enable toggle is not.
    const toggle = screen.getByTestId('provider-toggle-zeta') as HTMLButtonElement;
    expect(toggle.disabled).toBe(false);
    await userEvent.click(toggle);
    await waitFor(() =>
      expect(api.putProviderSettings).toHaveBeenCalledWith('zeta', { enabled: true }),
    );
    await waitFor(() => expect(toggle.getAttribute('aria-checked')).toBe('true'));
  });

  it('toggles enabled via PUT and reflects the response', async () => {
    api.putProviderSettings.mockResolvedValue(
      makePutResponse({ provider: 'alpha', defaultModel: null, enabled: false }),
    );

    renderPage();
    const toggle = (await screen.findByTestId('provider-toggle-alpha')) as HTMLButtonElement;
    expect(toggle.getAttribute('aria-checked')).toBe('true');

    await userEvent.click(toggle);

    await waitFor(() =>
      expect(api.putProviderSettings).toHaveBeenCalledWith('alpha', { enabled: false }),
    );
    await waitFor(() => expect(toggle.getAttribute('aria-checked')).toBe('false'));
  });

  it('surfaces a failed toggle inline and reverts the optimistic flip', async () => {
    api.putProviderSettings.mockRejectedValue(new Error('nope'));

    renderPage();
    const toggle = (await screen.findByTestId('provider-toggle-alpha')) as HTMLButtonElement;
    await userEvent.click(toggle);

    const error = await screen.findByTestId('provider-toggle-error-alpha');
    expect(error.textContent).toContain('nope');
    expect(toggle.getAttribute('aria-checked')).toBe('true');
  });

  it('keeps the page frame on a roster error and retries', async () => {
    api.listProviders.mockRejectedValueOnce(new Error('boom'));

    renderPage();
    expect(await screen.findByText('Failed to load providers')).toBeTruthy();
    expect(screen.getByText('boom')).toBeTruthy();
    // The page frame (heading) is kept.
    expect(screen.getByText('Provider Settings')).toBeTruthy();

    await userEvent.click(screen.getByText('Retry'));
    expect(await screen.findByTestId('provider-row-alpha')).toBeTruthy();
    expect(api.listProviders).toHaveBeenCalledTimes(2);
  });

  it('never renders key material or a key input', async () => {
    renderPage();
    await screen.findByTestId('provider-row-alpha');
    await openPicker('alpha');
    await screen.findByTestId('model-listbox');

    // No password/key inputs anywhere on the page.
    expect(document.querySelector('input[type="password"]')).toBeNull();
    // Remediation links to the existing secrets admin page.
    const links = screen.getAllByRole('link', { name: 'Manage in Secrets' });
    expect(links.length).toBeGreaterThan(0);
    for (const link of links) {
      expect(link.getAttribute('href')).toBe('/admin/secrets');
    }
  });
});
