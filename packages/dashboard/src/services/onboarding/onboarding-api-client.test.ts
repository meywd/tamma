import { describe, expect, it } from 'vitest';
import { deriveStep, type OnboardingStatus } from './onboarding-api-client.js';

const baseStatus: OnboardingStatus = {
  emailVerified: false,
  hasOrg: false,
  tenantId: null,
  hasInstallation: false,
  installationCount: 0,
  installations: [],
};

describe('deriveStep', () => {
  it('returns verify-email when email is unverified', () => {
    expect(deriveStep(baseStatus)).toBe('verify-email');
  });

  it('returns verify-email even when later steps look ready', () => {
    // Defensive: if the status payload says emailVerified=false but
    // somehow has an installation, we still gate on email — the rest
    // of the pipeline is built on a verified email.
    expect(
      deriveStep({
        ...baseStatus,
        hasOrg: true,
        hasInstallation: true,
        installationCount: 1,
      }),
    ).toBe('verify-email');
  });

  it('returns create-org when email verified but no tenant', () => {
    expect(deriveStep({ ...baseStatus, emailVerified: true })).toBe('create-org');
  });

  it('returns connect-github when email + org are present', () => {
    expect(
      deriveStep({
        ...baseStatus,
        emailVerified: true,
        hasOrg: true,
        tenantId: 't1',
      }),
    ).toBe('connect-github');
  });

  it('returns review-repos when an installation is linked', () => {
    expect(
      deriveStep({
        ...baseStatus,
        emailVerified: true,
        hasOrg: true,
        tenantId: 't1',
        hasInstallation: true,
        installationCount: 1,
        installations: [
          {
            installationId: 99,
            accountLogin: 'acme',
            accountType: 'Organization',
            suspended: false,
            repoCount: 2,
            repos: [
              { repoId: 1, fullName: 'acme/api' },
              { repoId: 2, fullName: 'acme/web' },
            ],
          },
        ],
      }),
    ).toBe('review-repos');
  });

  it('treats a fully-suspended install as connect-github (not review)', () => {
    // The backend already drops `hasInstallation` to false for suspended
    // installs; this test guards against a future regression where the
    // dashboard mistakenly looks at installationCount > 0 to decide.
    expect(
      deriveStep({
        ...baseStatus,
        emailVerified: true,
        hasOrg: true,
        tenantId: 't1',
        hasInstallation: false, // backend signal
        installationCount: 1,
        installations: [
          {
            installationId: 99,
            accountLogin: 'acme',
            accountType: 'Organization',
            suspended: true,
            repoCount: 0,
            repos: [],
          },
        ],
      }),
    ).toBe('connect-github');
  });
});
