---
title: "Task 1: Security Audit Log, Event Recording & API Routes"
sidebar:
  order: 230
---

**Story:** 23-10-security-access-audit
**Epic:** 23

## Task Description

Create the security audit event infrastructure: an in-memory audit log ring buffer, event recording hooks in auth/permission middleware, session tracking, API key usage tracking, and all security monitoring API routes. Also create the suspicious activity pattern detector.

## Acceptance Criteria

- In-memory audit log ring buffer (last 10,000 events) with append-only semantics
- Login events recorded via hooks in auth middleware (GitHub OAuth, JWT verification)
- Permission denied events recorded in `requirePermission` middleware
- Session tracking via JWT creation/verification/expiry
- API key usage tracking per authenticated request
- Suspicious activity pattern detector running every 5 minutes
- All 15 security monitoring API endpoints implemented
- `POST /sessions/:id/revoke` requires owner permission
- All security data is redacted (no raw tokens, passwords, or full API keys in responses)

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/services/monitoring/security-audit-log.ts`:
  ```typescript
  export type AuditEventType = 'login' | 'login_failed' | 'permission_denied' | 'api_key_used' | 'rate_limited' | 'session_created' | 'session_expired' | 'session_revoked';

  export interface AuditEvent {
    id: string;               // UUID
    timestamp: string;
    type: AuditEventType;
    userId: string | null;
    keyId: string | null;
    ip: string | null;
    userAgent: string | null;
    endpoint: string | null;
    method: string | null;
    result: 'success' | 'failure';
    details: Record<string, unknown>;
  }

  export class SecurityAuditLog {
    private buffer: AuditEvent[];
    private readonly maxSize: number;  // default 10000

    constructor(maxSize?: number);
    append(event: Omit<AuditEvent, 'id' | 'timestamp'>): void;
    query(options?: {
      type?: AuditEventType;
      userId?: string;
      since?: string;
      until?: string;
      result?: 'success' | 'failure';
      limit?: number;
    }): AuditEvent[];
    getCount(): number;
  }
  ```
  - Append-only: no update or delete methods
  - Ring buffer: drops oldest when exceeding maxSize
  - `query()` filters in-memory and returns matching events

- [ ] Create `packages/api/src/services/monitoring/login-recorder.ts`:
  ```typescript
  export class LoginRecorder {
    constructor(deps: { auditLog: SecurityAuditLog });

    recordLogin(data: {
      userId: string;
      method: 'github_oauth' | 'api_key' | 'jwt_refresh';
      success: boolean;
      ip: string | null;
      userAgent: string | null;
      failureReason?: string;
    }): void;
  }
  ```
  - Called from auth middleware on login attempt (success or failure)
  - Extracts IP from `request.ip` or `x-forwarded-for`

- [ ] Create `packages/api/src/services/monitoring/session-tracker.ts`:
  ```typescript
  export interface SessionInfo {
    id: string;               // JWT jti
    userId: string;
    username: string;
    role: string;
    startedAt: string;
    lastActivity: string;
    ip: string | null;
    userAgent: string | null;
  }

  export class SessionTracker {
    private sessions: Map<string, SessionInfo>;
    private revokedSet: Set<string>;     // revoked JWT jti values

    constructor();
    trackSession(info: SessionInfo): void;
    updateActivity(jti: string, ip?: string): void;
    revokeSession(jti: string): void;
    isRevoked(jti: string): boolean;
    getActiveSessions(): SessionInfo[];
    removeExpired(maxAgeMs: number): void;
  }
  ```
  - `trackSession()` called on JWT creation
  - `updateActivity()` called on each authenticated request
  - `revokeSession()` adds to revoked set and removes from active sessions
  - `isRevoked()` checked in JWT verification hook
  - `removeExpired()` called periodically to clean up

- [ ] Create `packages/api/src/services/monitoring/api-key-usage-tracker.ts`:
  ```typescript
  export interface ApiKeyUsage {
    keyPrefix: string;
    keyLabel: string;
    ownerUsername: string;
    totalUsage: number;
    usageToday: number;
    lastUsedAt: string | null;
    createdAt: string;
    status: 'active' | 'revoked';
  }

  export class ApiKeyUsageTracker {
    private usageCounts: Map<string, { total: number; today: number; lastUsed: string | null }>;

    constructor();
    recordUsage(keyId: string, endpoint: string, method: string): void;
    getUsage(keyStore: IUserApiKeyStore): Promise<ApiKeyUsage[]>;
    getDailyUsage(keyId: string, days?: number): { date: string; count: number }[];
  }
  ```
  - Called from API key auth middleware on each authenticated request
  - `usageToday` resets at midnight UTC

- [ ] Create `packages/api/src/services/monitoring/suspicious-pattern-detector.ts`:
  ```typescript
  export interface SuspiciousPattern {
    id: string;
    name: string;
    description: string;
    severity: 'low' | 'medium' | 'high';
    affectedUserId: string | null;
    affectedKeyId: string | null;
    evidence: AuditEvent[];
    suggestedAction: string;
    detectedAt: string;
  }

  export class SuspiciousPatternDetector {
    private detectedPatterns: SuspiciousPattern[];
    private timer: ReturnType<typeof setInterval> | null;

    constructor(deps: {
      auditLog: SecurityAuditLog;
      alertManager?: unknown;
    });

    start(): void;              // starts 5-minute evaluation loop
    stop(): void;
    evaluate(): Promise<void>;  // run all pattern checks
    getDetectedPatterns(): SuspiciousPattern[];
    getPatternEvidence(id: string): AuditEvent[];
  }
  ```
  - Patterns detected:
    1. Brute force: >5 failed logins from same IP in 10 minutes (high)
    2. Credential stuffing: multiple failed logins then success (high)
    3. Unusual IP: API calls from IPs not seen in last 7 days (medium)
    4. Bulk access: >100 API calls in 1 minute from single key (medium)
    5. Permission escalation: non-admin accessing admin endpoints (high)
    6. After-hours: API calls outside business hours if configured (low)
    7. Key creation spree: >3 keys created by single user in 1 day (medium)
  - High severity patterns fire alerts via AlertManager

- [ ] Create `packages/api/src/routes/monitoring/security-routes.ts`:
  ```typescript
  export function registerSecurityMonitoringRoutes(
    app: FastifyInstance,
    auditLog: SecurityAuditLog,
    sessionTracker: SessionTracker,
    apiKeyUsageTracker: ApiKeyUsageTracker,
    patternDetector: SuspiciousPatternDetector,
    userStore: IUserStore,
    apiKeyStore: IUserApiKeyStore,
  ): void;
  ```
  - 15 endpoints under `/api/monitoring/security/*`
  - Session revocation requires `admin:manage` permission
  - All responses redact sensitive data

### Files to Create

- CREATE `packages/api/src/services/monitoring/security-audit-log.ts`
- CREATE `packages/api/src/services/monitoring/login-recorder.ts`
- CREATE `packages/api/src/services/monitoring/session-tracker.ts`
- CREATE `packages/api/src/services/monitoring/api-key-usage-tracker.ts`
- CREATE `packages/api/src/services/monitoring/suspicious-pattern-detector.ts`
- CREATE `packages/api/src/routes/monitoring/security-routes.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/security-audit-log.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/session-tracker.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/api-key-usage-tracker.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/suspicious-pattern-detector.test.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/security-routes.test.ts`

### Files to Modify

- MODIFY `packages/api/src/routes/monitoring/index.ts` -- register security routes
- MODIFY `packages/api/src/auth/require-permission.ts` -- add side-effect to record permission denials
- MODIFY auth middleware (GitHub OAuth callback, JWT verification) -- add login recording hooks

### Dependencies

- Story 23-11: route registration
- Auth middleware (existing, `packages/api/src/auth/`)
- `requirePermission` (existing)
- IUserStore, IUserApiKeyStore (existing)
- AlertManager from `@tamma/cost-monitor` (existing) for high-severity alerts

## Testing Strategy

### Unit Tests

- [ ] SecurityAuditLog: append adds event to buffer
- [ ] SecurityAuditLog: ring buffer drops oldest at maxSize
- [ ] SecurityAuditLog: query filters by type, userId, time range, result
- [ ] SessionTracker: tracks new session
- [ ] SessionTracker: updateActivity updates lastActivity
- [ ] SessionTracker: revokeSession adds to revoked set
- [ ] SessionTracker: isRevoked returns true for revoked sessions
- [ ] SessionTracker: removeExpired cleans up old sessions
- [ ] ApiKeyUsageTracker: recordUsage increments counters
- [ ] ApiKeyUsageTracker: usageToday resets at midnight
- [ ] SuspiciousPatternDetector: detects brute force pattern
- [ ] SuspiciousPatternDetector: detects credential stuffing
- [ ] SuspiciousPatternDetector: detects bulk access
- [ ] SuspiciousPatternDetector: fires alert for high severity
- [ ] LoginRecorder: records success and failure events
- [ ] Security routes: session revoke requires admin permission
- [ ] Security routes: never returns raw tokens or passwords

## Completion Checklist

- [ ] Audit log ring buffer (10,000 events)
- [ ] Login recording in auth middleware
- [ ] Permission denied recording
- [ ] Session tracking with revocation
- [ ] API key usage tracking
- [ ] Suspicious pattern detection (7 patterns)
- [ ] All 15 API endpoints
- [ ] Auth middleware hooks integrated
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
