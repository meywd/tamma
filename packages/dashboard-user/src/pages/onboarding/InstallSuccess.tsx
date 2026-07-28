/**
 * InstallSuccess — /onboarding/success (Story 45-2 AC3).
 *
 * Terminal state of the GitHub App install flow. The API's install callback
 * (Endpoints/GitHubEndpoints.cs — Callback) redirects here with:
 *   - no query params, when the installation was linked to the caller's
 *     tenant, or
 *   - `?orphan=1&installation_id=<id>` when the install completed without a
 *     Tamma session (Marketplace-first install): the row was persisted
 *     unlinked so the user can sign in and claim it.
 * That is the complete redirect contract — nothing else is appended.
 */

import type { JSX } from 'react';
import { Link, useSearchParams } from 'react-router-dom';

export function InstallSuccess(): JSX.Element {
  const [params] = useSearchParams();
  const orphan = params.get('orphan') === '1';
  const installationId = params.get('installation_id');

  return (
    <div className="max-w-md mx-auto mt-16 p-6 bg-white rounded-lg shadow-sm border border-gray-200 text-center">
      <p className="text-4xl mb-3" aria-hidden="true">
        &#10003;
      </p>
      <h1 className="text-lg font-medium text-gray-900">GitHub App installed</h1>
      {orphan ? (
        <p className="mt-2 text-sm text-gray-500">
          The installation{installationId ? ` (#${installationId})` : ''} was recorded but is not
          yet linked to an organization. It will be connected to your account from the platforms
          page.
        </p>
      ) : (
        <p className="mt-2 text-sm text-gray-500">
          The installation is linked to your organization. You can review connected platforms and
          repositories now.
        </p>
      )}
      <Link
        to="/settings/platforms"
        className="mt-4 inline-block px-4 py-2 text-sm font-medium text-white bg-gray-900 rounded-md"
      >
        View connected platforms
      </Link>
    </div>
  );
}
