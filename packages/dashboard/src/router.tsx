
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
import { AdminGuard } from './guards/AdminGuard.js';
import { AuthGuard } from './guards/AuthGuard.js';
import { AdminErrorBoundary } from './pages/admin/AdminErrorBoundary.js';
import { LoadingSpinner } from './components/common/LoadingSpinner.js';
import { LoginPage } from './pages/LoginPage.js';
import { AccountPage } from './pages/AccountPage.js';
import { MyApiKeysPage } from './pages/MyApiKeysPage.js';

const AdminLayout = React.lazy(() =>
  import('./pages/admin/AdminLayout.js').then((m) => ({ default: m.AdminLayout })),
);

export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
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
    ],
  },
]);
