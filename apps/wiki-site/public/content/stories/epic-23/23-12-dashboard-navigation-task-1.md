---
title: "Task 1: Sidebar Navigation & Route Registration"
sidebar:
  order: 230
---

**Story:** 23-12-dashboard-navigation
**Epic:** 23

## Task Description

Add a "Monitoring" navigation group to the dashboard Sidebar component and register React Router routes for all 10 monitoring pages. Routes are lazy-loaded via `React.lazy()` to keep the initial bundle size small.

## Acceptance Criteria

- Sidebar gains a "Monitoring" nav group with 10 entries between "Settings" and "Administration"
- Monitoring section only visible to admin/owner users (same guard as Settings)
- React Router routes added for all monitoring pages, lazy-loaded
- Routes wrapped in `AdminGuard` for access control
- All monitoring pages initially render a placeholder component until their story is implemented

## Implementation Details

### Technical Requirements

- [ ] Modify `packages/dashboard/src/components/layout/Sidebar.tsx`:
  - Add a new `Monitoring` group to `ADMIN_NAV_GROUPS` between Settings and Administration:
    ```typescript
    {
      label: 'Monitoring',
      items: [
        { to: '/monitoring/health', label: 'System Health' },
        { to: '/monitoring/agents', label: 'Agent Monitor' },
        { to: '/monitoring/events', label: 'Event Explorer' },
        { to: '/monitoring/workflows', label: 'Workflows' },
        { to: '/monitoring/providers', label: 'Providers' },
        { to: '/monitoring/logs', label: 'Logs' },
        { to: '/monitoring/infrastructure', label: 'Infrastructure' },
        { to: '/monitoring/knowledge-base', label: 'Knowledge Base' },
        { to: '/monitoring/config', label: 'Config Audit' },
        { to: '/monitoring/security', label: 'Security Audit' },
      ],
    },
    ```

- [ ] Create `packages/dashboard/src/pages/monitoring/index.tsx` with lazy imports:
  ```typescript
  import { lazy } from 'react';

  export const SystemHealthPage = lazy(() =>
    import('./SystemHealthPage.js').then((m) => ({ default: m.SystemHealthPage })),
  );
  export const AgentMonitorPage = lazy(() =>
    import('./AgentMonitorPage.js').then((m) => ({ default: m.AgentMonitorPage })),
  );
  export const EventExplorerPage = lazy(() =>
    import('./EventExplorerPage.js').then((m) => ({ default: m.EventExplorerPage })),
  );
  export const WorkflowMonitorPage = lazy(() =>
    import('./WorkflowMonitorPage.js').then((m) => ({ default: m.WorkflowMonitorPage })),
  );
  export const ProviderDiagnosticsPage = lazy(() =>
    import('./ProviderDiagnosticsPage.js').then((m) => ({ default: m.ProviderDiagnosticsPage })),
  );
  export const LogExplorerPage = lazy(() =>
    import('./LogExplorerPage.js').then((m) => ({ default: m.LogExplorerPage })),
  );
  export const InfrastructureMonitorPage = lazy(() =>
    import('./InfrastructureMonitorPage.js').then((m) => ({ default: m.InfrastructureMonitorPage })),
  );
  export const KnowledgeBaseMonitorPage = lazy(() =>
    import('./KnowledgeBaseMonitorPage.js').then((m) => ({ default: m.KnowledgeBaseMonitorPage })),
  );
  export const ConfigAuditPage = lazy(() =>
    import('./ConfigAuditPage.js').then((m) => ({ default: m.ConfigAuditPage })),
  );
  export const SecurityAuditPage = lazy(() =>
    import('./SecurityAuditPage.js').then((m) => ({ default: m.SecurityAuditPage })),
  );
  ```

- [ ] Create placeholder page components for each (until their story is implemented):
  ```typescript
  // packages/dashboard/src/pages/monitoring/SystemHealthPage.tsx (and 9 others)
  export function SystemHealthPage(): JSX.Element {
    return (
      <div className="p-6">
        <h1 className="text-2xl font-bold text-gray-900 mb-2">System Health</h1>
        <p className="text-gray-500">Coming soon.</p>
      </div>
    );
  }
  ```

- [ ] Modify `packages/dashboard/src/router.tsx`:
  - Add `Suspense` wrapper with loading fallback
  - Add monitoring routes inside the `AuthGuard > AppLayout` children:
    ```typescript
    import { Suspense } from 'react';
    import * as Monitoring from './pages/monitoring/index.js';

    // Inside children array, after admin route:
    {
      path: '/monitoring/health',
      element: (
        <AdminGuard>
          <Suspense fallback={<div className="p-6 text-gray-400">Loading...</div>}>
            <Monitoring.SystemHealthPage />
          </Suspense>
        </AdminGuard>
      ),
    },
    // ... repeat for all 10 monitoring routes
    ```

### Files to Create

- CREATE `packages/dashboard/src/pages/monitoring/index.tsx`
- CREATE `packages/dashboard/src/pages/monitoring/SystemHealthPage.tsx` (placeholder)
- CREATE `packages/dashboard/src/pages/monitoring/AgentMonitorPage.tsx` (placeholder)
- CREATE `packages/dashboard/src/pages/monitoring/EventExplorerPage.tsx` (placeholder)
- CREATE `packages/dashboard/src/pages/monitoring/WorkflowMonitorPage.tsx` (placeholder)
- CREATE `packages/dashboard/src/pages/monitoring/ProviderDiagnosticsPage.tsx` (placeholder)
- CREATE `packages/dashboard/src/pages/monitoring/LogExplorerPage.tsx` (placeholder)
- CREATE `packages/dashboard/src/pages/monitoring/InfrastructureMonitorPage.tsx` (placeholder)
- CREATE `packages/dashboard/src/pages/monitoring/KnowledgeBaseMonitorPage.tsx` (placeholder)
- CREATE `packages/dashboard/src/pages/monitoring/ConfigAuditPage.tsx` (placeholder)
- CREATE `packages/dashboard/src/pages/monitoring/SecurityAuditPage.tsx` (placeholder)

### Files to Modify

- MODIFY `packages/dashboard/src/components/layout/Sidebar.tsx` -- add Monitoring nav group
- MODIFY `packages/dashboard/src/router.tsx` -- add monitoring routes with Suspense + AdminGuard

### Dependencies

- `react-router-dom` (existing)
- `AdminGuard` from `packages/dashboard/src/guards/AdminGuard.ts` (existing)
- `useCurrentUser` hook (existing, for sidebar visibility)

## Testing Strategy

### Unit Tests

- [ ] Test Sidebar renders "Monitoring" group for admin users
- [ ] Test Sidebar does NOT render "Monitoring" group for member users
- [ ] Test Monitoring group contains all 10 nav items with correct labels and paths
- [ ] Test Monitoring group appears between Settings and Administration
- [ ] Test each monitoring route path resolves to the correct lazy component
- [ ] Test Suspense fallback renders while loading

## Completion Checklist

- [ ] Sidebar updated with Monitoring nav group
- [ ] All 10 monitoring routes registered with lazy loading
- [ ] Placeholder pages created for each monitoring screen
- [ ] AdminGuard wraps all monitoring routes
- [ ] Suspense fallback provided for lazy-loaded pages
- [ ] All tests written and passing
- [ ] TypeScript strict mode compiles without errors
