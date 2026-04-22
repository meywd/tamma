/**
 * OnboardingPage — root of the Story 18-4 wizard.
 *
 * Polls `/api/v1/onboarding/status` and switches the visible card based
 * on the derived step. Internally always shows the OnboardingShell + a
 * stepper above the card.
 *
 * Route: `/onboarding` (gated by `AuthGuard` like every other auth'd page).
 *
 * Sub-routes:
 *   /onboarding/success — landing after GitHub redirects back (the
 *      install callback rewrites here on success).
 *   /onboarding/error — landing for failed callbacks.
 *   /onboarding/repos — alias of /onboarding when at the review-repos step.
 */

import { useNavigate, useSearchParams } from 'react-router-dom';
import { useOnboardingStatus } from '../../hooks/onboarding/useOnboardingStatus.js';
import {
  deriveStep,
  onboardingApi,
} from '../../services/onboarding/onboarding-api-client.js';
import { OnboardingShell } from '../../components/onboarding/OnboardingShell.js';
import { OnboardingStepper } from '../../components/onboarding/OnboardingStepper.js';
import { LoadingSpinner } from '../../components/common/LoadingSpinner.js';
import { ConnectGitHubStep } from '../../components/onboarding/ConnectGitHubStep.js';
import { VerifyEmailStep } from '../../components/onboarding/VerifyEmailStep.js';
import { CreateOrgStep } from '../../components/onboarding/CreateOrgStep.js';
import { ReviewReposStep } from '../../components/onboarding/ReviewReposStep.js';

export function OnboardingPage(): JSX.Element {
  const { loading, status, error, refresh } = useOnboardingStatus();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  // The install callback can append `?orphan=1&installation_id=...` when
  // the install arrived without a Tamma session — we surface that as a
  // banner inside the relevant step.
  const orphanInstallationId = params.get('orphan') === '1'
    ? params.get('installation_id')
    : null;

  if (loading && !status) {
    return (
      <OnboardingShell title="Setting things up..." eyebrow="Welcome">
        <div className="flex justify-center py-12">
          <LoadingSpinner size="lg" />
        </div>
      </OnboardingShell>
    );
  }

  if (error && !status) {
    return (
      <OnboardingShell
        title="Couldn't load your status"
        eyebrow="Something went wrong"
        footer={
          <button
            type="button"
            onClick={() => void refresh()}
            className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-500 rounded-md"
          >
            Try again
          </button>
        }
      >
        <p className="text-sm text-slate-300">{error}</p>
      </OnboardingShell>
    );
  }

  if (!status) {
    // Should be unreachable — loading=false + error=null implies status set.
    return (
      <OnboardingShell title="No status available" eyebrow="Unexpected">
        <p className="text-sm text-slate-300">
          Please reload the page or sign in again.
        </p>
      </OnboardingShell>
    );
  }

  const step = deriveStep(status);

  const handleSkip = (): void => {
    // Skipping the wizard parks the user on /account — they can return to
    // /onboarding any time via a future "Setup" entry point. We do NOT
    // persist a "skipped" flag because re-visiting is cheap and the
    // status endpoint is the source of truth.
    navigate('/account', { replace: true });
  };

  // Never render the standalone success page from here — the success
  // route owns that surface so a deep link works after refresh.
  return (
    <OnboardingShell
      eyebrow="Set up Tamma"
      title={titleForStep(step)}
      subtitle={subtitleForStep(step)}
      stepper={<OnboardingStepper current={step} />}
      footer={
        step !== 'review-repos' ? (
          <button
            type="button"
            onClick={handleSkip}
            className="text-sm text-slate-400 hover:text-slate-200 underline-offset-4 hover:underline"
          >
            Skip for now
          </button>
        ) : (
          <button
            type="button"
            onClick={() => navigate('/account')}
            className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-500 rounded-md"
          >
            Go to dashboard
          </button>
        )
      }
    >
      {step === 'verify-email' && (
        <VerifyEmailStep onRefresh={() => void refresh()} />
      )}
      {step === 'create-org' && <CreateOrgStep />}
      {step === 'connect-github' && (
        <ConnectGitHubStep
          installUrl={onboardingApi.getInstallUrl()}
          orphanInstallationId={orphanInstallationId}
          onRefresh={() => void refresh()}
        />
      )}
      {step === 'review-repos' && (
        <ReviewReposStep
          status={status}
          onRefresh={() => void refresh()}
          installUrl={onboardingApi.getInstallUrl()}
        />
      )}
    </OnboardingShell>
  );
}

function titleForStep(step: ReturnType<typeof deriveStep>): string {
  switch (step) {
    case 'verify-email':
      return 'Verify your email';
    case 'create-org':
      return 'Create your organization';
    case 'connect-github':
      return 'Connect Tamma to GitHub';
    case 'review-repos':
      return 'Installation complete';
    case 'complete':
      return "You're all set";
  }
}

function subtitleForStep(
  step: ReturnType<typeof deriveStep>,
): string | undefined {
  switch (step) {
    case 'verify-email':
      return 'Check your inbox for a verification link, then come back here.';
    case 'create-org':
      return 'Tamma needs an organization to scope your repositories and team.';
    case 'connect-github':
      return 'Install the Tamma GitHub App so we can read issues and open pull requests.';
    case 'review-repos':
      return 'Tamma is connected. Pick the next move below.';
    case 'complete':
      return undefined;
  }
}
