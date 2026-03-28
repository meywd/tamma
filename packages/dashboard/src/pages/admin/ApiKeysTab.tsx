/**
 * API Keys Tab
 *
 * Lists all API keys across all users. Each key shows:
 * prefix, label, user, created, last used.
 * Create new key (per-user, shown once in dialog) and revoke existing keys.
 */

import { useState } from 'react';
import { useApiKeys } from '../../hooks/admin/useApiKeys.js';
import { useUsers } from '../../hooks/admin/useUsers.js';
import { useCurrentUser } from '../../hooks/admin/useCurrentUser.js';
import { LoadingSpinner } from '../../components/common/LoadingSpinner.js';
import { ConfirmDialog } from '../../components/common/ConfirmDialog.js';

function formatDate(dateStr: string): string {
  return new Date(dateStr).toLocaleDateString();
}

function formatRelative(dateStr: string): string {
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60_000);
  const diffHours = Math.floor(diffMins / 60);
  const diffDays = Math.floor(diffHours / 24);

  if (diffMins < 1) return 'just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  if (diffHours < 24) return `${diffHours}h ago`;
  if (diffDays < 30) return `${diffDays}d ago`;
  return date.toLocaleDateString();
}

/** Dialog to create a new API key for a specific user */
function CreateApiKeyDialog({
  onClose,
}: {
  onClose: () => void;
}): JSX.Element {
  const { create } = useApiKeys();
  const { users } = useUsers();
  const { user: currentUser } = useCurrentUser();
  const [label, setLabel] = useState('');
  const [selectedUserId, setSelectedUserId] = useState(currentUser?.id ?? '');
  const [generatedKey, setGeneratedKey] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const handleCreate = async () => {
    if (!label.trim()) {
      setError('Label is required');
      return;
    }
    if (!selectedUserId) {
      setError('User is required');
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      const result = await create(selectedUserId, label.trim());
      setGeneratedKey(result.key);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create API key');
    } finally {
      setSubmitting(false);
    }
  };

  const handleCopy = () => {
    if (generatedKey) {
      void navigator.clipboard.writeText(generatedKey);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center" role="presentation">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} aria-hidden="true" />
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="create-key-dialog-title"
        className="relative bg-white rounded-lg shadow-xl p-6 max-w-lg w-full mx-4"
      >
        <h3 id="create-key-dialog-title" className="text-lg font-semibold text-gray-900 mb-4">
          {generatedKey ? 'API Key Created' : 'Create API Key'}
        </h3>

        {generatedKey ? (
          <div>
            <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-3 mb-4">
              <p className="text-sm font-medium text-yellow-800 mb-1">
                Copy this key now. You will not be able to see it again.
              </p>
            </div>
            <div className="flex items-center gap-2 mb-4">
              <code className="flex-1 text-sm bg-gray-100 border border-gray-300 rounded-md px-3 py-2 font-mono break-all">
                {generatedKey}
              </code>
              <button
                type="button"
                onClick={handleCopy}
                className="px-3 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md shrink-0"
              >
                {copied ? 'Copied!' : 'Copy'}
              </button>
            </div>
            <div className="flex justify-end">
              <button
                type="button"
                onClick={onClose}
                className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md"
              >
                Done
              </button>
            </div>
          </div>
        ) : (
          <div>
            <div className="mb-4">
              <label className="block text-sm font-medium text-gray-700 mb-1">User</label>
              <select
                value={selectedUserId}
                onChange={(e) => setSelectedUserId(e.target.value)}
                className="w-full text-sm border border-gray-300 rounded-md px-3 py-2 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500"
              >
                <option value="">Select a user...</option>
                {users.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.githubLogin} ({u.role})
                  </option>
                ))}
              </select>
            </div>

            <div className="mb-4">
              <label className="block text-sm font-medium text-gray-700 mb-1">Label</label>
              <input
                type="text"
                value={label}
                onChange={(e) => setLabel(e.target.value)}
                placeholder="e.g. CI Pipeline, Dev Machine"
                className="w-full text-sm border border-gray-300 rounded-md px-3 py-2 focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
            </div>

            {error && <p className="text-sm text-red-600 mb-4">{error}</p>}

            <div className="flex justify-end gap-3">
              <button
                type="button"
                onClick={onClose}
                className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => void handleCreate()}
                disabled={submitting}
                className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
              >
                {submitting ? 'Creating...' : 'Create Key'}
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

export function ApiKeysTab(): JSX.Element {
  const { apiKeys, loading, error, revoke } = useApiKeys();
  const { users } = useUsers();
  const [showCreate, setShowCreate] = useState(false);
  const [confirmRevoke, setConfirmRevoke] = useState<{
    userId: string;
    keyId: string;
    label: string;
  } | null>(null);

  // Build a lookup map for user display names
  const userMap = new Map(users.map((u) => [u.id, u.githubLogin]));

  if (loading && apiKeys.length === 0) {
    return (
      <div className="flex justify-center py-12">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-700">
        {error}
      </div>
    );
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-lg font-semibold text-gray-900">
          API Keys <span className="text-gray-400 font-normal">({apiKeys.length})</span>
        </h2>
        <button
          type="button"
          onClick={() => setShowCreate(true)}
          className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
        >
          Create API Key
        </button>
      </div>

      {apiKeys.length === 0 ? (
        <div className="text-center py-12 text-gray-500">
          <p className="text-lg mb-2">No API keys</p>
          <p className="text-sm">Create an API key for programmatic access.</p>
        </div>
      ) : (
        <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden">
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Key Prefix
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Label
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  User
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Created
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Last Used
                </th>
                <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Actions
                </th>
              </tr>
            </thead>
            <tbody className="bg-white divide-y divide-gray-200">
              {apiKeys.map((key) => (
                <tr key={key.id} className="hover:bg-gray-50">
                  <td className="px-6 py-4 whitespace-nowrap">
                    <code className="text-sm font-mono text-gray-700 bg-gray-100 px-2 py-0.5 rounded">
                      {key.keyPrefix}...
                    </code>
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
                    {key.label}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                    {userMap.get(key.userId) ?? key.userId}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                    {formatDate(key.createdAt)}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                    {key.lastUsedAt ? formatRelative(key.lastUsedAt) : 'Never'}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-right">
                    <button
                      type="button"
                      onClick={() =>
                        setConfirmRevoke({
                          userId: key.userId,
                          keyId: key.id,
                          label: key.label,
                        })
                      }
                      className="text-sm text-red-600 hover:text-red-800 font-medium"
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

      {/* Create Dialog */}
      {showCreate && <CreateApiKeyDialog onClose={() => setShowCreate(false)} />}

      {/* Confirm Revoke */}
      <ConfirmDialog
        open={confirmRevoke !== null}
        title="Revoke API Key"
        message={
          confirmRevoke
            ? `Are you sure you want to revoke the API key "${confirmRevoke.label}"? This action cannot be undone.`
            : ''
        }
        confirmLabel="Revoke"
        variant="danger"
        onConfirm={() => {
          if (confirmRevoke) {
            void revoke(confirmRevoke.userId, confirmRevoke.keyId);
          }
          setConfirmRevoke(null);
        }}
        onCancel={() => setConfirmRevoke(null)}
      />
    </div>
  );
}
