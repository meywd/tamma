# Story 28-8 Implementation Plan — TenantContextMiddleware (Async Provisioning)

**Status**: Planned (2026-04-20)
**Story brief**: [`28-8-tenant-context-middleware.md`](./28-8-tenant-context-middleware.md)
**Epic 28 phase**: C (Auth — after 28-7)
**Branch**: `feat/story-28-8-tenant-context-middleware`

---

## 1. Objective

Replace the old `EnsurePersonalTenantMiddleware` with a
`TenantContextMiddleware` that populates `ITenantContext` from the JWT
or API-key handler, consults the `tenants.Status` state machine with
a short-lived cache, and returns precise HTTP status codes (503 / 424
/ 410 / 404 / 409) for every non-active state. The middleware fronts
every tenant-scoped request and is the single code path that
translates provisioning state into client-observable HTTP semantics.

## 2. Dependencies

Hard blockers:

- **Story 28-7** — `ApiKeyAuthHandler` populates `HttpContext.Items["TenantId"]`.
- **Story 28-5** — `tenants.Status` state machine columns.
- **Story 28-4** — `ITenantConnectionResolver` for data-source lookup.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs` | Replaces `EnsurePersonalTenantMiddleware`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Tenancy/TenantStatusCache.cs` | 5-second in-memory cache keyed by tenantId (uses `IMemoryCache`). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Tenancy/TenantFreePaths.cs` | Static allowlist + `IsTenantFree(path)` helper. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Middleware/TenantContextMiddlewareTests.cs` | 14+ unit cases covering every status + rootless JWT scenario. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.IntegrationTests/Middleware/TenantStatusResponseTests.cs` | End-to-end per-status HTTP response verification. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs` | Delete (replaced). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Pipeline order: Authentication → TenantContextMiddleware → Authorization. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Tenancy/ITenantContext.cs` | Add `Status` and `TenantSlug` properties. |

## 5. Sequence of changes

### Step 1 — Allowlist + status cache (2h)

- `TenantFreePaths` static list: `/api/v1/auth/*`,
  `/api/v1/onboarding/register`, `/api/v1/healthz`, `/api/v1/auth/switch-org`, etc.
- `TenantStatusCache.GetStatusAsync(tenantId)` returns cached
  `(Status, FailedAt, Slug)` with 5s TTL; falls through to CP DB on miss.
- Unit test: cache hit vs. miss; correct eviction.
- **Commit**: `feat(tenancy): tenant status cache + free-path list`.

### Step 2 — Middleware skeleton (2h)

- `TenantContextMiddleware.InvokeAsync`:
  1. If `TenantFreePaths.IsTenantFree(path)` → next.
  2. Read tenantId from: `HttpContext.Items["TenantId"]` → `User.FindFirst("tid")?.Value` → `X-Tenant-Id` header.
  3. If none → 409 rootless error.
  4. Load status from cache.
  5. Switch on status → response or populate `ITenantContext`.
- **Commit**: `feat(tenancy): TenantContextMiddleware skeleton`.

### Step 3 — Status → HTTP mapping (3h)

- Per AC2:
  - `active` → pass through.
  - `pending_verification` → 503 + `Retry-After: 60`.
  - `provisioning` → 503 + `Retry-After: 5` + `progressUrl`.
  - `failed` → 424 + `failedAt` + `retryUrl`.
  - `deleted` → 410 Gone.
  - row missing → 404.
- Consistent JSON error body shape.
- **Commit**: `feat(tenancy): status-to-HTTP mapping`.

### Step 4 — Pipeline wiring + delete old middleware (1h)

- `Program.cs` order + comment block.
- Delete `EnsurePersonalTenantMiddleware.cs`.
- **Commit**: `fix(pipeline): swap to TenantContextMiddleware`.

### Step 5 — Integration tests + admin impersonation AC5 (3h)

- E2E tests per status with Testcontainers.
- Admin impersonation header `X-Impersonate-Tenant` respected
  when principal is platform admin.
- **Commit**: `test(tenancy): status response + impersonation E2E`.

### Step 6 — Docs (1h)

- Document pipeline order + status codes in a new section of
  `docs/deployment/request-lifecycle.md`.
- **Commit**: `docs(deploy): request lifecycle + status codes`.

## 6. Test strategy

### Unit

- 14 cases covering every status + source combination (JWT tid,
  API-key tid, X-Tenant-Id, no tid, impersonation).

### Integration

- Testcontainers with tenants in every status; assert HTTP body
  and headers match spec.

### Regression

- Remove `EnsurePersonalTenantMiddleware` — every existing auth
  test must still pass (proves the replacement is complete).

## 7. Rollback plan

- **Revert**: single commit chain; reverting restores
  `EnsurePersonalTenantMiddleware`.
- **Cache safety**: if `TenantStatusCache` returns stale data during
  status transitions, worst case is 5s of 503 after status became
  active (acceptable).

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Allowlist + cache | 2 |
| 2. Middleware skeleton | 2 |
| 3. Status→HTTP mapping | 3 |
| 4. Pipeline wiring | 1 |
| 5. Integration tests + impersonation | 3 |
| 6. Docs | 1 |
| **Total** | **12** (matches brief) |

## 9. Open questions

- **Cache TTL**: 5s vs. tenant.UpdatedAt watermark? Plan: 5s is
  fine — provisioning rarely flips faster.
- **Rootless JWT returning 409 vs. 401**: 409 is correct (user
  *is* authenticated but has no active tenant). 401 would be
  misleading. Confirmed with Doc 01 §2.3.
- **Impersonation audit**: every impersonation emits
  `PLATFORM_ADMIN.IMPERSONATED.SUCCESS` (28-6 event type). Already
  in Story 28-6 whitelist.
- **Retry-After header for 424**: not standard. Plan: omit;
  `retryUrl` in body is enough.
- **Will cache miss during provisioning thundering herd hammer
  CP?** Per-key `SemaphoreSlim` on the cache lookup protects CP.
  Already implemented in the 28-4 pattern; borrow it here.
