# Finding 023: Dashboard `/engines` hardcoded empty

**Scope**: engine (dashboard)
**Severity**: P1 (feature broken — dashboard engines tile blank)
**Estimated port effort**: 2h (after finding 013 lands)

## 1. What's in TS

- File: `packages/api/src/routes/dashboard/index.ts:57-62` (9e9a57c~1)

```typescript
// packages/api/src/routes/dashboard/index.ts:57-62 (9e9a57c~1)
fastify.get('/api/dashboard/engines', async (_request, reply) => {
  const engines = engineRegistry.list();
  return reply.send(engines);
});
```

`engineRegistry.list()` returns `EngineInfo[]` — `{id, state, stats}` per registered engine. This is the data that drove the dashboard "Engines" panel: one card per engine, with live status (idle / running / paused / error), event counts, and per-engine stats.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:23-24`

```csharp
// DashboardEndpoints.cs:23-24 (current)
public static Task<IResult> GetEngines() =>
    Task.FromResult(Results.Ok(Array.Empty<object>()));
```

Return: always `[]`. No attempt to count processes, enumerate installations, or proxy to any downstream registry.

## 3. The gap

- TS did: iterated a registry of TammaEngine instances, returned `{id, state, stats}` per engine.
- C# does: empty array.

For the dashboard's Engines panel:

- TS: one card per engine, status badge, last-seen timestamp, issue count.
- C#: "No engines connected" state — indistinguishable from a broken deployment.

Compounding: even when a user has three engines actively running workflows, the dashboard shows zero. There's no way to tell the system is working from the dashboard alone.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-5/5-3-real-time-dashboard-system-health.md`, `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md`.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented — blocked on the absent Engine Registry abstraction.
- **What's needed to finish**:
  1. Land finding 013 (engine registry).
  2. Inject `IEngineRegistry` into this endpoint.
  3. Return `registry.List()` filtered to the ambient tenant.
- **Is it "just a stub" or is scope missing?** Scope missing (registry absent).
- **Blockers**: finding 013 is a hard dependency.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:23-24`
- Tests to add:
  - `GetEngines_ReturnsRegisteredEngines`
  - `GetEngines_EmptyRegistry_Returns200EmptyArray`
  - `GetEngines_TenantIsolated`
- Estimated effort: 2h (after finding 013)
  - Endpoint rewrite: 30m
  - Tests: 1.5h

## References

- TS source: `packages/api/src/routes/dashboard/index.ts:57-62`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:23-24`
- Story: `docs/stories/epic-5/5-3-real-time-dashboard-system-health.md`, `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md`
- Related findings: `013-engine-registry-missing.md` (blocker), `022-dashboard-summary-shape-drift.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: a3d2e7e
- **Notes**: `DashboardEndpoints.GetEngines` now enumerates
  `IEngineRegistry.ListAsync(tc.TenantId)` (finding 013). Until the real
  `TammaEngine` ports the registry serves a synthetic per-tenant entry
  derived from workflow data so the tile is no longer blank.
