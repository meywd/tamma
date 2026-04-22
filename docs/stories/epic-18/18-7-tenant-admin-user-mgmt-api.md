# Story 18-7: Tenant-Admin User Management API completion

Status: todo (planning brief, 2026-04-21)

## Story

As a **tenant admin**,
I want the API surface for managing users inside my tenant to expose
resend-invite, a tenant-scoped audit log view, and to emit a role-
change event so the audit log is complete,
so that the upcoming tenant-admin user-management UI (Story 18-8) has
every endpoint it needs and so the audit trail of who did what to
whom is both complete and queryable by the tenant admin without
platform-admin access.

## Narrative

`OrgEndpoints.cs` already ships the full set of hierarchy-respecting
mutations: invite, list members, change role, remove member, transfer
ownership, list/delete invites. The audit (see
[`plans/tenant-user-mgmt-audit.md`](../plans/tenant-user-mgmt-audit.md))
found three thin gaps:

1. `UpdateMemberRole` changes the role but does not append to the
   event store. This makes the audit trail incomplete: every other
   tenant mutation emits an event; role change only writes a logger
   line. Missing event type: `TENANT.MEMBER_ROLE_CHANGED.SUCCESS`.
2. No first-class "resend invite" endpoint. The admin UI has to
   delete-and-recreate to nudge a user, which creates a new token and
   a new DB row (poor UX; invite-id changes; email throttle can't
   sanely distinguish retries).
3. No tenant-scoped audit log view. Events **are** emitted with
   `Tags.tenantId`, but there's no `GET /api/v1/orgs/{tenantId}/audit`
   endpoint tenant admins can call. The platform-admin event viewer is
   at `/api/admin/events` and is RBAC-gated to `platform_admin`.

This story closes all three gaps. Pure backend + tests; Story 18-8
takes it to the UI.

## Acceptance Criteria

1. `UpdateMemberRole` in `OrgEndpoints.cs` appends a
   `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` event to the event store with
   `Tags = { tenantId, userId = caller, targetUserId, oldRole, newRole }`
   in the same transaction semantics as the rest of the tenant event
   emitters (fire-and-forget after the role update commits; logger
   failure does not unwind the role change). Covered by unit test.
2. New endpoint `POST /api/v1/orgs/{tenantId}/invites/{inviteId}/resend`:
   - Auth: admin+ inside the path tenant (same filter chain as other
     invite endpoints).
   - Behaviour: loads the pending invite; if `AcceptedAt is not null`
     or `ExpiresAt < now`, returns 400 with the same error shape used
     by `AcceptInvite`. On the happy path, extends `ExpiresAt` by 72
     hours and dispatches the same `TenantInviteEmail` template the
     original create flow used. Does **not** mint a new token
     (UI-visible invite id stays stable; old link keeps working).
   - Emits `TENANT.MEMBER_INVITE_RESENT.SUCCESS` with
     `Tags = { tenantId, userId = caller, inviteId, email }`.
   - Rate limited via existing `IRateLimitService` keyed as
     `resend-tenant-invite:{tenantId}:{inviteId}` — 3 resends per
     hour per invite. Over the limit returns 429 with the same
     shape `AuthEndpoints.ResendVerification` uses.
3. New endpoint `GET /api/v1/orgs/{tenantId}/audit?limit=&offset=&type=`:
   - Auth: admin+ inside the path tenant.
   - Returns events filtered by `Tags.tenantId == {tenantId}` from the
     event store, most-recent first, paginated (default `limit=50`,
     max `200`).
   - Optional `type` filter accepts a substring match against
     `event.type` (e.g. `TENANT.MEMBER` matches role-change +
     invite + remove).
   - Response shape `{ events: [...], total, limit, offset }`. Each
     event row exposes `id, type, createdAt, tags, data` — `Metadata`
     is stripped (platform-internal fields).
   - RLS defence in depth: even if the filter is bypassed by a bug,
     the `events` table's RLS policy (Epic 28 phase B) restricts the
     app-role connection to rows where `tags->>'tenantId'` matches
     `app.current_tenant_id`. The endpoint explicitly sets the tenant
     context before the query.
4. `ITenantInviteRepository` grows an `ExtendExpiryAsync(inviteId,
   newExpiresAt)` method used by task 2. Old rows that use `Tenant
   _MEMBER_INVITED.SUCCESS` remain untouched; the resend path emits
   its own event type to keep the analytics distinction.
5. `IEventRepository` grows a `ListByTenantAsync(tenantId, type?,
   limit, offset)` method (or the existing list method accepts these
   filters). Implementation uses a single SQL query with an index on
   `(tenant_id, created_at desc)` — event table already indexes
   `tenant_id` post-Epic-28. No new migration needed.
6. Swagger / OpenAPI output registers all three new endpoints with
   correct auth descriptors so the dashboard client generator picks
   them up (Story 18-8 consumes the generated client).
7. Unit tests in `Tamma.Api.Tests.Endpoints.OrgEndpointsTests`:
   - `UpdateMemberRole` appends the event (verifies `Tags` payload).
   - `ResendInvite` extends expiry, does not rotate the token,
     emits the resend event, sends the email.
   - `ResendInvite` rejects accepted / expired invites with 400.
   - `ResendInvite` returns 429 after 3 calls within one hour.
   - `ListTenantAudit` returns only rows with the matching tenant
     tag, honours pagination, respects `type` filter.
   - Cross-tenant test: tenant A admin cannot read tenant B's audit
     log even with `type=TENANT.MEMBER_INVITED.SUCCESS` — gets 403
     from the `RequireTenantMembershipFilter`.
8. Integration test in `Tamma.Api.Tests.Integration`: full flow
   (create invite → resend → accept → change role → list audit →
   verify event sequence matches the UI expectation).

## Technical Context

### Existing code to modify

- `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs`:
  - Add event emission to `UpdateMemberRole`.
  - Add `ResendInvite` handler + route registration in
    `Program.cs` (matches the `DeleteInvite` pattern on the
    `/invites/{inviteId}` scope).
  - Add `ListTenantAudit` handler + route registration.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs`
  — add `ExtendExpiryAsync`.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs`
  — extend list method with optional type filter.
- `apps/tamma-elsa/src/Tamma.Api/Services/Email/EmailTemplates.cs`
  — reuse existing `TenantInviteEmail` (no change needed).

### Not in scope

- Platform-admin audit view lives at `/api/admin/events`; this story
  does not touch it.
- Does not add new event types beyond `TENANT.MEMBER_ROLE_CHANGED`
  and `TENANT.MEMBER_INVITE_RESENT`.
- Does not introduce a separate audit-events table — the canonical
  `events` table with tenantId tag is the source of truth.

## Dependencies

- Epic 18 Stories 18-1 / 18-2 / 18-3 (existing org + auth infrastructure)
- Epic 28 Phase B (RLS on `events` table — provides the defence-in-
  depth for the audit endpoint)
- Blocks Story 18-8 (UI)

## Estimated hours

**14h** — three handlers + two repo methods + tests + OpenAPI
wiring.

| Task | Hours |
|---|---|
| Emit `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` + test | 1 |
| `ResendInvite` endpoint + rate-limit + test | 4 |
| `ListTenantAudit` endpoint + repo filter + test | 4 |
| Integration test | 2 |
| Swagger registration + dashboard client regeneration | 1 |
| Code review feedback buffer | 2 |

## Files touched

- `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` (route registration)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/OrgEndpointsTests.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Integration/TenantAuditFlowTests.cs`

## References

- Gap audit: [`../plans/tenant-user-mgmt-audit.md`](../plans/tenant-user-mgmt-audit.md)
- Existing OrgEndpoints: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs`
- Existing rate-limit service: `apps/tamma-elsa/src/Tamma.Api/Services/RateLimit/IRateLimitService.cs`
- Event-store contract: `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs`
