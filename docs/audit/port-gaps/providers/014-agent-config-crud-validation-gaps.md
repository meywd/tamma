# Finding 014: Agent config CRUD missing ReDoS guard, provider regex, budget range, security split

**Scope**: providers
**Severity**: P2 (hardening regression on PUT-config)
**Status**: Incomplete port
**Estimated port effort**: 4–6h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/agents/agent-config-routes.ts` plus the shared validators.

- TS `PUT /api/v1/agents/config` body was validated via:
  - `validateAgentsConfig` from `@tamma/shared` — enforced `providerChain` non-empty, `provider` matches `/^[a-z0-9][a-z0-9_-]{0,63}$/`, rejects `__proto__`/`constructor`/`prototype`, `maxBudgetUsd in [0, 100]`, validated `phaseRoleMap` against `WorkflowPhase` × `AgentType`.
  - `validateSecurityConfig` — enforced `maxFetchSizeBytes in [0, 1 GiB]`, `blockedCommandPatterns` compile as valid regex, max 100 patterns, max 500 chars each, rejects nested-quantifier ReDoS shapes.
- The config document was a **union of two fields**: `{config: IAgentsConfig, security: SecurityConfig}`. A valid PUT could update one, the other, or both — TS validated each side separately.

```typescript
// packages/api/src/routes/agents/agent-config-routes.ts (9e9a57c~1) — lines 63-95
function validateConfigDocument(doc: Partial<AgentConfigDocument>): string[] {
  const errors: string[] = [];
  if (doc.agents !== undefined) {
    try { validateAgentsConfig(doc.agents); }
    catch (err) { errors.push(err instanceof TammaError ? err.message : ...); }
  }
  if (doc.security !== undefined) {
    try { validateSecurityConfig(doc.security); }
    catch (err) { errors.push(...); }
  }
  return errors;
}
```

- `POST /config/validate` exposed validation without persistence so dashboard UIs could dry-run.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs:178-227` (`ValidateConfigShape`)
- Contract/behavior: Validates only that:
  1. Body parses as JSON.
  2. Root is an object.
  3. Any `roles` key exists as a JSON object.
  4. Each role key is in `RolePhaseMap.ValidRoles` (and not a forbidden prototype key).

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs — lines 178-227
private static (bool Valid, string[] Errors) ValidateConfigShape(string configJson)
{
    var errors = new List<string>();
    JsonDocument doc;
    try { doc = JsonDocument.Parse(configJson); }
    catch (JsonException ex) { return (false, new[] { $"Invalid JSON: {ex.Message}" }); }
    using (doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { errors.Add("Root must be a JSON object."); ... }
        if (root.TryGetProperty("roles", out var roles))
        {
            if (roles.ValueKind != JsonValueKind.Object) { errors.Add("'roles' must be an object."); ... }
            foreach (var prop in roles.EnumerateObject())
            {
                if (RolePhaseMap.ForbiddenKeys.Contains(prop.Name)) { errors.Add($"Forbidden role key: '{prop.Name}'."); continue; }
                if (!RolePhaseMap.ValidRoles.Contains(prop.Name))   { errors.Add($"Unknown role '{prop.Name}'. ..."); }
            }
        }
    }
    return (errors.Count == 0, errors.ToArray());
}
```

- Missing validation:
  - No provider-name regex validation (e.g. `provider:"curl https://evil.com"` is accepted).
  - No `maxBudgetUsd` range check.
  - No `permissionMode` whitelist.
  - No `phaseRoleMap` validation.
  - No security-config branch at all — `UpdateAgentsConfig` and `UpdateSecurityConfig` both just `UpsertAsync(tenantId, JsonSerializer.Serialize(req.Config), null)` via `SettingsEndpoints.cs:19-35` without any validator.
  - No ReDoS guard on `blockedCommandPatterns` (TS's `NESTED_QUANTIFIER` regex pattern check — see `sanitization-store.ts:98`).
  - No separation of agents config from security config — both go into the same `agent_configs.config` column.

## 3. The gap

- A malicious tenant admin can PUT `{"roles":{"developer":{"provider":"$(rm -rf /)"}}}` and the validator passes. If the `provider` string later flows into any subprocess-spawn adapter (e.g. the CLI-agent ports from finding 003), code execution is possible.
- A tenant admin can PUT `"maxBudgetUsd": 1000000` with no warning — TS capped at `[0, 100]`.
- A tenant admin can PUT `"blockedCommandPatterns": ["(a+)+b"]` (a classic ReDoS) — TS rejected; C# accepts, and the sanitization engine has a `100ms` match timeout (see `SanitizationService.cs:43`) but the stored regex still causes a 100ms stall on every input.
- A tenant admin can put random garbage into `security.maxFetchSizeBytes = "not a number"` — no numeric validation.
- For a caller sending `PUT /api/v1/agents/config {config:{roles:{developer:{maxBudgetUsd:-500}}}}`:
  - TS: `400 {error:'Validation failed', errors:['maxBudgetUsd must be in [0, 100]']}`.
  - C#: `200 OK` — persisted.

Error paths:
- TS: `400` with `errors:[...]` array on any violation.
- C#: `400` with the four shallow checks only.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md`.
- Story 9-1 AC 6 lists **six** specific validation rules:
  > "Config validation rules enforced at both API and CLI load time:
  > - `providerChain` non-empty in defaults
  > - `provider` matches `/^[a-z0-9][a-z0-9_-]{0,63}$/`, rejects `__proto__`/`constructor`/`prototype`
  > - `maxBudgetUsd` in [0, 100], finite number
  > - `blockedCommandPatterns` compile as valid regex, max 100 patterns, max 500 chars each
  > - `maxFetchSizeBytes` in [0, 1 GiB]
  > - `bypassPermissions` emits WARN and requires `TAMMA_ALLOW_BYPASS_PERMISSIONS=true` env var"
- C# implements 0 of these 6 rules.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS).
  - [ ] Matches C# behavior.
  - [ ] Describes a third behavior.
  - [ ] No story — there is a story, and 6 ACs are unmet.

## 5. Status

- **Classification**: Incomplete port.
- **What's needed to finish**:
  1. Port `validateAgentsConfig` to `AgentConfigValidator` (static class or DI service) in `Tamma.Api.Services.Agents`.
  2. Port `validateSecurityConfig` similarly.
  3. Port `NESTED_QUANTIFIER` ReDoS check (see `packages/api/src/services/sanitization-store.ts:98`) — the regex `\([^)]*[*+?{][^)]*\)[*+?{]` catches `(a+)+` patterns. Apply to every user-supplied regex (agent config blocked patterns, sanitization rule patterns).
  4. Extend `UpdateAgentConfigRequest` to accept typed `Agents` and `Security` payloads instead of `object Config`.
  5. Wire validator into `AgentEndpoints.UpdateConfig` and `SettingsEndpoints.UpdateSecurityConfig`.
  6. Store agents and security in separate JSONB keys (`config.agents`, `config.security`) to match TS — or in separate columns.
- **Is it "just a stub" or is scope missing?** Just a stub — the scaffolded `ValidateConfigShape` is explicitly labelled as schema-level and leaves semantic validation as TODO.
- **Blockers**: Partially depends on finding 001 (role taxonomy) because `RolePhaseMap.ValidRoles` is already referenced.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs:178-227`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:19-35, 31-35`
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/UpdateAgentConfigRequest.cs`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentConfigValidator.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Agents/SecurityConfigValidator.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Security/ReDosGuard.cs`
- Tests to add:
  - `AgentConfigValidator_NegativeBudget_Rejects`
  - `AgentConfigValidator_NonMatchingProviderRegex_Rejects`
  - `AgentConfigValidator_ForbiddenRoleKey__proto__`
  - `SecurityConfigValidator_OverLargeFetchSize_Rejects`
  - `SecurityConfigValidator_ReDosPattern_Rejects`
  - `SecurityConfigValidator_TooManyPatterns_Rejects`
- Estimated effort: 5h broken down as:
  - Agents validator: 1.5h
  - Security validator + ReDosGuard: 1.5h
  - DTO rewire + endpoint rewire: 1h
  - Tests: 1h

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed (all six Story 9-1 AC 6 rules)
- **Commit**: `0dbccf9` `fix(providers): land P1/P2 diagnostics/health/validation/user-providers fixes [findings 008, 009, 010, 012, 013, 014, 018, 019]`
- **Notes**: New `Tamma.Api.Services.Security.ReDosGuard` ports the TS `NESTED_QUANTIFIER` heuristic (`\([^)]*[*+?{][^)]*\)[*+?{]`) plus the max-pattern-length / max-pattern-count caps (500 chars, 100 patterns). `ValidateConfigShape` extended with: provider-name regex `/^[a-z0-9][a-z0-9_-]{0,63}$/` (Story 9-1 AC 6), `maxBudgetUsd ∈ [0, 100]` (finite), `permissionMode ∈ {default, acceptEdits, bypassPermissions}` whitelist, `providerChain` non-empty + per-entry provider regex, `security.maxFetchSizeBytes ∈ [0, 1 GiB]`, `security.blockedCommandPatterns` ReDoS-guarded + count cap. Validates both legacy `roles.<r>.providerChain` and canonical `chains.<r>.<a>` shapes. Forbidden prototype-pollution keys (`__proto__`, `constructor`, `prototype`) rejected on every nested object. **Deferred**: separating `agents` and `security` payloads into typed DTO fields (audit step 4–6 of §5) — kept the existing single-blob shape since callers already work that way.

## References

- TS source: `packages/api/src/routes/agents/agent-config-routes.ts:63-95`, `packages/shared/src/config/validate-agents.ts`, `packages/shared/src/config/validate-security.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs:178-227`
- Story: `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md` AC 6
- Related findings: `001-role-phase-vocabulary-schism.md`, `007-task-overrides-clamping-lost.md`, `015-sanitization-data-model-rewrite.md`, `025-sanitization-redos-defense-stronger-positive.md`
- Archived SQL migration: `database/archived-sql-migrations/013_agent_configs.sql`
