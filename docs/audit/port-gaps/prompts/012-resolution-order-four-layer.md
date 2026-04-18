# Finding 012: Resolution order — TS 2-layer, C# 4-layer (matches CLAUDE.md aspirational spec)

**Scope**: prompts
**Severity**: P3 (drift/contract — positive)
**Status**: Behavioral drift (positive deviation)
**Estimated port effort**: 0h (documentation only)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/pg-prompt-store.ts`.

- File: `packages/api/src/services/pg-prompt-store.ts:62-82` (2-layer `get()`) and `packages/api/src/services/prompt-store.ts:73-97` (interface).
- Contract/behavior: TS had a **2-layer** resolution:
  1. Tenant override (`prompts WHERE tenant_id = $1 AND role = $2 AND action = $3`)
  2. System default (`prompts WHERE tenant_id IS NULL AND role = $1 AND action = $2`)
  Return `undefined` if neither matched.

The `action_prompts` table was provisioned in migration 012 but **never queried** by the store (see Finding #008). The system-prompt (role preamble) fallback was also 2-layer but lived in a separate `system_prompts` table with tenant override → tenant NULL.

- Key code (verbatim quote):

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

  return undefined;
}
```

- Dependencies: `prompts` table; `system_prompts` table for role preamble.
- Tests that exercised this: `pg-prompt-store.test.ts` — 2-layer scenarios only.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:108-183`.
- Contract/behavior: **4-layer** resolution for role+action, matching CLAUDE.md "Prompt Store Architecture > Resolution Order" (lines ~247-260). Plus a separate 2-layer fallback for role-system preamble.
- Key code (verbatim quote, `PromptStoreService.cs:108-165`):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs (current)
public async Task<ResolvedPrompt?> ResolveRoleActionAsync(Guid? userId, string role, string action)
{
    // Layer 1: user's role+action override
    if (userId is not null)
    {
        var userOverride = await _repository.GetAsync(userId, "role-action", role, action);
        if (userOverride is not null)
            return ToResolved(role, action, userOverride, PromptSource.UserOverride);
    }

    // Layer 2: system default role+action
    var systemRoleAction = SystemPrompts.GetRoleAction(role, action);
    if (systemRoleAction is not null)
        return new ResolvedPrompt(...);

    // Layer 3: user's action-default override
    if (userId is not null)
    {
        var userActionDefault = await _repository.GetAsync(userId, "action-default", null, action);
        if (userActionDefault is not null)
            return ToResolved(role, action, userActionDefault, PromptSource.UserActionDefault);
    }

    // Layer 4: system action default (safety net)
    var systemActionDefault = SystemPrompts.GetActionDefault(action);
    if (systemActionDefault is not null) return new ResolvedPrompt(...);

    return null;
}
```

Role-system preamble (`PromptStoreService.cs:171-183`) has its own 2-layer order:

```csharp
public async Task<string?> ResolveRoleSystemAsync(Guid? userId, string role)
{
    if (userId is not null)
    {
        var userOverride = await _repository.GetAsync(userId, "role-system", role, null);
        if (userOverride is not null) return userOverride.Template;
    }
    return SystemPrompts.RoleSystemPrompts.TryGetValue(role, out var prompt) ? prompt : null;
}
```

- Dependencies: `IPromptRepository.GetAsync` (scope-aware), `SystemPrompts.GetRoleAction` and `.GetActionDefault` (static in-memory registry).
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/PromptStoreServiceTests.cs` covers all 4 layers.

## 3. The gap

Concrete behavioral difference:

- TS did: return `undefined` for any `(role, action)` pair not present in the `prompts` table — typically only the 80 seeded rows plus any tenant override.
- C# does: cascade through 4 layers and return the system action-default (Layer 4) for any known action, even for roles outside the canonical 8.

For a caller sending `GET /api/prompts/custom_role/plan`:
- TS: 404 (`prompts` table has no `custom_role` seed; no tenant override).
- C#: 200 with the generic action-default template for `plan`, with `role` set to `custom_role` in the response and the `{{role}}` placeholder ready to be interpolated at render time.

For a caller sending `GET /api/prompts/developer/unknown_action`:
- TS: 404 (`unknown_action` not in seed; no tenant override).
- C#: 404 (Layer 2 misses, Layer 4 misses since action-defaults are keyed by action name).

Positive effects of the 4-layer model:
1. **Plugin roles work out of the box** — third-party roles (e.g., `data_engineer`) get a usable template via Layer 4.
2. **Per-user action-default customization** — users can override Layer 4 independent of their role-specific overrides (write-path currently missing; see Finding #006).
3. **Cleaner separation of concerns** — role-system preambles live in their own resolution pipeline.

Neutral effects:
- The 4-layer order does not regress any TS capability; TS was a strict subset.

Error paths:
- TS error path: 404 when both layers miss.
- C# error path: 404 only when all 4 layers miss (much rarer).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-2-prompt-store-service.md`.
- Story's acceptance criteria for this behavior: Epic 27-2 describes a 2-layer tenant→system resolution. CLAUDE.md "Prompt Store Architecture > Resolution Order" describes a 4-layer order.
- Story alignment:
  - [ ] Matches TS behavior
  - [x] Matches C# behavior — C# follows CLAUDE.md.
  - [ ] Describes a third behavior
  - The story lagged behind CLAUDE.md; C# chose CLAUDE.md as the authoritative spec.

## 5. Status

- **Classification**: Behavioral drift — positive deviation.
- **What's needed to finish**:
  1. Update `docs/stories/epic-27/27-2-prompt-store-service.md` to explicitly call out the 4-layer order and match CLAUDE.md.
  2. Optionally add documentation explaining the rationale (safety-net for arbitrary roles, future extension point).
  3. Close the write-path gap for Layer 3 per Finding #006.
- **Is it "just a stub" or is scope missing?** Scope was expanded beyond the story to match CLAUDE.md. Not a stub.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `docs/stories/epic-27/27-2-prompt-store-service.md` — document the 4-layer order.
- Files to create: None.
- Tests to add:
  - `PromptStoreServiceTests.cs` — ensure all 4 resolution paths are covered with explicit named tests:
    - `Resolve_UserOverride_Wins`
    - `Resolve_SystemRoleAction_WhenNoUserOverride`
    - `Resolve_UserActionDefault_WhenRoleActionMissing`
    - `Resolve_SystemActionDefault_AsSafetyNet`
    - `Resolve_AllMiss_ReturnsNull`
- Estimated effort: 0h code; 0.3h documentation/tests.

## References

- TS source: `packages/api/src/services/pg-prompt-store.ts:62-82` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:108-183`
- Story: `docs/stories/epic-27/27-2-prompt-store-service.md`
- Related findings: `docs/audit/port-gaps/prompts/006-missing-defaults-endpoints.md`, `docs/audit/port-gaps/prompts/008-action-default-layer-new-in-csharp.md`
- CLAUDE.md section: "Prompt Store Architecture > Resolution Order" (lines ~247-260)
