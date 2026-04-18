# Finding 006: Prompt-injection detection removed — sanitization is a regex redactor

**Scope**: providers
**Severity**: P1 (feature broken — security regression)
**Status**: Semantic rewrite
**Estimated port effort**: 14–20h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/sanitization-store.ts` and
`git show 9e9a57c~1:packages/api/src/services/pg-sanitization-store.ts`.

- File: `packages/api/src/services/pg-sanitization-store.ts:156-168`, `sanitization-store.ts:195-207`
- Contract/behavior: The store delegated the actual sanitization work to `ContentSanitizer` from `@tamma/shared/security`. `ContentSanitizer` implemented **five prompt-injection heuristic categories**, URL validation with private-IP octet parsing, zero-width character stripping, HTML stripping via a quote-aware state machine, fetch-size capping, and action gating against `blockedCommandPatterns`. It also distinguished `sanitize()` (input from user → LLM) from `sanitizeOutput()` (LLM → downstream).

```typescript
// packages/api/src/services/pg-sanitization-store.ts (9e9a57c~1) — lines 156-168
async sanitize(accountId: string | null, content: string, direction: 'input' | 'output'): Promise<SanitizeResult> {
  const rules = await this.getRules(accountId);

  const sanitizer = new ContentSanitizer({
    enabled: rules.enabled,
    extraInjectionPatterns: rules.extraInjectionPatterns,
  });

  if (direction === 'output') {
    return sanitizer.sanitizeOutput(content);
  }
  return sanitizer.sanitize(content);
}
```

- The persisted shape (`SanitizationRules`) carried typed fields: `enabled`, `extraInjectionPatterns[]`, `blockedCommandPatterns[]`, `maxFetchSizeBytes`, `validateUrls`, `gateActions`. System defaults hardcoded a sane baseline (see `pg-sanitization-store.ts:24-32`).
- `SanitizeResult` returned `{result, warnings: string[]}` — warnings carried cue codes like `injection:system-override`, `url:private-ip`, `html:script-tag`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs:63-128`
- Contract/behavior: Rule-based regex redactor. Each rule is `{name, pattern, replacement, caseSensitive, priority, enabled}`. All matches are replaced with `replacement` (usually `[REDACTED]`). No injection heuristics, no URL validation, no HTML stripping, no zero-width character removal, no distinction between input and output, no fetch-size cap, no action gating.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs — lines 87-106
foreach (var rule in orderedRules)
{
    cancellationToken.ThrowIfCancellationRequested();
    var regex = TryGetRegex(tenantId, rule);
    if (regex is null) continue;
    try
    {
        var matchCount = regex.Matches(result).Count;
        if (matchCount == 0) continue;
        result = regex.Replace(result, rule.Replacement);
        hits.Add(new SanitizationHit(rule.Name, matchCount));
    }
    catch (RegexMatchTimeoutException)
    {
        _logger.LogWarning("Sanitization rule '{RuleName}' hit MatchTimeout", rule.Name);
    }
}
```

- `SanitizeResult` is `{Text, Hits[]}` with `Hits` containing only `{RuleName, MatchCount}` — no warning codes, no severity.
- `SanitizationService.SanitizeAsync` ignores any `direction` parameter. The endpoint (`SettingsEndpoints.Sanitize`) does not accept a `direction` field either.
- Dependencies: `ISanitizationRepository`, `SanitizationRuleDefinition` entity, `ISanitizationDefaultsProvider` for system defaults.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/Sanitization/*` verify regex-replace behaviour, ReDoS timeout, and cache invalidation. None test prompt-injection detection or URL validation.

## 3. The gap

- TS produced **warnings** like `injection:ignore-previous-instructions` that let callers react to suspected injection attempts; C# produces only redaction hits.
- TS applied asymmetric rules to input vs output (e.g. output gets HTML escape, input gets injection-pattern matching); C# applies the same rules identically.
- TS enforced a URL-validation pass that parsed numeric octets to catch private-IP SSRF (e.g. `http://2130706433/` == `http://127.0.0.1/`); the C# rewrite has no URL inspector. **SSRF vector is reopened.**
- TS enforced `maxFetchSizeBytes` on the separate `secureFetch` helper (in `@tamma/shared/security`). The C# API has no analog. Any downstream Elsa activity that fetches external content is unthrottled.
- TS supported `gateActions: true` which blocked suspected command-invocation strings (shell injection, SQL injection). C# relies on per-tenant regex rules — but the system default rule set is **empty** (see `SanitizationRepository.cs:63-64`: `EmptyDefaultsProvider` when no provider is registered), so a tenant with no custom rules sanitizes **nothing**.
- For a caller sending `content: "Ignore all previous instructions and <script>evil()</script>"`:
  - TS: `{result: "Ignore all previous instructions and evil()", warnings: ['injection:ignore-previous-instructions', 'html:script-tag']}`.
  - C#: `{text: "Ignore all previous instructions and <script>evil()</script>", hits: []}` unless the tenant has explicitly configured a regex for `ignore.*previous.*instructions`.

Error paths:
- TS: throws on invalid user-supplied regex (`pg-sanitization-store.ts:38-46`); returns warnings array.
- C#: skips invalid regex with a log warning (no error bubble); no warning surface to caller.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-7/9-7-content-sanitization.md`.
- Story 9-7 AC 6: **"All existing sanitization behaviors preserved: HTML stripping (quote-aware state machine), Zero-width character removal (20+ Unicode code points), Prompt injection detection (5 categories), URL validation (numeric octet parsing for private IPs), Action gating (blocked command patterns), Secure fetch (redirect validation, size limits)"** — none of these six behaviours are implemented in the C# service.
- Story 9-7 AC 3: "The existing `ContentSanitizer` class in `packages/shared/src/security/content-sanitizer.ts` remains the core implementation. The API wraps it with account-scoped configuration." — `ContentSanitizer` was deleted; the new C# service is a different algorithm, not a wrap.
- Story alignment:
  - [ ] Matches TS behavior.
  - [ ] Matches C# behavior.
  - [x] Describes a third behavior — story explicitly enumerates 6 required behaviours; C# implements 0 of them (regex-rule replacement is not listed as one of the six).
  - [ ] No story — there is a story, and the story is directly contradicted by the implementation.

## 5. Status

- **Classification**: Semantic rewrite (regression).
- **What's needed to finish**:
  1. Port `ContentSanitizer` from `packages/shared/src/security/content-sanitizer.ts` to `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/ContentSanitizer.cs`. Preserve the five injection heuristic categories, HTML stripping state machine, zero-width stripping list, URL validator, action gating.
  2. Rewire `SanitizationService.SanitizeAsync` to chain: user rules (current behaviour) → `ContentSanitizer.Analyze(direction)` → return merged `{result, warnings, hits}`.
  3. Change `SanitizeEndpointRequest` DTO to accept `direction: "input" | "output"` (currently accepts `text` and legacy `content` only — see `SettingsEndpoints.cs:47`).
  4. Port `secureFetch` to a C# helper (`Tamma.Api.Services.Security.SecureHttpClient`) that caps response size and validates redirects.
  5. Extend `SanitizationRule` entity to carry the typed fields TS had (see finding 015): `MaxFetchSizeBytes`, `ValidateUrls`, `GateActions`, `Direction`.
  6. Re-seed system defaults so a fresh tenant is protected even without custom rules.
- **Is it "just a stub" or is scope missing?** Scope is missing. The C# engineer chose a different, narrower algorithm.
- **Blockers**: Depends on finding 015 (data-model rewrite) because the new behaviours need typed persistence.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizeResult.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:46-54` (accept `direction`)
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Settings/SanitizeEndpointRequest.cs`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/ContentSanitizer.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/HtmlStripper.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/UrlValidator.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/InjectionDetector.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Security/SecureHttpClient.cs`
- Tests to add:
  - `InjectionDetector_IgnorePreviousInstructionsVariants_ReturnWarning`
  - `UrlValidator_NumericOctetPrivateIp_Rejected`
  - `HtmlStripper_ScriptTagInAttributeValue_Removed`
  - `SanitizationService_InputVsOutputDirection_AppliesDifferentRules`
  - `SecureHttpClient_ResponseExceedsMaxSize_Throws`
- Estimated effort: 16h broken down as:
  - Port `ContentSanitizer` + 5 categories: 6h
  - HTML state-machine + zero-width: 2h
  - URL validator + secure fetch: 3h
  - DTO + endpoint rewire + rule-entity columns: 2h
  - Tests: 3h

## References

- TS source: `packages/shared/src/security/content-sanitizer.ts`, `packages/shared/src/security/url-validator.ts`, `packages/shared/src/security/secure-fetch.ts`, `packages/api/src/services/pg-sanitization-store.ts:156-168` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:37-102`
- Story: `docs/stories/epic-9/story-9-7/9-7-content-sanitization.md`
- Related findings: `015-sanitization-data-model-rewrite.md`, `025-sanitization-redos-defense-stronger-positive.md`
- CLAUDE.md section: "Security Requirements — Input Validation" requires "Sanitize all user inputs against injection attacks".
