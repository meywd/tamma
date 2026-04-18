# Finding 013: Engine Registry does not exist

**Scope**: engine
**Severity**: P1 (feature broken — multi-engine deployments impossible, dashboard engines tile dead)
**Status**: Not-yet-implemented — the entire abstraction was skipped in the C# port.
**Estimated port effort**: 16–20h

## 1. What's in TS

- File: `packages/api/src/engine-registry.ts` (9e9a57c~1), 77 LoC

```typescript
// packages/api/src/engine-registry.ts (9e9a57c~1)
export class EngineRegistry {
  private engines = new Map<string, TammaEngine>();

  register(id: string, engine: TammaEngine): void {
    if (this.engines.has(id)) {
      throw new Error(`Engine with id "${id}" is already registered`);
    }
    this.engines.set(id, engine);
  }

  get(id: string): TammaEngine | undefined {
    return this.engines.get(id);
  }

  list(): EngineInfo[] {
    const result: EngineInfo[] = [];
    for (const [id, engine] of this.engines) {
      result.push({
        id,
        state: engine.getState(),
        stats: engine.getStats(),
      });
    }
    return result;
  }

  async dispose(id: string): Promise<void> {
    const engine = this.engines.get(id);
    if (engine === undefined) { return; }
    await engine.dispose();
    this.engines.delete(id);
  }

  async disposeAll(): Promise<void> {
    const ids = [...this.engines.keys()];
    await Promise.all(ids.map((id) => this.dispose(id)));
  }

  get size(): number { return this.engines.size; }
}
```

Used by:
- `packages/api/src/routes/dashboard/index.ts` — `/api/dashboard/engines`, `/api/dashboard/summary` both iterate the registry.
- The API server's bootstrap: registers one or more engines per repository/project.

The registry was the seam between the HTTP layer and the engine process model. It let multiple engines run in a single API process (one per repo), let the dashboard enumerate them, and let clients route commands by id.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Engine/` — does not exist.
- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:23-24` reflects this:

```csharp
public static Task<IResult> GetEngines() =>
    Task.FromResult(Results.Ok(Array.Empty<object>()));
```

`Summary` (finding 022) has no engine references. There is no DI registration, no interface, no implementation, no tests.

## 3. The gap

- TS did: named map of engines, with register/get/list/dispose + disposeAll. Entry point for every engine-scoped HTTP endpoint and the dashboard.
- C# does: nothing. There is no registry, no engine abstraction (see finding 012), and `/api/dashboard/engines` hardcodes `[]`.

For a deployment that ran two engines (one for `acme/webapp`, one for `acme/api`):

- TS: both showed up in `GET /api/dashboard/engines` with their own state/stats. Commands scoped by id (`?engineId=webapp`).
- C#: no way to distinguish. Every `/api/engine/*` endpoint represents "the process", not a specific engine. Multi-tenant / multi-repo isolation doesn't exist at the engine layer.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-10/story-10-1/10-1-engine-static-workflow-and-brain.md` describes a single engine per process. The registry was a layered concern added when the API moved toward a multi-engine-per-process deployment model.
- Also referenced in the TS source header for `dashboard/index.ts`. No explicit story, but cross-referenced by `docs/stories/epic-5/5-3-real-time-dashboard-system-health.md` (dashboard consumer).
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

## 5. Status

- **Classification**: Not-yet-implemented — abstraction missing entirely.
- **What's needed to finish**:
  1. Port `EngineRegistry` to C# as `IEngineRegistry` + `InMemoryEngineRegistry`.
  2. Lifecycle tied to `IHostedService` so engines are torn down on shutdown.
  3. Wire it into the dashboard `/engines` endpoint and the engine `/state` / `/events/*` endpoints (cross-ref findings 012, 023).
  4. Decide multi-tenant story: registry is process-scoped, but should `list()` filter by ambient `ITenantContext`? (Probably yes — each tenant sees only their engines.)
  5. Add command routing: `POST /api/engine/command?engineId=...` → `registry.get(id).send(cmd)`.
- **Is it "just a stub" or is scope missing?** Scope missing entirely.
- **Blockers**: depends on a real `TammaEngine` C# abstraction (finding 012).

## Remediation

- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/IEngineRegistry.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/InMemoryEngineRegistry.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Engine/EngineInfo.cs` (DTO record)
- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs` — add `engineId` query binding on all engine endpoints.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs:23-24` — wire the registry.
  - `Program.cs` — `AddSingleton<IEngineRegistry, InMemoryEngineRegistry>()`; register as `IHostedService` for disposal.
- Tests to add:
  - `Register_Duplicate_Throws`
  - `List_FiltersByTenant_WhenContextSet`
  - `Dispose_CallsEngineDispose_ThenRemovesFromMap`
  - `DashboardEngines_ReturnsRegistryList`
  - `EngineCommand_RoutesById`
- Estimated effort: 16–20h
  - Registry port + DI: 2h
  - Tenant-scoped `list()`: 2h
  - Wire all engine endpoints to registry (7 endpoints): 4h
  - Dashboard `/engines` integration: 2h
  - Heartbeat / liveness column (stretch): 2h
  - Tests: 4–6h

## References

- TS source: `packages/api/src/engine-registry.ts`
- C# source: absent
- Story: (no direct story — registry was a TS-era layered abstraction)
- Related findings: `012-engine-lifecycle-sse-to-json.md`, `022-dashboard-summary-shape-drift.md`, `023-dashboard-engines-empty.md`
