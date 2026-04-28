/**
 * Step 4 — Review installed repos + next steps.
 *
 * Shows the list of installations linked to the active tenant with the
 * repos GitHub granted access to. Suspended installations get a banner.
 *
 * Outbound paths from this step:
 *   - "Go to dashboard" — exit the wizard, land on /account.
 *   - "Disconnect" — opens GitHub's installation settings page (real
 *     un-install lives on github.com; we don't risk the
 *     destructive-action UX of deleting on their behalf).
 *   - "Install on another org" — same as the connect step's button.
 */

import { useNavigate } from 'react-router-dom';
import type {
  OnboardingInstallation,
  OnboardingStatus,
} from '../../services/onboarding/onboarding-api-client.js';

import type { JSX } from "react";

interface ReviewReposStepProps {
  status: OnboardingStatus;
  installUrl: string;
  onRefresh: () => void;
}

export function ReviewReposStep({
  status,
  installUrl,
  onRefresh,
}: ReviewReposStepProps): JSX.Element {
  const navigate = useNavigate();
  return (
    <div className="space-y-5">
      <div className="rounded-md bg-emerald-900/30 border border-emerald-700/50 p-3 text-sm text-emerald-200">
        Tamma is now connected to {status.installationCount}{' '}
        {status.installationCount === 1 ? 'installation' : 'installations'}.
        Webhooks will start flowing as soon as activity happens on the
        repos below.
      </div>

      <ul className="space-y-3">
        {status.installations.map((inst) => (
          <InstallationCard
            key={inst.installationId}
            installation={inst}
            onRefresh={onRefresh}
          />
        ))}
      </ul>

      <div className="bg-slate-800/40 border border-slate-700 rounded-md p-4 space-y-3">
        <h3 className="text-sm font-semibold text-slate-200">Next steps</h3>
        <ul className="text-sm text-slate-400 space-y-1.5 list-disc list-inside pl-1">
          <li>
            <button
              type="button"
              onClick={() => navigate('/settings/agents')}
              className="text-blue-400 hover:text-blue-300 underline-offset-4 hover:underline"
            >
              Configure your AI providers
            </button>{' '}
            — pick a default model and set per-role overrides.
          </li>
          <li>
            <button
              type="button"
              onClick={() => navigate('/settings/budget')}
              className="text-blue-400 hover:text-blue-300 underline-offset-4 hover:underline"
            >
              Set a monthly budget
            </button>{' '}
            — Tamma will stop spending when the cap is hit.
          </li>
          <li>
            Open an issue in any connected repo and assign it to the Tamma
            bot to trigger your first run.
          </li>
        </ul>
      </div>

      <div className="flex flex-wrap gap-3">
        <button
          type="button"
          onClick={() => window.location.assign(installUrl)}
          className="px-4 py-2 text-sm font-medium text-slate-100 bg-slate-800 hover:bg-slate-700 border border-slate-700 rounded-md"
        >
          Install on another organization
        </button>
      </div>
    </div>
  );
}

interface InstallationCardProps {
  installation: OnboardingInstallation;
  onRefresh: () => void;
}

function InstallationCard({ installation, onRefresh }: InstallationCardProps): JSX.Element {
  const settingsUrl = `https://github.com/${
    installation.accountType === 'Organization' ? 'organizations/' : ''
  }${installation.accountLogin}/settings/installations/${installation.installationId}`;

  return (
    <li className="bg-slate-900/60 border border-slate-700 rounded-lg p-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="font-mono text-sm font-semibold text-slate-100">
              {installation.accountLogin}
            </span>
            <span className="text-[10px] uppercase tracking-wider text-slate-500 bg-slate-800 px-1.5 py-0.5 rounded">
              {installation.accountType}
            </span>
            {installation.suspended && (
              <span className="text-[10px] uppercase tracking-wider text-amber-300 bg-amber-900/40 border border-amber-700/50 px-1.5 py-0.5 rounded">
                Suspended
              </span>
            )}
          </div>
          <div className="text-xs text-slate-500 mt-0.5">
            Installation #{installation.installationId} · {installation.repoCount}{' '}
            {installation.repoCount === 1 ? 'repository' : 'repositories'}
          </div>
        </div>
        <a
          href={settingsUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="shrink-0 text-xs text-slate-400 hover:text-slate-200 underline-offset-4 hover:underline"
          onClick={() => {
            // After the user revokes/uninstalls on GitHub, they'll typically
            // tab back; refresh as a safety net so the wizard reflects
            // the new state without a manual page reload.
            setTimeout(onRefresh, 1500);
          }}
        >
          Manage on GitHub →
        </a>
      </div>

      {installation.repos.length > 0 && (
        <ul className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-1.5 text-xs">
          {installation.repos.map((repo) => (
            <li
              key={repo.repoId}
              className="font-mono text-slate-300 truncate"
              title={repo.fullName}
            >
              <span className="text-slate-500">›</span> {repo.fullName}
            </li>
          ))}
        </ul>
      )}
      {installation.repoCount > installation.repos.length && (
        <div className="mt-2 text-xs text-slate-500">
          + {installation.repoCount - installation.repos.length} more not shown
        </div>
      )}
    </li>
  );
}
