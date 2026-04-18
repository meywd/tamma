# Finding 025: `users.settings JSONB` column missing — user-level provider config has no home

**Scope**: auth (also crosses into providers)
**Severity**: P1 (SaaS-mode feature lost)
**Status**: Data-model regression
**Estimated port effort**: 3h

## 1. What's in TS

Pre-delete snapshots at archived SQL and user-store interface.

- Migration `004_user_settings.sql`:

```sql
-- database/archived-sql-migrations/004_user_settings.sql
-- Add settings JSONB column to users table for per-user provider configuration.
-- In SaaS mode, this is equivalent to ~/.tamma/providers.json in CLI mode.
ALTER TABLE users ADD COLUMN settings JSONB DEFAULT '{}' NOT NULL;

COMMENT ON COLUMN users.settings IS 'User-level provider config (IProvidersConfig): provider credentials, models, budgets. Equivalent to ~/.tamma/providers.json in CLI mode.';
```

- TS `User` interface includes `settings: IProvidersConfig` (`user-store.ts:14`).
- Default `DEFAULT_SETTINGS: IProvidersConfig = { providers: {} }` (`user-store.ts:119-121`).
- Callers:
  - `getUserSettings(userId)` → returns the JSONB as `IProvidersConfig`.
  - `updateUserSettings(userId, settings)` → writes JSONB.
  - Used by routes/settings/ and by LLM call middleware to resolve provider credentials when a request comes in from a SaaS user.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- `User.cs` entity (lines 1-30): no `Settings` property.
- `IUserRepository`: no `GetUserSettingsAsync` / `UpdateUserSettingsAsync`.
- EF migration `InitialSchema`: `users` table (lines 437-467) has no `Settings` column.
- The wider search for "settings" in `TammaDbContext.cs` finds matches at line 128 — `entity.Property(e => e.Settings).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");` — but that's on a DIFFERENT entity (some other — context suggests `AgentConfig` or `ProviderSession`; verified by grepping `AgentConfig.cs`).
- So there is a `.Settings` JSONB column somewhere in the C# schema, but **not on `users`**.
- SettingsEndpoints exist (`Endpoints/SettingsEndpoints.cs`) but at a tenant/agent level, not per-user.
- Tests: none exercise per-user settings.

## 3. The gap

In CLI mode, a user has `~/.tamma/providers.json` containing their API keys, model preferences, budgets. In SaaS mode (browser-only), this file has no equivalent on the user's machine. The TS approach was to mirror it into `users.settings` JSONB.

What Tamma actually uses user-level settings for:
- Anthropic / OpenAI / Gemini API keys the user brings ("BYOK" — bring-your-own-key).
- Default model choice overrides.
- Per-user budget limits.
- Feature flags (`enableTools`, `streamingPreferred`, etc.).

Without the column:
- **BYOK cannot be offered**. The user cannot configure their own provider keys via the dashboard; all LLM calls go through platform-owned keys (or nothing, if the platform hasn't configured any).
- **Per-user budget tracking broken**. All budget tracking must be tenant-level only. A user who wants to cap their own spend within a tenant's umbrella cannot.
- **Settings page has nothing to show**. Dashboard "My Provider Settings" page has no persistence layer.

Production scenario: A user logs into `dash.tamma.dev`, navigates to Settings → Providers, enters their Anthropic API key. The dashboard POSTs to `/api/config/providers` (line 407-408 of Program.cs). The handler routes to `SettingsEndpoints.UpdateProvidersConfig` — which writes to... ? Grep of SettingsEndpoints.cs shows it writes to the tenant level. So the user's "personal" key is written against the tenant (visible to all tenant members!). That's a privacy regression; users expected per-user scoping.

Error paths: none — the operation succeeds but at the wrong scope.

## 4. Gap from stories

- Referenced story: none explicitly covers `users.settings`. The column was added by a pre-epic migration.
- CLAUDE.md "Prompt Store Architecture" mentions user-scope prompt overrides → `prompt_overrides.user_id` exists. Per-user prompt scope is modeled. But per-user provider config isn't.
- Provider stories: `docs/stories/epic-9/` covers provider diagnostics / health / chain, but at tenant scope.
- Story alignment:
  - [x] Matches TS behavior (had the column)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — `users.settings` was an early TS addition not re-specified in a story

## 5. Status

- **Classification**: Data-model regression. User-level provider config no longer has a home.
- **What's needed to finish**:
  1. Add `Settings` property to `User` entity: `public string Settings { get; set; } = "{}";` mapped as `jsonb` with default `'{}'::jsonb`.
  2. Create EF migration `AddUserSettings.cs` adding the column.
  3. Add `GetUserSettingsAsync(Guid id) → Task<string>` and `UpdateUserSettingsAsync(Guid id, string json) → Task` on `IUserRepository`.
  4. Consider a typed `UserProviderConfig` DTO so callers don't pass raw JSON strings.
  5. Wire the SettingsEndpoints.UpdateProvidersConfig to detect scope: per-user if endpoint variant, per-tenant otherwise. OR introduce a new `/api/v1/me/settings` endpoint specifically for per-user.
  6. Update provider-resolution at call-time: look up user settings first, fall back to tenant config.
- **Is it "just a stub" or is scope missing?** Scope missing — the column and repo surface simply don't exist.
- **Blockers**: None. Storage decision can be JSONB text or a strongly-typed sub-entity. JSONB text matches TS's `IProvidersConfig` opaque shape.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Data/Entities/User.cs`, `TammaDbContext.cs`, `IUserRepository.cs`, `UserRepository.cs`, possibly `Endpoints/SettingsEndpoints.cs`.
- Files to create: EF migration `apps/tamma-elsa/src/Tamma.Data/Migrations/<ts>_AddUserSettings.cs`.
- Tests to add:
  - `UserRepository_GetUserSettings_DefaultsToEmptyJson`.
  - `UserRepository_UpdateUserSettings_Persists`.
  - `Migration_AddsSettingsColumn_WithDefaultEmptyJson`.
- Estimated effort: 3h
  - Entity + migration: 1h
  - Repo methods: 30m
  - Endpoint scope split + tests: 1h
  - SettingsEndpoint integration: 30m

## References

- TS source: `packages/api/src/persistence/user-store.ts:14, 119-121` (commit `9e9a57c~1`)
- Archived SQL: `database/archived-sql-migrations/004_user_settings.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/User.cs`, `Migrations/20260416172234_InitialSchema.cs:437-467`
- Story: No governing story — `users.settings` is pre-Epic-17 lore.
- Related findings: `022-user-repository-missing-methods.md` (depends on this for `GetUserSettingsAsync`/`UpdateUserSettingsAsync`)
- CLAUDE.md section: "Prompt Store Architecture" (as analog for per-user overrides modeling)
