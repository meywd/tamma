import { useState, type JSX } from 'react';
import {
  adminTenantsApi,
  AdminTenantApiError,
  type AdminTenantActionGate,
  type AdminTenantListItem,
} from '../../../../services/admin/admin-tenants-client.js';

/**
 * Story 28-11 — retry / delete / force-delete action controls for a single
 * tenant. Every button is gated by the server-computed
 * <see cref="AdminTenantActionGate"/> so the client never offers an
 * action the server would 409 anyway.
 *
 * Force-delete requires typed-slug friction (the user must type the
 * tenant slug verbatim) plus the server-side X-Admin-Confirm header —
 * two independent guards against fat-finger production catastrophe.
 */

interface DestructiveActionsProps {
  tenant: AdminTenantListItem;
  actions: AdminTenantActionGate;
  onActionComplete: () => void;
}

type ActionKind = 'retry' | 'delete' | 'force-delete';

const ACTION_LABELS: Record<ActionKind, { button: string; confirmTitle: string; confirmVerb: string }> = {
  retry: {
    button: 'Retry provisioning',
    confirmTitle: 'Retry tenant provisioning?',
    confirmVerb: 'Retry',
  },
  delete: {
    button: 'Initiate delete',
    confirmTitle: 'Delete this tenant?',
    confirmVerb: 'Delete',
  },
  'force-delete': {
    button: 'Force delete',
    confirmTitle: 'Force-delete this stuck tenant?',
    confirmVerb: 'Force delete',
  },
};

export function DestructiveActions({
  tenant,
  actions,
  onActionComplete,
}: DestructiveActionsProps): JSX.Element {
  const [pending, setPending] = useState<ActionKind | null>(null);
  const [confirming, setConfirming] = useState<ActionKind | null>(null);
  const [slugInput, setSlugInput] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [toast, setToast] = useState<string | null>(null);

  const run = async (kind: ActionKind): Promise<void> => {
    setError(null);
    setPending(kind);
    try {
      const resp =
        kind === 'retry' ? await adminTenantsApi.retry(tenant.id)
        : kind === 'delete' ? await adminTenantsApi.delete(tenant.id)
        : await adminTenantsApi.forceDelete(tenant.id);
      setToast(resp.message);
      setConfirming(null);
      setSlugInput('');
      onActionComplete();
    } catch (e) {
      if (e instanceof AdminTenantApiError) {
        setError(`${e.message} (status ${e.status})`);
      } else {
        setError((e as Error).message);
      }
    } finally {
      setPending(null);
    }
  };

  const canSubmitConfirm = (kind: ActionKind): boolean => {
    if (kind === 'force-delete') {
      // Typed-slug friction: user must re-type the tenant slug verbatim.
      return slugInput.trim() === tenant.slug;
    }
    // Retry / delete — a single confirm click is enough.
    return true;
  };

  const renderButton = (kind: ActionKind, enabled: boolean): JSX.Element => {
    const danger = kind !== 'retry';
    const classes = danger
      ? 'bg-red-600 hover:bg-red-700 text-white disabled:bg-red-300'
      : 'bg-blue-600 hover:bg-blue-700 text-white disabled:bg-blue-300';
    return (
      <button
        key={kind}
        type="button"
        disabled={!enabled || pending !== null}
        onClick={() => {
          setConfirming(kind);
          setError(null);
          setSlugInput('');
        }}
        className={`px-4 py-2 text-sm font-medium rounded-md disabled:cursor-not-allowed disabled:opacity-60 ${classes}`}
      >
        {ACTION_LABELS[kind].button}
      </button>
    );
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-3">
        {renderButton('retry', actions.canRetry)}
        {renderButton('delete', actions.canDelete)}
        {renderButton('force-delete', actions.canForceDelete)}
      </div>

      {toast && (
        <div
          role="status"
          className="bg-green-50 border border-green-200 text-sm text-green-800 rounded-md px-3 py-2"
        >
          {toast}
        </div>
      )}
      {error && (
        <div
          role="alert"
          className="bg-red-50 border border-red-200 text-sm text-red-800 rounded-md px-3 py-2"
        >
          {error}
        </div>
      )}

      {confirming && (
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="tenant-action-confirm-title"
          className="fixed inset-0 z-50 flex items-center justify-center"
        >
          <div
            className="fixed inset-0 bg-black/50"
            onClick={() => {
              if (pending === null) {
                setConfirming(null);
                setSlugInput('');
              }
            }}
            aria-hidden="true"
          />
          <div className="relative bg-white rounded-lg shadow-xl p-6 max-w-md w-full mx-4">
            <h3
              id="tenant-action-confirm-title"
              className="text-lg font-semibold text-gray-900 mb-2"
            >
              {ACTION_LABELS[confirming].confirmTitle}
            </h3>
            <p className="text-sm text-gray-600 mb-4">
              Tenant:{' '}
              <code className="font-mono bg-gray-100 px-1 rounded">
                {tenant.name}
              </code>{' '}
              <span className="text-gray-400">({tenant.slug})</span>
            </p>

            {confirming === 'force-delete' && (
              <div className="mb-4">
                <label
                  htmlFor="slug-confirm"
                  className="block text-sm font-medium text-gray-700 mb-1"
                >
                  Type the tenant slug to confirm:{' '}
                  <code className="font-mono text-xs bg-gray-100 px-1 rounded">
                    {tenant.slug}
                  </code>
                </label>
                <input
                  id="slug-confirm"
                  type="text"
                  value={slugInput}
                  onChange={(e) => setSlugInput(e.target.value)}
                  autoComplete="off"
                  className="w-full text-sm font-mono border border-gray-300 rounded-md px-3 py-2 focus:outline-none focus:ring-2 focus:ring-red-500"
                />
              </div>
            )}

            <div className="flex justify-end gap-3">
              <button
                type="button"
                onClick={() => {
                  setConfirming(null);
                  setSlugInput('');
                }}
                disabled={pending !== null}
                className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md"
              >
                Cancel
              </button>
              <button
                type="button"
                disabled={pending !== null || !canSubmitConfirm(confirming)}
                onClick={() => void run(confirming)}
                className="px-4 py-2 text-sm font-medium text-white bg-red-600 hover:bg-red-700 rounded-md disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {pending ? 'Working…' : ACTION_LABELS[confirming].confirmVerb}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
