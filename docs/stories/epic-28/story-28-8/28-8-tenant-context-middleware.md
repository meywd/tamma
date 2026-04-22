# Story 28.8: `TenantContextMiddleware` Async-Provisioning Handling

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Auth
**Status**: Draft
**Priority**: High (without this middleware correctly honouring the
`tenants.Status` state machine, a client polling during provisioning
crashes the API or leaks data across tenants during pool eviction)
**Estimated Effort**: M (12h)

## User Story

As a **platform engineer**, I want **`TenantContextMiddleware` to
populate `ITenantContext` from the JWT / API-key handler, consult the
`tenants.Status` state machine with a short-lived cache, and return
precise HTTP status codes (503 / 424 / 410 / 404) for every non-active
state**, so that **async tenant provisioning (Story 28-5) is
client-observable via status codes + `Retry-After`, the former
`EnsurePersonalTenantMiddleware` synchronous-create path is eliminated,
and admin impersonation + cross-tenant leak scenarios from the epic
success-metric suite all resolve to a single predictable code path**.

## Acceptance Criteria

### AC1: Middleware order and context population

- [ ] Pipeline order (documented in `Program.cs` with a comment
      block): `UseAuthentication()` → `ApiKeyAuthHandler` /
      JWT handler populate `HttpContext.User` →
      `TenantContextMiddleware` → `UseAuthorization()` → endpoints.
- [ ] Middleware reads the tenant id from the first resolvable
      source:
  1. `HttpContext.Items["TenantId"]` if populated by `ApiKeyAuthHandler`
     (set for `tk_t_` keys, see Story 28-7 AC2).
  2. `ClaimTypes.NameIdentifier`-parallel claim `tid` on the JWT.
  3. Explicit `X-Tenant-Id` header (only when the authenticated
     principal is a user-scoped API key `tk_u_` per Story 28-7 AC1
     or a rootless JWT with multiple memberships).
- [ ] On the `TenantFreePaths` allowlist (existing list +
      `/api/v1/auth/switch-org` newly added per Doc 01 §2.3), the
      middleware exits early without any resolution — matches the
      existing behaviour.
- [ ] A rootless JWT (no `tid`) hitting a tenant-scoped path returns
      409 `{ "error": "no_active_tenant", "action": "POST
      /api/v1/auth/switch-org" }` per Doc 01 §2.3 and the Story 28-9
      cross-dependency.

### AC2: `tenants.Status` state machine produces precise status codes

Per Doc 04 §8.1 table (extended with the `pending_verification` and
`failed` rows from Doc 03 §6.1):

- [ ] `active` → pass through, populate `TenantDbContext` factory
      with the resolved data source.
- [ ] `pending_verification` → **503** `{ "error":
      "tenant_not_ready", "status": "pending_verification",
      "retryAfter": 60, "action": "verify email" }`, header
      `Retry-After: 60`.
- [ ] `provisioning` → **503** `{ "error": "tenant_not_ready",
      "status": "provisioning", "retryAfter": 5, "progressUrl":
      "/api/v1/tenants/{id}/provisioning-status" }` per Doc 03 §6.2,
      `Retry-After: 5`. The 5s value matches the Doc 03 §6.2
      "dynamic retry-after" recommendation for the normal-polling
      band.
- [ ] `failed` → **424** Failed Dependency `{ "error":
      "tenant_provisioning_failed", "status": "failed",
      "lastError": "<sanitized>", "retryUrl":
      "/api/v1/tenants/{id}/provisioning-status" }`. `lastError` is
      the Doc 03 §5.3 error-class value (`Transient`, `Permanent`,
      `Quarantined`) — **never the raw Postgres message or any SQL
      state**. `Retry-After` is absent (client stops polling).
- [ ] `suspended` → **402** Payment Required — unchanged from current
      behaviour but routed through this middleware so the state
      machine lives in one place.
- [ ] `delete_requested` (grace not expired) → pass through (per Doc
      04 §8.1 — "allow last-minute cancel").
- [ ] `delete_requested` (grace expired) / `dropping` / `deleting`
      → **503** `{ "error": "tenant_deleting" }`, `Retry-After: 0`
      (client should not retry). Per Doc 04 §8.1 footnote.
- [ ] `deleted` → **410 Gone** `{ "error": "tenant_deleted" }` per
      Doc 04 §8.1.
- [ ] Unknown / non-existent tenant id → **404** `{ "error":
      "tenant_not_found" }`.

### AC3: Status cache with event-driven invalidation

- [ ] A process-scoped `IMemoryCache` (existing ASP.NET Core
      `MemoryCache` DI service is fine) caches the `tenants` row
      projection `{ Status, DeleteRequestedAt, LastError }` keyed
      by `TenantId` with a **10-second absolute expiration**.
- [ ] Cache is invalidated on `TENANT.STATUS_CHANGED` events
      consumed from the RabbitMQ topic `tamma.platform.events` —
      Story 28-5's workflow publishes these on every `Status`
      transition. The invalidation is best-effort: if the RabbitMQ
      consumer is down, the 10s TTL still bounds staleness.
- [ ] Cache miss cost: one CP query `SELECT Status,
      DeleteRequestedAt, LastError FROM tenants WHERE Id = $1`.
      Measured against an indexed PK — budget 2ms p95.
- [ ] Cache hit cost: single `IMemoryCache.TryGetValue` call —
      budget 50µs.
- [ ] Metric `tamma_tenant_status_cache_hits_total{outcome=hit|miss}`;
      alert if `hit_rate < 95%` for 5 minutes (suggests the RabbitMQ
      consumer is down or TTL is too short for the traffic pattern).

### AC4: `EnsurePersonalTenantMiddleware` replaced by async dispatch

- [ ] The existing `EnsurePersonalTenantMiddleware` at
      `apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs`
      is **renamed and rewritten**, no longer creates a tenant DB
      synchronously. New behaviour when the authenticated user has
      no `tenant_memberships` row:
  1. Check CP for an existing personal `tenants` row with
     `OwnerId=<userId>` and `Type='personal'`.
  2. If absent: insert a CP `tenants` row with
     `Status='pending_verification'`, insert `tenant_memberships`
     with `Role='owner'`, publish a `TENANT.PROVISIONING_REQUESTED`
     event to `platform_events` (Story 28-6's table). The
     `CreateTenantWorkflow` (Story 28-5) correlates on the event.
     No synchronous wait.
  3. Return **503** with `Retry-After: 30` and `progressUrl`
     pointing at `/api/v1/tenants/{newTenantId}/provisioning-status`
     per Doc 03 §6.2.
- [ ] The middleware is **idempotent**: a second request from the
      same user while the workflow is mid-run does not create a
      duplicate CP row (partial unique index `(OwnerId, Type) WHERE
      Type='personal' AND Status != 'deleted'` enforces this — add
      to the migration set in this story).
- [ ] Per Epic 28 README conflict resolution #1, the tenant's
      `Status` flip from `pending_verification` to `provisioning`
      happens inside the **verify-email endpoint**, not this
      middleware. This middleware only creates the
      `pending_verification` row.

### AC5: Admin impersonation crosses tenants cleanly

- [ ] When the authenticated principal has `IsPlatformAdmin=true`
      and the request carries `X-Impersonate-Tenant-Id: <tid>`, the
      middleware resolves that tenant id instead of the JWT `tid`
      claim. Per Doc 04 §3.4 and §8 the impersonation:
  - Checks the 15-minute impersonation TTL: every impersonation
    starts with `POST /api/admin/impersonate/{tid}` which writes
    an `admin_impersonations` row in CP with `ExpiresAt=NOW()+15min`.
    The middleware validates the row is non-null and not expired.
  - Emits `PLATFORM_ADMIN.IMPERSONATED.SUCCESS` to
    `platform_events` on first use per impersonation session (dedup
    via `impersonationId` tag).
- [ ] Impersonation does NOT bypass the `tenants.Status` check — a
      platform admin cannot impersonate into a `deleted` tenant.
      Returns 410 as in AC2.
- [ ] Impersonation never falls through to the rootless-JWT 409 in
      AC1 — a platform admin always has a resolvable
      `X-Impersonate-Tenant-Id` claim or hits the admin route without
      tenant resolution.

### AC6: Performance and observability

- [ ] p95 middleware overhead (measured between pipeline-entry and
      pipeline-exit, excluding downstream handler) < 5ms on a warm
      cache, < 15ms on a cold cache miss. Measured via the existing
      OpenTelemetry instrumentation, exported as
      `tamma_tenant_context_middleware_ms`.
- [ ] Structured log on every non-pass-through outcome:
      `log.Info("tenant_context.middleware", tenantId=<g>,
      status=<s>, http_code=<n>, cache_outcome=<hit|miss>)`.
- [ ] **No tenant data is logged** in the middleware — only tenant
      id + status enum value + HTTP code. Supports the Doc 03 §2
      "no-PII-in-events" guarantee at the log layer too.
- [ ] A request-level attribute `tamma.tenant_status` is added to
      the OTel span so Grafana Tempo can group traces by tenant
      lifecycle state.

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/01-control-plane-split.md` §2.3 (middleware
    three new responsibilities) and §2.4 (permission checks stay
    JWT-based; `token_revocations` table is consulted only on
    `/api/admin/*` and is out of scope here — that's Story 28-9's
    `/admin` path).
  - `plans/db-per-tenant/03-async-tenant-provisioning.md` §6.1
    (`/auth/me` shape during provisioning), §6.2 (middleware 503
    behaviour), §6.3 (provisioning-status endpoint — lives in
    Story 28-5 but this middleware's `progressUrl` points at it).
  - `plans/db-per-tenant/04-connection-pool-and-delete.md` §8.1
    (full state-machine → HTTP table — the single source of
    truth this story implements), §8.2 (in-flight requests at
    grace expiry).
  - Epic 28 README conflict resolution #1 (registration trigger
    lives in verify-email, not here — this middleware only creates
    the `pending_verification` row on a rootless login).
- **File layout**:
  - `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs`
    — modified; core logic lives here.
  - `apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs`
    — **renamed to** `EnsurePendingPersonalTenantMiddleware.cs`,
    rewritten per AC4.
  - `apps/tamma-elsa/src/Tamma.Api/Services/TenantStatusCache.cs` —
    new, wraps `IMemoryCache` + RabbitMQ invalidation subscriber.
  - `apps/tamma-elsa/src/Tamma.Api/Services/Events/TenantStatusChangedSubscriber.cs`
    — new, consumes `TENANT.STATUS_CHANGED` from RabbitMQ and
    invalidates the cache.
  - `apps/tamma-elsa/src/Tamma.Data/Entities/AdminImpersonation.cs`
    — new CP entity for the 15-min TTL check.
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/04x_admin_impersonations.cs`
    — new migration.
- **Existing code this story modifies**:
  - `Program.cs` pipeline ordering (documented in a comment block
    and validated by a DI-resolution integration test).
  - `Tamma.Api/Dtos/Auth/MeResponse.cs` — per Doc 03 §6.1 add the
    `status`, `provisioningStartedAt`, `provisionedAt`,
    `failureReason`, `progressUrl` fields per membership entry.

## Dependencies

- **Blocks**: 28-9 (switch-org reads the `tenants.Status` check
  this middleware encapsulates; the 503 / 410 / 409 logic is shared
  via a single `TenantStatusEvaluator` service).
- **Blocked by**: 28-4 (`ITenantConnectionResolver` — the middleware
  calls it to obtain the data source for active tenants), 28-5
  (the `tenants.Status` state machine this middleware honours is
  driven by the `CreateTenantWorkflow` and `DeleteTenantWorkflow`),
  28-6 (`platform_events` table — `TENANT.STATUS_CHANGED` events
  are written here by Story 28-5 and consumed by the cache
  invalidator).
- **External**: RabbitMQ topic `tamma.platform.events` (existing
  broker), the existing `IMemoryCache` service.

## Test Plan

### Unit tests

- `TenantStatusEvaluatorTests` — table-driven across all `Status`
  values × `DeleteRequestedAt` combinations, asserting the expected
  HTTP code + `Retry-After` value per AC2.
- `TenantStatusCacheTests` — hit, miss, invalidation on event,
  expiration after 10s. Uses `FakeTimeProvider` for deterministic
  TTL.
- `TenantContextMiddlewareTests` — mocked `IAuthenticationHandler`
  and `ITenantConnectionResolver`:
  - JWT with `tid=<active>` → passes through.
  - JWT with `tid=<provisioning>` → 503 + `Retry-After: 5`.
  - JWT with `tid=<failed>` → 424 + sanitized `lastError`.
  - JWT with `tid=<deleted>` → 410.
  - Rootless JWT on tenant-scoped path → 409 with switch-org action.
  - Rootless JWT on `/api/v1/auth/switch-org` → pass through.
  - `X-Impersonate-Tenant-Id` with expired TTL → 401.
  - `X-Impersonate-Tenant-Id` on `deleted` tenant → 410.
- `EnsurePendingPersonalTenantMiddlewareTests`:
  - User with no membership → creates CP row, emits
    `TENANT.PROVISIONING_REQUESTED`, returns 503.
  - Second call from same user while workflow is running → no
    duplicate row (partial unique index enforces), returns 503.

### Integration tests (Testcontainers.PostgreSQL + RabbitMQ)

- **T1 End-to-end provisioning polling**: register → verify email
  → poll `/api/v1/issues` — first call returns 503 with
  `Retry-After: 5`, subsequent calls after workflow completes
  return 200.
- **T2 Status transition propagation**: with middleware warm on
  tenant X (`active`), publish a `TENANT.STATUS_CHANGED` event to
  RabbitMQ flipping to `delete_requested` (grace expired) →
  asserts the next request returns 503 `tenant_deleting` within
  the 10s TTL window (cache-invalidation path exercised).
- **T3 Impersonation happy path**: platform admin calls
  `/api/admin/impersonate/<tid>` → receives impersonation id →
  subsequent request with `X-Impersonate-Tenant-Id` accesses the
  tenant's data → `PLATFORM_ADMIN.IMPERSONATED.SUCCESS` event
  written to `platform_events`.
- **T4 Impersonation on deleted tenant**: platform admin tries
  `X-Impersonate-Tenant-Id: <deleted-id>` → 410.
- **T5 Middleware overhead benchmark**: 1000 warm-cache requests
  → p95 < 5ms; 1000 cold-cache requests → p95 < 15ms. Captured as
  a Gauge in the report attached to the story.
- **T6 Rootless JWT → switch-org**: rootless JWT on `/api/v1/issues`
  → 409; same JWT on `/api/v1/auth/switch-org` → pass through to
  Story 28-9's handler.
- **T7 Legacy synchronous-create path is gone**: a fresh user whose
  first request hits a tenant-scoped path does NOT trigger a
  synchronous `CREATE DATABASE`. Asserted via a
  `TenantConnectionResolverSpy` that fails the test if
  `GetAsync` is called for a tenant in `pending_verification`.

### Manual verification

- Local dev: follow Story 28-5 AC6 manual flow (signup → verify-email
  → poll status). Observe the dashboard making the poll request and
  receiving 503 → 200 transition. Verify `Retry-After` header in
  Chrome DevTools Network tab.
- Kill the RabbitMQ container mid-session — confirm the middleware
  falls back to 10s TTL and still functions (cache-hit rate gauge
  drops but the API stays up).

## Definition of Done

- [ ] AC all green
- [ ] Unit + integration tests added, suite passes
- [ ] No new CodeQL alerts (the `X-Impersonate-Tenant-Id` header
      handling gets scrutiny for privilege-escalation patterns —
      ensure the `IsPlatformAdmin=true` check is unconditional)
- [ ] Design-doc references updated if the impl deviated
- [ ] Reviewed by a second engineer (cross-stream)

## Risks / Open Questions

- **10-second cache TTL vs. RabbitMQ consumer lag.** If the
  `TenantStatusChangedSubscriber` lags behind event publication by
  > 10s (e.g. during a broker restart), a stale `active` cache
  entry could serve a request that should have returned 503. The
  TTL is short enough that the window is bounded; a louder
  mitigation is a synchronous CP read on suspicious outcomes
  (e.g. when the resolver reports a dropped connection pool) —
  deferred to a follow-up if ops data shows the bound is hit.
- **`EnsurePendingPersonalTenantMiddleware` vs concurrent first
  requests.** If a user's first session fires three concurrent
  requests, three middleware instances race to create the CP row.
  The partial unique index guarantees no duplicate row lands, but
  two will see a Postgres unique-violation on the insert and must
  retry the read. Cost: at most two retry reads per first-session
  burst — acceptable.
- **Rootless JWT + API-key hybrid.** If a caller somehow presents
  both a rootless JWT AND a `tk_u_` API key with conflicting
  `sub` claims, the current pipeline lets the last-writer-wins on
  `HttpContext.User.Identity`. Story 28-7 already prefers
  API-key auth to override the JWT, so the behaviour is
  deterministic; verify with a dedicated test T8 if the
  ambiguity concerns reviewers.
