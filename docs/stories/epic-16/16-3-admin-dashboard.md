# Story 16.3: Admin Dashboard Panel

Status: ready-for-dev

## Story

As an **owner or admin user**,
I want an admin panel within the Tamma Dashboard where I can manage users, roles, API keys, and view system health,
so that I can administer the platform through a UI without needing direct API calls or database access.

## Acceptance Criteria

1. A new "Admin" page is accessible at `/admin` in the Tamma Dashboard (React SPA), visible only to users with `admin` or `owner` role
2. The admin page has a tabbed layout with sections: Users, API Keys, System Health, Quick Links
3. **Users tab**: displays a table of all users with columns: Avatar (GitHub), Username, Email, Role, Last Active, Created At, Actions
4. **Users tab**: "Invite User" button opens a dialog to create an invite with role selection and generates a shareable link
5. **Users tab**: Role dropdown per user row allows changing the role (owner-only for admin/owner promotions), with confirmation dialog
6. **Users tab**: "Remove" button per user row (owner-only) soft-deletes the user with confirmation dialog
7. **API Keys tab**: displays per-user API keys with columns: Key Prefix, Label, Created At, Last Used, Actions (Revoke)
8. **API Keys tab**: "Create API Key" button generates a new key and displays it once in a copy-to-clipboard dialog
9. **System Health tab**: displays health status of all services: Tamma API, ELSA Server, PostgreSQL, OpenSearch, RabbitMQ, ChromaDB
10. **System Health tab**: each service shows status (healthy/unhealthy/unknown), last checked timestamp, and response time
11. **Quick Links tab**: clickable links to ELSA Studio (elsa.tamma.dev), OpenSearch Dashboards (logs.tamma.dev), GitHub repository
12. Navigation guard: members who navigate to `/admin` are redirected to `/` with a "not authorized" toast message
13. Admin link in the sidebar/navigation is conditionally rendered based on user role
14. All user management actions call the Story 16.2 API endpoints
15. Loading states, error handling, and empty states are implemented for all data-fetching sections

## Technical Context

### Existing Dashboard

The Tamma Dashboard is a React SPA built with Vite, served via nginx in Docker. It currently has:
- GitHub OAuth login flow
- Workflow monitoring views
- Installation management
- No admin/user management UI

### Files to Create

| File | Purpose |
|------|---------|
| `packages/dashboard/src/pages/AdminPage.tsx` | Main admin page with tab layout |
| `packages/dashboard/src/components/admin/UserTable.tsx` | User list table component |
| `packages/dashboard/src/components/admin/InviteDialog.tsx` | Invite user dialog |
| `packages/dashboard/src/components/admin/RoleSelector.tsx` | Role change dropdown with confirmation |
| `packages/dashboard/src/components/admin/ApiKeyTable.tsx` | API key list table |
| `packages/dashboard/src/components/admin/CreateApiKeyDialog.tsx` | API key creation + copy dialog |
| `packages/dashboard/src/components/admin/SystemHealth.tsx` | Service health status cards |
| `packages/dashboard/src/components/admin/QuickLinks.tsx` | External service links |
| `packages/dashboard/src/hooks/useUsers.ts` | React Query hook for user management API |
| `packages/dashboard/src/hooks/useApiKeys.ts` | React Query hook for API key management |
| `packages/dashboard/src/hooks/useSystemHealth.ts` | React Query hook for health endpoints |
| `packages/dashboard/src/guards/AdminGuard.tsx` | Route guard for admin-only pages |

### Files to Modify

| File | Change |
|------|--------|
| `packages/dashboard/src/App.tsx` (or router config) | Add `/admin` route wrapped in AdminGuard |
| `packages/dashboard/src/components/Sidebar.tsx` (or navigation) | Add conditional "Admin" link |
| `packages/dashboard/src/types/user.ts` (or equivalent) | Add User, ApiKey, Invite TypeScript types |

## Implementation Plan

### Step 1: Route Guard

```tsx
// packages/dashboard/src/guards/AdminGuard.tsx
import { Navigate } from 'react-router-dom';
import { useAuth } from '../hooks/useAuth';
import { toast } from '...'; // whatever toast library the dashboard uses

export function AdminGuard({ children }: { children: React.ReactNode }) {
  const { user, isLoading } = useAuth();

  if (isLoading) return <LoadingSpinner />;

  if (!user || (user.role !== 'admin' && user.role !== 'owner')) {
    toast.error('Not authorized to access admin panel');
    return <Navigate to="/" replace />;
  }

  return <>{children}</>;
}
```

### Step 2: Admin Page Layout

```tsx
// packages/dashboard/src/pages/AdminPage.tsx
import { useState } from 'react';
import { UserTable } from '../components/admin/UserTable';
import { ApiKeyTable } from '../components/admin/ApiKeyTable';
import { SystemHealth } from '../components/admin/SystemHealth';
import { QuickLinks } from '../components/admin/QuickLinks';

type AdminTab = 'users' | 'api-keys' | 'health' | 'links';

export function AdminPage() {
  const [activeTab, setActiveTab] = useState<AdminTab>('users');

  return (
    <div className="admin-page">
      <h1>Admin Panel</h1>
      <nav className="admin-tabs">
        <button onClick={() => setActiveTab('users')}
                className={activeTab === 'users' ? 'active' : ''}>
          Users
        </button>
        <button onClick={() => setActiveTab('api-keys')}
                className={activeTab === 'api-keys' ? 'active' : ''}>
          API Keys
        </button>
        <button onClick={() => setActiveTab('health')}
                className={activeTab === 'health' ? 'active' : ''}>
          System Health
        </button>
        <button onClick={() => setActiveTab('links')}
                className={activeTab === 'links' ? 'active' : ''}>
          Quick Links
        </button>
      </nav>

      <div className="admin-content">
        {activeTab === 'users' && <UserTable />}
        {activeTab === 'api-keys' && <ApiKeyTable />}
        {activeTab === 'health' && <SystemHealth />}
        {activeTab === 'links' && <QuickLinks />}
      </div>
    </div>
  );
}
```

### Step 3: User Table Component

```tsx
// packages/dashboard/src/components/admin/UserTable.tsx
import { useUsers, useUpdateUserRole, useDeleteUser } from '../../hooks/useUsers';
import { useAuth } from '../../hooks/useAuth';
import { InviteDialog } from './InviteDialog';
import { RoleSelector } from './RoleSelector';

export function UserTable() {
  const { user: currentUser } = useAuth();
  const { data, isLoading, error } = useUsers();
  const updateRole = useUpdateUserRole();
  const deleteUser = useDeleteUser();
  const [showInvite, setShowInvite] = useState(false);

  if (isLoading) return <LoadingSpinner />;
  if (error) return <ErrorMessage error={error} />;
  if (!data?.users.length) return <EmptyState message="No users yet" />;

  return (
    <div>
      <div className="table-header">
        <h2>Users ({data.total})</h2>
        <button onClick={() => setShowInvite(true)}>Invite User</button>
      </div>

      <table>
        <thead>
          <tr>
            <th>User</th>
            <th>Email</th>
            <th>Role</th>
            <th>Last Active</th>
            <th>Created</th>
            <th>Actions</th>
          </tr>
        </thead>
        <tbody>
          {data.users.map(user => (
            <tr key={user.id}>
              <td>
                <img src={`https://github.com/${user.githubLogin}.png?size=32`}
                     alt={user.githubLogin} className="avatar" />
                {user.githubLogin}
              </td>
              <td>{user.email ?? '-'}</td>
              <td>
                <RoleSelector
                  currentRole={user.role}
                  canPromote={currentUser?.role === 'owner'}
                  disabled={user.id === currentUser?.id}
                  onChange={(role) => updateRole.mutate({ userId: user.id, role })}
                />
              </td>
              <td>{user.lastActiveAt ? formatRelative(user.lastActiveAt) : 'Never'}</td>
              <td>{formatDate(user.createdAt)}</td>
              <td>
                {currentUser?.role === 'owner' && user.id !== currentUser.id && (
                  <button onClick={() => {
                    if (confirm(`Remove user ${user.githubLogin}?`)) {
                      deleteUser.mutate(user.id);
                    }
                  }}>
                    Remove
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {showInvite && <InviteDialog onClose={() => setShowInvite(false)} />}
    </div>
  );
}
```

### Step 4: System Health Component

```tsx
// packages/dashboard/src/components/admin/SystemHealth.tsx
import { useSystemHealth } from '../../hooks/useSystemHealth';

interface ServiceStatus {
  name: string;
  url: string;
  status: 'healthy' | 'unhealthy' | 'unknown';
  responseTime?: number;
  checkedAt: string;
}

export function SystemHealth() {
  const { data: services, isLoading, refetch } = useSystemHealth();

  // Services to check:
  // - Tamma API: GET /api/health
  // - ELSA Server: GET /health (proxied via app.tamma.dev/api/ or directly)
  // - PostgreSQL: checked via API health endpoint (connection pool)
  // - OpenSearch: GET opensearch:9200/_cluster/health (proxied via API)
  // - RabbitMQ: management API health (proxied via API)
  // - ChromaDB: GET /api/v2/heartbeat (proxied via API)

  return (
    <div>
      <div className="table-header">
        <h2>System Health</h2>
        <button onClick={() => refetch()}>Refresh</button>
      </div>

      <div className="health-grid">
        {services?.map(service => (
          <div key={service.name} className={`health-card ${service.status}`}>
            <div className="status-indicator" />
            <h3>{service.name}</h3>
            <p>Status: {service.status}</p>
            {service.responseTime && <p>Response: {service.responseTime}ms</p>}
            <p className="checked-at">Checked: {formatRelative(service.checkedAt)}</p>
          </div>
        ))}
      </div>
    </div>
  );
}
```

### Step 5: API Health Endpoint

Add a new API endpoint that checks all services and returns aggregated health:

```typescript
// In Tamma API routes
app.get('/api/admin/health', {
  preHandler: [requireRole('admin')],
}, async (request, reply) => {
  const checks = await Promise.allSettled([
    checkService('Tamma API', 'http://localhost:3100/health'),
    checkService('ELSA Server', 'http://elsa-server:5000/health'),
    checkService('PostgreSQL', async () => { await pool.query('SELECT 1'); }),
    checkService('OpenSearch', 'http://opensearch:9200/_cluster/health'),
    checkService('RabbitMQ', 'http://rabbitmq:15672/api/health/checks/alarms'),
    checkService('ChromaDB', 'http://chromadb:8000/api/v2/heartbeat'),
  ]);

  return reply.send({ services: checks.map(formatResult) });
});
```

### Step 6: Quick Links Component

```tsx
// packages/dashboard/src/components/admin/QuickLinks.tsx
export function QuickLinks() {
  const links = [
    {
      name: 'ELSA Studio',
      description: 'Workflow designer and execution monitor',
      url: 'https://elsa.tamma.dev',
      icon: 'workflow',
    },
    {
      name: 'OpenSearch Dashboards',
      description: 'Log aggregation and search',
      url: 'https://logs.tamma.dev',
      icon: 'search',
    },
    {
      name: 'GitHub Repository',
      description: 'Source code and issues',
      url: 'https://github.com/meywd/tamma',
      icon: 'github',
    },
    {
      name: 'RabbitMQ Management',
      description: 'Message queue monitoring',
      url: '#', // Proxied through API or separate subdomain in future
      icon: 'queue',
    },
  ];

  return (
    <div>
      <h2>Quick Links</h2>
      <div className="links-grid">
        {links.map(link => (
          <a key={link.name} href={link.url} target="_blank" rel="noopener noreferrer"
             className="link-card">
            <h3>{link.name}</h3>
            <p>{link.description}</p>
          </a>
        ))}
      </div>
    </div>
  );
}
```

### Step 7: React Query Hooks

```typescript
// packages/dashboard/src/hooks/useUsers.ts
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';

export function useUsers(options?: { limit?: number; offset?: number }) {
  return useQuery({
    queryKey: ['users', options],
    queryFn: () => api.get('/api/users', { params: options }),
  });
}

export function useUpdateUserRole() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: string }) =>
      api.put(`/api/users/${userId}/role`, { role }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['users'] }),
  });
}

export function useDeleteUser() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (userId: string) => api.delete(`/api/users/${userId}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['users'] }),
  });
}

export function useCreateInvite() {
  return useMutation({
    mutationFn: (data: { email?: string; role: string }) =>
      api.post('/api/users/invite', data),
  });
}
```

## Logging Requirements

| Event | Level | Output | Notes |
|-------|-------|--------|-------|
| Admin page accessed | DEBUG | Browser console (dev only) | Track who views admin panel |
| User role changed via UI | INFO | Via API (Story 16.2 logging) | UI triggers API call which logs |
| User deleted via UI | INFO | Via API (Story 16.2 logging) | UI triggers API call which logs |
| API key created via UI | INFO | Via API (Story 16.2 logging) | UI triggers API call which logs |
| Health check failed | WARN | Via API health endpoint log | Log which service is unhealthy |

### Sensitive Data Redaction

- API key is shown once in the UI dialog and must not be stored in browser localStorage or sessionStorage
- User email addresses are displayed in the table (acceptable for admin panel)

## Testing Strategy

### Unit Tests (Vitest + React Testing Library)

Create test files colocated with components:

1. `AdminGuard.test.tsx` — renders children for admin/owner, redirects for member, shows loading state
2. `UserTable.test.tsx` — renders user list, invite button, role selector, remove button visibility
3. `RoleSelector.test.tsx` — dropdown options based on permissions, disabled state for self
4. `InviteDialog.test.tsx` — form validation, submit calls API, displays generated link
5. `ApiKeyTable.test.tsx` — renders key list, create/revoke actions
6. `CreateApiKeyDialog.test.tsx` — displays key once, copy-to-clipboard works
7. `SystemHealth.test.tsx` — renders status cards, refresh button, handles loading/error states
8. `useUsers.test.ts` — React Query hook returns correct data, mutations invalidate cache

### Integration Tests

1. Full admin flow: login as owner -> navigate to /admin -> invite user -> change role -> remove user
2. API key flow: create key -> copy to clipboard -> revoke key -> verify key no longer shown

### Manual Verification

1. Log in as a `member` user -> verify `/admin` redirects to `/` with toast
2. Log in as an `admin` user -> verify Users and API Keys tabs work, owner-only actions are disabled
3. Log in as `owner` -> verify all actions work including role promotion and user deletion
4. Verify system health shows correct status for all services

## Dependencies

- **Story 16.2** (User Management REST API) — admin panel calls these endpoints
- **Story 16.1** (OAuth2 Proxy) — unified auth must work for the admin to be meaningful across services
- Internal: Tamma Dashboard React app, React Query, existing auth hooks

## Estimated Effort

| Task | Hours |
|------|-------|
| AdminGuard + route setup | 2 |
| AdminPage layout + tabs | 2 |
| UserTable + RoleSelector | 4 |
| InviteDialog | 2 |
| ApiKeyTable + CreateApiKeyDialog | 3 |
| SystemHealth + API endpoint | 4 |
| QuickLinks | 1 |
| React Query hooks | 2 |
| Unit tests | 3 |
| Styling + responsive layout | 1 |
| **Total** | **24 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation | Architecture Team |
