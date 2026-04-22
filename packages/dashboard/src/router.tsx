
import React, { Suspense } from 'react';
import { Navigate, createBrowserRouter } from 'react-router-dom';
import { AppLayout } from './components/layout/AppLayout.js';
import { KnowledgeBaseDashboard } from './pages/knowledge-base/KnowledgeBaseDashboard.js';
import { AgentsPage } from './pages/settings/AgentsPage.js';
import { PhaseRolePage } from './pages/settings/PhaseRolePage.js';
import { SecurityPage } from './pages/settings/SecurityPage.js';
import { ProviderHealthPage } from './pages/settings/ProviderHealthPage.js';
import { BudgetPage } from './pages/settings/BudgetPage.js';
import { PromptsPage } from './pages/settings/PromptsPage.js';
import { PromptsAdminPage } from './pages/admin/prompts/PromptsAdminPage.js';
// Story 28-11 — platform-admin tenant-status UX.
import { TenantsListPage } from './pages/admin/tenants/TenantsListPage.js';
import { TenantDetailPage } from './pages/admin/tenants/TenantDetailPage.js';
// Story 29-4 — platform-admin secrets management.
import { SecretsAdminPage } from './pages/admin/secrets/SecretsAdminPage.js';
// Story 29-5 — tenant-admin secrets management.
import { TenantSecretsPage } from './pages/secrets/TenantSecretsPage.js';
import { AdminGuard } from './guards/AdminGuard.js';
import { AuthGuard } from './guards/AuthGuard.js';
import { AdminErrorBoundary } from './pages/admin/AdminErrorBoundary.js';
import { LoadingSpinner } from './components/common/LoadingSpinner.js';
import { LoginPage } from './pages/LoginPage.js';
import { AccountPage } from './pages/AccountPage.js';
import { MyApiKeysPage } from './pages/MyApiKeysPage.js';
import { OrganizationLayout } from './pages/organization/OrganizationLayout.js';
import { TenantAdminGuard } from './guards/TenantAdminGuard.js';
// Story 18-4: onboarding wizard. Lives outside AppLayout because new
// users don't have a tenant yet and the wizard is a single-card focused
// flow (no sidebar / nav chrome).
import { OnboardingPage } from './pages/onboarding/OnboardingPage.js';
import { OnboardingSuccessPage } from './pages/onboarding/OnboardingSuccessPage.js';
import { OnboardingErrorPage } from './pages/onboarding/OnboardingErrorPage.js';

const AdminLayout = React.lazy(() =>
  import('./pages/admin/AdminLayout.js').then((m) => ({ default: m.AdminLayout })),
);

export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  // Story 18-4 — onboarding wizard. Auth-gated but rendered without
  // AppLayout so the user sees the focused single-card UX during setup.
  {
    path: '/onboarding',
    element: (
      <AuthGuard>
        <OnboardingPage />
      </AuthGuard>
    ),
  },
  {
    path: '/onboarding/repos',
    // Alias of /onboarding — the connect step's redirect chain lands on
    // /onboarding/repos historically. Render the same wizard; it picks
    // the right step from live status.
    element: (
      <AuthGuard>
        <OnboardingPage />
      </AuthGuard>
    ),
  },
  {
    path: '/onboarding/success',
    element: (
      <AuthGuard>
        <OnboardingSuccessPage />
      </AuthGuard>
    ),
  },
  {
    path: '/onboarding/error',
    // Error page must be reachable without auth — the callback that
    // routes here may have failed *because* the user isn't signed in
    // (`unknown_user` reason). Skipping the AuthGuard avoids a redirect
    // loop in that case.
    element: <OnboardingErrorPage />,
  },
  {
    element: (
      <AuthGuard>
        <AppLayout />
      </AuthGuard>
    ),
    children: [
      // Member routes (all authenticated users)
      { path: '/', element: <Navigate to="/account" replace /> },
      { path: '/account', element: <AccountPage /> },
      { path: '/keys', element: <MyApiKeysPage /> },
      // Tenant-admin routes (Story 18-8) — gated by TenantAdminGuard which
      // reads the caller's role inside their currently-active tenant from
      // /auth/me, NOT the platform role.
      {
        path: '/settings/organization',
        element: (
          <TenantAdminGuard>
            <OrganizationLayout />
          </TenantAdminGuard>
        ),
      },
      // Admin routes
      {
        path: '/dashboard',
        element: (
          <AdminGuard>
            <KnowledgeBaseDashboard />
          </AdminGuard>
        ),
      },
      {
        path: '/settings/agents',
        element: (
          <AdminGuard>
            <AgentsPage />
          </AdminGuard>
        ),
      },
      {
        path: '/settings/phases',
        element: (
          <AdminGuard>
            <PhaseRolePage />
          </AdminGuard>
        ),
      },
      {
        path: '/settings/security',
        element: (
          <AdminGuard>
            <SecurityPage />
          </AdminGuard>
        ),
      },
      {
        path: '/settings/health',
        element: (
          <AdminGuard>
            <ProviderHealthPage />
          </AdminGuard>
        ),
      },
      {
        path: '/settings/budget',
        element: (
          <AdminGuard>
            <BudgetPage />
          </AdminGuard>
        ),
      },
      {
        path: '/settings/prompts',
        element: (
          <AdminGuard>
            <PromptsPage />
          </AdminGuard>
        ),
      },
      {
        path: '/admin',
        element: (
          <AdminGuard>
            <AdminErrorBoundary>
              <Suspense fallback={<LoadingSpinner size="lg" />}>
                <AdminLayout />
              </Suspense>
            </AdminErrorBoundary>
          </AdminGuard>
        ),
      },
      // Story 27-4: platform-admin prompt-store management UI.
      {
        path: '/admin/prompts',
        element: (
          <AdminGuard>
            <PromptsAdminPage />
          </AdminGuard>
        ),
      },
      // Story 28-11: platform-admin tenant-status UX. List + detail for
      // every tenant with status badge, lifecycle events, and
      // state-gated destructive actions (retry / delete / force-delete).
      {
        path: '/admin/tenants',
        element: (
          <AdminGuard>
            <TenantsListPage />
          </AdminGuard>
        ),
      },
      {
        path: '/admin/tenants/:tenantId',
        element: (
          <AdminGuard>
            <TenantDetailPage />
          </AdminGuard>
        ),
      },
      // Story 29-4: platform-admin secret-management UI. Lists every
      // platform-scoped secret, lets owners create / rotate / retire
      // with reveal-once-on-create UX.
      {
        path: '/admin/secrets',
        element: (
          <AdminGuard>
            <SecretsAdminPage />
          </AdminGuard>
        ),
      },
      // Story 29-5: tenant-admin secret-management UI. Lists only the
      // caller's active tenant's secrets; gated by TenantAdminGuard
      // (admin or owner role in the active tenant).
      {
        path: '/settings/organization/secrets',
        element: (
          <TenantAdminGuard>
            <TenantSecretsPage />
          </TenantAdminGuard>
        ),
      },
    ],
  },
]);
