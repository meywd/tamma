/**
 * App — router tree for the user-facing dashboard SPA.
 *
 *   /login                                public
 *   /register                             public
 *   /verify-email                         public (auto-verifies ?token=)
 *   /verify                               public ALIAS of /verify-email — the API
 *                                         emails this path (AuthEndpoints); the
 *                                         alias mounts the same element so the
 *                                         ?token= query is preserved (45-2 D1/D2)
 *   /forgot-password                      public (password-reset request, 45-3)
 *   /reset-password                       public (password-reset confirm, ?token=, 45-3)
 *   /invites/accept                       public (?token=; branches on auth itself —
 *                                         the accept endpoint needs a session but an
 *                                         invitee may have no account, 45-3)
 *   /invites/pending                      public (?inviteId=; informational, 45-3)
 *   /                                     AuthGuard → AppLayout → DashboardHome
 *   /alerts                               AuthGuard → AppLayout → TenantAlertFeed
 *   /settings/billing                     AuthGuard → AppLayout → PlanPricingPage
 *   /settings/alerts                      AuthGuard → TenantAdminGuard → AppLayout → TenantAlertChannels
 *   /onboarding                           AuthGuard → redirect → /onboarding/platforms (45-2 AC4)
 *   /onboarding/platforms                 AuthGuard → TenantAdminGuard → AppLayout → PlatformPicker          (Story 31-9)
 *   /onboarding/platforms/:kind/install   AuthGuard → TenantAdminGuard → AppLayout → PlatformInstallForm     (Story 31-9)
 *   /onboarding/success                   AuthGuard → AppLayout → InstallSuccess (GitHub install callback, 45-2 AC3)
 *   /onboarding/error                     AuthGuard → AppLayout → InstallError   (GitHub install callback, 45-2 AC3)
 *   /settings/platforms                   AuthGuard → TenantAdminGuard → AppLayout → ConnectedPlatforms      (Story 31-9)
 *   *                                     NotFoundPage — in the shell when signed in,
 *                                         standalone (NO login redirect) when anonymous
 *
 * Catch-all note (45-2 AC5, deviating from the story's "declare it twice"):
 * two `path="*"` branches rank identically in React Router, so only the
 * earlier-declared one can ever match — declaring both is dead code. Instead
 * ONE catch-all outside the guard renders auth-aware: inside AppLayout for a
 * signed-in user, standalone for an anonymous one (never bouncing through
 * /login?redirect=<garbage>). Both behaviours are pinned in App.test.tsx.
 */

import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { AuthProvider, useAuth } from './hooks/useAuth';
import { AuthGuard } from './guards/AuthGuard';
import { TenantAdminGuard } from './guards/TenantAdminGuard';
import { AppLayout } from './layouts/AppLayout';
import { LoginPage } from './pages/auth/LoginPage';
import { RegisterPage } from './pages/auth/RegisterPage';
import { VerifyEmailPage } from './pages/auth/VerifyEmailPage';
import { ForgotPasswordPage } from './pages/auth/ForgotPasswordPage';
import { ResetPasswordPage } from './pages/auth/ResetPasswordPage';
import { InviteAcceptPage } from './pages/invites/InviteAcceptPage';
import { InvitePendingPage } from './pages/invites/InvitePendingPage';
import { DashboardHome } from './pages/DashboardHome';
import { TenantAlertFeed } from './pages/alerts/TenantAlertFeed';
import { TenantAlertChannels } from './pages/alerts/TenantAlertChannels';
import { PlatformPicker } from './pages/onboarding/PlatformPicker';
import { PlatformInstallForm } from './pages/onboarding/PlatformInstallForm';
import { InstallSuccess } from './pages/onboarding/InstallSuccess';
import { InstallError } from './pages/onboarding/InstallError';
import { ConnectedPlatforms } from './pages/settings/ConnectedPlatforms';
// Story 34-9 — tenant Plan & Pricing page.
import { PlanPricingPage } from './pages/settings/PlanPricingPage';
import { NotFoundPage } from './pages/NotFoundPage';

import type { JSX } from 'react';

/**
 * Every concrete path the router declares (parameterized paths carry a sample
 * value). App.test.tsx renders each entry and asserts something appears, and
 * AppLayout.test.tsx cross-checks that every sidebar link is in this list —
 * the two pins that stop "six emailed URLs render a blank pane" recurring.
 * Keep this in lockstep with the <Routes> tree below.
 */
export const ROUTE_PATHS: string[] = [
  '/login',
  '/register',
  '/verify-email',
  '/verify',
  '/forgot-password',
  '/reset-password',
  '/invites/accept',
  '/invites/pending',
  '/',
  '/alerts',
  '/settings/billing',
  '/settings/alerts',
  '/onboarding',
  '/onboarding/platforms',
  '/onboarding/platforms/github/install',
  '/onboarding/success',
  '/onboarding/error',
  '/settings/platforms',
];

/** Auth-aware catch-all — see the header comment. */
function NotFoundRoute(): JSX.Element {
  const { user, loading } = useAuth();
  if (loading) {
    return (
      <div
        role="status"
        aria-live="polite"
        className="min-h-screen flex items-center justify-center text-gray-500"
      >
        Loading...
      </div>
    );
  }
  if (user !== null) {
    return (
      <AppLayout>
        <NotFoundPage />
      </AppLayout>
    );
  }
  return <NotFoundPage />;
}

export function App(): JSX.Element {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/register" element={<RegisterPage />} />
          {/* Alias pair, NOT a redirect: a <Navigate> would drop ?token=
              unless rebuilt by hand; mounting the element twice keeps the
              query untouched on both paths (45-2 D1). */}
          <Route path="/verify-email" element={<VerifyEmailPage />} />
          <Route path="/verify" element={<VerifyEmailPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
          <Route path="/invites/accept" element={<InviteAcceptPage />} />
          <Route path="/invites/pending" element={<InvitePendingPage />} />
          <Route
            element={
              <AuthGuard>
                <AppLayout />
              </AuthGuard>
            }
          >
            <Route path="/" element={<DashboardHome />} />
            <Route path="/alerts" element={<TenantAlertFeed />} />
            {/* Story 34-9 — Plan & Pricing. Rendered for all members; the page
                itself gates mutations (member = read-only) so members can VIEW. */}
            <Route path="/settings/billing" element={<PlanPricingPage />} />
            <Route
              path="/settings/alerts"
              element={
                <TenantAdminGuard>
                  <TenantAlertChannels />
                </TenantAdminGuard>
              }
            />
            {/* /onboarding is a redirect (not an alias): there is no query
                string to preserve and /onboarding/platforms is the real first
                step (45-2 AC4). */}
            <Route
              path="/onboarding"
              element={<Navigate to="/onboarding/platforms" replace />}
            />
            <Route
              path="/onboarding/platforms"
              element={
                <TenantAdminGuard>
                  <PlatformPicker />
                </TenantAdminGuard>
              }
            />
            <Route
              path="/onboarding/platforms/:kind/install"
              element={
                <TenantAdminGuard>
                  <PlatformInstallForm />
                </TenantAdminGuard>
              }
            />
            {/* GitHub App install callback terminal states (45-2 AC3). Inside
                the guard: the orphan-install flow WANTS a sign-in so the
                installation can be claimed, and AuthGuard preserves the full
                path + query through /login?redirect=. */}
            <Route path="/onboarding/success" element={<InstallSuccess />} />
            <Route path="/onboarding/error" element={<InstallError />} />
            <Route
              path="/settings/platforms"
              element={
                <TenantAdminGuard>
                  <ConnectedPlatforms />
                </TenantAdminGuard>
              }
            />
          </Route>
          {/* Catch-all — single, auth-aware (see header). */}
          <Route path="*" element={<NotFoundRoute />} />
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  );
}
