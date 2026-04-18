# Finding 003: Service-key POST permission drift — `SettingsManage` vs owner-only

**Scope**: admin-db
**Severity**: P1
**Status**: Behavioral drift
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/admin/service-keys.ts`.

- File: `packages/api/src/routes/admin/service-keys.ts:29-45` (and every other route handler in that file)
- Contract/behavior: every service-key route is gated by `requirePermission('settings:manage')`. In the RBAC matrix (epic 16-5), `settings:manage` is an **owner-only** permission — `admin` and `member` cannot create, list, rotate, or revoke service keys. Service keys are platform credentials (Elsa → API, etc.) so they must be owner-gated.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/admin/service-keys.ts (9e9a57c~1)
app.post<{...}>(
  '/api/admin/service-keys',
  { preHandler: [requirePermission('settings:manage')] },  // OWNER ONLY
  async (request, reply) => { ... }
);
app.get('/api/admin/service-keys',
  { preHandler: [requirePermission('settings:manage')] }, ...);
app.post<{Params:{id:string}}>('/api/admin/service-keys/:id/rotate',
  { preHandler: [requirePermission('settings:manage')] }, ...);
app.delete<{Params:{id:string}}>('/api/admin/service-keys/:id',
  { preHandler: [requirePermission('settings:manage')] }, ...);
```

- Dependencies: `requirePermission()` from `../../auth/require-permission.ts`, which resolves the caller's role from a JWT cookie and matches against the RBAC permission map.
- Tests that exercised this: `packages/api/src/__tests__/create-app-admin-auth.test.ts` asserts 401 for unauthenticated callers. No explicit test for admin-vs-owner distinction, but `requirePermission('settings:manage')` is unit-tested in the RBAC tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Program.cs:337-343`
- Contract/behavior: the entire `/api/admin` group is gated by `.RequireAuthorization("AdminAccess")`, which maps to the `admin:access` permission (admin + owner). `SettingsManage` is defined as a separate policy but is **not** applied to service-key routes. An admin (not just owner) can now create/rotate/delete platform service keys.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current)
var admin = app.MapGroup("/api/admin").RequireAuthorization("AdminAccess");
admin.MapGet("/health", AdminEndpoints.GetHealth);
admin.MapPost("/service-keys", AdminEndpoints.CreateServiceKey);        // admin+, not owner-only
admin.MapGet("/service-keys", AdminEndpoints.ListServiceKeys);
admin.MapPost("/service-keys/{id}/rotate", AdminEndpoints.RotateServiceKey);
admin.MapDelete("/service-keys/{id}", AdminEndpoints.DeleteServiceKey);
```

Compared to policies elsewhere in the same file, e.g.:
```csharp
admin.MapPut("/users/{id}/role", AdminEndpoints.UpdateUserRole).RequireAuthorization("OwnerAccess");
admin.MapDelete("/users/{id}", AdminEndpoints.DeleteUser).RequireAuthorization("OwnerAccess");
```
— role-changing and user-deletion *are* owner-gated, but service keys are not.

- Dependencies: `PermissionHandler` (`apps/tamma-elsa/src/Tamma.Api/Auth/PermissionHandler.cs`), policy map in `Program.cs:197-242`.
- Tests: no Tamma.Api.Tests currently asserts that an admin JWT is *rejected* from POST `/api/admin/service-keys`. The gap is silently untested.

## 3. The gap

- TS did: reject an admin-role caller from `POST /api/admin/service-keys` with 403.
- C# does: accept an admin-role caller, silently minting a platform-level API key.
- For a caller with `role=admin` JWT, TS returns 403 and C# returns 201 with a live `tamma_sk_...` key.
- In production with existing data / deployed clients, this means: any organization admin (not just the owner) can mint service-to-service keys and use them to bypass tenant boundaries. This is a **privilege-escalation regression**.

Error paths:
- TS error path: `403 { error: "Forbidden" }` (`requirePermission()` throws).
- C# error path: succeeds with 201 for admin, 403 only for `member`.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-5-role-based-access-control.md` (defines `settings:manage` permission set) and `docs/stories/epic-16/16-7-service-to-service-auth.md` (defines service-key CRUD as owner-only).
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story 16-7 AC explicitly says service keys are owner-only; TS honored it, C# widened access.

## 5. Status

- **Classification**: Behavioral drift
- **What's needed to finish**:
  1. Change `Program.cs:340-343` from `admin.MapPost("/service-keys", ...)` (inherits `AdminAccess`) to append `.RequireAuthorization("SettingsManage")` on each service-key route.
  2. Update `Tamma.Api.Tests` to assert 403 for admin JWTs on these routes.
- **Is it "just a stub" or is scope missing?** The scope was ported but the policy name was wrong. Easy fix.
- **Blockers**: none. Policies `SettingsManage` and `OwnerAccess` already exist in `Program.cs:217-221`.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Program.cs:340-343`.
- Files to create: none.
- Tests to add: `Tamma.Api.Tests/Admin/ServiceKeyAuthTests.cs` — admin JWT → 403, owner JWT → 201.
- Estimated effort: 2h broken down as:
  - Policy swap: 15min
  - Tests: 1.5h
  - Regression pass on dashboard: 15min

## References

- TS source: `packages/api/src/routes/admin/service-keys.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Program.cs`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md`
- Related findings: `004-service-keys-owner-id-hardcoded.md`, `005-service-keys-tenant-auto-bound.md`
- CLAUDE.md section: "Naming Conventions → API Endpoints" (service-key CRUD)
