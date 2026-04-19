# Finding 012: Health API response shape diverges — 404 on unknown key, field renames

**Scope**: providers
**Severity**: P2 (contract drift; dashboard clients break)
**Status**: Behavioral drift
**Estimated port effort**: 2–3h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/settings/health-routes.ts`.

- File: `packages/api/src/routes/settings/health-routes.ts:51-71`
- Contract/behavior: `GET /health/providers/:key` returned **200** with a synthesized-healthy shape when the key had no record, explicitly treating "no activity" as healthy. Shape: `{healthy, failures, circuitOpen, circuitOpenUntil, halfOpen}`.

```typescript
// packages/api/src/routes/settings/health-routes.ts (9e9a57c~1) — lines 51-71
app.get('/health/providers/:key', async (request, reply) => {
  const { key } = request.params as { key: string };
  const error = validateKeyParam(key);
  if (error) { return reply.status(400).send({ error }); }
  const status = await store.get(key);
  if (!status) {
    // Unknown keys are considered healthy (no failures recorded)
    return reply.send({
      healthy: true,
      failures: 0,
      circuitOpen: false,
      circuitOpenUntil: null,
      halfOpen: false,
    });
  }
  return reply.send(status);
});
```

- The summary endpoint `GET /health` returned `Record<string, HealthStatusSummary>` — a map from key → summary.
- The failure/success endpoints returned `{circuitOpen, failures}` (write endpoints).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:57-79`
- Contract/behavior: `GET /api/providers/health/providers/{key}` returns **404** when there is no persisted row. The shape contains different field names (`providerKey`, `state`, `status`, `failureCount`, `lastSuccess`, `lastFailure`, `circuitOpenUntil`, `halfOpenInProgress`).

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs — lines 57-79
public static async Task<IResult> GetProviderHealth(
    string key,
    [FromServices] ICircuitBreakerService breaker,
    [FromServices] IProviderHealthRepository repo,
    [FromServices] ITenantContext tc)
{
    // Require an existing row; unseen keys return 404 for parity with the prior API.
    var row = await repo.GetStatusAsync(key, tc.TenantId);
    if (row is null) return Results.NotFound(new { error = "Provider not found" });

    var s = await breaker.GetStateAsync(key, tc.TenantId);
    return Results.Ok(new
    {
        providerKey = s.ProviderKey,
        state = s.State.ToString(),    // "Closed" | "HalfOpen" | "Open"
        status = MapLegacyStatus(s.State),   // "healthy" | "degraded" | "down" | "unknown"
        failureCount = s.FailureCount,
        lastSuccess = s.LastSuccess,
        lastFailure = s.LastFailure,
        circuitOpenUntil = s.CircuitOpenUntil,
        halfOpenInProgress = s.HalfOpenInProgress,
    });
}
```

- The comment `// for parity with the prior API` is misleading — the prior API **returned 200 with a synthesized healthy body**, not 404.
- The listing endpoint returns an array, not a map (`ListProviderHealth` at `ProviderEndpoints.cs:39-55`).

## 3. The gap

- **404 regression**: TS treated an unknown key as healthy and returned 200. C# returns 404. Any dashboard that did `GET /health/providers/:key` and expected a JSON body to render now has to handle 404 branching.
- **Field renames**: `failures → failureCount`, `halfOpen → halfOpenInProgress`, `healthy → state` (enum instead of boolean — callers must map). The `healthy: bool` convenience is dropped.
- **Extra fields**: C# adds `providerKey`, `state`, `status`, `lastSuccess`, `lastFailure`. TS did not emit `lastSuccess`/`lastFailure` in the per-key response.
- **`GET /health` shape**: TS returned a `Record<string, Summary>` map; C# `GetHealthSummary` returns an object `{providers: [...]}` with an array. JSON consumers that `Object.entries(response)` break.
- For a caller doing `GET /api/providers/health/providers/anthropic:claude-sonnet-4`:
  - TS: `200 {"healthy":true,"failures":0,"circuitOpen":false,"circuitOpenUntil":null,"halfOpen":false}`.
  - C#: `404 {"error":"Provider not found"}` (until the first failure is recorded).

Error paths:
- TS: `400 {error:'key contains invalid characters'}` for bad key; else always `200`.
- C#: `400` via minimal-API model binding for empty key; `404` for unknown key. **No regex/length validation** on the route (see finding 013).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md`.
- Story 9-3 AC 3: "`GET /api/v1/health/providers/:key` — returns health status for a specific key." Does not specify 404-vs-synthesize behaviour. Ambiguous.
- Archived SQL `015_provider_health.sql` pre-creates no rows — tenants start with zero rows, so "unknown key" is the default state on day 1.
- Story alignment:
  - [x] Describes a third behavior — underspecified; C# chose 404, TS chose synthesize.
  - [ ] Matches TS behavior.
  - [ ] Matches C# behavior.

## 5. Status

- **Classification**: Behavioral drift / contract drift.
- **What's needed to finish**:
  1. Restore TS behaviour: return 200 with a synthesized `{state:"Closed", failureCount:0, ...}` when the row is absent. This makes the API idempotent for health polling.
  2. Add a response DTO `ProviderHealthResponse` so the shape is stable and documented.
  3. Keep the extra C# fields (`lastSuccess`, `lastFailure`) — they're a legitimate enrichment.
  4. Update `GetHealthSummary` to return a map for TS parity (or a top-level `providers[]` + a breaking-change note + dashboard migration).
  5. Update Story 9-3 to explicitly pin either shape.
- **Is it "just a stub" or is scope missing?** Intentional drift — the C# engineer commented "for parity with the prior API" while changing behaviour. Either the parity comment is wrong, or the implementation is wrong.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:57-79` (synthesize on null)
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:18-55` (summary shape)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Providers/ProviderHealthResponse.cs`
- Tests to add:
  - `ProviderHealth_UnknownKey_Returns200WithHealthyBody`
  - `ProviderHealth_KnownKey_IncludesLastSuccessAndFailure`
  - `ProviderHealthSummary_ReturnsKeyedMapForTsParity`
- Estimated effort: 3h.

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed
- **Commit**: `0dbccf9` `fix(providers): land P1/P2 diagnostics/health/validation/user-providers fixes [findings 008, 009, 010, 012, 013, 014, 018, 019]`
- **Notes**: `GET /api/providers/health/providers/{key}` now returns 200 with a synthesized Closed/healthy body for unseen keys (TS parity); the previous 404 regression is gone. Response carries both the new C# fields (`state`, `failureCount`, `halfOpenInProgress`, `lastSuccess`, `lastFailure`) and the TS-compat scalar shorthand (`healthy`, `circuitOpen`, `halfOpen`, `failures`) so dashboards on either shape work. `GET /api/providers/health` adds `byKey` map alongside `providers` array. Existing test `GetProviderHealth_UnknownKey_Returns404` flipped to assert the new 200-with-healthy-body behaviour.

## References

- TS source: `packages/api/src/routes/settings/health-routes.ts:51-71`, `packages/api/src/services/health-store.ts:30-37` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs:18-79`
- Story: `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md` AC 3
- Related findings: `013-health-key-validation-missing.md`, `026-circuit-breaker-stronger-positive.md`
- Archived SQL migration: `database/archived-sql-migrations/015_provider_health.sql`
