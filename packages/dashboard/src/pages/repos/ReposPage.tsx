/**
 * Repos page (Story 21-4) — the tenant's connected repositories / platform
 * installations behind the SPA's `/repos` destination.
 *
 * Read-only: lists what the tenant has connected (from
 * `GET /api/v1/repos`) with connection status. Connecting a new repository
 * reuses the existing onboarding flow (`/onboarding`) rather than a bespoke
 * add-dialog, so there is no new write surface here. The route is already
 * behind `AuthGuard` (applied at the AppLayout level); the API scopes every
 * row to the caller's tenant.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import { useNavigate } from 'react-router-dom';
import { EmptyState } from '../../components/monitoring/EmptyState.js';
import { ErrorBanner } from '../../components/monitoring/ErrorBanner.js';
import { StatusBadge, type StatusKind } from '../../components/monitoring/StatusBadge.js';
import { reposApi, type ConnectedRepo } from '../../services/repos/repos-api-client.js';

function statusKind(status: string): StatusKind {
  switch (status.toLowerCase()) {
    case 'connected':
      return 'healthy';
    case 'suspended':
      return 'degraded';
    case 'disconnected':
      return 'down';
    default:
      return 'unknown';
  }
}

function platformLabel(platform: string): string {
  switch (platform) {
    case 'github':
      return 'GitHub';
    case 'gitlab':
      return 'GitLab';
    case 'gitea':
      return 'Gitea';
    case 'forgejo':
      return 'Forgejo';
    case 'bitbucket':
      return 'Bitbucket';
    case 'azure_devops':
      return 'Azure DevOps';
    default:
      return platform;
  }
}

function RepoCard({ repo }: { repo: ConnectedRepo }): JSX.Element {
  return (
    <div
      data-testid="repo-card"
      className="flex flex-col gap-3 rounded-lg border border-gray-200 bg-white p-5 shadow-sm dark:border-gray-700 dark:bg-gray-800"
    >
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <h2 className="truncate text-base font-semibold text-gray-900 dark:text-gray-100">
            {repo.name}
          </h2>
          <p className="mt-0.5 text-xs text-gray-500 dark:text-gray-400">
            {platformLabel(repo.platform)}
            {repo.isPrimary && (
              <span className="ml-2 rounded bg-blue-100 px-1.5 py-0.5 text-[10px] font-medium text-blue-800 dark:bg-blue-900/40 dark:text-blue-300">
                Primary
              </span>
            )}
          </p>
        </div>
        <StatusBadge status={statusKind(repo.status)}>{repo.status}</StatusBadge>
      </div>

      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
        <dt className="text-gray-500 dark:text-gray-400">Base URL</dt>
        <dd className="truncate text-gray-700 dark:text-gray-300">{repo.baseUrl || '—'}</dd>
        {repo.externalId && (
          <>
            <dt className="text-gray-500 dark:text-gray-400">Account</dt>
            <dd className="truncate text-gray-700 dark:text-gray-300">{repo.externalId}</dd>
          </>
        )}
        <dt className="text-gray-500 dark:text-gray-400">Connected</dt>
        <dd className="text-gray-700 dark:text-gray-300">
          {new Date(repo.connectedAt).toLocaleDateString()}
        </dd>
      </dl>
    </div>
  );
}

export function ReposPage(): JSX.Element {
  const navigate = useNavigate();
  const [repos, setRepos] = useState<ConnectedRepo[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (): Promise<void> => {
    setLoading(true);
    setError(null);
    try {
      const res = await reposApi.list();
      setRepos(res.repos);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load repositories');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <div className="mx-auto max-w-6xl">
      <div className="mb-6 flex items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 dark:text-gray-100">Repositories</h1>
          <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
            Git platforms connected to your account.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => void load()}
            className="rounded-md border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
          >
            Refresh
          </button>
          <button
            type="button"
            onClick={() => navigate('/onboarding')}
            className="rounded-md bg-blue-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
          >
            Add repository
          </button>
        </div>
      </div>

      {error && (
        <div className="mb-4">
          <ErrorBanner message={error} onRetry={() => void load()} />
        </div>
      )}

      {loading && repos.length === 0 && !error && (
        <p className="text-sm text-gray-500 dark:text-gray-400">Loading repositories…</p>
      )}

      {!loading && repos.length === 0 && !error && (
        <EmptyState
          title="No repositories connected yet"
          description="Connect a GitHub, GitLab, or Gitea repository to let Tamma start working on your issues."
          action={{ label: 'Add repository', onClick: () => navigate('/onboarding') }}
        />
      )}

      {repos.length > 0 && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {repos.map((repo) => (
            <RepoCard key={repo.id} repo={repo} />
          ))}
        </div>
      )}
    </div>
  );
}
