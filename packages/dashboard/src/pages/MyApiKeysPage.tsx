/**
 * My API Keys Page — Self-service API key management for the current user.
 *
 * Uses the Story 16.2 endpoints at /api/admin/users/:userId/keys, accessed
 * through the apiKeysApi client in services/admin/admin-api-client.ts.
 * That client owns the request/response shape contracts (path, label vs
 * name, { apiKeys: [] } wrapper, raw-key-once response) — keeping the
 * page logic free of low-level fetch boilerplate also means we can't
 * accidentally drift from the contract again.
 */

import { useState, useEffect, useCallback, type JSX } from 'react';
import { useAuth } from '../hooks/useAuth.js';
import { LoadingSpinner } from '../components/common/LoadingSpinner.js';
import { apiKeysApi, type ApiKeyEntry } from '../services/admin/admin-api-client.js';

export function MyApiKeysPage(): JSX.Element {
  const { user } = useAuth();
  const [keys, setKeys] = useState<ApiKeyEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [newKeyLabel, setNewKeyLabel] = useState('');
  const [createdKey, setCreatedKey] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const fetchKeys = useCallback(async () => {
    if (!user) return;
    try {
      const list = await apiKeysApi.list(user.id);
      setKeys(list);
      setError(null);
    } catch {
      setError('Failed to load API keys');
    } finally {
      setLoading(false);
    }
  }, [user]);

  useEffect(() => {
    void fetchKeys();
  }, [fetchKeys]);

  async function handleCreate(): Promise<void> {
    if (!user || !newKeyLabel.trim()) return;
    setCreating(true);
    try {
      const result = await apiKeysApi.create(user.id, newKeyLabel.trim());
      setCreatedKey(result.key);
      setNewKeyLabel('');
      void fetchKeys();
    } catch {
      setError('Failed to create API key');
    } finally {
      setCreating(false);
    }
  }

  async function handleRevoke(keyId: string): Promise<void> {
    if (!user) return;
    try {
      await apiKeysApi.revoke(user.id, keyId);
      void fetchKeys();
    } catch {
      setError('Failed to revoke API key');
    }
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-[40vh]">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  return (
    <div className="p-6 max-w-3xl">
      <h1 className="text-2xl font-bold text-gray-900 mb-2 dark:text-gray-100">API Keys</h1>
      <p className="text-sm text-gray-600 mb-6 dark:text-gray-400">
        Personal credentials for command-line tools and automated scripts that
        need to call the Tamma API on your behalf.
      </p>

      {/* Documentation panel — explains who needs API keys, what they grant,
          and how to use them. Lots of users land here unsure whether they
          should generate one; this surfaces the answer without a docs trip. */}
      <details className="mb-6 bg-blue-50 border border-blue-200 rounded-lg overflow-hidden dark:bg-blue-950 dark:border-blue-800">
        <summary className="cursor-pointer px-4 py-3 text-sm font-medium text-blue-900 hover:bg-blue-100 dark:text-blue-100 dark:hover:bg-blue-900">
          When do I need an API key?
        </summary>
        <div className="px-4 py-3 text-sm text-blue-900 space-y-3 border-t border-blue-200 dark:text-blue-100 dark:border-blue-800">
          <div>
            <div className="font-semibold mb-1">You need a key if you want to:</div>
            <ul className="list-disc list-inside space-y-0.5 ml-2">
              <li>
                Run the Tamma CLI in worker mode (<code className="text-xs bg-blue-100 px-1 rounded dark:bg-blue-900">tamma process-issue</code>,
                <code className="text-xs bg-blue-100 px-1 rounded ml-1 dark:bg-blue-900">tamma execute-task</code>) outside this browser
              </li>
              <li>Call the Tamma API from a CI pipeline, GitHub Action, or external script</li>
              <li>Pull dashboard data into another tool (status board, Slack notifier, etc.)</li>
            </ul>
          </div>
          <div>
            <div className="font-semibold mb-1">You don't need a key if you only use the dashboard.</div>
            <div>The browser session cookie covers everything you do here.</div>
          </div>
          <div>
            <div className="font-semibold mb-1">What this key can do:</div>
            <div>
              Read-only access to dashboard data and workflow status (
              <code className="text-xs bg-blue-100 px-1 rounded dark:bg-blue-900">dashboard:view</code>{' '}
              + <code className="text-xs bg-blue-100 px-1 rounded dark:bg-blue-900">workflows:view</code>).
              It cannot create or modify resources, manage users, or read other tenants' data.
              For tenant-wide automation use an organization key under{' '}
              <span className="font-mono text-xs">Settings → Organization → API Keys</span>.
            </div>
          </div>
          <div>
            <div className="font-semibold mb-1">How to use it:</div>
            <div className="space-y-1">
              <div>Set as an environment variable:</div>
              <code className="block p-2 bg-blue-100 rounded text-xs dark:bg-blue-900">
                export TAMMA_API_KEY=tamma_sk_us_…
              </code>
              <div>Or pass on every request:</div>
              <code className="block p-2 bg-blue-100 rounded text-xs dark:bg-blue-900">
                curl -H "Authorization: Bearer tamma_sk_us_…" https://api.tamma.dev/api/dashboard/summary
              </code>
            </div>
          </div>
          <div className="text-xs text-blue-800 italic dark:text-blue-200">
            Security: the full key is shown <strong>once</strong> when you create it. Store it in a
            secrets manager or your CI's secret store — never commit it. If a key is leaked,
            revoke it here immediately; revoked keys reject all requests.
          </div>
        </div>
      </details>

      {error && (
        <div className="mb-4 p-3 text-sm text-red-700 bg-red-50 border border-red-200 rounded-md dark:bg-red-950 dark:text-red-300 dark:border-red-800">
          {error}
        </div>
      )}

      {createdKey && (
        <div className="mb-4 p-3 text-sm text-green-700 bg-green-50 border border-green-200 rounded-md dark:bg-green-950 dark:text-green-300 dark:border-green-800">
          <div className="font-medium mb-1">API key created. Copy it now — it won't be shown again:</div>
          <code className="block p-2 bg-white rounded border text-xs break-all dark:bg-gray-800">{createdKey}</code>
          <button
            onClick={() => setCreatedKey(null)}
            className="mt-2 text-xs text-green-600 hover:underline dark:text-green-400"
          >
            Dismiss
          </button>
        </div>
      )}

      {/* Create new key */}
      <div className="bg-white rounded-lg border border-gray-200 p-4 mb-6 dark:bg-gray-800 dark:border-gray-700">
        <h2 className="text-sm font-semibold text-gray-700 mb-3 dark:text-gray-300">Create New Key</h2>
        <div className="flex gap-2">
          <input
            type="text"
            value={newKeyLabel}
            onChange={(e) => setNewKeyLabel(e.target.value)}
            placeholder="Label (e.g. CI pipeline)"
            className="flex-1 px-3 py-2 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 dark:border-gray-600"
          />
          <button
            onClick={() => void handleCreate()}
            disabled={creating || !newKeyLabel.trim()}
            className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50 transition-colors"
          >
            {creating ? 'Creating...' : 'Create'}
          </button>
        </div>
      </div>

      {/* Key list */}
      {keys.length === 0 ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">No API keys yet.</p>
      ) : (
        <div className="bg-white rounded-lg border border-gray-200 overflow-hidden dark:bg-gray-800 dark:border-gray-700">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 border-b border-gray-200 dark:bg-gray-900 dark:border-gray-700">
              <tr>
                <th className="text-left px-4 py-2 font-medium text-gray-500 dark:text-gray-400">Label</th>
                <th className="text-left px-4 py-2 font-medium text-gray-500 dark:text-gray-400">Prefix</th>
                <th className="text-left px-4 py-2 font-medium text-gray-500 dark:text-gray-400">Created</th>
                <th className="text-left px-4 py-2 font-medium text-gray-500 dark:text-gray-400">Last Used</th>
                <th className="px-4 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {keys.filter((k) => !k.revokedAt).map((k) => (
                <tr key={k.id} className="border-b border-gray-100 last:border-0 dark:border-gray-800">
                  <td className="px-4 py-3 font-medium text-gray-900 dark:text-gray-100">{k.label}</td>
                  <td className="px-4 py-3 text-gray-500 font-mono text-xs dark:text-gray-400">{k.keyPrefix}...</td>
                  <td className="px-4 py-3 text-gray-500 dark:text-gray-400">{new Date(k.createdAt).toLocaleDateString()}</td>
                  <td className="px-4 py-3 text-gray-500 dark:text-gray-400">
                    {k.lastUsedAt ? new Date(k.lastUsedAt).toLocaleDateString() : 'Never'}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <button
                      onClick={() => void handleRevoke(k.id)}
                      className="text-xs text-red-600 hover:text-red-800 font-medium dark:text-red-400"
                    >
                      Revoke
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
