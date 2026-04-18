# Finding 010: `PUT /orgs/:id/settings` Cannot Rename and Drops Length Validation

**Scope**: orgs
**Severity**: P2 (correctness)
**Status**: Incomplete (partial port — name field dropped entirely)
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:193-236`.
- Contract/behavior: the endpoint accepts `{ name?, settings? }` in the body. When `name` is present, TS validates `2 <= name.trim().length <= 100` and includes it in the update; when `settings` is present, it is included as a JSONB replacement. At least one of the two must be provided or the handler returns 400 "No fields to update". Response echoes the full tenant dto.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L193-L236
app.put<{
  Params: { tenantId: string };
  Body: { name?: string; settings?: Record<string, unknown> };
}>(
  '/api/v1/orgs/:tenantId/settings',
  async (request, reply) => {
    const jwt = await getAuthenticatedUser(request, reply);
    if (!jwt) return;

    const { tenantId } = request.params;

    // Verify admin+ role
    const membership = await membershipStore.getMembership(tenantId, jwt.sub);
    if (!membership || (ROLE_HIERARCHY[membership.role] ?? 0) < (ROLE_HIERARCHY['admin'] ?? 1)) {
      return reply.status(403).send({ error: 'Requires admin role or higher' });
    }

    const { name, settings } = request.body ?? {};
    const update: Partial<{ name: string; settings: Record<string, unknown> }> = {};
    if (name !== undefined) {
      if (typeof name !== 'string' || name.trim().length < 2 || name.trim().length > 100) {
        return reply.status(400).send({ error: 'Name must be between 2 and 100 characters' });
      }
      update.name = name.trim();
    }
    if (settings !== undefined) {
      update.settings = settings;
    }

    if (Object.keys(update).length === 0) {
      return reply.status(400).send({ error: 'No fields to update' });
    }

    const tenant = await tenantStore.updateTenant(tenantId, update);

    return reply.send({
      id: tenant.id,
      name: tenant.name,
      slug: tenant.slug,
      plan: tenant.plan,
      settings: tenant.settings,
    });
  },
);
```

- Dependencies: `ITenantStore.updateTenant(id, { name?, settings?, plan?, slug? })` from `packages/api/src/persistence/pg-tenant-store.ts:60-106`.
- Tests: explicit tests for 400 on too-short name, 200 on rename, 200 on settings-only.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:45-55`, `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs:4`.
- Contract/behavior: the request body shape is `{ Settings: object }` — no `Name` field. Settings are stringified via `JsonSerializer.Serialize(req.Settings)` and written to `tenant.Settings`. No length validation anywhere.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs (current) L4
public record UpdateOrgSettingsRequest(object Settings);   // ← no Name, no Plan
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L45-L55
public static async Task<IResult> UpdateOrgSettings(
    Guid tenantId,
    UpdateOrgSettingsRequest req,
    ITenantRepository tenantRepo)
{
    var tenant = await tenantRepo.GetByIdAsync(tenantId);
    if (tenant is null) return Results.NotFound(new { error = "Organization not found" });
    tenant.Settings = System.Text.Json.JsonSerializer.Serialize(req.Settings);
    await tenantRepo.UpdateAsync(tenant);
    return Results.Ok(new { message = "Settings updated" });
}
```

- Dependencies: `ITenantRepository.UpdateAsync` exists.
- Tests: none.

## 3. The gap

Concrete behavioral difference — what a caller can do.

- TS did: `{ "name": "New Name" }` rename-only, `{ "settings": {...} }` settings-only, `{ "name": "New", "settings": {...} }` combined, `{}` → 400 "No fields to update", `{ "name": "A" }` → 400 "Name must be between 2 and 100 characters".
- C# does: only `{ "settings": {...} }` is honored. A rename request `{ "name": "New Name" }` is silently discarded because the DTO has no `Name` property, and no validation runs. `{}` results in `Settings = "null"` being written to the tenant row (the `object` binds to null, `Serialize(null) = "null"`), which is a silent data corruption path.
- For a caller sending `PUT /api/v1/orgs/:id/settings {"name":"Acme Inc."}`: TS renames to "Acme Inc."; C# writes `Settings = "{\"Name\":\"Acme Inc.\"}"` — the JSON body is swallowed whole into settings as a string, because `req.Settings` is declared `object` and the binder dumps the whole body.
- For `{}`: TS returns 400; C# writes `Settings = "null"` and returns 200.
- In production: the dashboard's org-settings page cannot rename the organization. Plans to expose plan switching here are blocked too (no `plan` field in the DTO).

Error paths:
- TS error path: `400 { "error": "Name must be between 2 and 100 characters" }`, `400 { "error": "No fields to update" }`, `403 { "error": "Requires admin role or higher" }`.
- C# error path: only `404 { "error": "Organization not found" }`.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - AC 11: "**Organization settings** endpoint `GET/PUT /api/v1/orgs/:tenantId/settings` for name, billing plan, default provider config (updates `tenants.settings` and `tenants.name`)".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Incomplete (partial port).
- **What's needed to finish**:
  1. Change `UpdateOrgSettingsRequest` to `(string? Name, string? Plan, object? Settings)`.
  2. In `UpdateOrgSettings`, validate `Name.Trim().Length 2..100` when present; validate `Plan in { "free", "pro", "enterprise" }` when present; return 400 if all three are null.
  3. Only write the fields the caller provided; do not clobber `Name` to empty string or `Settings` to `"null"`.
  4. Membership-level check also missing (finding 001) — ensure caller is admin+ of path tenant.
  5. Echo the full tenant DTO in the response to match TS contract.
- **Is it "just a stub" or is scope missing?** Scope was understood (AC 11 names both `tenants.settings` and `tenants.name`); port shrank to settings-only.
- **Blockers**: none; depends on finding 001 for the membership gate.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs` (add Name/Plan).
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs`.
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/UpdateOrgSettingsTests.cs`.
- Tests to add:
  - `UpdateSettings_RenamesTenant_WhenNameProvided`
  - `UpdateSettings_Returns400_WhenNameTooShort`
  - `UpdateSettings_Returns400_WhenNameTooLong`
  - `UpdateSettings_Returns400_WhenNoFieldsProvided`
  - `UpdateSettings_PersistsJsonSettings_Unchanged_WhenOnlyNameProvided`
  - `UpdateSettings_Returns403_WhenMember_NotAdmin`
- Estimated effort: 0.5h broken down as:
  - DTO + handler changes: 0.25h
  - Tests: 0.25h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:193-236` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:45-55`, `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs:4`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (AC 11)
- Related findings: `001-cross-tenant-access-on-path-tenantid.md`, `007-post-orgs-validation-missing.md`
