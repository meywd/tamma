/**
 * `/onboarding/error`
 *
 * Landing page after `GET /api/github/callback` fails. The callback
 * appends `?reason=<code>` so we can surface a friendly explanation
 * matched to the failure mode.
 *
 * Reason codes (from `InstallationRouterService.HandleCallbackAsync`):
 *   - `unknown_user` — JWT pointed at a deleted user.
 *   - `no_active_tenant` — user has no membership; needs an org first.
 *   - `tenant_not_found` — tenant id on the user record is stale.
 *   - `server_error` — exception thrown during link.
 *   - `missing_installation_id` / `invalid_installation_id` — bad query
 *     param from GitHub (extremely rare; usually a misconfigured
 *     callback URL).
 */

import { useNavigate, useSearchParams } from 'react-router-dom';
import { OnboardingShell } from '../../components/onboarding/OnboardingShell.js';

import type { JSX } from "react";

const REASON_COPY: Record<string, { title: string; body: string }> = {
  unknown_user: {
    title: 'Your session looks stale',
    body: 'We could not find a Tamma account matching your sign-in. Sign back in and try the install again.',
  },
  no_active_tenant: {
    title: 'No organization yet',
    body: 'Create or join an organization in Tamma first, then re-run the install so we can link the new GitHub installation to it.',
  },
  tenant_not_found: {
    title: 'Organization missing',
    body: "Your active organization could not be loaded — it may have been deleted. Pick a different organization and try again.",
  },
  server_error: {
    title: 'Something went wrong on our side',
    body: 'The install reached us but we hit an internal error linking it. The installation may already be visible on the next page; otherwise try again.',
  },
};

const FALLBACK = {
  title: 'Install could not be completed',
  body: 'GitHub returned a status we did not recognise. Please try the install again.',
};

export function OnboardingErrorPage(): JSX.Element {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const reason = params.get('reason') ?? '';
  const copy = REASON_COPY[reason] ?? FALLBACK;

  return (
    <OnboardingShell
      eyebrow="Install failed"
      title={copy.title}
      subtitle={copy.body}
      footer={
        <>
          <button
            type="button"
            onClick={() => navigate('/onboarding', { replace: true })}
            className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-500 rounded-md"
          >
            Back to setup
          </button>
          <a
            href="/login"
            className="text-sm text-slate-400 hover:text-slate-200"
          >
            Sign in again
          </a>
        </>
      }
    >
      {reason && (
        <div className="rounded-md bg-slate-800/50 border border-slate-700 p-3 text-xs text-slate-400 font-mono">
          reason: {reason}
        </div>
      )}
    </OnboardingShell>
  );
}
