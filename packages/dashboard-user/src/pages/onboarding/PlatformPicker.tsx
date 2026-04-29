/**
 * PlatformPicker — /onboarding/platforms
 *
 * Story 31-9 — first step of the onboarding picker. Lists every
 * `PlatformKind` from the backend's static capability matrix and
 * marks each as "available" (driver registered) or "coming soon"
 * (driver not yet shipped or platform deferred per 31-11/31-12). On
 * select, navigates to the per-kind install form at
 * `/onboarding/platforms/:kind/install`.
 *
 * Available drivers today: Gitea (31-4). Adding a new driver via
 * keyed-DI on the backend automatically lights up its card in this
 * UI — no client-side change needed.
 */

import { useEffect, useState, type JSX } from 'react';
import { Link } from 'react-router-dom';
import {
  listSupportedPlatforms,
  type PlatformDescriptor,
} from '../../api/platforms';

export function PlatformPicker(): JSX.Element {
  const [platforms, setPlatforms] = useState<PlatformDescriptor[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const resp = await listSupportedPlatforms();
        if (!cancelled) {
          setPlatforms(resp.items);
          setLoading(false);
        }
      } catch (err) {
        if (!cancelled) {
          setError(
            err instanceof Error ? err.message : 'Failed to load platforms',
          );
          setLoading(false);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  if (loading) {
    return (
      <div className="text-sm text-gray-500" role="status">
        Loading platforms…
      </div>
    );
  }

  if (error !== null) {
    return (
      <div role="alert" className="p-4 bg-red-50 text-red-700 rounded">
        {error}
      </div>
    );
  }

  return (
    <div>
      <header className="mb-6">
        <h1 className="text-2xl font-semibold text-gray-900">
          Connect a Git platform
        </h1>
        <p className="mt-1 text-sm text-gray-600">
          Choose where Tamma should orchestrate your repositories. You can
          connect more than one platform; the first one becomes your primary.
        </p>
      </header>

      <ul className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
        {platforms.map((p) => (
          <PlatformCard key={p.kind} platform={p} />
        ))}
      </ul>
    </div>
  );
}

interface PlatformCardProps {
  platform: PlatformDescriptor;
}

function PlatformCard({ platform }: PlatformCardProps): JSX.Element {
  const cardClass = platform.available
    ? 'block p-4 bg-white border border-gray-200 rounded-lg shadow-sm hover:border-blue-400 hover:shadow-md transition'
    : 'block p-4 bg-gray-50 border border-gray-200 rounded-lg opacity-70 cursor-not-allowed';

  const content = (
    <div className="flex items-start justify-between">
      <div>
        <h2 className="text-base font-medium text-gray-900">
          {platform.displayName}
        </h2>
        <p className="mt-1 text-xs text-gray-500">
          {platform.capabilities.length} capabilities
        </p>
      </div>
      {platform.available ? (
        <span className="text-xs font-medium text-blue-600">Connect →</span>
      ) : (
        <span
          className="text-xs font-medium text-gray-500"
          title="A driver for this platform is on the roadmap; check back after the next wave."
        >
          Coming soon
        </span>
      )}
    </div>
  );

  if (!platform.available) {
    return (
      <li>
        <div
          aria-disabled="true"
          aria-label={`${platform.displayName} — coming soon`}
          className={cardClass}
        >
          {content}
        </div>
      </li>
    );
  }

  return (
    <li>
      <Link
        to={`/onboarding/platforms/${platform.kind}/install`}
        aria-label={`Connect ${platform.displayName}`}
        className={cardClass}
      >
        {content}
      </Link>
    </li>
  );
}
