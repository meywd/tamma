
import { createBrowserRouter } from 'react-router-dom';
import { AppLayout } from './components/layout/AppLayout.js';
import { KnowledgeBaseDashboard } from './pages/knowledge-base/KnowledgeBaseDashboard.js';
import { AgentsPage } from './pages/settings/AgentsPage.js';
import { PhaseRolePage } from './pages/settings/PhaseRolePage.js';
import { SecurityPage } from './pages/settings/SecurityPage.js';
import { ProviderHealthPage } from './pages/settings/ProviderHealthPage.js';
import { BudgetPage } from './pages/settings/BudgetPage.js';
import { PromptsPage } from './pages/settings/PromptsPage.js';
import { AdminGuard } from './guards/AdminGuard.js';
import { AdminLayout } from './pages/admin/AdminLayout.js';

export const router = createBrowserRouter([
  {
    element: <AppLayout />,
    children: [
      { path: '/', element: <KnowledgeBaseDashboard /> },
      { path: '/settings/agents', element: <AgentsPage /> },
      { path: '/settings/phases', element: <PhaseRolePage /> },
      { path: '/settings/security', element: <SecurityPage /> },
      { path: '/settings/health', element: <ProviderHealthPage /> },
      { path: '/settings/budget', element: <BudgetPage /> },
      { path: '/settings/prompts', element: <PromptsPage /> },
      {
        path: '/admin',
        element: (
          <AdminGuard>
            <AdminLayout />
          </AdminGuard>
        ),
      },
    ],
  },
]);
