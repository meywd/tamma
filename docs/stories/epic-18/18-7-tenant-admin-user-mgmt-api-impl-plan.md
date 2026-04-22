# Story 18-7 Implementation Plan — Tenant-Admin User Management API Completion

**Status**: Planned (2026-04-21)
**Story brief**: [`18-7-tenant-admin-user-mgmt-api.md`](./18-7-tenant-admin-user-mgmt-api.md)
**Epic 18 phase**: Layer 4 Team A (backend completion).
**Branch**: `feat/story-18-7-tenant-admin-user-mgmt-api`

---

## 1. Objective

Close three thin backend gaps in `OrgEndpoints.cs` so that the tenant-
admin audit trail is complete and the upcoming tenant-admin management
UI (Story 18-8) has every endpoint it needs. Emit the missing
`TENANT.MEMBER_ROLE_CHANGED.SUCCESS` event from `UpdateMemberRole`, add
a first-class `POST /invites/{inviteId}/resend` handler that extends
expiry without rotating the token, and add a tenant-scoped audit-log
view at `GET /api/v1/orgs/{tenantId}/audit` that returns events filtered
by `Tags.tenantId`. Pure backend + tests; no new tables, no new event
store, no platform-admin endpoint changes.

## 2. Dependencies

Hard blockers:

- **Story 18-3** — org creation ships the existing `OrgEndpoints`
  surface + `RequireTenantMembershipFilter` that gates every new
  handler.
- **Story 18-1 / 18-2** — existing user + auth infrastructure.
- **Epic 28 Phase B** — RLS on the `events` table. This story's
  tenant-audit endpoint provides defence-in-depth via the RLS policy;
  the backend filter alone would work, but 28-B is the co-requisite.

Soft:

- **Story 28-9** (switch-org) — not required here; tenant-context is
  already in the JWT for every call.
- Blocks **Story 18-8** (UI consumer).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/TenantAuditEndpointTests.cs` | Unit tests for the new `GET /audit` handler. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/ResendInviteEndpointTests.cs` | Unit tests for the new resend-invite handler (happy path, expired, accepted, rate-limited). |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Integration/TenantAuditFlowTests.cs` | Integration test for the full create-invite → resend → accept → change-role → list-audit loop. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | (1) Append `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` event emission inside `UpdateMemberRole` after the role row commits. (2) Add `ResendInvite` handler mirroring `DeleteInvite`'s shape. (3) Add `ListTenantAudit` handler. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register two new routes: `POST /api/v1/orgs/{tenantId}/invites/{inviteId}/resend` and `GET /api/v1/orgs/{tenantId}/audit`. Both use `RequireTenantMembershipFilter` with `admin+` role requirement. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs` | Add `Task ExtendExpiryAsync(Guid inviteId, DateTimeOffset newExpiresAt, CancellationToken ct)` and `Task<TenantInvite?> GetByIdScopedAsync(Guid tenantId, Guid inviteId, CancellationToken ct)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/ITenantInviteRepository.cs` | Declare the two new methods. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs` | Add `Task<(IReadOnlyList<DomainEvent>, int total)> ListByTenantAsync(Guid tenantId, string? typeFilter, int limit, int offset, CancellationToken ct)`. Uses existing `tenant_id` index added in Epic 28. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` | Declare the new method. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/OrgEndpointsTests.cs` | Add assertions that `UpdateMemberRole` now appends the role-change event. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/RateLimit/RateLimitKeys.cs` (if exists; otherwise add a constant inline) | Add `ResendInvitePerInviteHour` key scheme. |

## 5. Sequence of changes

### Step 1 — Emit `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` (1h)

- In `OrgEndpoints.UpdateMemberRole`, after the membership row commits,
  call `_ = _events.AppendAsync(new DomainEvent { Type = "TENANT.MEMBER_ROLE_CHANGED.SUCCESS", Tags = { tenantId, userId = callerId, targetUserId, oldRole, newRole }, Data = new { at = dayjs-equivalent UtcNow } });` using the existing fire-and-forget pattern (matches `CreateInvite`). A logger failure does not unwind the role change.
- Unit test in `OrgEndpointsTests`: assert exactly one event appended
  with the expected Tags + Data shape.
- **Commit**: `feat(orgs): emit TENANT.MEMBER_ROLE_CHANGED.SUCCESS`.

### Step 2 — `ExtendExpiryAsync` repo method (1h)

- `InviteRepository.ExtendExpiryAsync`: single UPDATE SET `expires_at =
  @new` WHERE `id = @id`.
- `GetByIdScopedAsync`: SELECT WHERE `id = @id AND tenant_id = @tenantId`
  — guards against cross-tenant id spoofing.
- Interface declarations added.
- Repo unit tests for each.
- **Commit**: `feat(data): ExtendExpiryAsync + scoped invite lookup`.

### Step 3 — `ResendInvite` handler (4h)

- Route handler in `OrgEndpoints.ResendInvite(tenantId, inviteId, …)`:
  1. Resolve caller via `RequireTenantMembershipFilter` (admin+).
  2. Rate limit: `_rateLimits.CheckAsync("resend-tenant-invite:{tenantId}:{inviteId}", max:3, window:1h)`. Over limit → 429 with same shape as `ResendVerification`.
  3. Load invite via `GetByIdScopedAsync`. 404 if not found.
  4. If `AcceptedAt != null` → 400 `{ error: "already_accepted" }`.
  5. If `ExpiresAt < now` → 400 `{ error: "expired" }`. (UI surfaces
     both conditions distinctly so the user can decide "send new"
     instead of "resend".)
  6. New expiry = `now + 72h`.
  7. Call `ExtendExpiryAsync`.
  8. Fire-and-forget `_emailDispatcher.SendTenantInviteEmailAsync(invite.Email, invite.RawToken?)` — **invite row stores only the SHA-256 hash** of the token; the raw token was only known at create time. The resend email is a reminder with the original link derived from the hash? NO — the email-template key has to accept the hash plus tenant name; actual email copy should reference the original URL. Decision recorded in Open Questions.
  9. Emit `TENANT.MEMBER_INVITE_RESENT.SUCCESS` with `Tags = { tenantId, userId=caller, inviteId, email }`.
  10. Return 200 `{ id: inviteId, expiresAt: newExpiresAt }`.
- Unit tests: happy path; accepted invite rejection (400); expired
  rejection (400); rate-limited (429 after 3 calls in an hour); cross-
  tenant 403 via filter.
- **Commit**: `feat(orgs): resend-invite endpoint`.

### Step 4 — `ListByTenantAsync` event repo method (2h)

- SQL: `SELECT id, type, created_at, tags, data FROM events WHERE
  tags->>'tenantId' = @tenantId AND (@type IS NULL OR type LIKE @type ||
  '%') ORDER BY created_at DESC LIMIT @limit OFFSET @offset;`
  plus a `COUNT(*)` for pagination total.
- Uses `idx_events_tenant_id_created_at` (already exists post-28).
- Repo unit test: asserts matching / non-matching rows + type prefix
  behaviour.
- **Commit**: `feat(data): EventRepository.ListByTenantAsync`.

### Step 5 — `ListTenantAudit` handler (3h)

- Route handler in `OrgEndpoints.ListTenantAudit(tenantId, limit,
  offset, type, …)`:
  1. `RequireTenantMembershipFilter` (admin+).
  2. Parse + clamp `limit` (default 50, max 200); `offset` default 0.
  3. Before the query, set `app.current_tenant_id` via
     `ITenantContextAccessor.SetAsync(tenantId)` so the RLS policy on
     `events` (from Epic 28 Phase B) gates reads defence-in-depth.
  4. Call `_events.ListByTenantAsync(tenantId, type, limit, offset, ct)`.
  5. Project each `DomainEvent` to an `AuditEventDto { id, type,
     createdAt, tags, data }` — Metadata is stripped.
  6. Return `{ events, total, limit, offset }`.
- Unit tests: returns only rows matching `Tags.tenantId`, honours
  pagination, honours type prefix filter, cross-tenant call returns
  403 via filter (no rows exposed even if bug in filter because RLS
  also applies — assert this via a manual `SET app.current_tenant_id
  = '<other>'` probe).
- **Commit**: `feat(orgs): tenant audit endpoint`.

### Step 6 — Swagger / OpenAPI registration (1h)

- `Program.cs` registers both routes with `WithOpenApi()` + tagged
  `"Organizations"`. Auth descriptor uses existing `RequireTenantAdmin`
  scheme.
- Regenerate dashboard client: `pnpm --filter @tamma/dashboard-user
  generate:api` (dashboard-user consumer in 18-8) — plan is docs-only
  here, UI story kicks the regen.
- **Commit**: `chore(openapi): register resend + audit routes`.

### Step 7 — Integration test + cleanup (2h)

- `TenantAuditFlowTests.EndToEnd`: create invite → resend → accept →
  change role → list audit → assert five events in order:
  `TENANT.MEMBER_INVITED.SUCCESS`, `TENANT.MEMBER_INVITE_RESENT.SUCCESS`,
  `TENANT.MEMBER_JOINED.SUCCESS`, `TENANT.MEMBER_ROLE_CHANGED.SUCCESS`.
- Tests run against the in-process `TestWebApplicationFactory` with
  a Postgres testcontainer (same pattern 18-4 uses).
- **Commit**: `test(orgs): tenant audit flow integration`.

## 6. Test strategy

### Unit (in `OrgEndpointsTests`, `TenantAuditEndpointTests`, `ResendInviteEndpointTests`)

- `UpdateMemberRole` appends the role-change event with correct Tags.
- `ResendInvite` extends expiry (verify DB row value) + does not
  rotate the token (verify `token_hash` column unchanged).
- `ResendInvite` rejects accepted invites with 400 `already_accepted`.
- `ResendInvite` rejects expired invites with 400 `expired`.
- `ResendInvite` returns 429 after 3 calls in one hour (use stub
  `IRateLimitService` + advancing clock).
- `ResendInvite` emits `TENANT.MEMBER_INVITE_RESENT.SUCCESS` + sends
  email (verify `IEmailDispatcher` call).
- `ListTenantAudit` returns only rows with matching `Tags.tenantId`.
- `ListTenantAudit` honours pagination (limit clamp 200, offset).
- `ListTenantAudit` honours `type=TENANT.MEMBER_INVITED.SUCCESS` prefix
  (matches role-change too if prefix is `TENANT.MEMBER`).
- Cross-tenant: tenant A's admin calling `tenantId=B` → 403 from the
  filter (not 200 empty).

### Integration (`TenantAuditFlowTests`)

- Real Postgres container, RLS policies active, full loop through the
  five events described in Step 7. Assert ordering by `created_at`.

### Defence-in-depth test

- Manually set `SET app.current_tenant_id = '<tenantB>'` in a session
  then call `_events.ListByTenantAsync(tenantA, …)` — RLS must reject,
  returning 0 rows. Test guards against a bug where the filter is
  accidentally dropped.

## 7. Rollback plan

- **Revert**: single chain of commits; no data migration. Reverting
  leaves the existing UpdateMemberRole (no event emitted), no resend
  endpoint, no audit endpoint — same as pre-story state.
- **Data produced**: `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` and
  `TENANT.MEMBER_INVITE_RESENT.SUCCESS` events accumulated after this
  ships remain in the event store after revert. They are immutable;
  nothing consumes them if the plan is rolled back.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Emit role-change event | 1 |
| 2. `ExtendExpiryAsync` repo | 1 |
| 3. `ResendInvite` handler + tests | 4 |
| 4. `ListByTenantAsync` repo | 2 |
| 5. `ListTenantAudit` handler + tests | 3 |
| 6. OpenAPI registration | 1 |
| 7. Integration test | 2 |
| **Total** | **14** (matches brief). |

## 9. Open questions

- **Resend email contents**: the invite table stores only the SHA-256
  hash of the token, not the raw token. The original email contained a
  link `/accept-invite?token=<raw>`. For resend, we cannot re-derive
  the raw token. Two options:
  - (A) Email just says "your invite to {tenantName} is still pending
    — check your earlier email from {createdAt}" without including a
    new link. Simple; accepts the UX penalty that the user must find
    the original email.
  - (B) Generate a new raw token on resend, update `token_hash`, send
    new email with the new link. This voids the original link.
  Plan proposes (B) — update acceptance criterion 2 in brief §5 note:
  "Does **not** mint a new token" is inaccurate for the SHA-hash
  storage model. Resend **does** rotate the token in storage; UI
  invite-id (row PK) stays stable. Flagging for user confirmation
  before Step 3 ships.
- **Type filter semantics**: substring vs. prefix? Plan uses prefix
  (`type LIKE @type || '%'`) because prefix lends to category matching
  (`TENANT.MEMBER` catches invited/joined/removed/role-changed). Full
  substring would be ambiguous.
- **Actor resolution for audit rows**: the audit handler returns
  `tags.userId` (caller UUID) verbatim; 18-8 UI does a 1-hop lookup to
  `displayName` via `GET /api/v1/users/{id}/display`. Confirm that
  endpoint exists — yes, present in `UsersEndpoints` post-18-1. No
  new backend lookup needed here.
- **Defence-in-depth probe test**: the RLS-rejection assertion in Step
  5 requires connecting as the `app_user` role (not the postgres
  superuser used for test setup). Fixture `TestDbUserFactory` already
  distinguishes — confirm post-19-6 wiring is in place before this
  ships; documented as co-requisite.
