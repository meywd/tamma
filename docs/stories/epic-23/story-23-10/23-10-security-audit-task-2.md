# Task 2: Security Audit Frontend Components

**Story:** 23-10-security-access-audit
**Epic:** 23

## Task Description

Build the SecurityAuditPage with tabs for Overview, Logins, Sessions, API Keys, Roles, Permissions, Rate Limits, and Suspicious Activity. Provides operators with full visibility into who is accessing the system and how.

## Acceptance Criteria

- Security overview metrics bar at top (total logins, failed logins, active sessions, alerts)
- Login attempts table with daily failure chart and brute force alert banner
- Active sessions table with session duration and revoke button (owner-only)
- API key usage table with daily chart and stale key warnings
- Role distribution pie chart and user-role table
- Permission denied events table with daily chart
- Rate limit violations table with hourly chart
- Suspicious activity panel with severity badges and evidence drill-down

## Implementation Details

### Technical Requirements

- [ ] Replace placeholder `packages/dashboard/src/pages/monitoring/SecurityAuditPage.tsx`:
  - MonitoringLayout with title "Security Audit"
  - SecurityOverviewMetrics bar at top
  - Tab navigation: Logins, Sessions, API Keys, Roles, Permissions, Rate Limits, Suspicious

- [ ] Create `packages/dashboard/src/hooks/monitoring/useSecurityAudit.ts`:
  ```typescript
  export interface UseSecurityAuditResult {
    logins: LoginAttempt[];
    dailyLogins: { date: string; success: number; failure: number }[];
    sessions: SessionInfo[];
    apiKeyUsage: ApiKeyUsage[];
    roleDistribution: { role: string; count: number }[];
    users: UserWithRole[];
    permissionDenied: PermissionDeniedEvent[];
    dailyDenials: { date: string; count: number }[];
    rateLimitViolations: RateLimitViolation[];
    suspiciousPatterns: SuspiciousPattern[];
    overview: SecurityOverview;
    loading: boolean;
    error: string | null;
    revokeSession: (sessionId: string) => Promise<void>;
    acknowledgePattern: (patternId: string) => Promise<void>;
    refresh: () => Promise<void>;
  }
  ```

- [ ] Create `packages/dashboard/src/components/monitoring/security/SecurityOverviewMetrics.tsx`:
  - MetricGrid of MetricCards at top of page:
    - Logins Today (success/failure), Active Sessions, API Keys Active, Permission Denials Today, Rate Limit Violations Today, Active Alerts (count by severity)

- [ ] Create `packages/dashboard/src/components/monitoring/security/LoginAttemptsTable.tsx`:
  - DataTable: Timestamp, Username, Method, Result, IP, User Agent, Failure Reason
  - Result color: success=green, failure=red
  - Sortable and filterable
  - BruteForceAlert banner above table

- [ ] Create `packages/dashboard/src/components/monitoring/security/LoginFailureChart.tsx`:
  - TimeSeriesChart: daily failed login count over last 30 days
  - Red markers on spikes (>2x rolling average)

- [ ] Create `packages/dashboard/src/components/monitoring/security/BruteForceAlert.tsx`:
  - Red banner: "Possible brute force attack detected: X failed logins from IP Y in Z minutes"
  - Only shown when pattern detected
  - Dismissible

- [ ] Create `packages/dashboard/src/components/monitoring/security/ActiveSessionsTable.tsx`:
  - DataTable: User (avatar, name, role), Started, Last Activity, Duration (live-updating), IP, User Agent
  - "Revoke" button (owner-only) with confirmation
  - Warning badge if user has >5 concurrent sessions
  - Session count per user summary

- [ ] Create `packages/dashboard/src/components/monitoring/security/ApiKeyUsageTable.tsx`:
  - DataTable: Key Prefix, Label, Owner, Created, Last Used, Total Usage, Usage Today, Status
  - "Stale" warning for keys unused >30 days
  - "High Usage" alert for >1000 calls/hour
  - Status badge: active=green, revoked=red, unused=gray

- [ ] Create `packages/dashboard/src/components/monitoring/security/ApiKeyUsageChart.tsx`:
  - TimeSeriesChart per selected key: daily API call count over 30 days

- [ ] Create `packages/dashboard/src/components/monitoring/security/RoleDistributionPanel.tsx`:
  - Pie/donut chart: users per role (owner, admin, member)
  - Role summary MetricCards: total users, admins, owners
  - Warning if 0 owners or >5 owners
  - Per-role permissions list

- [ ] Create `packages/dashboard/src/components/monitoring/security/UserRoleTable.tsx`:
  - DataTable: Username, Role, Joined, Last Active, Permission Count
  - Sortable by any column

- [ ] Create `packages/dashboard/src/components/monitoring/security/PermissionDeniedTable.tsx`:
  - DataTable: Timestamp, User, Endpoint, Permission Required, HTTP Method, User Role
  - Grouped view: unique (user, permission) with count
  - Warning for >10 denials from same user in 1 hour

- [ ] Create `packages/dashboard/src/components/monitoring/security/PermissionDeniedChart.tsx`:
  - TimeSeriesChart: permission denied events per day over 30 days

- [ ] Create `packages/dashboard/src/components/monitoring/security/RateLimitViolationsTable.tsx`:
  - DataTable: Timestamp, User/Key, Endpoint, Rate, Limit, Window, Response
  - "Not configured" message if rate limiting not implemented

- [ ] Create `packages/dashboard/src/components/monitoring/security/SuspiciousActivityPanel.tsx`:
  - List of detected patterns as SuspiciousPatternCard components
  - Sorted by severity (high first)

- [ ] Create `packages/dashboard/src/components/monitoring/security/SuspiciousPatternCard.tsx`:
  - Pattern name, description, severity badge (low=blue, medium=yellow, high=red)
  - Affected user/key
  - Evidence: expandable list of audit events
  - Suggested action
  - Detection timestamp
  - "Acknowledge" button

- [ ] Create `packages/dashboard/src/services/monitoring/security-api-client.ts`

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/security/SecurityOverviewMetrics.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/LoginAttemptsTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/LoginFailureChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/BruteForceAlert.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/ActiveSessionsTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/ApiKeyUsageTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/ApiKeyUsageChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/RoleDistributionPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/UserRoleTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/PermissionDeniedTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/PermissionDeniedChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/RateLimitViolationsTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/SuspiciousActivityPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/security/SuspiciousPatternCard.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/useSecurityAudit.ts`
- CREATE `packages/dashboard/src/services/monitoring/security-api-client.ts`

### Files to Modify

- MODIFY `packages/dashboard/src/pages/monitoring/SecurityAuditPage.tsx` -- replace placeholder

### Dependencies

- Story 23-12: MonitoringLayout, MetricCard, MetricGrid, DataTable, TimeSeriesChart, StatusBadge, ErrorBanner
- Task 1: Security monitoring API endpoints

## Testing Strategy

### Unit Tests

- [ ] SecurityOverviewMetrics: renders all metric cards
- [ ] LoginAttemptsTable: color-codes success/failure
- [ ] LoginFailureChart: highlights spikes with red markers
- [ ] BruteForceAlert: shows only when brute force detected
- [ ] ActiveSessionsTable: live-updating duration
- [ ] ActiveSessionsTable: revoke button only for owner
- [ ] ActiveSessionsTable: warning for >5 concurrent sessions
- [ ] ApiKeyUsageTable: stale warning for unused keys
- [ ] ApiKeyUsageTable: high usage alert
- [ ] RoleDistributionPanel: pie chart renders correct segments
- [ ] RoleDistributionPanel: warning for 0 or >5 owners
- [ ] PermissionDeniedTable: grouped view shows counts
- [ ] SuspiciousPatternCard: severity badge with correct color
- [ ] SuspiciousPatternCard: evidence expandable
- [ ] SuspiciousPatternCard: acknowledge calls API
- [ ] useSecurityAudit: fetches all endpoints on mount

## Completion Checklist

- [ ] All 14 child components created
- [ ] 7-tab navigation
- [ ] Login monitoring with brute force alerts
- [ ] Session management with revocation
- [ ] API key usage tracking with alerts
- [ ] Role distribution visualization
- [ ] Permission denial tracking
- [ ] Suspicious activity detection display
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
