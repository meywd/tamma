/**
 * Monitoring route table (Story 23-12).
 *
 * Exposes `monitoringRoutes` — an array of RouteObjects mounted inside the
 * authenticated AppLayout in `router.tsx`. Every page is lazy-loaded via
 * `React.lazy` (AC8) and gated behind `AdminGuard` (AC2 — admin/owner only),
 * so an Epic-23 page author only replaces the target page component; the route
 * wiring and RBAC are already in place.
 */

import React, { Suspense, type JSX } from 'react';
import type { RouteObject } from 'react-router-dom';
import { AdminGuard } from '../../guards/AdminGuard.js';
import { LoadingSpinner } from '../../components/common/LoadingSpinner.js';

const MonitoringOverviewPage = React.lazy(() =>
  import('./MonitoringOverviewPage.js').then((m) => ({ default: m.MonitoringOverviewPage })),
);
const SystemHealthPage = React.lazy(() =>
  import('./SystemHealthPage.js').then((m) => ({ default: m.SystemHealthPage })),
);
const AgentMonitorPage = React.lazy(() =>
  import('./AgentMonitorPage.js').then((m) => ({ default: m.AgentMonitorPage })),
);
const EventExplorerPage = React.lazy(() =>
  import('./EventExplorerPage.js').then((m) => ({ default: m.EventExplorerPage })),
);
const WorkflowMonitorPage = React.lazy(() =>
  import('./WorkflowMonitorPage.js').then((m) => ({ default: m.WorkflowMonitorPage })),
);
const ProviderDiagnosticsPage = React.lazy(() =>
  import('./ProviderDiagnosticsPage.js').then((m) => ({ default: m.ProviderDiagnosticsPage })),
);
const LogExplorerPage = React.lazy(() =>
  import('./LogExplorerPage.js').then((m) => ({ default: m.LogExplorerPage })),
);
const InfrastructureMonitorPage = React.lazy(() =>
  import('./InfrastructureMonitorPage.js').then((m) => ({ default: m.InfrastructureMonitorPage })),
);
const KnowledgeBaseMonitorPage = React.lazy(() =>
  import('./KnowledgeBaseMonitorPage.js').then((m) => ({ default: m.KnowledgeBaseMonitorPage })),
);
const ConfigAuditPage = React.lazy(() =>
  import('./ConfigAuditPage.js').then((m) => ({ default: m.ConfigAuditPage })),
);
const SecurityAuditPage = React.lazy(() =>
  import('./SecurityAuditPage.js').then((m) => ({ default: m.SecurityAuditPage })),
);

/** Wrap a lazy page in the admin RBAC guard + a Suspense fallback. */
function monitoringRoute(element: JSX.Element): JSX.Element {
  return (
    <AdminGuard>
      <Suspense
        fallback={
          <div className="flex min-h-[40vh] items-center justify-center">
            <LoadingSpinner size="lg" />
          </div>
        }
      >
        {element}
      </Suspense>
    </AdminGuard>
  );
}

export const monitoringRoutes: RouteObject[] = [
  { path: '/monitoring', element: monitoringRoute(<MonitoringOverviewPage />) },
  { path: '/monitoring/health', element: monitoringRoute(<SystemHealthPage />) },
  { path: '/monitoring/agents', element: monitoringRoute(<AgentMonitorPage />) },
  { path: '/monitoring/events', element: monitoringRoute(<EventExplorerPage />) },
  { path: '/monitoring/workflows', element: monitoringRoute(<WorkflowMonitorPage />) },
  { path: '/monitoring/providers', element: monitoringRoute(<ProviderDiagnosticsPage />) },
  { path: '/monitoring/logs', element: monitoringRoute(<LogExplorerPage />) },
  { path: '/monitoring/infrastructure', element: monitoringRoute(<InfrastructureMonitorPage />) },
  { path: '/monitoring/knowledge-base', element: monitoringRoute(<KnowledgeBaseMonitorPage />) },
  { path: '/monitoring/config', element: monitoringRoute(<ConfigAuditPage />) },
  { path: '/monitoring/security', element: monitoringRoute(<SecurityAuditPage />) },
];
