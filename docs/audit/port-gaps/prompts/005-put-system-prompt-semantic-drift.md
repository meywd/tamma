# Finding 005: PUT/DELETE /api/prompts/system/:role/:action semantic drift

**Scope**: prompts
**Severity**: P1 (feature broken — admin endpoint does the wrong thing)
**Status**: Semantic rewrite (structure changed, not a port)
**Estimated port effort**: 2h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (URL normalized to {role}-only)
- **Commit**: ea4d5e5
- **Notes**: Per CLAUDE.md, role-system overrides are keyed by `(userId, role)` only — there is no action axis. The `{action}` URL segment was silently ignored. Renamed routes to `PUT /api/prompts/system/{role}` and `DELETE /api/prompts/system/{role}`; removed the dead `action` parameter from `UpsertSystemPrompt`/`DeleteSystemPrompt`. The TS endpoint's "platform-admin writes to a global system default" semantic was deliberately not restored — CLAUDE.md does not describe such a write path; system defaults remain in code (`SystemPrompts.cs`, immutable at runtime). The route is now coherent with the data model and authorization (`SettingsManage` permission gates per-user role-system override). Wires `EmitCreatedAsync` for new rows and `EmitResetAsync` for deletes (closes part of finding 007).

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/prompts/prompt-routes.ts`.

- File: `packages/api/src/routes/prompts/prompt-routes.ts:214-265` (PUT/DELETE for `/system/:role/:action`)
- Contract/behavior: `PUT /api/prompts/system/:role/:action` is a **platform-admin-only** endpoint that updates the row in the `prompts` table where `tenant_id IS NULL` — i.e., it mutates the system default seen by every tenant. `DELETE` on the same URL restores the hardcoded default from `default-prompts.ts` by deleting that NULL-tenant row (so the next read falls back to the in-memory `SYSTEM_PROMPTS` export).
- Key code (verbatim quote, `prompt-routes.ts:214-241`):

```typescript
// packages/api/src/routes/prompts/prompt-routes.ts (9e9a57c~1)
// ---------- PUT /api/prompts/system/:role/:action ----------
// Update a system default prompt (platform admin only).
app.put(
  '/api/prompts/system/:role/:action',
  async (request, reply) => {
    if (!isPlatformAdmin(request)) {
      return reply.status(403).send({
        error: 'Only platform administrators can modify system defaults',
      });
    }

    const { role, action } = request.params;
    const body = request.body as UpsertBody;
    if (!validateUpsertBody(body, reply)) return;

    try {
      const input = buildUpsertInput(body);
      const userId = getUserId(request);
      const updated = await store.upsertSystemDefault(role, action, input, userId);
      return reply.status(200).send(updated);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to update system default';
      return reply.status(400).send({ error: message });
    }
  },
);
```

`store.upsertSystemDefault()` in `pg-prompt-store.ts` used the `ON CONFLICT (role, action) WHERE tenant_id IS NULL` partial unique index to write the NULL-tenant row.

- Dependencies: `isPlatformAdmin()` guard, `upsertSystemDefault()` / `resetSystemDefault()` store methods, partial unique index `idx_prompts_system_default`.
- Tests that exercised this: `prompt-routes.test.ts` — "platform admin can PUT /system", "non-admin gets 403".

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:194-246` (`UpsertSystemPrompt`, `DeleteSystemPrompt`) and `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:219-241` (`UpsertRoleSystemAsync`, `DeleteRoleSystemAsync`).
- Contract/behavior: `PUT /api/prompts/system/:role/:action` writes a row in `prompt_overrides` with `Scope = "role-system"` — a **user-scoped override** of the role's *system prompt (preamble)*, not of the role+action template. Authorization via `RequireAuthorization("SettingsManage")` (any user with the SettingsManage permission, not platform admin). The C# endpoint also accepts an `{action}` URL parameter but **ignores** it when scoping the write — the new row has `Action = null`. `DELETE` removes the user's own role-system override, not the platform-wide default.
- Key code (verbatim quote, `PromptEndpoints.cs:194-225`):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs (current)
public static async Task<IResult> UpsertSystemPrompt(
    string role,
    string action,                      // <-- URL param accepted but ignored in storage
    UpsertPromptRequest req,
    PromptStoreService store,
    PromptEventsService events,
    ClaimsPrincipal principal,
    ITenantContext tenantContext)
{
    var userId = TryGetUserId(principal);
    var input = new UpsertPromptInput(
        Template: req.Template,
        SystemPrompt: req.SystemPrompt,
        Variables: req.Variables,
        EnableTools: req.EnableTools,
        MaxTokens: req.MaxTokens);

    var saved = await store.UpsertRoleSystemAsync(userId, tenantContext.TenantId, role, input);
    // ^^^ action parameter is NOT passed through — the role-system row has Action=null
    ...
    return Results.Ok(new { message = "System prompt updated", scope = "role-system", role });
}
```

Store method (`PromptStoreService.cs:219-238`):

```csharp
public async Task<PromptOverride> UpsertRoleSystemAsync(
    Guid? userId,
    Guid? tenantId,
    string role,
    UpsertPromptInput input)
{
    return await _repository.UpsertAsync(new PromptOverride
    {
        UserId = userId,
        TenantId = tenantId,
        Scope = "role-system",
        Role = role,
        Action = null,         // <-- always null regardless of URL action
        ...
```

- Dependencies: `PromptRepository.UpsertAsync`, `PromptEventsService.EmitUpdatedAsync`, JWT auth with `SettingsManage` scope.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/PromptStoreServiceTests.cs` — covers `UpsertRoleSystemAsync` but not the endpoint's semantic mismatch with the TS contract.

## 3. The gap

Concrete behavioral difference:

Three distinct mismatches stacked in one endpoint:

1. **Scope axis changed**: TS writes a *platform-wide system default* (tenant NULL). C# writes a *user-scoped role-system preamble override*. These are different database rows with different visibility.
2. **`action` path parameter ignored**: TS uses both `role` and `action` to key the write. C# accepts `{action}` in the URL but silently drops it. `PUT /api/prompts/system/developer/plan` and `PUT /api/prompts/system/developer/code-review` write **identical rows** (same `Role = "developer"`, `Action = null`).
3. **Authorization model changed**: TS gated on `role === 'owner'` (platform admin). C# gated on the `SettingsManage` permission, which any tenant admin has.

For a caller flow:
- Before: `PUT /api/prompts/system/developer/plan` with `{ "template": "..." }` as the owner → all tenants see the new developer-plan template.
- After: same request as a tenant admin → writes a role-system preamble override (not a role+action template) for that user only; the developer-plan template remains the hardcoded default for everyone.

In production with existing data / deployed clients, this means: workflows/dashboards that relied on the endpoint to push a global system-default refresh will silently no-op on the role+action template and instead overwrite the role's preamble for the caller. The error is subtle because the endpoint returns 200 OK with `{ message: "System prompt updated", scope: "role-system", role }`.

Error paths:
- TS error path: 403 when caller is not owner; 400 on invalid body.
- C# error path: 401 when not authenticated; 403 when caller lacks `SettingsManage`; 200 even when the URL's `action` segment was meaningless.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md` AC #7 and #8.
- Story's acceptance criteria for this behavior: AC #7 says *"PUT /api/prompts/system/:role/:action updates a system default prompt (platform admin only); returns 403 for non-admins"*. This unambiguously describes the TS behavior — update the platform default and require admin.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs the story's AC #7)
  - [ ] Matches C# behavior — story AC explicitly says "system default prompt", not "user preamble override".

## 5. Status

- **Classification**: Semantic rewrite — same URL, completely different operation.
- **What's needed to finish**:
  1. Decide whether platform-admin writes to system defaults should exist at all. If yes, mount them under a distinct URL (e.g., `/api/admin/system-prompts/:role/:action`) and require `owner` role; if no, deprecate the URL and document the break.
  2. Either:
     - **Restore TS semantics**: re-introduce a separate "system defaults" table (or a `scope = "platform-default"` row where `UserId IS NULL`), add an owner-only authorization, use both `role` and `action` URL parameters as the key.
     - **Rename URL**: change the route to `/api/prompts/system-prompt/:role` (dropping `{action}`) to reflect what the endpoint actually does.
- **Is it "just a stub" or is scope missing?** Scope was misunderstood during port — the verb "system" referred to different concepts ("system-default full template" in TS vs "role-system preamble" in C#) and the router collapsed them.
- **Blockers**: Finding #004 (tenant vs user scoping) — the right fix depends on whether overrides are tenant-scoped or user-scoped.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs:388-389` — either drop the `{action}` segment or re-route to a dedicated admin endpoint.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:194-246` — remove the `action` parameter signature if it remains unused, or wire it through if the intent is role+action scope.
  - `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:219-241` — if adding "platform-default" scope, thread it through repository queries.
- Files to create:
  - New admin endpoint `SystemDefaultEndpoints.cs` OR rename `UpsertSystemPrompt`/`DeleteSystemPrompt` to `UpsertRoleSystemOverride`/`DeleteRoleSystemOverride` and change URL.
- Tests to add:
  - `PromptEndpointsTests.cs` — `PutSystemPromptWithDifferentActionsYieldsDifferentRows` (currently fails because action is dropped).
  - `PromptEndpointsTests.cs` — `NonAdminCannotUpdatePlatformSystemDefault` (currently passes accidentally).
- Estimated effort: 2h broken down as:
  - Decision + route design: 0.5h
  - Impl refactor: 1h
  - Tests: 0.5h

## References

- TS source: `packages/api/src/routes/prompts/prompt-routes.ts:214-265` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:194-246`, `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:219-241`
- Story: `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md` AC #7
- Related findings: `docs/audit/port-gaps/prompts/004-tenant-scoped-to-user-scoped.md`, `docs/audit/port-gaps/prompts/006-missing-defaults-endpoints.md`
- CLAUDE.md section: "Prompt Store Architecture > Resolution Order"
