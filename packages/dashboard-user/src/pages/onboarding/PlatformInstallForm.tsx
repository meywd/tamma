/**
 * PlatformInstallForm — /onboarding/platforms/:kind/install
 *
 * Story 31-9 — second step of the onboarding picker. Renders a
 * per-kind credential form whose shape is determined by the backend's
 * `authMode` field on the platform descriptor:
 *
 *   github_app             → deep-link button (Story 18-4 redirect)
 *   personal_access_token  → baseUrl + PAT input
 *   coming_soon            → tooltip "this platform is on the roadmap"
 *
 * On submit, POSTs to /api/onboarding/install via the api client. The
 * backend writes the credential to Epic 29's cabinet and runs an
 * auth-probe via the driver factory before persisting the
 * tenant_platform_installations row. Failure responses carry a
 * `hint` string the form renders inline so the operator can fix the
 * input and retry.
 *
 * Plaintext rule: the credential is held only inside this component's
 * `useState` until submit settles; we clear it explicitly on success
 * (and on error to force the operator to re-enter) so the bytes don't
 * linger in React's component tree.
 */

import { useEffect, useState, type FormEvent, type JSX } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import {
  installPlatform,
  listSupportedPlatforms,
  type PlatformDescriptor,
  type PlatformKind,
} from '../../api/platforms';
import { ApiError } from '../../api/client';

const GITHUB_APP_INSTALL_PATH = '/api/v1/onboarding/install-github';

export function PlatformInstallForm(): JSX.Element {
  const params = useParams<{ kind?: string }>();
  const kind = (params.kind ?? '') as PlatformKind;
  const navigate = useNavigate();

  const [descriptor, setDescriptor] = useState<PlatformDescriptor | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  // Form state.
  const [baseUrl, setBaseUrl] = useState(defaultBaseUrlFor(kind));
  const [externalId, setExternalId] = useState('');
  const [credential, setCredential] = useState('');

  // Submit state.
  const [submitting, setSubmitting] = useState(false);
  const [hint, setHint] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const resp = await listSupportedPlatforms();
        if (cancelled) return;
        const match = resp.items.find((p) => p.kind === kind);
        if (match === undefined) {
          setLoadError(`Unknown platform kind: ${kind}`);
        } else {
          setDescriptor(match);
        }
      } catch (err) {
        if (!cancelled) {
          setLoadError(
            err instanceof Error ? err.message : 'Failed to load platforms',
          );
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [kind]);

  // Redirect to /settings/platforms 1.2 s after a successful submit so
  // the operator reads the success line first. Stored in useEffect so
  // the cleanup cancels the timer when the user navigates away
  // manually before the redirect fires.
  useEffect(() => {
    if (success === null) return;
    const timer = setTimeout(() => {
      navigate('/settings/platforms');
    }, 1200);
    return () => clearTimeout(timer);
  }, [success, navigate]);

  if (loading) {
    return (
      <div role="status" className="text-sm text-gray-500">
        Loading…
      </div>
    );
  }

  if (loadError !== null) {
    return (
      <div role="alert" className="p-4 bg-red-50 text-red-700 rounded">
        {loadError}
      </div>
    );
  }

  if (descriptor === null) {
    return (
      <div role="alert" className="p-4 bg-red-50 text-red-700 rounded">
        Unknown platform: {kind}
      </div>
    );
  }

  if (descriptor.authMode === 'coming_soon' || !descriptor.available) {
    return <ComingSoon descriptor={descriptor} />;
  }

  if (descriptor.authMode === 'github_app') {
    return <GitHubAppInstall descriptor={descriptor} />;
  }

  // PAT-style auth (Gitea / Forgejo / GitLab / others).
  const submit = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
    event.preventDefault();
    setHint(null);
    setSuccess(null);

    if (baseUrl.trim() === '') {
      setHint('Base URL is required.');
      return;
    }
    if (credential.trim() === '') {
      setHint('Token / credential is required.');
      return;
    }

    setSubmitting(true);
    try {
      const resp = await installPlatform({
        kind: descriptor.kind,
        baseUrl: baseUrl.trim(),
        externalId: externalId.trim() === '' ? null : externalId.trim(),
        credentialPlaintext: credential,
      });
      setCredential(''); // do not linger plaintext on success
      setSuccess(
        `Connected ${descriptor.displayName} (installation ${resp.installationId.slice(0, 8)}…).`,
      );
      // Redirect is scheduled by the useEffect below — keeping the
      // timer there means an unmount (e.g. user navigated away
      // manually before the 1.2 s elapses) cleanly cancels it instead
      // of firing a stale navigate() on a torn-down tree.
    } catch (err) {
      setCredential(''); // force re-entry — keeps the bytes off the heap
      if (err instanceof ApiError) {
        const body = err.body as
          | { error?: string; hint?: string | null }
          | null
          | undefined;
        const text = body?.hint ?? body?.error ?? `HTTP ${err.status}`;
        setHint(text);
      } else if (err instanceof Error) {
        setHint(err.message);
      } else {
        setHint('Unexpected error');
      }
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="max-w-xl">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold text-gray-900">
          Connect {descriptor.displayName}
        </h1>
        <p className="mt-1 text-sm text-gray-600">
          We'll store your token in the secret cabinet and verify it works
          before saving the connection.
        </p>
      </header>

      <form onSubmit={submit} className="space-y-4" aria-label="Install platform form">
        <div>
          <label
            htmlFor="baseUrl"
            className="block text-sm font-medium text-gray-700"
          >
            Base URL
          </label>
          <input
            id="baseUrl"
            type="url"
            required
            value={baseUrl}
            onChange={(e) => setBaseUrl(e.target.value)}
            placeholder={defaultBaseUrlFor(descriptor.kind)}
            className="mt-1 w-full px-3 py-2 border border-gray-300 rounded-md text-sm"
          />
        </div>

        <div>
          <label
            htmlFor="externalId"
            className="block text-sm font-medium text-gray-700"
          >
            Owner / organization (optional)
          </label>
          <input
            id="externalId"
            type="text"
            value={externalId}
            onChange={(e) => setExternalId(e.target.value)}
            placeholder="organization or user account"
            className="mt-1 w-full px-3 py-2 border border-gray-300 rounded-md text-sm"
          />
        </div>

        <div>
          <label
            htmlFor="credential"
            className="block text-sm font-medium text-gray-700"
          >
            Personal access token
          </label>
          <input
            id="credential"
            type="password"
            required
            autoComplete="off"
            value={credential}
            onChange={(e) => setCredential(e.target.value)}
            className="mt-1 w-full px-3 py-2 border border-gray-300 rounded-md text-sm font-mono"
          />
          <p className="mt-1 text-xs text-gray-500">
            We store this in the secret cabinet, encrypted at rest. The form
            clears the value after submit.
          </p>
        </div>

        {hint !== null && (
          <div role="alert" className="p-3 bg-red-50 text-red-700 text-sm rounded">
            {hint}
          </div>
        )}

        {success !== null && (
          <div role="status" className="p-3 bg-green-50 text-green-700 text-sm rounded">
            {success}
          </div>
        )}

        <div className="flex gap-2">
          <button
            type="submit"
            disabled={submitting}
            className="px-4 py-2 bg-blue-600 text-white rounded-md text-sm hover:bg-blue-700 disabled:bg-blue-300"
          >
            {submitting ? 'Connecting…' : `Connect ${descriptor.displayName}`}
          </button>
          <button
            type="button"
            onClick={() => navigate('/onboarding/platforms')}
            className="px-4 py-2 border border-gray-300 rounded-md text-sm text-gray-700 hover:bg-gray-50"
          >
            Cancel
          </button>
        </div>
      </form>
    </div>
  );
}

function ComingSoon({
  descriptor,
}: {
  descriptor: PlatformDescriptor;
}): JSX.Element {
  return (
    <div className="max-w-xl">
      <header className="mb-4">
        <h1 className="text-2xl font-semibold text-gray-900">
          {descriptor.displayName}
        </h1>
      </header>
      <div className="p-4 bg-gray-50 border border-gray-200 rounded">
        <p className="text-sm text-gray-700">
          {descriptor.displayName} support is on the roadmap. The capability
          matrix already encodes its expected feature set so the picker can
          render it now; the driver itself ships in a later wave.
        </p>
      </div>
    </div>
  );
}

function GitHubAppInstall({
  descriptor,
}: {
  descriptor: PlatformDescriptor;
}): JSX.Element {
  return (
    <div className="max-w-xl">
      <header className="mb-4">
        <h1 className="text-2xl font-semibold text-gray-900">
          Connect {descriptor.displayName}
        </h1>
        <p className="mt-1 text-sm text-gray-600">
          GitHub uses an App-style installation. The button below will redirect
          you to GitHub to install the Tamma app on the org or repos you choose;
          you'll come back to Tamma when it's done.
        </p>
      </header>
      <a
        href={GITHUB_APP_INSTALL_PATH}
        className="inline-block px-4 py-2 bg-gray-900 text-white rounded-md text-sm hover:bg-gray-800"
      >
        Install Tamma on GitHub
      </a>
    </div>
  );
}

function defaultBaseUrlFor(kind: PlatformKind): string {
  switch (kind) {
    case 'GitLab':
      return 'https://gitlab.com';
    case 'Gitea':
    case 'Forgejo':
      return '';
    case 'GitHub':
      return 'https://api.github.com';
    default:
      return '';
  }
}
