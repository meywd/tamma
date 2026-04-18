# Finding 015: Sanitization data model rewritten — typed columns → opaque JSONB blob

**Scope**: providers
**Severity**: P1 (feature loss + data-model regression)
**Status**: Data-model regression
**Estimated port effort**: 14–20h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/sanitization-store.ts` and archived `database/archived-sql-migrations/016_sanitization_rules.sql`.

- File: `packages/api/src/services/sanitization-store.ts:18-29`
- Contract/behavior: Typed persistence — one row per account with **six** dedicated columns, each with its own semantic meaning.

```typescript
// packages/api/src/services/sanitization-store.ts (9e9a57c~1) — lines 18-29
export interface SanitizationRules {
  id: string;
  accountId: string | null;
  enabled: boolean;
  extraInjectionPatterns: string[];     // user-supplied regex to *add* to built-in injection heuristics
  blockedCommandPatterns: string[];     // regex that abort actions if matched (rm -rf, DROP TABLE, etc.)
  maxFetchSizeBytes: number;            // cap for secureFetch — 10 MiB default
  validateUrls: boolean;                // enable private-IP / numeric-octet URL checks
  gateActions: boolean;                 // require policy approval for sensitive actions
  createdAt: string;
  updatedAt: string;
}
```

- Archived SQL at `016_sanitization_rules.sql`:

```sql
CREATE TABLE IF NOT EXISTS sanitization_rules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
  enabled BOOLEAN NOT NULL DEFAULT true,
  extra_injection_patterns TEXT[] DEFAULT '{}',
  blocked_command_patterns TEXT[] DEFAULT '{}',
  max_fetch_size_bytes INTEGER DEFAULT 10485760,
  validate_urls BOOLEAN DEFAULT true,
  gate_actions BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (account_id)
);
```

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Entities/SanitizationRule.cs`
- Contract/behavior: Two-column entity — opaque JSONB blob.

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/SanitizationRule.cs (current)
public class SanitizationRule
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Rules { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

- The `Rules` JSONB holds an **array of `SanitizationRuleDefinition`**: `[{name, pattern, replacement, caseSensitive, priority, enabled}, ...]`. A completely different shape from the TS single-record-of-typed-fields model. Default shape is `"[]"` in `SanitizationRepository.LoadOrCreateRowAsync:164-172` (conflicts with the `'{}'::jsonb` default declared in `TammaDbContext.cs:311`).
- The typed fields from TS (`maxFetchSizeBytes`, `validateUrls`, `gateActions`, `extraInjectionPatterns`, `blockedCommandPatterns` as a named collection) are **absent** — they cannot be expressed in the C# shape at all.

## 3. The gap

- TS was a **per-account policy row** with typed toggles for six security features.
- C# is a **per-account list of regex-replace rules** with no feature toggles.
- The two shapes are not interconvertible:
  - A TS row `{extraInjectionPatterns:['ignore.*previous'], blockedCommandPatterns:['rm\\s+-rf']}` has no C# equivalent (no `replacement` field, no `name`, no distinction between "redact this" and "abort on match").
  - A C# row `[{name:"ssn", pattern:"\\d{3}-\\d{2}-\\d{4}", replacement:"[REDACTED-SSN]"}]` has no TS equivalent (TS does redaction via `ContentSanitizer` with fixed `[REDACTED]` output — no custom replacement).
- Missing TS fields in C#: `maxFetchSizeBytes` (SSRF size cap), `validateUrls` (URL inspector toggle), `gateActions` (action gating toggle), `direction` (input vs output).
- `TammaDbContext.cs:306-317` does **not** declare `UNIQUE(TenantId)` for `SanitizationRule` (see finding 016), so multiple rows per tenant can coexist nondeterministically.
- No cascade-on-tenant-delete (see finding 017).
- For a caller restoring a backup of TS `sanitization_rules` rows:
  - The JSONB columns `extra_injection_patterns[]`, `blocked_command_patterns[]`, `max_fetch_size_bytes`, `validate_urls`, `gate_actions` are lost.

Error paths:
- TS: `400 {error:'Invalid regex pattern in extraInjectionPatterns: ...'}` from `validateRulesInput`.
- C#: compile failures per-rule are swallowed with a log warning and the rule is skipped at runtime (see `SanitizationService.cs:159-166`).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-7/9-7-content-sanitization.md`.
- Story 9-7 AC 1: `PUT /api/v1/sanitize/rules` accepts "extra injection patterns, enabled/disabled, custom blocked commands" — matches TS six-field shape.
- Story 9-7 AC 2: "Sanitization rules stored in Postgres per account with system default fallback" — one row per account, matches TS.
- Story 9-7 archived SQL block (lines 46-60) is the six-field schema.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS).
  - [ ] Matches C# behavior.
  - [ ] Describes a third behavior.
  - [ ] No story — there is one; the regex-rule model is a new invention.

## 5. Status

- **Classification**: Data-model regression / semantic rewrite.
- **What's needed to finish**:
  1. Extend `SanitizationRule` entity with typed columns: `Enabled`, `ExtraInjectionPatterns jsonb`, `BlockedCommandPatterns jsonb`, `MaxFetchSizeBytes`, `ValidateUrls`, `GateActions`, `Direction`.
  2. Rename `Rules` → `CustomRedactionRules` (the JSONB list of `{name, pattern, replacement}`) so both concerns coexist.
  3. Wire the six typed fields into `ContentSanitizer` (see finding 006).
  4. Extend `UpdateSanitizationRulesRequest` DTO to accept typed fields.
  5. Write a migration that adds the columns and seeds defaults.
  6. Add `UNIQUE(TenantId)` (finding 016).
  7. Add cascade-on-tenant-delete FK (finding 017).
- **Is it "just a stub" or is scope missing?** Both. The C# engineer invented a different abstraction (typed-rules list with replacement strings) instead of porting the TS typed-policy row. The existing C# feature (custom redaction rules) is legitimate and should be kept as an additional capability.
- **Blockers**: Depends on finding 006 (prompt-injection detection port) — the typed fields only have meaning once `ContentSanitizer` exists.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/SanitizationRule.cs`
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:305-317`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/SanitizationRepository.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:60-102`
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Settings/UpdateSanitizationRulesRequest.cs`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/<next>_ExpandSanitizationPolicy.cs`
- Tests to add:
  - `SanitizationRule_MaxFetchSizeBytes_Persists`
  - `SanitizationRule_ValidateUrlsToggle_AppliesToSecureFetch`
  - `SanitizationRule_GateActionsToggle_AppliesToActionEvaluator`
  - `SanitizationRule_BlockedCommandPatterns_ReturnWarningOnMatch`
  - `SanitizationRule_UniquePerTenant_UpsertReplacesInPlace`
  - `SanitizationRule_DeleteTenant_CascadeRemovesRules`
- Estimated effort: 16h (coordinated with finding 006).

## References

- TS source: `packages/api/src/services/sanitization-store.ts:18-29`, `packages/api/src/services/pg-sanitization-store.ts:66-154` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/SanitizationRule.cs`, `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs`
- Story: `docs/stories/epic-9/story-9-7/9-7-content-sanitization.md`
- Related findings: `006-prompt-injection-detection-gone.md`, `016-sanitization-missing-unique-tenant.md`, `017-sanitization-missing-cascade-fk.md`
- Archived SQL migration: `database/archived-sql-migrations/016_sanitization_rules.sql`
