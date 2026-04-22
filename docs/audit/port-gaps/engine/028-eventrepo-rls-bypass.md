# Finding 028: `EventRepository` uses `IgnoreQueryFilters()` everywhere — RLS bypass risk

**Scope**: engine (DCB event store)
**Severity**: P0 (cutover-blocking — undermines tenant isolation at the repo layer)
**Status**: Behavioral drift (ported but RLS semantics inverted)
**Estimated port effort**: 6h

## 1. What's in TS

- File: `packages/api/src/persistence/pg-event-store.ts` (9e9a57c~1)

TS relied on Postgres RLS policies set up in migration `011_tenant_scoped_stores.sql` (archived). Every query was wrapped in `withTenantContext(pool, tenantId, async (client) => ...)`, which ran `SET LOCAL app.current_tenant_id = '<uuid>'` inside a transaction. RLS policies then filtered rows automatically:

```sql
-- database/archived-sql-migrations/011_tenant_scoped_stores.sql:31-37
ALTER TABLE engine_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE engine_events FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON engine_events
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);
```

```typescript
// packages/api/src/persistence/pg-event-store.ts:48-64 (9e9a57c~1)
async getEvents(tenantId: string, issueNumber?: number): Promise<EngineEvent[]> {
  return withTenantContext(this.pool, tenantId, async (client) => {
    let query = 'SELECT * FROM engine_events WHERE tenant_id = $1';
    const params: unknown[] = [tenantId];
    if (issueNumber !== undefined) {
      query += ' AND issue_number = $2';
      params.push(issueNumber);
    }
    query += ' ORDER BY timestamp ASC, id ASC';
    const result = await client.query<EngineEventRow>(query, params);
    return result.rows.map((row) => this._mapRow(row));
  });
}
```

Three layers of defence: (a) session variable, (b) RLS policy enforced in DB, (c) explicit `WHERE tenant_id = $1` in the query.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs`

```csharp
// Tamma.Data/Repositories/EventRepository.cs (current, full file)
public async Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
{
    var query = db.DomainEvents.IgnoreQueryFilters().AsQueryable();
    if (tenantId.HasValue)
        query = query.Where(e => e.TenantId == tenantId.Value);
    if (!string.IsNullOrEmpty(type))
        query = query.Where(e => e.Type == type);
    if (issueNumber.HasValue)
        query = query.Where(e => e.IssueNumber == issueNumber.Value);
    return await query.OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync();
}

public async Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
    => await db.DomainEvents.IgnoreQueryFilters()
        .Where(e => e.TenantId == tenantId && e.Type == type)
        .OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync();

public async Task ClearAsync(Guid tenantId)
{
    var events = await db.DomainEvents.IgnoreQueryFilters()
        .Where(e => e.TenantId == tenantId).ToListAsync();
    db.DomainEvents.RemoveRange(events);
    await db.SaveChangesAsync();
}
```

**Every** method uses `IgnoreQueryFilters()`. This EF Core call turns off global query filters — the safety net that enforces `WHERE TenantId = @ambientTenantId` on every query that goes through EF. Once `IgnoreQueryFilters()` runs, the only isolation is whatever `WHERE` clauses the caller remembered to add.

Two concrete failure modes:

1. **Missing caller `WHERE`**: when `QueryAsync(null, ...)` is called (e.g. finding 016), no tenant filter applies at all.
2. **Session variable bypass**: there's no Postgres session variable set. Even if RLS were enabled on `domain_events`, EF's `IgnoreQueryFilters()` plus a direct query wouldn't invoke it — but since the PG connection's role is the owner, RLS is bypassed by default (`ALTER TABLE ... FORCE ROW LEVEL SECURITY` was the TS safeguard; it's not clear the C# schema has this).

### Why was it done this way?

Presumably because the EF Core `OnModelCreating` has a global filter like:

```csharp
modelBuilder.Entity<DomainEvent>()
    .HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
```

And certain queries (system-scope processors, cross-tenant admin reports) need to bypass it. The problem is that the bypass was made the default rather than the exception.

## 3. The gap

- TS: three-layer defence (session var + RLS + explicit WHERE). A caller forgetting the WHERE still gets filtered by RLS.
- C#: one layer (explicit caller-supplied WHERE). A caller forgetting is one finding-016 away from cross-tenant data leak.

Observable instances:
- Finding 016 — `GetInstanceEvents(Guid id)` calls `QueryAsync(null, null, null, limit)` → every tenant's events returned.
- Finding 022 — Dashboard `GetSummary` calls `QueryAsync(tc.TenantId, ...)` — correct, but only because this caller remembered.
- `GetLastByTypeAsync(Guid tenantId, ...)` — includes the tenant in the WHERE, so safe.
- `ClearAsync(Guid tenantId)` — includes the tenant, safe, but `IgnoreQueryFilters()` is still a landmine for copy-paste.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` — spec for RLS.
- Also `docs/stories/epic-17/17-1-tenant-model-database-schema.md`.
- Archived SQL: `database/archived-sql-migrations/011_tenant_scoped_stores.sql` explicitly enables `FORCE ROW LEVEL SECURITY` on engine_events.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression — RLS was the whole design pattern)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift with security implications.
- **What's needed to finish**:
  1. Remove `IgnoreQueryFilters()` from the default code path.
  2. Add a global query filter `HasQueryFilter(e => _tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId)` so an unset ambient tenant is explicit-opt-in cross-tenant.
  3. Add a separate method `QueryCrossTenantAsync(...)` for the few genuine system-scope callers (admin tools, task processor reaper). Mark with `[Obsolete("Cross-tenant. Use QueryAsync when possible.")]` or an equivalent docstring.
  4. Confirm `FORCE ROW LEVEL SECURITY` exists on `domain_events` in the current EF migrations (or add it).
  5. Audit every caller of `EventRepository.QueryAsync`, `GetLastByTypeAsync`, `ClearAsync` to make sure they pass the right tenant.
  6. Add integration test `EventRepo_CrossTenant_Rejected_ByDefault` — assert that without explicit opt-in, tenant-A can't see tenant-B's events.
- **Is it "just a stub" or is scope missing?** Anti-pattern shipped — every method sidesteps a safety net by default. Correction is mechanical but touches several call sites.
- **Blockers**: finding 016 (cross-tenant leak) is the observable consequence; this is the root cause. Also interacts with finding 027 (processor cross-tenant design) — any "system scope" audit path must go through the explicit cross-tenant method.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs` — remove `IgnoreQueryFilters()` from all default paths.
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` — add global query filter keyed on `ITenantContext.TenantId`.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepositoryCrossTenant.cs` — explicit cross-tenant interface for the rare audit-scope callers.
- Tests to add:
  - `EventRepo_QueryAsync_HonorsAmbientTenantContext`
  - `EventRepo_QueryAsync_NullTenant_ReturnsEmpty_UnlessCrossTenant`
  - `EventRepo_CrossTenant_Method_ExplicitlyBypasses`
  - `Migration_ForcesRowLevelSecurity_OnDomainEvents`
- Estimated effort: 6h
  - Global filter + remove IgnoreQueryFilters: 2h
  - Audit all callers: 2h
  - Cross-tenant explicit path + tests: 2h

## References

- TS source: `packages/api/src/persistence/pg-event-store.ts`, `packages/api/src/persistence/with-tenant-context.ts`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs`
- Story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`, `17-1-tenant-model-database-schema.md`
- Archived SQL: `database/archived-sql-migrations/011_tenant_scoped_stores.sql:31-42`
- Related findings: `016-instance-events-cross-tenant-leak.md` (observable consequence), `027-task-queue-cross-tenant-processor.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (EF query-filter level; Postgres RLS deferred to Phase-3)
- **Commit**: c9dd51e
- **Notes**: `EventRepository.QueryAsync` and `GetLastByTypeAsync` no
  longer call `IgnoreQueryFilters()` on the default path — they honour
  the global query filter on `DomainEvent`
  (`e => tenantId == null || e.TenantId == tenantId`) so the ambient
  `ITenantContext` scopes by default. `ClearAsync` keeps the explicit
  bypass since it carries an authoritative `tenantId` argument. The
  Postgres RLS / `FORCE ROW LEVEL SECURITY` half lands at the
  Phase-3 connection-string split (per project status: RLS dormant
  until the `tamma_app` role connection string ships). Combined with
  finding 016 (which fixed the observable cross-tenant leak), the
  default path is now safe.
