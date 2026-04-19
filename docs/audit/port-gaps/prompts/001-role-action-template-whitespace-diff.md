# Finding 001: 16 role+action templates diverge from TS in plan-review and code-review conditional blocks

**Scope**: prompts
**Severity**: P3 (drift/contract)
**Status**: Behavioral drift (ported but semantics diverged)
**Estimated port effort**: 1h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (Option B — formalize C# shape)
- **Commit**: ea4d5e5
- **Notes**: Updated `SystemPrompts.cs` header comment to document the deliberate divergence (single role-tailored block instead of TS's four parallel ternaries collapsing to whitespace). Added 10 lock-shape tests in `SystemPromptsTests.cs` covering security/tester/architect/devops bullet bodies plus the four roles (developer, product_owner, senior_developer, tech_writer) that hit the generic fallback for both plan-review and code-review.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/default-prompts.ts`.

- File: `packages/api/src/services/default-prompts.ts:260-323` (plan-review) and `:517-580` (code-review)
- Contract/behavior: `createDefaultPrompts()` iterates `VALID_ROLES` (8 roles) and emits a single template literal per role+action. For `plan-review` and `code-review`, the prompt body contains four inline ternaries that evaluate at prompt-construction time — they return either a role-specific bullet pair or an empty string `''` (three spaces of indentation survive on the unmatched lines because the template literal's leading whitespace is preserved).
- Key code (verbatim quote, `plan-review` template, lines 267-295 of `default-prompts.ts`):

```typescript
// packages/api/src/services/default-prompts.ts (9e9a57c~1)
`...
<thinking>
1. Verify the plan addresses all requirements in the work item
2. Check for missing tasks or overlooked edge cases
3. Review from your specific expertise as a {{role}}:
   ${role === 'security' ? '- Check for security implications in each task\n   - Verify input validation and auth concerns are addressed' : ''}
   ${role === 'tester' ? '- Check that testing strategy is comprehensive\n   - Verify edge cases and error paths are covered' : ''}
   ${role === 'architect' ? '- Check that architectural patterns are followed\n   - Verify service boundaries and interface contracts' : ''}
   ${role === 'devops' ? '- Check for deployment and infrastructure impact\n   - Verify CI/CD pipeline compatibility' : ''}
4. Identify risks or improvements
</thinking>
...`
```

Observed wire output for `role === 'developer'` (a role without a matching ternary branch) is a block of four whitespace-only lines between the "Review from your specific expertise" header and "4. Identify risks or improvements":

```
3. Review from your specific expertise as a {{role}}:
   
   
   
   
4. Identify risks or improvements
```

- Dependencies: `VALID_ROLES`, `SYSTEM_PROMPTS`, `makeTemplate()` helper.
- Tests that exercised this: `packages/api/src/services/prompt-store.test.ts` and `pg-prompt-store.test.ts` asserted template content equality but not the specific whitespace shape.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs:310-344` (plan-review), `:461-499` (code-review), and `:610-642` (role-lens switch expressions)
- Contract/behavior: C# ports the four ternaries as two `switch` expressions — `RoleReviewLens(role)` (for plan-review) and `RoleReviewLensForCodeReview(role)` (for code-review). Each switch returns **either** the matching role's two-bullet block **or** a generic fallback `"   - Apply your role-specific expertise to the plan\n"` (plan) / `"   - Apply your role-specific expertise to the diff\n"` (code-review) for the four unmatched roles (developer, product_owner, senior_developer, tech_writer).
- Key code (verbatim quote, lines 310-324 of `SystemPrompts.cs`):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs (current)
private static PromptTemplate PlanReview(string role) => new(
    Role: role,
    Action: "plan-review",
    Template:
        "You are a {{role}} reviewing an implementation plan.\n\n" +
        ...
        "<thinking>\n" +
        "1. Verify the plan addresses all requirements in the work item\n" +
        "2. Check for missing tasks or overlooked edge cases\n" +
        "3. Review from your specific expertise as a {{role}}:\n" +
        RoleReviewLens(role) +
        "4. Identify risks or improvements\n" +
        ...
```

And the switch expression (lines 610-625):

```csharp
private static string RoleReviewLens(string role) => role switch
{
    "security" =>
        "   - Check for security implications in each task\n" +
        "   - Verify input validation and auth concerns are addressed\n",
    "tester" =>
        "   - Check that testing strategy is comprehensive\n" +
        "   - Verify edge cases and error paths are covered\n",
    "architect" =>
        "   - Check that architectural patterns are followed\n" +
        "   - Verify service boundaries and interface contracts\n",
    "devops" =>
        "   - Check for deployment and infrastructure impact\n" +
        "   - Verify CI/CD pipeline compatibility\n",
    _ => "   - Apply your role-specific expertise to the plan\n",
};
```

For `role = "developer"` the emitted block is a single non-empty bullet instead of four empty lines:

```
3. Review from your specific expertise as a {{role}}:
   - Apply your role-specific expertise to the plan
4. Identify risks or improvements
```

- Dependencies: `BuildRoleActionTemplates()`, `SystemFor(role)`.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/SystemPromptsTests.cs` asserts 80 templates exist and all keys resolve; it does not compare byte-for-byte to the TS source.

## 3. The gap

All 16 templates are affected. Concretely:
- `plan-review` × {developer, tester, security, devops, architect, product_owner, senior_developer, tech_writer}
- `code-review` × {developer, tester, security, devops, architect, product_owner, senior_developer, tech_writer}

For the 4 roles with matching branches (security, tester, architect, devops):
- TS emitted the role's own bullet pair plus three empty whitespace-only lines from the other three ternaries.
- C# emits only the matching role's bullet pair.

For the 4 roles without a matching branch (developer, product_owner, senior_developer, tech_writer):
- TS emitted four empty whitespace-only lines (so the LLM saw three blank indented lines under the header).
- C# emits the single-line fallback `"   - Apply your role-specific expertise to the plan"` (or "...diff").

For a caller sending `GET /api/prompts/security/plan-review`, TS returns:
```
3. Review from your specific expertise as a {{role}}:
   - Check for security implications in each task
   - Verify input validation and auth concerns are addressed
   
   
   
4. Identify risks or improvements
```
C# returns:
```
3. Review from your specific expertise as a {{role}}:
   - Check for security implications in each task
   - Verify input validation and auth concerns are addressed
4. Identify risks or improvements
```

In production with existing data / deployed clients, this means: any dashboard or workflow that pins a template-content hash for cache keys or diff detection will register all 16 templates as "changed" on cutover. LLM output quality for the 4 roles without a matching branch is arguably **improved** (a concrete instruction replaces three blank lines) but is **not byte-equivalent**.

Error paths:
- TS error path: N/A — templates are static.
- C# error path: N/A — templates are static.

## 4. Gap from stories

Which Epic / story file describes what this surface SHOULD be?

- Referenced story: `docs/stories/epic-12/12-5-prompt-engineering-framework.md`, `docs/stories/epic-27/27-2-prompt-store-service.md`
- Story's acceptance criteria for this behavior: Epic 27-2 requires "System defaults seeded from default-prompts.ts" with no guidance on the role-specific lens. Epic 12-5 describes the role-action matrix (80 templates) without specifying the conditional formatting.
- Story alignment:
  - [x] Matches C# behavior (story describes the role+action matrix; neither TS nor C# is explicitly mandated) — both are plausible implementations of the spec.
  - The story does not pick a winner between "empty ternary branches" and "explicit fallback text".

The C# header comment at `SystemPrompts.cs:243-246` acknowledges the drift: *"Templates are byte-for-byte equivalent to default-prompts.ts, aside from the role-specific review bullet conditionals (inlined here as literal text appropriate to each role)."* — so the divergence is known and deliberate, but the "byte-for-byte" claim elsewhere in the comment is imprecise.

## 5. Status

- **Classification**: Behavioral drift
- **What's needed to finish**: Decide whether to preserve TS wire-shape or formalize C# shape:
  1. Option A — preserve TS: change `RoleReviewLens`/`RoleReviewLensForCodeReview` to concatenate four branch results with `\n` separators, emitting empty `"   \n"` lines for non-matching roles.
  2. Option B — formalize C#: update the `SystemPrompts.cs` header comment to say "byte-for-byte equivalent except for 16 templates where role-specific review bullets are resolved to a single role-tailored block; see `RoleReviewLens`."; update epic-27-2 to codify the new behavior.
- **Is it "just a stub" or is scope missing?** Scope understood and implemented; the whitespace drift is a deliberate authorial choice, not an oversight.
- **Blockers**: None. Decision is purely about whether LLM prompt quality or contract-equivalence matters more to the team.

## Remediation

- Files to modify (Option A): `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs` (`RoleReviewLens`, `RoleReviewLensForCodeReview`).
- Files to modify (Option B): `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs` (comment), `docs/stories/epic-27/27-2-prompt-store-service.md`.
- Tests to add: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/SystemPromptsTests.cs` — add per-role assertions for `plan-review` and `code-review` bodies that lock the chosen shape.
- Estimated effort: 1h broken down as:
  - Decide option + implement switch expression: 0.5h
  - Add 16 test cases (8 roles × 2 actions): 0.5h

## Affected templates (complete list)

| Role | Action |
|------|--------|
| developer | plan-review |
| tester | plan-review |
| security | plan-review |
| devops | plan-review |
| architect | plan-review |
| product_owner | plan-review |
| senior_developer | plan-review |
| tech_writer | plan-review |
| developer | code-review |
| tester | code-review |
| security | code-review |
| devops | code-review |
| architect | code-review |
| product_owner | code-review |
| senior_developer | code-review |
| tech_writer | code-review |

## References

- TS source: `packages/api/src/services/default-prompts.ts` (commit `9e9a57c~1`, lines 260-295 and 517-550)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs:310-344, 461-499, 610-642`
- Story: `docs/stories/epic-27/27-2-prompt-store-service.md`, `docs/stories/epic-12/12-5-prompt-engineering-framework.md`
- Related findings: `docs/audit/port-gaps/prompts/003-render-response-field-names.md`
- CLAUDE.md section: "Prompt Store Architecture" (lines ~230-310)
