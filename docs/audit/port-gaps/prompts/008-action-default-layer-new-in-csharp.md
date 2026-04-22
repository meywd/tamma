# Finding 008: Action-default safety-net layer is new in C# (positive deviation, but TS had no runtime equivalent)

**Scope**: prompts
**Severity**: P3 (drift/contract — positive)
**Status**: Behavioral drift (ported but added a layer)
**Estimated port effort**: 0h (documentation update only)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Already-fixed (matches CLAUDE.md spec; locked by tests)
- **Commit**: ea4d5e5
- **Notes**: Layer-4 fallback is the CLAUDE.md spec; no code change needed. Added two regression tests in `PromptStoreServiceTests.cs`: `ResolveRoleActionAsync_UnknownRole_KnownAction_ResolvesToActionDefault` (locks the positive deviation — unknown roles still get a usable template) and `ResolveRoleActionAsync_UnknownRole_UnknownAction_ReturnsNull` (locks the remaining 404 path). The write path for Layer 3 (user action-default overrides) is implemented in `PromptStoreService.UpsertActionDefaultAsync` but no HTTP route exposes it yet — deliberate; CLAUDE.md does not document a write route for action-defaults.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/prompt-store.ts` and `pg-prompt-store.ts`.

- File: `packages/api/src/services/prompt-store.ts:73-97` (`IPromptStore` interface) and `pg-prompt-store.ts:62-82` (`get()` resolution).
- Contract/behavior: TS resolution was a **2-layer** fallback: tenant override → system default (both stored in the `prompts` table, distinguished by `tenant_id IS NULL`). There was **no** runtime "action-default" layer — if a role+action template did not exist in the `prompts` table, the caller received `undefined`. The `action_prompts` table existed in migration 012 (archived) but was **never populated or read** by the production TS store; it was provisioned for future use per epic-27-1 AC #5.
- Key code (verbatim quote, `pg-prompt-store.ts:62-82`):

```typescript
// packages/api/src/services/pg-prompt-store.ts (9e9a57c~1)
async get(tenantId: string | null, role: string, action: string): Promise<PromptTemplate | undefined> {
  // 1. Try tenant override
  if (tenantId !== null) {
    const override = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM prompts WHERE tenant_id = $1 AND role = $2 AND action = $3',
      [tenantId, role, action],
    );
    if (override.rows.length > 0) {
      return this._mapRow(override.rows[0]!);
    }
  }

  // 2. Fall back to system default
  const systemDefault = await this.pool.query<Record<string, unknown>>(
    'SELECT * FROM prompts WHERE tenant_id IS NULL AND role = $1 AND action = $2',
    [role, action],
  );
  if (systemDefault.rows.length > 0) {
    return this._mapRow(systemDefault.rows[0]!);
  }

  return undefined;   // no action-default fallback
}
```

- Dependencies: `prompts` table only.
- Tests that exercised this: `pg-prompt-store.test.ts` — "missing role+action returns undefined".

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:108-165` (4-layer resolution) and `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs:100-172` (`ActionDefaults` dictionary).
- Contract/behavior: C# implements a **4-layer** fallback matching CLAUDE.md's aspirational spec:
  1. User's role+action override (from `prompt_overrides` table, `Scope = "role-action"`)
  2. System default role+action (from `SystemPrompts.RoleActionTemplates` — 80 hardcoded templates)
  3. User's action-default override (from `prompt_overrides` table, `Scope = "action-default"`)
  4. System action-default template (from `SystemPrompts.ActionDefaults` — 10 hardcoded safety-net templates)
- Key code (verbatim quote, `PromptStoreService.cs:108-165`):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs (current)
public async Task<ResolvedPrompt?> ResolveRoleActionAsync(Guid? userId, string role, string action)
{
    // Layer 1: user's role+action override
    if (userId is not null)
    {
        var userOverride = await _repository.GetAsync(userId, "role-action", role, action);
        if (userOverride is not null) return ToResolved(role, action, userOverride, PromptSource.UserOverride);
    }

    // Layer 2: system default role+action
    var systemRoleAction = SystemPrompts.GetRoleAction(role, action);
    if (systemRoleAction is not null) return new ResolvedPrompt(...);

    // Layer 3: user's action-default override
    if (userId is not null)
    {
        var userActionDefault = await _repository.GetAsync(userId, "action-default", null, action);
        if (userActionDefault is not null) return ToResolved(role, action, userActionDefault, PromptSource.UserActionDefault);
    }

    // Layer 4: system action default (safety net)
    var systemActionDefault = SystemPrompts.GetActionDefault(action);
    if (systemActionDefault is not null) return new ResolvedPrompt(...);

    return null;
}
```

Ten hardcoded action-default templates in `SystemPrompts.cs:100-172` cover the 10 known actions (context-scan, plan, plan-review, implement, write-tests, refactor, code-review, triage, summarize, debug) with generic, role-agnostic prompts suitable as safety nets.

- Dependencies: `IPromptRepository.GetAsync(userId, "action-default", null, action)`, `SystemPrompts.ActionDefaults`.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/PromptStoreServiceTests.cs` — covers all 4 layers including Layer 4 fallback.

## 3. The gap

Concrete behavioral difference:

- TS did: for an unknown role (e.g., `GET /api/prompts/ai_auditor/plan` where `ai_auditor` is not a known role), return 404 — the `prompts` table has no rows for that role and the lookup returns `undefined`.
- C# does: for the same request, Layer 2 returns null (unknown role), Layer 3 returns null (no user override at action-default scope), Layer 4 returns the generic plan action-default template with `Role: "ai_auditor", Action: "plan"` — a usable response instead of 404.

Two notable effects:

1. **Unknown roles become usable.** Under TS, roles outside the canonical 8 (`developer, tester, security, devops, architect, product_owner, senior_developer, tech_writer`) had no prompts. Under C#, any role string reaches a generic action-default template as long as the action is one of the canonical 10. This is a **positive deviation** — it enables third-party plugins or dynamically registered roles.

2. **Schema flex.** Users can customize the action-default layer per-user via `Scope = "action-default"` rows — a feature that had no runtime endpoint or behavior in TS. No endpoint currently writes to this layer (see Finding #006 for the missing `GET /api/prompts/defaults/:action` endpoint), so the write-path gap remains.

For a caller sending `GET /api/prompts/ai_auditor/plan`:
- TS returns `404 { error: "Prompt template not found for role=ai_auditor, action=plan" }`.
- C# returns `200 { role: "ai_auditor", action: "plan", template: "You are a {{role}} creating an implementation plan for {{workItemJson}}...", source: "system" }` — the action-default template with `{{role}}` variable that will be interpolated to "ai_auditor" at render time.

Error paths:
- TS error path: 404 when no role+action match.
- C# error path: 404 only when action is also unknown (not in the 10 canonical actions).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-1-prompt-store-database-schema.md` AC #5 (`action_prompts` table) and `docs/stories/epic-27/27-2-prompt-store-service.md` (resolution order).
- Story's acceptance criteria for this behavior: Epic 27-1 AC #5 provisions the `action_prompts` table but 27-2 does not require using it at runtime. CLAUDE.md "Prompt Store Architecture > Resolution Order" describes the 4-layer order.
- Story alignment:
  - [ ] Matches TS behavior
  - [x] Matches C# behavior — story was ahead of TS impl; C# is closer to spec than TS ever was.
  - [ ] Describes a third behavior

CLAUDE.md "Prompt Store Architecture > Resolution Order" (lines ~247-260) explicitly lists all 4 layers, and the C# implementation matches it exactly.

## 5. Status

- **Classification**: Behavioral drift — positive deviation vs TS, matching spec.
- **What's needed to finish**: Nothing in production code. Documentation:
  1. Update CLAUDE.md or a migration-notes doc to flag this as a **contract expansion** (not a bug) so integrators are aware the resolution space now covers arbitrary role names.
  2. Consider adding write-path for Layer 3 (user action-default overrides) — see Finding #006.
  3. Review endpoint AC in epic-27-3 to ensure it reflects that 404 is less likely now.
- **Is it "just a stub" or is scope missing?** Scope was fully understood and implemented beyond what TS shipped. The deliberate expansion needs a documentation note, not code changes.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `docs/stories/epic-27/27-2-prompt-store-service.md` — add note that resolution now covers action-default fallback for unknown roles.
  - `CLAUDE.md` — no change needed (already matches).
- Files to create: None (unless a migration-notes doc is standard).
- Tests to add:
  - `PromptStoreServiceTests.cs` — `UnknownRole_KnownAction_ResolvesToActionDefault` (lock the positive deviation).
  - `PromptStoreServiceTests.cs` — `UnknownRole_UnknownAction_ReturnsNull` (lock the remaining 404 path).
- Estimated effort: 0h broken down as:
  - Documentation: 0h (trivial paragraph).
  - Tests (optional but recommended): 0.2h.

## References

- TS source: `packages/api/src/services/pg-prompt-store.ts:62-82` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:108-165`, `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs:100-172`
- Story: `docs/stories/epic-27/27-1-prompt-store-database-schema.md` AC #5, `docs/stories/epic-27/27-2-prompt-store-service.md`
- Related findings: `docs/audit/port-gaps/prompts/006-missing-defaults-endpoints.md`, `docs/audit/port-gaps/prompts/012-resolution-order-four-layer.md`
- CLAUDE.md section: "Prompt Store Architecture > Resolution Order"
- Archived SQL migration: `database/archived-sql-migrations/012_prompt_store.sql` (defines `action_prompts` but was never wired to runtime)
