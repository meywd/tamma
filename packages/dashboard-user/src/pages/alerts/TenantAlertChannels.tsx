/**
 * TenantAlertChannels — /settings/alerts
 *
 * Tenant-admin-only. Channels are scoped to the caller's active tenant
 * via /api/v1/orgs/{tenantId}/alert-channels/*. Credentials are never
 * submitted as plaintext — the user either creates an email channel
 * (no secret) or pastes the `credentialsSecretId` they obtained from
 * the Secrets page.
 *
 * Access control: wrapped in <TenantAdminGuard> at the route level.
 * As a defense-in-depth safety net this component also checks the
 * role directly and hides destructive buttons when the caller isn't
 * admin+.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import { useAuth } from '../../hooks/useAuth';
import {
  createTenantChannel,
  deleteTenantChannel,
  hasPlaintextCredential,
  listTenantChannels,
  updateTenantChannel,
  type ChannelDto,
  type CreateChannelBody,
} from '../../api/alerts';

type ChannelType = CreateChannelBody['channelType'];
const CHANNEL_TYPES: ChannelType[] = ['email', 'slack', 'pagerduty', 'webhook'];

export function TenantAlertChannels(): JSX.Element {
  const { user } = useAuth();
  const tenantId = user?.tenantId ?? null;
  const canMutate = user?.role === 'admin' || user?.role === 'owner';

  const [channels, setChannels] = useState<ChannelDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [wizardOpen, setWizardOpen] = useState(false);
  const [wizardStep, setWizardStep] = useState<1 | 2>(1);
  const [form, setForm] = useState<{
    name: string;
    channelType: ChannelType;
    config: string;
    credentialsSecretId: string;
  }>({
    name: '',
    channelType: 'email',
    config: '{}',
    credentialsSecretId: '',
  });
  const [wizardErr, setWizardErr] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    if (!tenantId) return;
    setLoading(true);
    setError(null);
    try {
      const resp = await listTenantChannels(tenantId);
      setChannels(resp.items);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load channels');
    } finally {
      setLoading(false);
    }
  }, [tenantId]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const resetWizard = (): void => {
    setForm({
      name: '',
      channelType: 'email',
      config: '{}',
      credentialsSecretId: '',
    });
    setWizardStep(1);
    setWizardErr(null);
  };

  const canAdvanceToStep2 = (): boolean =>
    form.name.trim().length > 0 && form.channelType !== undefined;

  const createChannel = async (): Promise<void> => {
    if (!tenantId) return;
    setWizardErr(null);

    if (hasPlaintextCredential(form.config)) {
      setWizardErr(
        'Config contains a credential-like field. Use the Secret Store instead.',
      );
      return;
    }

    const needsSecret = form.channelType !== 'email';
    if (needsSecret && form.credentialsSecretId.trim().length === 0) {
      setWizardErr(
        `${form.channelType} channels require a credentialsSecretId from the Secret Store.`,
      );
      return;
    }

    try {
      await createTenantChannel(tenantId, {
        name: form.name.trim(),
        channelType: form.channelType,
        // body.tenantId omitted — server forces path-tenant ownership.
        config: form.config,
        credentialsSecretId:
          form.credentialsSecretId.trim().length === 0
            ? null
            : form.credentialsSecretId.trim(),
      });
      setWizardOpen(false);
      resetWizard();
      await refresh();
    } catch (err) {
      setWizardErr(err instanceof Error ? err.message : 'Create failed');
    }
  };

  const toggleEnabled = async (ch: ChannelDto): Promise<void> => {
    if (!tenantId) return;
    try {
      await updateTenantChannel(tenantId, ch.id, { isEnabled: !ch.isEnabled });
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Toggle failed');
    }
  };

  const softDelete = async (ch: ChannelDto): Promise<void> => {
    if (!tenantId) return;
    try {
      await deleteTenantChannel(tenantId, ch.id);
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Delete failed');
    }
  };

  if (!tenantId) {
    return (
      <div className="p-4 text-sm text-gray-500">
        No active organization.
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Alert channels</h1>
          <p className="mt-1 text-sm text-gray-500">
            Where your organization's alerts get sent. Credentials live in the
            Secret Store — this page never accepts plaintext tokens.
          </p>
        </div>
        {canMutate && (
          <button
            type="button"
            onClick={() => {
              resetWizard();
              setWizardOpen(true);
            }}
            className="px-3 py-1.5 text-sm bg-gray-900 text-white rounded hover:bg-gray-800"
          >
            New channel
          </button>
        )}
      </div>

      {error !== null && (
        <div role="alert" className="p-3 text-sm text-red-700 bg-red-50 rounded-md">
          {error}
        </div>
      )}

      {loading ? (
        <p className="text-sm text-gray-500">Loading…</p>
      ) : channels.length === 0 ? (
        <p className="text-sm text-gray-500">No channels configured.</p>
      ) : (
        <div className="bg-white border border-gray-200 rounded-md overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-xs uppercase text-gray-600">
              <tr>
                <th className="px-3 py-2 text-left">Name</th>
                <th className="px-3 py-2 text-left">Type</th>
                <th className="px-3 py-2 text-left">Enabled</th>
                <th className="px-3 py-2 text-left">Credentials</th>
                {canMutate && <th className="px-3 py-2 text-right">Actions</th>}
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {channels.map((ch) => (
                <tr key={ch.id} className="hover:bg-gray-50">
                  <td className="px-3 py-2 text-gray-900">{ch.name}</td>
                  <td className="px-3 py-2 text-gray-700">{ch.channelType}</td>
                  <td className="px-3 py-2">
                    {canMutate ? (
                      <button
                        type="button"
                        onClick={() => void toggleEnabled(ch)}
                        className={`px-2 py-0.5 text-xs rounded ${
                          ch.isEnabled
                            ? 'bg-green-100 text-green-800'
                            : 'bg-gray-100 text-gray-600'
                        }`}
                        aria-label={`Toggle ${ch.name} ${ch.isEnabled ? 'off' : 'on'}`}
                      >
                        {ch.isEnabled ? 'on' : 'off'}
                      </button>
                    ) : (
                      <span className="text-xs text-gray-600">
                        {ch.isEnabled ? 'on' : 'off'}
                      </span>
                    )}
                  </td>
                  <td className="px-3 py-2 text-xs text-gray-700">
                    {ch.credentialsSecretId === null ? (
                      <span className="text-gray-400">none</span>
                    ) : (
                      <span className="text-green-700">linked</span>
                    )}
                  </td>
                  {canMutate && (
                    <td className="px-3 py-2 text-right whitespace-nowrap">
                      <button
                        type="button"
                        onClick={() => void softDelete(ch)}
                        className="px-2 py-1 text-xs border border-red-300 text-red-700 rounded hover:bg-red-50"
                      >
                        Delete
                      </button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {wizardOpen && (
        <Modal
          title={`New channel — step ${wizardStep} of 2`}
          onClose={() => setWizardOpen(false)}
        >
          {wizardErr !== null && (
            <div
              role="alert"
              className="mb-3 p-2 text-xs text-red-700 bg-red-50 rounded"
            >
              {wizardErr}
            </div>
          )}

          {wizardStep === 1 && (
            <>
              <label className="block text-xs text-gray-600 mb-1" htmlFor="wz-type">
                Type
              </label>
              <select
                id="wz-type"
                value={form.channelType}
                onChange={(e) =>
                  setForm({ ...form, channelType: e.target.value as ChannelType })
                }
                className="w-full mb-3 border border-gray-300 rounded px-2 py-1 text-sm"
              >
                {CHANNEL_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {t}
                  </option>
                ))}
              </select>

              <label className="block text-xs text-gray-600 mb-1" htmlFor="wz-name">
                Name
              </label>
              <input
                id="wz-name"
                type="text"
                value={form.name}
                onChange={(e) => setForm({ ...form, name: e.target.value })}
                className="w-full mb-1 border border-gray-300 rounded px-2 py-1 text-sm"
              />

              <div className="mt-3 flex justify-end gap-2">
                <button
                  type="button"
                  onClick={() => setWizardOpen(false)}
                  className="px-3 py-1 text-sm border border-gray-300 rounded"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  disabled={!canAdvanceToStep2()}
                  onClick={() => setWizardStep(2)}
                  className="px-3 py-1 text-sm bg-gray-900 text-white rounded disabled:opacity-50"
                >
                  Next
                </button>
              </div>
            </>
          )}

          {wizardStep === 2 && (
            <>
              {form.channelType !== 'email' ? (
                <>
                  <div className="mb-3 p-2 bg-yellow-50 text-yellow-800 text-xs rounded">
                    Credentials live in the Secret Store. Create the secret
                    first (via Settings → Secrets) and paste its ID here — we
                    NEVER send plaintext tokens on this request.
                  </div>
                  <label
                    className="block text-xs text-gray-600 mb-1"
                    htmlFor="wz-sid"
                  >
                    Credentials secret ID
                  </label>
                  <input
                    id="wz-sid"
                    type="text"
                    value={form.credentialsSecretId}
                    onChange={(e) =>
                      setForm({ ...form, credentialsSecretId: e.target.value })
                    }
                    placeholder="uuid from the secret store"
                    className="w-full mb-3 border border-gray-300 rounded px-2 py-1 text-sm font-mono"
                  />
                </>
              ) : (
                <div className="mb-3 p-2 bg-blue-50 text-blue-800 text-xs rounded">
                  Email channels use the shared SMTP cabinet — no per-channel
                  secret needed.
                </div>
              )}

              <label
                className="block text-xs text-gray-600 mb-1"
                htmlFor="wz-config"
              >
                Config (JSON, non-credential only)
              </label>
              <textarea
                id="wz-config"
                value={form.config}
                onChange={(e) => setForm({ ...form, config: e.target.value })}
                rows={4}
                className="w-full mb-3 border border-gray-300 rounded px-2 py-1 text-xs font-mono"
              />

              <div className="mt-3 flex justify-end gap-2">
                <button
                  type="button"
                  onClick={() => setWizardStep(1)}
                  className="px-3 py-1 text-sm border border-gray-300 rounded"
                >
                  Back
                </button>
                <button
                  type="button"
                  onClick={() => void createChannel()}
                  className="px-3 py-1 text-sm bg-gray-900 text-white rounded"
                >
                  Create
                </button>
              </div>
            </>
          )}
        </Modal>
      )}
    </div>
  );
}

function Modal({
  title,
  children,
  onClose,
}: {
  title: string;
  children: React.ReactNode;
  onClose: () => void;
}): JSX.Element {
  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="channel-modal-title"
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40"
    >
      <div className="bg-white rounded-lg shadow-lg p-5 w-full max-w-md">
        <div className="flex items-center justify-between mb-3">
          <h2 id="channel-modal-title" className="text-lg font-medium">
            {title}
          </h2>
          <button
            type="button"
            aria-label="Close"
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600"
          >
            ×
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}
