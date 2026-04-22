/**
 * Onboarding API Client (Story 18-4)
 *
 * Talks to the C# `/api/v1/onboarding/*` endpoints in
 * `apps/tamma-elsa/src/Tamma.Api/Endpoints/OnboardingEndpoints.cs`.
 *
 * The client shape matches the `OnboardingStatusResponse` record on the
 * backend exactly — keep them in sync.
 */

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api';

async function fetchJSON<T>(url: string): Promise<T> {
  const r = await fetch(`${API_BASE}${url}`, { credentials: 'include' });
  if (!r.ok) {
    const err = new Error(`HTTP ${r.status}`);
    (err as Error & { status?: number }).status = r.status;
    throw err;
  }
  return r.json() as Promise<T>;
}

export interface OnboardingRepo {
  /** GitHub repo numeric id. */
  repoId: number;
  /** "owner/name" full slug. */
  fullName: string;
}

export interface OnboardingInstallation {
  /** GitHub-issued installation id. */
  installationId: number;
  /** Account login (org or user). */
  accountLogin: string;
  /** "User" | "Organization". */
  accountType: string;
  /** True when the install is suspended on GitHub. */
  suspended: boolean;
  /** Active repo count (deactivated repos excluded). */
  repoCount: number;
  /** Up to 20 most-recent active repos for preview. */
  repos: OnboardingRepo[];
}

export interface OnboardingStatus {
  /** Email verification — auto-true for GitHub-OAuth users. */
  emailVerified: boolean;
  /** True when the user has at least one tenant membership. */
  hasOrg: boolean;
  /** Active tenant id (may differ from JWT tid claim during invite-accept races). */
  tenantId: string | null;
  /** True when at least one non-suspended installation is linked to the active tenant. */
  hasInstallation: boolean;
  /** Total installation count, including suspended ones. */
  installationCount: number;
  /** Installations linked to the active tenant. */
  installations: OnboardingInstallation[];
}

export const onboardingApi = {
  /** Read the current onboarding state for the authenticated user. */
  getStatus: () => fetchJSON<OnboardingStatus>('/v1/onboarding/status'),

  /**
   * Build the absolute URL of the install-redirect endpoint. Returns a
   * URL the dashboard should navigate to (full page nav, NOT fetch) so
   * the browser follows the 302 to GitHub. We return a string instead of
   * triggering navigation so callers can decide between
   * `window.location.assign` (current tab) and `window.open` (new tab).
   */
  getInstallUrl: (): string => `${API_BASE}/v1/onboarding/install-github`,
};

/**
 * Wizard step derivation. The wizard renders a single visible card whose
 * content depends on which step is active. Step transitions are driven
 * entirely by `OnboardingStatus` so the wizard state is the API state —
 * no client-side step counter to drift out of sync.
 */
export type OnboardingStep =
  | 'verify-email'      // user must click verification link first
  | 'create-org'        // unusual today — register flow auto-creates a personal tenant
  | 'connect-github'    // dominant happy-path entry point
  | 'review-repos'      // success page — installation arrived, show repos
  | 'complete';         // wizard finished (transient — usually we redirect to dashboard)

export function deriveStep(status: OnboardingStatus): OnboardingStep {
  if (!status.emailVerified) return 'verify-email';
  if (!status.hasOrg) return 'create-org';
  if (!status.hasInstallation) return 'connect-github';
  return 'review-repos';
}
