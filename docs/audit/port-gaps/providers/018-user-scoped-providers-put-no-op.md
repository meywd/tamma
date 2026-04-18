# Finding 018: `PUT /api/config/providers` is a no-op stub (user-scoped provider config)

**Scope**: providers
**Severity**: P1 (feature broken; SaaS user provider config goes nowhere)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 6–8h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/settings/providers-routes.ts` and
`git show 9e9a57c~1:packages/api/src/services/settings/ConfigService.ts`.

- File: `packages/api/src/routes/settings/providers-routes.ts:34-62`
- Contract/behavior: `GET /providers` and `PUT /providers` were **user-scoped** (not tenant-scoped). The authenticated user's identity (from JWT or `X-User-Id` dev header) was looked up in `IUserStore.getUserSettings/updateUserSettings`, which persisted the `IProvidersConfig` JSONB to the `users.settings` column (migration 004).

```typescript
// packages/api/src/routes/settings/providers-routes.ts (9e9a57c~1) — lines 34-62
export function registerProvidersRoutes(app: FastifyInstance, service: ConfigService): void {
  app.get('/providers', async (request, reply) => {
    const userId = getUserId(request);
    if (!userId) return reply.status(401).send({ error: 'Authentication required' });
    const config = await service.getUserProviders(userId);
    return reply.send(config);
  });
  app.put('/providers', async (request, reply) => {
    const userId = getUserId(request);
    if (!userId) return reply.status(401).send({ error: 'Authentication required' });
    try {
      const body = request.body;
      if (!body || typeof body !== 'object' || Array.isArray(body)) {
        return reply.status(400).send({ error: 'Request body must be a JSON object' });
      }
      const updated = await service.updateUserProviders(userId, body as IProvidersConfig);
      return reply.send(updated);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Invalid configuration';
      return reply.status(400).send({ error: message });
    }
  });
}
```

- `ConfigService.updateUserProviders` (ConfigService.ts:187-193) validated via `validateProvidersConfig` and called `userStore.updateUserSettings(userId, config)`.
- `ConfigService.resolveForRepo` (ConfigService.ts:199-215) composed user providers + repo config for SaaS mode.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:159-168`
- Contract/behavior: `GET /api/config/providers` returns the **tenant**-scoped `agent_configs.config` blob (not user-scoped). `PUT /api/config/providers` is a hard-coded success response that does nothing.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs — lines 159-168
public static async Task<IResult> GetProvidersConfig(IAgentConfigRepository configRepo, ITenantContext tc)
{
    var config = await configRepo.GetAsync(tc.TenantId);
    return Results.Ok(config is not null ? JsonSerializer.Deserialize<object>(config.Config) : new { });
}

public static async Task<IResult> UpdateProvidersConfig(IAgentConfigRepository configRepo, ITenantContext tc)
{
    return Results.Ok(new { message = "Providers config updated" });
}
```

- The PUT handler:
  1. Doesn't read the request body at all — no `UpdateProvidersConfigRequest` DTO.
  2. Doesn't validate anything.
  3. Doesn't persist anything.
  4. Returns `200 {message: "Providers config updated"}`.
- There is no `IUserRepository.GetSettingsAsync` / `UpdateSettingsAsync` surface in the C# data layer (see audit `25-orgs.md` finding about `users.settings jsonb` missing).

## 3. The gap

- SaaS-mode user-scoped provider config is non-functional.
- Scope semantics flipped: TS was user-scoped; C# is tenant-scoped on GET and no-op on PUT.
- For a caller doing `PUT /api/config/providers {providers: {anthropic: {apiKey: "..."}}}`:
  - TS: `200 {providers: {anthropic: {apiKey: "..."}}}` after validating and persisting to `users.settings` JSONB.
  - C#: `200 {message: "Providers config updated"}`. The body is ignored. No row updated anywhere.
- For a caller doing `GET /api/config/providers`:
  - TS: `200 {providers: {...}}` from the user's `users.settings`.
  - C#: `200 {config, security, ...}` from the tenant's `agent_configs.config` — a different shape entirely.
- In production with existing data / deployed clients, this means: dashboard UI "Providers" page appears to let the user configure their API keys; user clicks Save; sees success; on reload, nothing changed. User reports "my API keys aren't being saved" — the issue is silent on the server.

Error paths:
- TS: `401` without auth, `400` on invalid body, `400` on `validateProvidersConfig` failure.
- C#: no error paths — every PUT succeeds.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (SaaS onboarding, user-scoped creds) and Epic 27 (prompt store) transitively references user-scoped config.
- CLAUDE.md § "Prompt Store Architecture" describes user-scoped storage: "User Overrides (per userId, stored in Postgres)".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story intent and TS).
  - [ ] Matches C# behavior.
  - [ ] Describes a third behavior.
  - [ ] No story — the user-scoped provider config was a TS concept; no C# story exists for it. Spec gap.

## 5. Status

- **Classification**: Not-yet-implemented (stub).
- **What's needed to finish**:
  1. Decide: keep tenant-scoped (current C# path) or add a user-scoped surface (TS path). Recommendation: add a new `GET /api/users/me/providers` + `PUT /api/users/me/providers` route that targets `users.settings` JSONB. Keep `/api/config/providers` as tenant-scoped.
  2. Add `Settings jsonb` column to `User` entity.
  3. Add `IUserRepository.GetSettingsAsync` / `UpdateSettingsAsync`.
  4. Add validator (port `validateProvidersConfig`).
  5. Wire endpoint with `RequireAuthorization` against the user's own account (not `SettingsManage` — users should edit their own API keys without being org admins).
  6. Implement `ConfigService.resolveForRepo`-equivalent for the repo-context composition (needed by Epic 27 prompt resolution).
- **Is it "just a stub" or is scope missing?** Both. The PUT is a literal stub (`return Results.Ok(new { message = "..." })`), and the scope (user-scoped vs tenant-scoped) was never re-specified.
- **Blockers**: Depends on adding `users.settings` column (also surfaced in `25-orgs.md` audit).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:159-168` (implement the PUT)
  - `apps/tamma-elsa/src/Tamma.Data/Entities/User.cs` (add `Settings` column)
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` (column mapping)
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` (new route if user-scoped)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/UserSettingsEndpoints.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/<next>_AddUserSettingsColumn.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Settings/ProvidersConfigValidator.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Settings/UpdateUserProvidersRequest.cs`
- Tests to add:
  - `UpdateUserProviders_PersistsToUsersSettings`
  - `UpdateUserProviders_InvalidConfig_Returns400`
  - `GetUserProviders_ReturnsEmptyWhenUnset`
  - `UpdateUserProviders_UnauthenticatedOther_Forbidden`
- Estimated effort: 7h.

## References

- TS source: `packages/api/src/routes/settings/providers-routes.ts`, `packages/api/src/services/settings/ConfigService.ts:176-215`, `packages/api/src/persistence/user-store.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:159-168`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (implicit), Epic 27 (prompt store with user overrides)
- Related findings: `019-prompts-config-put-no-op.md`, `014-agent-config-crud-validation-gaps.md`
- CLAUDE.md section: "Prompt Store Architecture — Data Model" (user overrides pattern)
