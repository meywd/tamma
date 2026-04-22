// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { SecretsApi } from '../SecretsListView.js';
import { SecretsListView } from '../SecretsListView.js';
import type {
  RevealEnvelope,
  SecretListItem,
} from '../../../services/secrets/secrets-api-client.js';

// Mock the reveal consume endpoint — the list view fires a single GET
// against `/api/v1/secrets/reveal/{token}` after a successful create.
const mockConsume = vi.fn();
vi.mock('../../../services/secrets/secrets-api-client.js', async () => {
  const actual = await vi.importActual<
    typeof import('../../../services/secrets/secrets-api-client.js')
  >('../../../services/secrets/secrets-api-client.js');
  return {
    ...actual,
    revealApi: {
      consume: (token: string) => mockConsume(token),
    },
  };
});

const SECRET: SecretListItem = {
  secretId: '11111111-1111-1111-1111-111111111111',
  name: 'db/app-role',
  scope: 'platform',
  tenantId: null,
  purpose: 'DbCredential',
  consumerRefs: [{ type: 'postgres', target: 'tamma_app' }],
  activeVersion: 1,
  lastRotatedAt: null,
  nextRotationDueAt: null,
  createdAt: '2026-04-22T12:00:00Z',
  updatedAt: '2026-04-22T12:00:00Z',
};

function fakeApi(overrides?: Partial<SecretsApi>): SecretsApi {
  return {
    list: vi.fn().mockResolvedValue({ secrets: [SECRET] }),
    create: vi.fn().mockResolvedValue({
      secretId: '11111111-1111-1111-1111-111111111111',
      name: 'new/secret',
      scope: 'platform',
      tenantId: null,
      purpose: 'Generic',
      activeVersion: 1,
      createdAt: '2026-04-22T12:00:00Z',
      updatedAt: '2026-04-22T12:00:00Z',
      revealToken: 'TOKEN-123',
      revealExpiresAt: new Date(Date.now() + 60_000).toISOString(),
      revealUrl: '/api/v1/secrets/reveal/TOKEN-123',
      message: 'x',
    } as RevealEnvelope),
    ...overrides,
  };
}

describe('SecretsListView', () => {
  const user = userEvent.setup();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders the list of secrets with names, purposes, and consumers', async () => {
    const api = fakeApi();
    render(<SecretsListView api={api} scopeLabel="Platform" />);

    expect(await screen.findByText('db/app-role')).toBeInTheDocument();
    expect(screen.getByText('DbCredential')).toBeInTheDocument();
    expect(screen.getByText(/Postgres role/)).toBeInTheDocument();
    expect(screen.getByText('tamma_app')).toBeInTheDocument();
  });

  it('renders the empty-state message when no secrets exist', async () => {
    const api = fakeApi({
      list: vi.fn().mockResolvedValue({ secrets: [] }),
    });
    render(
      <SecretsListView
        api={api}
        scopeLabel="Platform"
        emptyStateMessage="Totally empty."
      />,
    );
    expect(await screen.findByText('Totally empty.')).toBeInTheDocument();
  });

  it('hides the create button when allowCreate=false', async () => {
    const api = fakeApi();
    render(<SecretsListView api={api} scopeLabel="Platform" allowCreate={false} />);

    await screen.findByText('db/app-role');
    expect(screen.queryByRole('button', { name: /create secret/i })).not.toBeInTheDocument();
  });

  it('shows the reveal modal after a successful create + consume', async () => {
    mockConsume.mockResolvedValueOnce({
      secretId: 'new-id',
      name: 'new/secret',
      version: 1,
      plaintext: 'revealed-plaintext',
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
    });
    const api = fakeApi();

    render(<SecretsListView api={api} scopeLabel="Platform" />);

    await screen.findByText('db/app-role');
    await user.click(screen.getByRole('button', { name: /create secret/i }));

    await user.type(screen.getByLabelText(/Name/), 'new/secret');
    await user.type(screen.getByLabelText(/Initial value/), 'long-enough-value');
    // Submit in the form, not the top-level "Create secret" button
    const form = screen.getByRole('form', { name: /create platform secret/i });
    await user.click(
      form.querySelector('button[type="submit"]')!,
    );

    await waitFor(() => {
      expect(mockConsume).toHaveBeenCalledWith('TOKEN-123');
    });

    expect(await screen.findByText('Secret created: new/secret')).toBeInTheDocument();
    expect(screen.getByDisplayValue('revealed-plaintext')).toBeInTheDocument();
    expect(screen.getByText(/will not be shown again/i)).toBeInTheDocument();
  });

  it('shows an error banner when the reveal fails after create', async () => {
    mockConsume.mockRejectedValueOnce(new Error('reveal failed'));
    const api = fakeApi();

    render(<SecretsListView api={api} scopeLabel="Platform" />);

    await screen.findByText('db/app-role');
    await user.click(screen.getByRole('button', { name: /create secret/i }));

    await user.type(screen.getByLabelText(/Name/), 'another/secret');
    await user.type(screen.getByLabelText(/Initial value/), 'long-enough-value');
    const form = screen.getByRole('form', { name: /create platform secret/i });
    await user.click(form.querySelector('button[type="submit"]')!);

    expect(await screen.findByRole('alert')).toHaveTextContent(/reveal failed/i);
  });

  it('renders an error banner when list fails', async () => {
    const api = fakeApi({
      list: vi.fn().mockRejectedValue(new Error('boom')),
    });
    render(<SecretsListView api={api} scopeLabel="Platform" />);

    expect(await screen.findByRole('alert')).toHaveTextContent('boom');
  });
});
