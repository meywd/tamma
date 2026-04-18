# Finding 004: Prompt overrides moved from tenant-scoped to user-scoped

**Scope**: prompts
**Severity**: P1 (feature broken — multi-user tenants)
**Status**: Semantic rewrite (structure changed, not a port)
**Estimated port effort**: 4h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/prompt-store.ts` and `pg-prompt-store.ts`.

- File: `packages/api/src/services/prompt-store.ts:73-97` (`IPromptStore` interface) and `packages/api/src/services/pg-prompt-store.ts:62-109` (`get`/`upsert` with tenant key).
- Contract/behavior: The TS store is **tenant-scoped**. A prompt override row has `tenant_id` as its principal key. All users in the same tenant share a single override set. Resolution is 2-layer: tenant override (`tenant_id = <uuid>`) → system default (`tenant_id IS NULL`).
- Key code (verbatim quote, `prompt-store.ts:82-87`):

```typescript
// packages/api/src/services/prompt-store.ts (9e9a57c~1)
export interface IPromptStore {
  // --- Tenant-scoped operations ---
  get(tenantId: string | null, role: string, action: string): Promise<PromptTemplate | undefined>;
  upsert(tenantId: string | null, role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate>;
  delete(tenantId: string, role: string, action: string, userId?: string): Promise<boolean>;
  list(tenantId: string | null): Promise<PromptSummary[]>;
  render(tenantId: string | null, role: string, action: string, input: RenderInput): Promise<RenderedPrompt | undefined>;
```

Handler extracts `tenantId` with a 3-level fallback (`prompt-routes.ts:52-81`):

```typescript
function getTenantId(request: FastifyRequest): string | null {
  // 1. From tenant-context middleware (Epic 17)
  if (request.tenantId !== undefined && request.tenantId !== null) return request.tenantId;
  // 2. From X-Tenant-Id header (service-to-service, e.g., Elsa)
  const headerTenantId = request.headers['x-tenant-id'];
  if (typeof headerTenantId === 'string' && headerTenantId.length > 0) return headerTenantId;
  // 3. From query parameter (fallback)
  const query = request.query as Record<string, string | undefined>;
  const queryTenantId = query['tenantId'];
  if (typeof queryTenantId === 'string' && queryTenantId.length > 0) return queryTenantId;
  return null;
}
```

`userId` was captured for **event emission only** — not for scoping the row.

- Dependencies: Epic 17 tenant-context middleware, `X-Tenant-Id` header, `prompts` / `system_prompts` / `action_prompts` tables (migration 012).
- Tests that exercised this: `prompt-routes.test.ts` scenarios for "two users in same tenant see shared override", "different tenants see different overrides".

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:21-37, 100-125, 296-300` and `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:108-165`.
- Contract/behavior: Keyed by **`userId` from `ClaimsPrincipal.NameIdentifier`**. Each user has their own override set; different users in the same tenant see **different** prompts. `TenantId` is still passed through for event emission only (`PromptEventsService.EmitUpdatedAsync(tenantId, userId, ...)`).
- Key code (verbatim quote, `PromptEndpoints.cs:296-300`):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs (current)
private static Guid? TryGetUserId(ClaimsPrincipal principal)
{
    var raw = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    return Guid.TryParse(raw, out var id) ? id : null;
}
```

Repository query (`PromptRepository.cs:8-10`):

```csharp
public async Task<PromptOverride?> GetAsync(Guid? userId, string scope, string? role, string? action)
    => await db.PromptOverrides
        .FirstOrDefaultAsync(p => p.UserId == userId && p.Scope == scope && p.Role == role && p.Action == action);
```

Entity model (`PromptOverride.cs:3-18`) retains both `UserId` and `TenantId` columns, but all repository lookups filter by `UserId` exclusively. `TenantId` is write-only telemetry.

- Dependencies: JWT auth (`ClaimTypes.NameIdentifier` claim), `ITenantContext` (used for event emission only), `PromptRepository`.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/PromptStoreServiceTests.cs` — scenarios use a single `Guid userId` and do not test tenant-sharing semantics.

## 3. The gap

Concrete behavioral difference:

- TS did: for two users A and B in tenant T, a prompt override upserted by A at `(developer, plan)` is visible to B on the next `GET /api/prompts/developer/plan` request.
- C# does: the same override is **invisible** to B — each user has their own private override namespace. `GET` from B still resolves to the system default.
- For a caller flow:
  1. User A (tenant T) sends `PUT /api/prompts/developer/plan` with a custom template.
  2. User B (tenant T, same company) sends `GET /api/prompts/developer/plan`.
  3. TS: B gets A's override.
  4. C#: B gets the system default.
- In production with existing data / deployed clients, this means: tenants that had shared prompt customization under the TS API will lose that sharing on cutover. The behavior regressed from "tenant-wide prompt sharing" to "per-user private prompts". This is consistent with CLAUDE.md's "Prompt Store Architecture" section (which describes user-keyed overrides), so it matches the *target* spec but diverges from what TS actually shipped.

Tenant-scoped behaviors lost:
1. Shared tenant customization
2. Tenant-level prompt governance (an admin editing for the org)
3. Ability to migrate org-wide prompt conventions

Error paths:
- TS error path: returns 400 when `DELETE /api/prompts/:role/:action` is called with no tenant context.
- C# error path: silently operates on the caller's own user record; no tenant-context guard.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-2-prompt-store-service.md` and `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md`.
- Story's acceptance criteria for this behavior: Epic 27-3 AC #3 says "PUT /api/prompts/:role/:action creates or updates an **tenant override for the current user's account**" — the story is genuinely ambiguous, conflating "tenant" and "user account". AC #11 says "Tenant context is extracted from the authenticated user's session".
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [x] Describes a third behavior (neither TS nor C# matches the story) — the story's own wording is inconsistent; different ACs point in different directions.
  - [ ] No story

CLAUDE.md's "Prompt Store Architecture" section (lines ~230-310) describes the target state: `prompt_overrides` table keyed by `user_id` with a `UNIQUE(user_id, scope, role, action)` constraint. The C# implementation matches CLAUDE.md; the TS implementation matched Epic 27-1's archived SQL migration 012 (tenant-keyed).

## 5. Status

- **Classification**: Semantic rewrite — the scoping axis changed, not just the structure.
- **What's needed to finish**: Decide which model is correct and align all three (stories, CLAUDE.md, impl):
  1. Option A — user-scoped (current C#): update epic-27-2 and epic-27-3 to say "user-scoped"; add migration notes for the cutover (existing tenant-scoped rows need to be fanned out to each tenant member or dropped); clarify CLAUDE.md.
  2. Option B — tenant-scoped (TS): change `PromptRepository` to query by `TenantId`, update `PromptOverride` entity to make `UserId` auditor-only, update tests, fix CLAUDE.md.
  3. Option C — both (most flexible): add a `scope` dimension where overrides can be user-owned or tenant-owned; change resolution order to user → tenant → system; update story AC and tests.
- **Is it "just a stub" or is scope missing?** Scope is fully implemented; the question is whether it's the *right* scope.
- **Blockers**: This decision blocks findings #005 (system-prompt PUT semantics) and #011 (unique constraint shape). Resolve first.

## Remediation

- Files to modify (Option A, minimum): 
  - `docs/stories/epic-27/27-2-prompt-store-service.md`, `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md` — replace "tenant" with "user account".
  - `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs` — drop `TenantId` or mark as `[Obsolete]`.
- Files to modify (Option B): 
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/PromptRepository.cs` — swap `UserId` for `TenantId` in all predicates.
  - `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs` — rename `userId` parameters to `tenantId`, re-route event tags.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs` — replace `TryGetUserId(principal)` with `tenantContext.TenantId`.
- Tests to add:
  - `PromptStoreServiceTests.cs` — `TwoUsers_SameTenant_Share_Overrides` (Option B) or `TwoUsers_SameTenant_Have_Independent_Overrides` (Option A).
- Estimated effort: 4h broken down as:
  - Decision + story update: 1h
  - Impl change + tests: 2h
  - Data-migration note for cutover: 1h

## References

- TS source: `packages/api/src/services/prompt-store.ts:73-97`, `packages/api/src/services/pg-prompt-store.ts:62-109` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:100-300`, `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:108-165`, `apps/tamma-elsa/src/Tamma.Data/Repositories/PromptRepository.cs`
- Story: `docs/stories/epic-27/27-2-prompt-store-service.md`, `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md`
- Related findings: `docs/audit/port-gaps/prompts/005-put-system-prompt-semantic-drift.md`, `docs/audit/port-gaps/prompts/011-missing-unique-constraint.md`, `docs/audit/port-gaps/prompts/012-resolution-order-four-layer.md`
- CLAUDE.md section: "Prompt Store Architecture > Data Model > Storage"
- Archived SQL migration: `database/archived-sql-migrations/012_prompt_store.sql`
