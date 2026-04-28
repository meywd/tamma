import type { JSX } from "react";
/**
 * Step 3 — Install Tamma GitHub App.
 *
 * The big "Install Tamma" button is a full-page navigate to the
 * `install-github` endpoint, which 302s to GitHub's app install page
 * (`https://github.com/apps/<slug>/installations/new?state=<jwt>`).
 *
 * After the user completes the install on GitHub:
 *   1. GitHub sends `installation.created` to our webhook (router
 *      persists the row + provisions API key + repos).
 *   2. GitHub redirects the user to `setup_url` configured on the App
 *      manifest (we point that at `/api/github/callback`, which
 *      re-binds the install to the active tenant if needed and
 *      redirects to `/onboarding/success`).
 *   3. While the user is at GitHub the wizard's polling hook sees
 *      `hasInstallation=true` on the next poll and auto-advances.
 *
 * Why a full-page nav instead of `window.open`:
 *   - GitHub's install page redirects back to `/onboarding/success`
 *     after install. A new-tab flow leaves the original tab stuck on
 *     the wizard (until the polling kicks in) and creates two stale
 *     sessions; the single-tab nav is the documented happy path.
 */

interface ConnectGitHubStepProps {
  installUrl: string;
  /** Set when the user landed back here from a callback that lacked a session. */
  orphanInstallationId: string | null;
  onRefresh: () => void;
}

export function ConnectGitHubStep({
  installUrl,
  orphanInstallationId,
  onRefresh,
}: ConnectGitHubStepProps): JSX.Element {
  const handleInstall = (): void => {
    // Full-page navigation — see the doc comment above for why this is
    // not `window.open`.
    window.location.assign(installUrl);
  };

  return (
    <div className="space-y-5">
      {orphanInstallationId !== null && (
        <div className="rounded-md bg-amber-900/30 border border-amber-700/50 p-3 text-sm text-amber-200">
          We received an installation (id{' '}
          <code className="font-mono">{orphanInstallationId}</code>) but
          couldn't link it to your account automatically. Re-run the
          install below — we'll bind it to your active organization.
        </div>
      )}

      <div className="space-y-3 text-sm text-slate-300">
        <p>The Tamma GitHub App needs permission to:</p>
        <ul className="list-disc list-inside space-y-1 text-slate-400 pl-2">
          <li>Read your repositories &amp; issues.</li>
          <li>Open pull requests, write check runs, comment on issues.</li>
          <li>Receive webhooks so Tamma reacts to events in real time.</li>
        </ul>
      </div>

      <button
        type="button"
        onClick={handleInstall}
        className="w-full inline-flex items-center justify-center gap-2 px-4 py-3 text-base font-semibold text-white bg-blue-600 hover:bg-blue-500 rounded-md transition-colors"
      >
        <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 20 20" aria-hidden="true">
          <path
            fillRule="evenodd"
            d="M10 0C4.477 0 0 4.484 0 10.017c0 4.425 2.865 8.18 6.839 9.504.5.092.682-.217.682-.483 0-.237-.008-.868-.013-1.703-2.782.605-3.369-1.343-3.369-1.343-.454-1.158-1.11-1.466-1.11-1.466-.908-.62.069-.608.069-.608 1.003.07 1.531 1.032 1.531 1.032.892 1.53 2.341 1.088 2.91.832.092-.647.35-1.088.636-1.338-2.22-.253-4.555-1.113-4.555-4.951 0-1.093.39-1.988 1.029-2.688-.103-.253-.446-1.272.098-2.65 0 0 .84-.27 2.75 1.026A9.564 9.564 0 0110 4.844c.85.004 1.705.115 2.504.337 1.909-1.296 2.747-1.027 2.747-1.027.546 1.379.203 2.398.1 2.651.64.7 1.028 1.595 1.028 2.688 0 3.848-2.339 4.695-4.566 4.942.359.31.678.921.678 1.856 0 1.338-.012 2.419-.012 2.747 0 .268.18.58.688.482A10.019 10.019 0 0020 10.017C20 4.484 15.522 0 10 0z"
            clipRule="evenodd"
          />
        </svg>
        Install Tamma on GitHub
      </button>

      <button
        type="button"
        onClick={onRefresh}
        className="w-full text-xs text-slate-400 hover:text-slate-200"
      >
        Already installed? Refresh status
      </button>
    </div>
  );
}
