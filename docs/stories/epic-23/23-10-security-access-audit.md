# Story 23-10: Security & Access Audit

Status: planned

## Summary

Build a security audit screen showing login attempts (success/failure), active sessions, API key usage, role distribution, permission denied events, rate limit violations, and suspicious activity pattern detection. This screen gives operators visibility into who is accessing the system and how.

## Acceptance Criteria

### Login Attempts

1. A table of recent login attempts shows:
   - Timestamp
   - Username or GitHub login
   - Login method: GitHub OAuth, API key, JWT refresh
   - Result: success (green), failure (red)
   - IP address (if available from request headers)
   - User agent (browser/OS info)
   - Failure reason (if failed): invalid credentials, account locked, expired session, unknown user
2. Columns are sortable and filterable.
3. A "Failed Logins" chart shows:
   - Daily failed login count over last 30 days
   - Highlighted spikes (>2x rolling average)
4. Brute force detection: if >5 failed logins from the same IP in 10 minutes, show a red alert banner.
5. Login data is recorded by instrumenting the auth middleware (`packages/api/src/auth/`) to emit login events.

### Active Sessions

6. A table of active sessions shows:
   - User: username, role, avatar
   - Session start time
   - Last activity timestamp
   - Session duration (live-updating)
   - IP address
   - User agent
   - Current page/endpoint (if available)
   - "Revoke" button (owner-only) that invalidates the session JWT
7. Session count per user (sorted by count descending).
8. A warning if any single user has >5 concurrent sessions.
9. Session data comes from JWT verification middleware tracking active tokens.

### API Key Usage

10. A table of API key usage shows:
    - Key prefix (first 8 chars of the key, e.g., `tamma_k_...`)
    - Key label
    - Owner username
    - Created date
    - Last used timestamp
    - Usage count (total API calls made with this key)
    - Usage count today
    - Status: active (green), revoked (red), unused (gray)
11. A usage chart per key: daily API call count over last 30 days.
12. Keys not used in >30 days are flagged with a "Stale" warning.
13. A "High Usage" alert if any key exceeds 1000 calls/hour.
14. Data comes from instrumenting the API key auth middleware to record usage.

### Role Distribution

15. A role distribution panel shows:
    - Pie chart: count of users per role (owner, admin, member)
    - Role summary: total users, total admins, total owners
    - Per-role permissions list (what each role can do)
16. A user-role table:
    - Username, role, joined date, last active, permission count
    - Sortable by any column
17. Anomaly: if there are 0 owners or >5 owners, show a warning.

### Permission Denied Events

18. A table of permission denied events shows:
    - Timestamp
    - User who was denied
    - Endpoint/resource attempted
    - Permission required (e.g., `settings:manage`, `admin:users`)
    - HTTP method and path
    - User's actual role
19. Grouped view: unique (user, permission) pairs with count and last occurrence.
20. A chart: permission denied events per day over last 30 days.
21. High-frequency denial patterns (>10 denials from same user in 1 hour) trigger a warning.
22. Data comes from the `requirePermission()` middleware logging denied requests.

### Rate Limit Violations

23. A table of rate limit violations shows:
    - Timestamp
    - User or API key (identified by prefix)
    - Endpoint
    - Current rate (requests per window)
    - Limit (max requests per window)
    - Window duration
    - Response: throttled (429) or warned
24. A chart: rate limit violations per hour over last 7 days.
25. Top rate-limited users/keys (by violation count).
26. If rate limiting is not yet implemented, show "Rate limiting not configured" with setup instructions.

### Suspicious Activity Patterns

27. A pattern detection panel evaluates rules and flags suspicious behavior:
    - Multiple failed logins followed by a success (potential credential stuffing)
    - API calls from unusual IP addresses (IPs not seen in the last 7 days)
    - Bulk data access patterns (>100 API calls in 1 minute from a single key)
    - Permission escalation attempts (user accessing admin endpoints without admin role)
    - After-hours activity (API calls outside configured business hours, if set)
    - Unusual API key creation (>3 keys created by a single user in 1 day)
28. Each detected pattern shows:
    - Pattern name and description
    - Severity: low (blue), medium (yellow), high (red)
    - Affected user/key
    - Evidence: list of events that triggered the pattern
    - Suggested action
    - Timestamp of detection
29. Pattern detection runs every 5 minutes, evaluating the audit log for the patterns above.
30. Patterns with severity "high" generate alerts via the existing AlertManager.

### Audit Event Storage

31. All security events (logins, permission denials, rate limits, API key usage) are stored in a dedicated audit log:
    - In-memory ring buffer: last 10,000 events
    - Each event: `{ timestamp, type, userId, keyId, ip, userAgent, endpoint, method, result, details }`
    - The audit log is append-only and cannot be modified or deleted (except by time-based rotation)
32. Future: persist to PostgreSQL audit table (documented as follow-up, not required for this story).

## API Endpoints Needed

- GET /api/monitoring/security/logins -- recent login attempts, query params: `result`, `since`, `until`, `limit`, `username`
- GET /api/monitoring/security/logins/daily -- daily login counts (success/failure)
- GET /api/monitoring/security/sessions -- active sessions
- POST /api/monitoring/security/sessions/:id/revoke -- revoke a session (owner-only)
- GET /api/monitoring/security/api-keys/usage -- API key usage stats
- GET /api/monitoring/security/api-keys/:prefix/daily -- daily usage for a specific key
- GET /api/monitoring/security/roles -- role distribution with user counts
- GET /api/monitoring/security/roles/users -- user list with roles
- GET /api/monitoring/security/denied -- permission denied events, query params: `since`, `until`, `userId`, `permission`, `limit`
- GET /api/monitoring/security/denied/daily -- daily denial counts
- GET /api/monitoring/security/rate-limits -- rate limit violations, query params: `since`, `until`, `limit`
- GET /api/monitoring/security/rate-limits/hourly -- hourly violation counts
- GET /api/monitoring/security/suspicious -- detected suspicious activity patterns
- GET /api/monitoring/security/suspicious/:id/evidence -- evidence events for a specific pattern detection
- GET /api/monitoring/security/overview -- combined security overview (counts, active alerts, severity summary)

## Dashboard Components

- `SecurityAuditPage` -- page container with tabs
- `LoginAttemptsTable` -- login attempts table with filters
- `LoginFailureChart` -- daily failed login chart
- `BruteForceAlert` -- brute force detection banner
- `ActiveSessionsTable` -- active sessions with revoke
- `SessionCountWarning` -- high session count warning
- `ApiKeyUsageTable` -- API key usage stats
- `ApiKeyUsageChart` -- per-key daily usage chart
- `StaleKeyWarning` -- unused key warning
- `RoleDistributionPanel` -- pie chart and role summary
- `UserRoleTable` -- user list with roles
- `PermissionDeniedTable` -- denied events table
- `PermissionDeniedChart` -- daily denial chart
- `RateLimitViolationsTable` -- rate limit violations
- `RateLimitChart` -- hourly violation chart
- `SuspiciousActivityPanel` -- detected patterns with severity
- `SuspiciousPatternCard` -- individual pattern detection card
- `SecurityOverviewMetrics` -- summary metrics at top of page

## Data Sources

- New: Security Audit Log (in-memory ring buffer, instrumented in auth middleware)
- IUserStore (existing) -- user list, roles
- IUserApiKeyStore (existing) -- API key list, metadata
- Auth middleware (existing, `packages/api/src/auth/`) -- login events
- requirePermission middleware (existing) -- permission denied events
- requireSelfOrRole middleware (existing) -- role checks
- JWT verification (existing) -- session tracking

## Implementation Notes

- Login event recording: add an `onLoginAttempt` hook to the GitHub OAuth callback and JWT verification paths. Record `{ timestamp, userId, method, result, ip, userAgent }`.
- Session tracking: maintain a `Map<string, SessionInfo>` in the auth middleware. Add entries on JWT creation, remove on JWT expiry or revoke. Update `lastActivity` on each authenticated request.
- API key usage: add a counter in the API key auth middleware. Record `{ timestamp, keyId, endpoint, method }` per authenticated request.
- Permission denied recording: the existing `requirePermission()` function already returns 403. Add a side-effect that records the denial.
- Rate limit violations: if rate limiting middleware exists, instrument it. If not, this panel shows "Not configured" with a link to configuration.
- Suspicious pattern detection: a dedicated `SuspiciousPatternDetector` service runs every 5 minutes, queries the audit log, and applies rule-based detection.
- IP addresses: extracted from `request.ip` or `x-forwarded-for` header (behind nginx).
- Session revocation: add the JWT `jti` to a revocation set (in-memory `Set<string>`). The JWT verification hook checks against this set.
- All security audit data is sensitive. Never log raw tokens, passwords, or full API keys. Only log key prefixes and user IDs.

## Files to Create

- `packages/api/src/routes/monitoring/security-routes.ts`
- `packages/api/src/services/monitoring/security-audit-log.ts`
- `packages/api/src/services/monitoring/session-tracker.ts`
- `packages/api/src/services/monitoring/api-key-usage-tracker.ts`
- `packages/api/src/services/monitoring/suspicious-pattern-detector.ts`
- `packages/api/src/services/monitoring/login-recorder.ts`
- `packages/dashboard/src/pages/monitoring/SecurityAuditPage.tsx`
- `packages/dashboard/src/components/monitoring/security/LoginAttemptsTable.tsx`
- `packages/dashboard/src/components/monitoring/security/LoginFailureChart.tsx`
- `packages/dashboard/src/components/monitoring/security/BruteForceAlert.tsx`
- `packages/dashboard/src/components/monitoring/security/ActiveSessionsTable.tsx`
- `packages/dashboard/src/components/monitoring/security/ApiKeyUsageTable.tsx`
- `packages/dashboard/src/components/monitoring/security/ApiKeyUsageChart.tsx`
- `packages/dashboard/src/components/monitoring/security/RoleDistributionPanel.tsx`
- `packages/dashboard/src/components/monitoring/security/UserRoleTable.tsx`
- `packages/dashboard/src/components/monitoring/security/PermissionDeniedTable.tsx`
- `packages/dashboard/src/components/monitoring/security/PermissionDeniedChart.tsx`
- `packages/dashboard/src/components/monitoring/security/RateLimitViolationsTable.tsx`
- `packages/dashboard/src/components/monitoring/security/SuspiciousActivityPanel.tsx`
- `packages/dashboard/src/components/monitoring/security/SuspiciousPatternCard.tsx`
- `packages/dashboard/src/components/monitoring/security/SecurityOverviewMetrics.tsx`
- `packages/dashboard/src/hooks/monitoring/useSecurityAudit.ts`
- Tests for all API routes, services, and components
