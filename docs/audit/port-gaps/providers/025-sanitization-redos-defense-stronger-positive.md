# Finding 025: Sanitization ReDoS defense stronger in C# — POSITIVE

**Scope**: providers
**Severity**: None (positive finding)
**Status**: No gap (C# is better than TS here)
**Estimated port effort**: 0h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/sanitization-store.ts`.

- File: `packages/api/src/services/sanitization-store.ts:98-125`
- Contract/behavior: TS used a **static regex-shape heuristic** (`NESTED_QUANTIFIER = /\([^)]*[*+?{][^)]*\)[*+?{]/`) applied at upsert time to reject patterns that contain a nested quantifier inside a group. This catches `(a+)+`, `(a*)+`, `(.*)*`, etc. It is a heuristic and does not recurse into nested groups; it is a write-time, structural check.

```typescript
// packages/api/src/services/sanitization-store.ts (9e9a57c~1) — lines 80-125
const NESTED_QUANTIFIER = /\([^)]*[*+?{][^)]*\)[*+?{]/;

function validatePatterns(patterns: string[], label: string): void {
  for (const pattern of patterns) {
    if (typeof pattern !== 'string') { throw new Error(...); }
    if (pattern.length > MAX_PATTERN_LENGTH) { throw new Error(...); }
    if (NESTED_QUANTIFIER.test(pattern)) {
      throw new Error(`Invalid regex pattern in ${label}: unsafe nested quantifier "${pattern}"`);
    }
    try { new RegExp(pattern); } catch { throw new Error(...); }
  }
}
```

- Defense: write-time rejection of static-shape matches. Runtime: no timeout (V8 regex engine runs until finish).
- Limitation: the heuristic misses nested-inside-nested cases (e.g. `((a+)+)+`), misses lookahead/lookbehind ReDoS (e.g. `(?=(a+))+`), and gives false positives for legitimate non-pathological patterns that happen to contain `(.+)+` in a non-backtracking way.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs:40-56`, `130-166`
- Contract/behavior: C# uses a **runtime `MatchTimeout`** of 100ms on every regex execution. `RegexMatchTimeoutException` is caught per-rule at `SanitizationService.cs:108-115`; the rule is skipped and logging emitted. Compile is also wrapped with `options | RegexOptions.Compiled | RegexOptions.CultureInvariant`.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs — lines 40-43
private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

// apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs — lines 108-115
catch (RegexMatchTimeoutException)
{
    _logger.LogWarning(
        "Sanitization rule '{RuleName}' hit MatchTimeout ({TimeoutMs} ms) and was skipped",
        rule.Name,
        (int)MatchTimeout.TotalMilliseconds);
}

// apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs — lines 155
var regex = new Regex(rule.Pattern, options, MatchTimeout);
```

- Defense: runtime timeout, not structural. **Sound** against all ReDoS shapes — no pattern can stall longer than 100ms regardless of what it looks like.
- Limitation: any caller-controlled pattern triggering the timeout silently produces no redactions for that rule on that request. If the attacker crafts a payload that reliably hits the timeout, the rule effectively becomes disabled for their input — they can smuggle content past the redactor. Mitigation: the attacker must first own a tenant and install such a rule (requires `SettingsManage`). In practice this is a lower-severity path.

## 3. The gap

- There is **no gap** on the defense itself. C# is strictly stronger (works for all ReDoS shapes, not just the common ones).
- The `/tmp/tamma-audit/31-providers.md` summary line 69 correctly flags this as a positive finding ("Sanitization ReDoS defense ✅ (better) 0h").
- Gap to be mindful of:
  - **Missing write-time check.** TS rejected pathological patterns at PUT time; C# accepts any pattern (that compiles) and lets the runtime catch the problem. A tenant admin configuring a ReDoS pattern learns about it only when the rule silently starts timing out on their tenant's traffic. Adding the TS heuristic as a **write-time warning** would combine best of both worlds.
  - **Silent skip vs observable failure.** TS threw at PUT; C# logs at runtime. Neither surfaces the skipped-rule to the caller of `POST /sanitize`. A ReDoS-induced skip is invisible to callers, which is undesirable for security-critical flows.

For a caller doing `PUT /sanitize/rules [{pattern: "(a+)+b"}]`:
- TS: `400 {error: 'Invalid regex pattern in ... : unsafe nested quantifier "(a+)+b"'}`.
- C#: `200 OK`. First call to `/sanitize` with a long `a` string takes exactly 100ms, emits `LogWarning`, produces no hits for this rule. Subsequent calls each cost 100ms.

Error paths:
- TS: `400` at upsert.
- C#: `200` at upsert; `200` at sanitize with `hits` array missing the offending rule.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-7/9-7-content-sanitization.md`.
- Story 9-7 does not specify ReDoS defense strategy.
- Story alignment:
  - [x] Describes a third behavior — story agnostic; C# exceeds story expectations with the runtime timeout.
  - [ ] Matches TS behavior.
  - [ ] Matches C# behavior.

## 5. Status

- **Classification**: No gap (positive). Optional hardening: add TS's write-time heuristic as a warning/rejection layer.
- **What's needed to finish (optional hardening)**:
  1. Port `NESTED_QUANTIFIER` to `apps/tamma-elsa/src/Tamma.Api/Services/Security/ReDosGuard.cs`.
  2. Call it from `UpdateSanitizationRules` (`SettingsEndpoints.cs:73-102`) **before** persisting. Reject at 400 (matches TS).
  3. Also reject in `AgentConfigValidator` for `blockedCommandPatterns` (see finding 014).
  4. Add telemetry to `SanitizationService`: emit a distinct log event (`SANITIZATION.RULE_TIMEOUT`) and include `rule_name` in response `warnings[]` so callers know a rule was skipped.
- **Is it "just a stub" or is scope missing?** Not applicable — C# is already above bar on the core defense.
- **Blockers**: None.

## Remediation

Informational / optional hardening only.

- Files to modify (optional):
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/SettingsEndpoints.cs:73-102`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs` (add warnings array to result)
  - `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizeResult.cs`
- Files to create (optional):
  - `apps/tamma-elsa/src/Tamma.Api/Services/Security/ReDosGuard.cs`
- Tests to add (optional):
  - `ReDosGuard_NestedQuantifier_Rejects`
  - `UpdateSanitizationRules_NestedQuantifierPattern_Returns400BeforePersist`
  - `SanitizationService_RuleTimeoutSkip_SurfacesWarning`
- Estimated effort: 2h (if the hardening is pursued).

## References

- TS source: `packages/api/src/services/sanitization-store.ts:80-125` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Sanitization/SanitizationService.cs:40-43, 108-115, 155`
- Story: `docs/stories/epic-9/story-9-7/9-7-content-sanitization.md`
- Related findings: `014-agent-config-crud-validation-gaps.md`, `006-prompt-injection-detection-gone.md`, `015-sanitization-data-model-rewrite.md`
