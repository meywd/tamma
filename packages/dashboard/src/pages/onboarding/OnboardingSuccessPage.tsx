/**
 * `/onboarding/success`
 *
 * Landing page after `GET /api/github/callback` redirects on success.
 * Two arrival shapes:
 *
 *   1. Linked install (callback persisted with TenantId set):
 *      `/onboarding/success` (no query params) — show "Installation
 *      complete" + auto-redirect to `/onboarding` so the wizard's
 *      polling fetches the now-up-to-date status and renders the
 *      review-repos step.
 *
 *   2. Orphan install (callback persisted with TenantId null because
 *      the user wasn't authenticated when GitHub bounced them back):
 *      `/onboarding/success?orphan=1&installation_id=<id>` — show a
 *      sign-in prompt; once signed in, the user re-runs the install and
 *      the next callback links the install to their tenant. We forward
 *      the orphan id back into `/onboarding` so the connect step can
 *      surface it as a hint.
 */

import { useEffect } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { OnboardingShell } from '../../components/onboarding/OnboardingShell.js';
import { LoadingSpinner } from '../../components/common/LoadingSpinner.js';

export function OnboardingSuccessPage(): JSX.Element {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const isOrphan = params.get('orphan') === '1';
  const installationId = params.get('installation_id');

  useEffect(() => {
    if (isOrphan) return;
    // Auto-bounce to the wizard after a short pause so the user sees the
    // confirmation; the wizard's polling hook then renders the
    // review-repos step from the live status.
    const handle = setTimeout(() => {
      navigate('/onboarding', { replace: true });
    }, 1200);
    return () => clearTimeout(handle);
  }, [isOrphan, navigate]);

  if (isOrphan) {
    const claimQs = installationId
      ? `?orphan=1&installation_id=${encodeURIComponent(installationId)}`
      : '';
    return (
      <OnboardingShell
        eyebrow="Almost there"
        title="Sign in to claim your installation"
        subtitle={
          <>
            We received installation{' '}
            {installationId ? (
              <code className="font-mono text-slate-200">{installationId}</code>
            ) : (
              'one'
            )}{' '}
            from GitHub but couldn't link it to a Tamma account
            automatically.
          </>
        }
        footer={
          <>
            <a
              href="/login"
              className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-500 rounded-md"
            >
              Sign in
            </a>
            <button
              type="button"
              onClick={() => navigate(`/onboarding${claimQs}`, { replace: true })}
              className="text-sm text-slate-400 hover:text-slate-200"
            >
              I'm already signed in
            </button>
          </>
        }
      >
        <p className="text-sm text-slate-300">
          Sign in (or create an account) and we'll bind this installation
          to your active organization on the next install attempt.
        </p>
      </OnboardingShell>
    );
  }

  return (
    <OnboardingShell
      eyebrow="Installation complete"
      title="GitHub is connected"
      subtitle="Hold tight while we finish wiring things up."
    >
      <div className="flex flex-col items-center py-6 gap-4">
        <LoadingSpinner size="lg" />
        <div className="text-sm text-slate-400">
          Redirecting to your dashboard…
        </div>
      </div>
    </OnboardingShell>
  );
}
