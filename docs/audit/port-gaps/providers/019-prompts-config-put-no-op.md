# Finding 019: `PUT /api/config/prompts/{role}` is a no-op stub

**Scope**: providers
**Severity**: P1 (feature broken on settings path; duplicate surface elsewhere)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 2–3h (or close as duplicate of `/api/prompts/...`)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/settings/prompts-routes.ts` and
`git show 9e9a57c~1:packages/api/src/services/settings/ConfigService.ts`.

- File: `packages/api/src/routes/settings/prompts-routes.ts:27-45`
- Contract/behavior: `PUT /prompts/:role` validated the `role` against a whitelist of 10 names (`VALID_ROLES` at lines 8-19), then called `ConfigService.updatePromptTemplate(role, {systemPrompt?, providerPrompts?})`. The service persisted to the in-memory `agentsConfig` (the CLI-mode case) and separately synced to the ELSA Agents DB via `ElsaAgentsClient.updateAgent` for the llm-call workflow.

```typescript
// packages/api/src/routes/settings/prompts-routes.ts (9e9a57c~1) — lines 27-45
app.put('/prompts/:role', async (request, reply) => {
  try {
    const params = request.params as { role: string };
    if (!VALID_ROLES.has(params.role)) {
      return reply.status(400).send({ error: `Unknown role: ${params.role}` });
    }
    const body = request.body as {
      systemPrompt?: string;
      providerPrompts?: Record<string, string>;
    };
    await service.updatePromptTemplate(params.role, body);
    return reply.send({ message: `Prompts updated for role: ${params.role}` });
  } catch (error) {
    const message = error instanceof Error ? error.message : 'Failed to update prompts';
    return reply.status(400).send({ error: message });
  }
});
```

- `ConfigService.updatePromptTemplate` (ConfigService.ts:118-168) did real work: deep-cloned the config, respected empty-string-means-delete semantics, rejected prototype-pollution keys, and fired-and-forgot the ELSA sync.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:148-157`
- Contract/behavior: Stub. Returns a success message. No body parse, no validation, no persistence, no ELSA sync.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs — lines 148-157
public static async Task<IResult> GetPromptsConfig(IAgentConfigRepository configRepo, ITenantContext tc)
{
    var config = await configRepo.GetAsync(tc.TenantId);
    return Results.Ok(config is not null ? JsonSerializer.Deserialize<object>(config.Config) : new { });
}

public static async Task<IResult> UpdatePromptsConfig(string role, IAgentConfigRepository configRepo, ITenantContext tc)
{
    return Results.Ok(new { message = $"Prompt config for role '{role}' updated" });
}
```

- There **is** a working prompt CRUD surface elsewhere: `/api/prompts/{role}/{action}` via `PromptEndpoints.UpsertPrompt` (`Program.cs:386`). That one is fully implemented and uses `PromptOverride` table (`apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs`).

## 3. The gap

- The settings-UI `PUT /api/config/prompts/{role}` endpoint appears to succeed but stores nothing.
- There is redundancy: `/api/config/prompts/{role}` (stub) and `/api/prompts/{role}/{action}` (working). The TS version of `/api/config/prompts/:role` targeted an in-memory `agentsConfig` object + ELSA Agents DB, i.e. a different storage. The C# path lacks any storage backing.
- For a caller doing `PUT /api/config/prompts/developer {systemPrompt: "You are..."}`:
  - TS: `200 {message: "Prompts updated for role: developer"}`, in-memory config updated, ELSA Agents DB synced.
  - C#: `200 {message: "Prompt config for role 'developer' updated"}`. Nothing persisted.
- Callers integrating against the Tamma dashboard's "Settings → Agents → Prompt Templates" page see their edits appear to save but never take effect on the next LLM call.

Error paths:
- TS: `400` for invalid role, `400` for prototype-pollution key.
- C#: no error paths; every PUT succeeds trivially.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-12/12-5-prompt-engineering-framework.md`.
- Story 12-5 describes the prompt store architecture in detail (Role+Action 2D, Postgres-backed, user/tenant overrides). The C# `/api/prompts/{role}/{action}` endpoint **does** implement this. The stubbed `/api/config/prompts/{role}` settings endpoint is the **old, pre-12-5** shape from `ConfigService.ts`.
- Story alignment:
  - [ ] Matches TS behavior (C# is a regression vs TS behavior on the same URL).
  - [ ] Matches C# behavior.
  - [x] Describes a third behavior — Story 12-5 says the Prompt Store is the canonical surface; the settings-path endpoint is effectively deprecated.
  - [x] No story — the decision to deprecate `/api/config/prompts/{role}` in favour of `/api/prompts/{role}/{action}` is not documented anywhere.

## 5. Status

- **Classification**: Not-yet-implemented (stub); also ambiguous whether to implement or delete.
- **What's needed to finish**. Two options:

  **Option A — delete the stub**: remove `GetPromptsConfig` and `UpdatePromptsConfig` from `SettingsEndpoints.cs`, delete the two route registrations in `Program.cs:405-406`, publish a deprecation note pointing callers at `/api/prompts/{role}/{action}`.

  **Option B — make it a thin proxy**: rewrite `UpdatePromptsConfig(string role, UpdatePromptsConfigRequest body, IPromptStore store)` to upsert the `{role}/default` action entry in the Prompt Store. This preserves the settings-UI URL but routes to the Epic 12 storage.

  Either option is acceptable. Option A is cleaner; Option B avoids dashboard churn.
- **Is it "just a stub" or is scope missing?** The scope was intentionally moved (12-5 replaced the settings-path surface) but the stub wasn't removed. So: scope is defined elsewhere, the stub is a carry-over.
- **Blockers**: Needs a product decision on whether the settings-UI path stays.

## Remediation

- Files to modify (Option A):
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:148-157` (delete)
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs:405-406` (delete)
- Files to modify (Option B):
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:148-157` (implement as proxy)
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Settings/UpdatePromptsConfigRequest.cs` (new)
- Tests to add (Option A):
  - `Settings_PromptsConfig_Returns404` (route-not-found assertion)
- Tests to add (Option B):
  - `Settings_UpdatePromptsConfig_ProxiesToPromptStore`
  - `Settings_UpdatePromptsConfig_InvalidRole_Returns400`
- Estimated effort: 1h (Option A) / 3h (Option B).

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed (Option A — deprecated with 410 Gone)
- **Commit**: `0dbccf9` `fix(providers): land P1/P2 diagnostics/health/validation/user-providers fixes [findings 008, 009, 010, 012, 013, 014, 018, 019]`
- **Notes**: `GET /api/config/prompts` and `PUT /api/config/prompts/{role}` now return `410 Gone` with a clear pointer to the canonical `GET/PUT /api/prompts/{role}/{action}` (Story 12-5 / `PromptStore`). Eliminates the silent-success failure mode (callers used to think saves succeeded). Route registrations kept so the 410 is observable; deletion would yield a confusing 404.

## References

- TS source: `packages/api/src/routes/settings/prompts-routes.ts`, `packages/api/src/services/settings/ConfigService.ts:118-168` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:148-157`
- Story: `docs/stories/epic-12/12-5-prompt-engineering-framework.md`, `apps/wiki-site/public/content/stories/epic-12/12-5-prompt-engineering-framework.md`
- Related findings: `018-user-scoped-providers-put-no-op.md`
- CLAUDE.md section: "Prompt Store Architecture"
