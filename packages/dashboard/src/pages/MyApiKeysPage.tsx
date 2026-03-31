/**
 * My API Keys Page — Self-service API key management for the current user.
 *
 * Similar to the admin ApiKeysTab but scoped to the current user only.
 * Users can create and revoke their own API keys.
 */

import { useState, useEffect, useCallback } from 'react';
import { useAuth } from '../hooks/useAuth.js';
import { LoadingSpinner } from '../components/common/LoadingSpinner.js';

interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
}

export function MyApiKeysPage(): JSX.Element {
  const { user } = useAuth();
  const [keys, setKeys] = useState<ApiKey[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [newKeyName, setNewKeyName] = useState('');
  const [createdKey, setCreatedKey] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const fetchKeys = useCallback(async () => {
    if (!user) return;
    try {
      const res = await fetch(`/api/admin/users/${user.id}/api-keys`, { credentials: 'include' });
      if (!res.ok) throw new Error('Failed to fetch API keys');
      const data = (await res.json()) as { keys: ApiKey[] };
      setKeys(data.keys);
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
    if (!user || !newKeyName.trim()) return;
    setCreating(true);
    try {
      const res = await fetch(`/api/admin/users/${user.id}/api-keys`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: newKeyName.trim() }),
      });
      if (!res.ok) throw new Error('Failed to create API key');
      const data = (await res.json()) as { key: string };
      setCreatedKey(data.key);
      setNewKeyName('');
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
      const res = await fetch(`/api/admin/users/${user.id}/api-keys/${keyId}`, {
        method: 'DELETE',
        credentials: 'include',
      });
      if (!res.ok) throw new Error('Failed to revoke API key');
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
      <h1 className="text-2xl font-bold text-gray-900 mb-6">API Keys</h1>

      {error && (
        <div className="mb-4 p-3 text-sm text-red-700 bg-red-50 border border-red-200 rounded-md">
          {error}
        </div>
      )}

      {createdKey && (
        <div className="mb-4 p-3 text-sm text-green-700 bg-green-50 border border-green-200 rounded-md">
          <div className="font-medium mb-1">API key created. Copy it now — it won't be shown again:</div>
          <code className="block p-2 bg-white rounded border text-xs break-all">{createdKey}</code>
          <button
            onClick={() => setCreatedKey(null)}
            className="mt-2 text-xs text-green-600 hover:underline"
          >
            Dismiss
          </button>
        </div>
      )}

      {/* Create new key */}
      <div className="bg-white rounded-lg border border-gray-200 p-4 mb-6">
        <h2 className="text-sm font-semibold text-gray-700 mb-3">Create New Key</h2>
        <div className="flex gap-2">
          <input
            type="text"
            value={newKeyName}
            onChange={(e) => setNewKeyName(e.target.value)}
            placeholder="Key name (e.g. CI pipeline)"
            className="flex-1 px-3 py-2 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
          <button
            onClick={() => void handleCreate()}
            disabled={creating || !newKeyName.trim()}
            className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50 transition-colors"
          >
            {creating ? 'Creating...' : 'Create'}
          </button>
        </div>
      </div>

      {/* Key list */}
      {keys.length === 0 ? (
        <p className="text-sm text-gray-500">No API keys yet.</p>
      ) : (
        <div className="bg-white rounded-lg border border-gray-200 overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 border-b border-gray-200">
              <tr>
                <th className="text-left px-4 py-2 font-medium text-gray-500">Name</th>
                <th className="text-left px-4 py-2 font-medium text-gray-500">Prefix</th>
                <th className="text-left px-4 py-2 font-medium text-gray-500">Created</th>
                <th className="text-left px-4 py-2 font-medium text-gray-500">Last Used</th>
                <th className="px-4 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {keys.map((k) => (
                <tr key={k.id} className="border-b border-gray-100 last:border-0">
                  <td className="px-4 py-3 font-medium text-gray-900">{k.name}</td>
                  <td className="px-4 py-3 text-gray-500 font-mono text-xs">{k.prefix}...</td>
                  <td className="px-4 py-3 text-gray-500">{new Date(k.createdAt).toLocaleDateString()}</td>
                  <td className="px-4 py-3 text-gray-500">
                    {k.lastUsedAt ? new Date(k.lastUsedAt).toLocaleDateString() : 'Never'}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <button
                      onClick={() => void handleRevoke(k.id)}
                      className="text-xs text-red-600 hover:text-red-800 font-medium"
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
