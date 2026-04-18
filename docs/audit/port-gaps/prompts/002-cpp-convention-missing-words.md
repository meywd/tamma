# Finding 002: `cpp` convention template drops "for readability"

**Scope**: prompts
**Severity**: P3 (drift/contract)
**Status**: Behavioral drift (ported but text diverged)
**Estimated port effort**: 0.1h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/convention-templates.ts`.

- File: `packages/api/src/services/convention-templates.ts:723` (cpp template, within the `Conventions` string literal)
- Contract/behavior: The `cpp` starter template contains a bullet in the `## Language & Style` section instructing LLMs to use `auto` for complex types but to keep explicit types in function signatures for readability.
- Key code (verbatim quote):

```typescript
// packages/api/src/services/convention-templates.ts (9e9a57c~1)
- Files: snake_case.cpp/.h or .cc/.hpp; Classes: PascalCase; Functions: PascalCase or camelCase
- Namespaces: lowercase (project::module); avoid using namespace std; in headers
- Use constexpr and consteval for compile-time computation
- Prefer auto for complex types; explicit types for readability in function signatures
```

- Dependencies: None — static template data exposed via `listConventionTemplates()` / `getConventionTemplate('cpp')` and consumed by `GET /api/convention-templates/cpp`.
- Tests that exercised this: `packages/api/src/routes/convention-templates.test.ts` (snapshot comparison on the conventions body for `cpp`).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionTemplates.cs:738`
- Contract/behavior: Identical template structure, same key (`cpp`), same section order, but the bullet drops the two-word qualifier `for readability`.
- Key code (verbatim quote):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionTemplates.cs (current)
- Files: snake_case.cpp/.h or .cc/.hpp; Classes: PascalCase; Functions: PascalCase or camelCase
- Namespaces: lowercase (project::module); avoid using namespace std; in headers
- Use constexpr and consteval for compile-time computation
- Prefer auto for complex types; explicit types in function signatures
```

- Dependencies: `ConventionEndpoints.GetByKey`, served at `GET /api/convention-templates/{key}`.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/Conventions/ConventionTemplatesTests.cs` verifies 20 templates exist and keys round-trip; there is **no content-equivalence test** against the TS source, which is why this regression slipped through. File header comment at `ConventionTemplates.cs:10-12` explicitly states "wording here is load-bearing and must be preserved byte-for-byte when ported" — so this is a violation of the file's own contract.

## 3. The gap

Concrete behavioral difference:

- TS did: emit the 4-word tail `explicit types for readability in function signatures`.
- C# does: emit `explicit types in function signatures`.
- For a caller sending `GET /api/convention-templates/cpp`, TS returns a body containing `...for readability in function signatures`, C# returns the trimmed variant.
- In production with existing data / deployed clients, this means: repositories whose `.tamma/config.json` `conventions` field contains the C# response (new installs after cutover) will produce LLM prompts with slightly less rationale for the rule; repositories still pinning the pre-cutover TS response will continue to include "for readability". For the LLM, this is a negligible quality delta — the *rule* is preserved, only its justification is dropped — but it is a contract drift. No consumer API semantics change; only the prompt text content.

Error paths: N/A on both sides (static data).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-2-prompt-store-service.md` (convention templates are technically Epic 12-5 scope but are ported alongside prompts)
- Story's acceptance criteria: No story mandates the exact cpp bullet text. The source-of-truth is the TS file, which the C# file claims to mirror byte-for-byte (see header comment at `ConventionTemplates.cs:10-12`).
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS) — the C# file's own header says it should match.
  - No story explicitly arbitrates; CLAUDE.md convention-starter section does not specify cpp details.

## 5. Status

- **Classification**: Behavioral drift (text edit during port)
- **What's needed to finish**:
  1. Restore the phrase `for readability` to the cpp template line.
  2. Add a content-equivalence test against a fixture extracted from the TS source.
- **Is it "just a stub" or is scope missing?** Scope was fully ported; this is a single-word typo during the manual port. Trivial fix.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionTemplates.cs` — change line 738 from `explicit types in function signatures` to `explicit types for readability in function signatures`.
- Files to create: None.
- Tests to add:
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Conventions/ConventionTemplatesTests.cs` — add `GetByKey_Cpp_IncludesReadabilityClause` asserting the phrase `for readability` appears in the conventions body.
  - Consider a broader per-key fixture compare against TS snapshots to prevent recurrences for the other 19 templates.
- Estimated effort: 0.1h broken down as:
  - Edit: 0.02h
  - Test: 0.08h

## References

- TS source: `packages/api/src/services/convention-templates.ts` line 723 (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionTemplates.cs:738`
- Story: `docs/stories/epic-27/27-2-prompt-store-service.md`
- Related findings: None
- CLAUDE.md section: "Convention Templates" (lines ~298-310)
