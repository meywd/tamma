import { useCallback, useEffect, useState, type JSX } from 'react';
import type {
  CreateSecretBody,
  RevealEnvelope,
  RevealResult,
  SecretListItem,
} from '../../services/secrets/secrets-api-client.js';
import {
  SecretApiError,
  revealApi,
} from '../../services/secrets/secrets-api-client.js';
import { SecretRevealModal } from './SecretRevealModal.js';
import { CreateSecretForm } from './CreateSecretForm.js';
import { ConsumerLink } from './ConsumerLink.js';

/**
 * Story 29-4 + 29-5 — shared list view used by both the platform-admin
 * `/admin/secrets` route and the tenant-admin `/secrets` route. Callers
 * inject an `api` object implementing the list + create methods; the
 * two routes differ only in:
 *   • The API surface (scope filter, endpoint path).
 *   • The `scopeLabel` string shown in the header / form.
 *   • Whether the "Create" button is enabled (read-only "view-as-tenant"
 *     disables it).
 */

export interface SecretsApi {
  list: () => Promise<{ secrets: SecretListItem[] }>;
  create: (body: CreateSecretBody) => Promise<RevealEnvelope>;
}

export interface SecretsListViewProps {
  readonly api: SecretsApi;
  readonly scopeLabel: string;
  readonly tenantId?: string | null;
  /**
   * Set false to suppress the "Create secret" button. Used by the
   * platform-admin "view-as-tenant" page (Story 29-5 AC4).
   */
  readonly allowCreate?: boolean;
  /** Optional empty-state copy override. */
  readonly emptyStateMessage?: string;
}

export function SecretsListView({
  api,
  scopeLabel,
  tenantId,
  allowCreate = true,
  emptyStateMessage,
}: SecretsListViewProps): JSX.Element {
  const [secrets, setSecrets] = useState<SecretListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [revealState, setRevealState] = useState<{
    name: string;
    version: number;
    plaintext: string;
    expiresAt: string;
  } | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const resp = await api.list();
      setSecrets(resp.secrets);
    } catch (e) {
      if (e instanceof SecretApiError) {
        setError(`${e.message} (status ${e.status})`);
      } else {
        setError((e as Error).message);
      }
    } finally {
      setLoading(false);
    }
  }, [api]);

  useEffect(() => {
    void load();
  }, [load]);

  const handleCreate = useCallback(
    async (body: CreateSecretBody) => {
      setSubmitting(true);
      setError(null);
      try {
        const envelope = await api.create(body);
        // Burn the reveal token exactly once. If this throws, we still
        // have the envelope on the server but plaintext is now unreachable —
        // user must rotate to recover. Surface that clearly.
        let reveal: RevealResult;
        try {
          reveal = await revealApi.consume(envelope.revealToken);
        } catch (revealErr) {
          const msg =
            revealErr instanceof SecretApiError
              ? `${revealErr.message} (status ${revealErr.status})`
              : (revealErr as Error).message;
          throw new Error(
            `Secret was created but the reveal failed: ${msg}. Rotate the secret to get a new value.`,
          );
        }
        setRevealState({
          name: reveal.name,
          version: reveal.version,
          plaintext: reveal.plaintext,
          expiresAt: reveal.expiresAt,
        });
        setShowForm(false);
        await load();
      } catch (e) {
        setError((e as Error).message);
      } finally {
        setSubmitting(false);
      }
    },
    [api, load],
  );

  const handleCloseReveal = useCallback(() => {
    // Zero the plaintext in our local state and drop the whole reveal
    // state object so a re-render cannot surface the value.
    if (revealState) {
      setRevealState({ ...revealState, plaintext: '' });
    }
    setRevealState(null);
  }, [revealState]);

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-xl font-semibold text-gray-900">
          {scopeLabel} secrets
        </h2>
        {allowCreate ? (
          <button
            type="button"
            onClick={() => setShowForm((v) => !v)}
            className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
          >
            {showForm ? 'Cancel' : 'Create secret'}
          </button>
        ) : null}
      </div>

      {error ? (
        <div
          role="alert"
          className="mb-4 text-sm text-red-700 bg-red-50 border border-red-200 rounded-md p-3"
        >
          {error}
        </div>
      ) : null}

      {showForm ? (
        <div className="mb-6">
          <CreateSecretForm
            scopeLabel={scopeLabel}
            onSubmit={handleCreate}
            onCancel={() => setShowForm(false)}
            submitting={submitting}
          />
        </div>
      ) : null}

      {loading ? (
        <p className="text-sm text-gray-500">Loading secrets…</p>
      ) : secrets.length === 0 ? (
        <div className="bg-white border border-gray-200 rounded-lg p-8 text-center">
          <p className="text-sm text-gray-600">
            {emptyStateMessage ?? 'No secrets yet.'}
          </p>
        </div>
      ) : (
        <div className="bg-white border border-gray-200 rounded-lg overflow-hidden">
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr>
                <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">
                  Name
                </th>
                <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">
                  Purpose
                </th>
                <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">
                  Consumers
                </th>
                <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">
                  Active version
                </th>
                <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">
                  Last rotated
                </th>
                <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">
                  Next due
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-200">
              {secrets.map((s) => (
                <tr key={s.secretId}>
                  <td className="px-4 py-2 text-sm font-mono text-gray-900">{s.name}</td>
                  <td className="px-4 py-2 text-sm text-gray-700">{s.purpose}</td>
                  <td className="px-4 py-2 text-sm text-gray-700">
                    {s.consumerRefs.length === 0 ? (
                      <span className="text-gray-400">—</span>
                    ) : (
                      <ul className="space-y-1">
                        {s.consumerRefs.map((c, i) => (
                          <li key={`${c.type}-${c.target}-${i}`}>
                            <ConsumerLink consumer={c} tenantId={tenantId ?? s.tenantId} />
                          </li>
                        ))}
                      </ul>
                    )}
                  </td>
                  <td className="px-4 py-2 text-sm text-gray-700">{s.activeVersion}</td>
                  <td className="px-4 py-2 text-sm text-gray-500">
                    {s.lastRotatedAt ? new Date(s.lastRotatedAt).toLocaleDateString() : '—'}
                  </td>
                  <td className="px-4 py-2 text-sm text-gray-500">
                    {s.nextRotationDueAt
                      ? new Date(s.nextRotationDueAt).toLocaleDateString()
                      : '—'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {revealState ? (
        <SecretRevealModal
          open
          name={revealState.name}
          version={revealState.version}
          plaintext={revealState.plaintext}
          expiresAt={revealState.expiresAt}
          onClose={handleCloseReveal}
        />
      ) : null}
    </div>
  );
}
