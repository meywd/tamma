/**
 * InstallError — /onboarding/error (Story 45-2 AC3).
 *
 * Terminal failure state of the GitHub App install flow. The API's install
 * callback (Endpoints/GitHubEndpoints.cs — Callback) redirects here with
 * `?reason=<snake_case_code>` — either the router service's ErrorReason or
 * the literal `server_error` when the callback threw. That is the complete
 * redirect contract.
 */

import type { JSX } from 'react';
import { Link, useSearchParams } from 'react-router-dom';

export function InstallError(): JSX.Element {
  const [params] = useSearchParams();
  const reason = params.get('reason');

  return (
    <div className="max-w-md mx-auto mt-16 p-6 bg-white rounded-lg shadow-sm border border-gray-200 text-center">
      <p className="text-4xl mb-3" aria-hidden="true">
        &#9888;
      </p>
      <h1 className="text-lg font-medium text-gray-900">GitHub App install failed</h1>
      <p className="mt-2 text-sm text-gray-500">
        The installation could not be completed
        {reason ? (
          <>
            {' '}
            (reason: <span className="font-mono">{reason}</span>)
          </>
        ) : null}
        . You can try connecting the platform again.
      </p>
      <Link
        to="/onboarding/platforms"
        className="mt-4 inline-block px-4 py-2 text-sm font-medium text-white bg-gray-900 rounded-md"
      >
        Back to platform setup
      </Link>
    </div>
  );
}
