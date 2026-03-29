# Story 21.4: User Dashboard — Repos & Workflow Runs

Status: planned

## Story

As a **logged-in Tamma user**,
I want to see my connected repositories and browse the history of workflow runs with their status and logs,
so that I can monitor what Tamma is doing on my behalf and troubleshoot failures.

## Acceptance Criteria

1. A "My Repos" page at `/user/repos` displays a list of repositories connected to the user's account, showing: repo name (owner/repo), platform icon (GitHub/GitLab/etc.), connection status (active/paused/disconnected), last workflow run timestamp, and total runs count
2. Each repo card has actions: "View Runs" (navigates to filtered run list), "Pause/Resume" (toggles workflow processing), "Disconnect" (removes repo with confirmation dialog)
3. An "Add Repository" button opens a flow to connect a new repository (GitHub App install redirect or manual token entry for other platforms)
4. A "Workflow Runs" page at `/user/runs` displays a paginated, filterable list of workflow runs showing: run ID, repo name, trigger (issue number/title), status (queued/running/succeeded/failed/cancelled), started at, duration, and AI provider used
5. Clicking a run row expands an inline detail panel or navigates to `/user/runs/:runId` showing: full event timeline (from DCB event stream), step-by-step progress (14-step orchestrator loop), logs output, files changed, and PR link (if created)
6. The runs list supports filtering by: repository, status, date range, and trigger type (issue/manual)
7. The runs list supports sorting by: started at (default desc), duration, status
8. A real-time status indicator shows actively running workflows with a pulsing dot and auto-updates via SSE (`/api/v1/events/stream`)
9. The pages are accessible only to authenticated users (redirects to login if no session)
10. Members see only their own repos and runs; admins and owners see all repos and runs (respecting RBAC from Epic 16)
11. Empty states are handled gracefully: "No repositories connected yet" with CTA to add one, "No workflow runs yet" with explanation
12. The pages are responsive and follow the existing dashboard design system (Tailwind 4, consistent with admin pages)

## Technical Context

### Integration with Existing Dashboard

The user dashboard lives inside `packages/dashboard/` (React 18 + react-router-dom + Zustand + Tailwind 4). New routes are added under the `/user` prefix:

```typescript
// Addition to packages/dashboard/src/router.tsx
import { UserLayout } from './pages/user/UserLayout.js';
import { ReposPage } from './pages/user/ReposPage.js';
import { RunsPage } from './pages/user/RunsPage.js';
import { RunDetailPage } from './pages/user/RunDetailPage.js';

// Inside router children:
{
  path: '/user',
  element: <UserLayout />,
  children: [
    { path: 'repos', element: <ReposPage /> },
    { path: 'runs', element: <RunsPage /> },
    { path: 'runs/:runId', element: <RunDetailPage /> },
  ],
}
```

### API Endpoints Required

These endpoints must exist on `api.tamma.dev` (some may already exist, others need creation):

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `GET` | `/api/v1/repos` | List connected repos for current user |
| `POST` | `/api/v1/repos` | Connect a new repository |
| `PATCH` | `/api/v1/repos/:repoId` | Update repo (pause/resume) |
| `DELETE` | `/api/v1/repos/:repoId` | Disconnect a repository |
| `GET` | `/api/v1/runs` | List workflow runs (paginated, filterable) |
| `GET` | `/api/v1/runs/:runId` | Get run detail with event timeline |
| `GET` | `/api/v1/runs/:runId/logs` | Get run log output |
| `SSE` | `/api/v1/events/stream` | Real-time event stream for live updates |

### Component Architecture

```
packages/dashboard/src/pages/user/
├── UserLayout.tsx              User section layout (sidebar nav for user pages)
├── ReposPage.tsx               Connected repos list
├── RepoCard.tsx                Individual repo card component
├── AddRepoDialog.tsx           Add repository modal/dialog
├── RunsPage.tsx                Workflow runs list with filters
├── RunsFilter.tsx              Filter controls (repo, status, date, type)
├── RunRow.tsx                  Individual run list row
├── RunDetailPage.tsx           Full run detail view
├── RunTimeline.tsx             Event timeline visualization
├── RunSteps.tsx                14-step orchestrator progress
└── RunLogs.tsx                 Log output viewer (scrollable, searchable)
```

### Zustand Stores

```typescript
// packages/dashboard/src/stores/reposStore.ts
interface ReposStore {
  repos: Repository[];
  loading: boolean;
  error: string | null;
  fetchRepos: () => Promise<void>;
  connectRepo: (data: ConnectRepoRequest) => Promise<void>;
  updateRepo: (repoId: string, data: Partial<Repository>) => Promise<void>;
  disconnectRepo: (repoId: string) => Promise<void>;
}

// packages/dashboard/src/stores/runsStore.ts
interface RunsStore {
  runs: WorkflowRun[];
  selectedRun: WorkflowRunDetail | null;
  filters: RunFilters;
  pagination: { page: number; pageSize: number; total: number };
  loading: boolean;
  fetchRuns: () => Promise<void>;
  fetchRunDetail: (runId: string) => Promise<void>;
  setFilters: (filters: Partial<RunFilters>) => void;
  setPage: (page: number) => void;
}
```

### Real-Time Updates via SSE

```typescript
// packages/dashboard/src/hooks/useRunUpdates.ts
export function useRunUpdates(onUpdate: (event: DomainEvent) => void) {
  useEffect(() => {
    const eventSource = new EventSource('/api/v1/events/stream');

    eventSource.addEventListener('WORKFLOW.STEP_COMPLETED', (e) => {
      onUpdate(JSON.parse(e.data));
    });

    eventSource.addEventListener('WORKFLOW.COMPLETED', (e) => {
      onUpdate(JSON.parse(e.data));
    });

    return () => eventSource.close();
  }, [onUpdate]);
}
```

### Data Models

```typescript
interface Repository {
  id: string;
  name: string;              // "owner/repo"
  platform: 'github' | 'gitlab' | 'gitea' | 'forgejo' | 'bitbucket' | 'azure-devops';
  status: 'active' | 'paused' | 'disconnected';
  lastRunAt: string | null;
  totalRuns: number;
  connectedAt: string;
}

interface WorkflowRun {
  id: string;
  repoId: string;
  repoName: string;
  trigger: {
    type: 'issue' | 'manual';
    issueNumber?: number;
    issueTitle?: string;
  };
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
  startedAt: string;
  completedAt: string | null;
  duration: number | null;     // milliseconds
  provider: string;
  currentStep?: string;        // For running workflows
}

interface WorkflowRunDetail extends WorkflowRun {
  events: DomainEvent[];       // Full event timeline
  steps: WorkflowStep[];       // 14-step progress
  logs: string;                // Combined log output
  filesChanged: string[];
  prUrl: string | null;
}
```

### Files to Create

| File | Purpose |
|------|---------|
| `packages/dashboard/src/pages/user/UserLayout.tsx` | Layout wrapper for user pages with sub-navigation |
| `packages/dashboard/src/pages/user/ReposPage.tsx` | Connected repos list page |
| `packages/dashboard/src/pages/user/RepoCard.tsx` | Repo card component |
| `packages/dashboard/src/pages/user/AddRepoDialog.tsx` | Add repo dialog |
| `packages/dashboard/src/pages/user/RunsPage.tsx` | Workflow runs list page |
| `packages/dashboard/src/pages/user/RunsFilter.tsx` | Filter controls |
| `packages/dashboard/src/pages/user/RunRow.tsx` | Run list row |
| `packages/dashboard/src/pages/user/RunDetailPage.tsx` | Run detail page |
| `packages/dashboard/src/pages/user/RunTimeline.tsx` | Event timeline component |
| `packages/dashboard/src/pages/user/RunSteps.tsx` | Orchestrator step progress |
| `packages/dashboard/src/pages/user/RunLogs.tsx` | Log viewer component |
| `packages/dashboard/src/stores/reposStore.ts` | Repos Zustand store |
| `packages/dashboard/src/stores/runsStore.ts` | Runs Zustand store |
| `packages/dashboard/src/hooks/useRunUpdates.ts` | SSE hook for real-time updates |
| `packages/dashboard/src/services/reposService.ts` | API client for repos endpoints |
| `packages/dashboard/src/services/runsService.ts` | API client for runs endpoints |

### Files to Modify

| File | Change |
|------|--------|
| `packages/dashboard/src/router.tsx` | Add `/user/*` routes |
| `packages/dashboard/src/components/layout/AppLayout.tsx` | Add user section to navigation sidebar |

## Implementation Notes

- **API-first**: The UI depends on API endpoints. If endpoints do not exist yet, create mock data services first, then wire to real APIs. The service layer (`reposService.ts`, `runsService.ts`) abstracts this.
- **Pagination**: Use cursor-based pagination for runs (UUID v7 is time-sortable). The API should accept `cursor` and `limit` query parameters. Display page numbers in the UI using offset-based UI pagination backed by cursor pagination.
- **Run detail loading**: Load the event timeline and logs lazily when the user opens a run detail, not on the list page. Events can be large.
- **Log viewer**: Use a monospace, scrollable container with line numbers. Consider virtual scrolling for large logs (>10,000 lines). A simple search-within-logs feature (Ctrl+F or a search input) is useful.
- **Platform icons**: Use SVG icons for each Git platform. These can be sourced from Simple Icons or created as components.
- **Duration formatting**: Display durations as human-readable (e.g., "2m 34s", "1h 12m"). Use `dayjs.duration()` or a simple formatter.
- **Empty states**: Design meaningful empty states with illustration/icon and actionable CTA. Do not show empty tables.
- **Error states**: Handle API errors gracefully with retry buttons and error messages. Use the existing error handling patterns from the dashboard.
- **RBAC enforcement**: The API enforces RBAC. The UI should also hide admin-only actions (like viewing other users' repos) based on the user's role from the auth context.

## Dependencies

- **Epic 16** (Unified Auth + RBAC) — user authentication, role-based access
- **Story 21.1** (Marketing Landing Page) — only for cross-linking ("Sign In" link on marketing site navigates to dashboard)
- **Existing packages/dashboard** — provides React, router, Zustand, Tailwind, and layout infrastructure

## Estimated Effort

**32 hours**

| Task | Hours |
|------|-------|
| UserLayout + router integration | 3 |
| ReposPage + RepoCard + empty states | 5 |
| AddRepoDialog (GitHub App flow) | 3 |
| RunsPage + RunRow + pagination | 5 |
| RunsFilter (repo, status, date, type) | 3 |
| RunDetailPage + RunTimeline | 5 |
| RunSteps + RunLogs viewer | 4 |
| Zustand stores + API services | 3 |
| SSE real-time updates hook | 1 |

---

**Last Updated**: 2026-03-28
