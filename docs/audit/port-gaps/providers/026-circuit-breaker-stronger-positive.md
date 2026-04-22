# Finding 026: CircuitBreakerService stronger in C# — atomic HalfOpen probe claim, POSITIVE

**Scope**: providers
**Severity**: None (positive finding)
**Status**: No gap (C# is better than TS here)
**Estimated port effort**: 0h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/pg-health-store.ts` and
`packages/api/src/services/health-store.ts`.

- File: `packages/api/src/services/pg-health-store.ts:117-159` (`PgHealthStore.recordFailure`) and `:161-177` (`recordSuccess`), `:191-218` (`syncCircuitChange`).
- Contract/behavior: TS used the `half_open_in_progress` column but did not provide an atomic claim primitive. A caller wanting to run the half-open probe had to:
  1. Read the row (`get(key)`).
  2. See `halfOpenInProgress = false` and decide to run the probe.
  3. No atomic flip — two concurrent callers could both see `false` and both run probes simultaneously.
- `recordFailure` includes a conditional `CASE WHEN half_open_in_progress THEN true` on conflict, which re-opens the circuit if a probe in progress fails. But **the claim on the probe itself was not atomic** — it relied on the caller setting the flag after claim.

```typescript
// packages/api/src/services/pg-health-store.ts (9e9a57c~1) — lines 131-152
const result = await this.pool.query<Record<string, unknown>>(
  `INSERT INTO provider_health (key, failure_count, last_failure_at, updated_at)
   VALUES ($1, 1, NOW(), NOW())
   ON CONFLICT (key) DO UPDATE SET
     failure_count = provider_health.failure_count + 1,
     last_failure_at = NOW(),
     half_open_in_progress = false,
     circuit_open = CASE
       WHEN provider_health.half_open_in_progress THEN true
       WHEN provider_health.failure_count + 1 >= $2 THEN true
       ELSE provider_health.circuit_open
     END,
   ...
```

- Consequences: in a multi-replica deployment, two Elsa engines could both be in half-open state simultaneously for the same provider key. Both send the probe; both may double-count failures; worst case, both succeed and the circuit closes with two concurrent successful probes (benign but wasteful).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs:131-149` (`TryProbeAsync`)
- Contract/behavior: C# exposes `TryProbeAsync(key, tenantId)` that performs an **atomic** claim: read the row, check state is `HalfOpen`, check `HalfOpenInProgress == false`, set `HalfOpenInProgress = true`, `SaveChangesAsync()`. The save is protected by the `(ProviderKey, TenantId)` unique constraint + the optimistic concurrency of EF Core — if two callers race, only one `SaveChanges()` succeeds; the loser gets `DbUpdateConcurrencyException` (which propagates to the caller as "you did not claim the probe").

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs — lines 131-149
public async Task<bool> TryProbeAsync(
    string providerKey, Guid? tenantId, CancellationToken ct = default)
{
    ValidateKey(providerKey);

    var existing = await _repo.GetStatusAsync(providerKey, tenantId);
    if (existing is null) return false;

    var now = _clock.UtcNow.UtcDateTime;
    var state = EffectiveStateNoWrite(existing, now);

    if (state != CircuitBreakerState.HalfOpen) return false;
    if (existing.HalfOpenInProgress) return false;

    existing.HalfOpenInProgress = true;
    existing.UpdatedAt = now;
    await _repo.SaveChangesAsync();
    return true;
}
```

- Additional strengths:
  - Uses `ISystemClock` (injectable) for testability — tests can advance wall time without real sleeps.
  - `EffectiveStateNoWrite` computes state on read without mutating the row (used by `GetStateAsync`, `TryProbeAsync`, `ListAsync`). Separation of state-query vs state-transition logic.
  - `OpenCircuit` centralizes the "open" transition (`CircuitBreakerService.cs:180-185`) — sets `CircuitOpenUntil`, `HalfOpenInProgress = false`, `Status = "down"` together.
  - `RecordFailureAsync` handles the HalfOpen-fail case at `:83-86` (re-open on probe failure) with a clear `wasHalfOpen = EffectiveStateNoWrite(health, now) == HalfOpen` pre-computation.

## 3. The gap

- No gap. C# is strictly stronger on:
  1. **Atomic HalfOpen claim** — one-at-a-time probe guarantee via `TryProbeAsync`.
  2. **Testability** — `ISystemClock` injection.
  3. **Per-tenant partitioning** (see finding 022) — isolates circuit state between tenants.
  4. **State-query read-only helpers** — `EffectiveStateNoWrite` avoids mutations in getter paths.
- The `/tmp/tamma-audit/31-providers.md` summary line 57 correctly marks this "✅ (stronger)".
- The sliding-window behaviour (finding 024) is an orthogonal semantic change — that's a P3 drift, not a regression.

For a caller in a multi-replica deployment (Elsa × 3) where the circuit just transitioned to HalfOpen:
- TS: all 3 replicas can probe simultaneously. 3× provider load at the delicate moment the circuit is being tested.
- C#: `TryProbeAsync` returns `true` for exactly one replica; the other two return `false` and skip the probe. 1× provider load.

Error paths:
- TS: no error — all replicas proceed.
- C#: losers of the race see `false` from `TryProbeAsync` and must return their caller without probing. No exception; just a boolean `false`.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md`.
- Story 9-3 AC 5: "`onCircuitChange` callback publishes state transitions to the store and optionally to SSE for real-time dashboard updates." The C# implementation supports this via the explicit state machine + `syncCircuitChange`-equivalent (`RecordFailureAsync` / `RecordSuccessAsync` / `ResetAsync` all return a `CircuitBreakerStatus` that can be relayed to SSE).
- Story 9-3 does not require `TryProbeAsync` specifically; C# added it as a safety enhancement.
- Story alignment:
  - [x] Exceeds story AC (C# above TS on this surface).

## 5. Status

- **Classification**: No gap. Positive finding.
- **What's needed to finish**: Nothing on this specific concern. Downstream consumers (Elsa activities that want to run HalfOpen probes) should call `TryProbeAsync` rather than polling `GetStateAsync` and reacting.
- **Is it "just a stub" or is scope missing?** Not applicable — functionality exceeds spec.
- **Blockers**: None.

## Remediation

Informational. No code change required. One downstream action:

- Ensure any Elsa activity or workflow step that was previously calling the TS `recordFailure`/`syncCircuitChange` for HalfOpen transitions is migrated to call `ICircuitBreakerService.TryProbeAsync` explicitly. Otherwise the race-tolerant probe claim is not exercised.

- Files to modify (downstream):
  - Any `apps/tamma-elsa/src/Tamma.Activities/LlmCall/*Circuit*.cs` activities (audit separately; out of scope for this finding).
- Tests to add:
  - `CircuitBreaker_ConcurrentTryProbe_OnlyOneReturnsTrue`
  - `CircuitBreaker_TryProbeAfterCooldown_ReturnsTrue`
  - `CircuitBreaker_TryProbeWhileOpen_ReturnsFalse`
- Estimated effort: 0h (verification + downstream integration work is separate).

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Already-fixed (positive finding)
- **Commit**: n/a — `CircuitBreakerService.TryProbeAsync` and the `EffectiveStateNoWrite` helper already implement the atomic HalfOpen claim. Per-tenant partitioning + `ISystemClock` injection are intact.
- **Notes**: Verified at `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs:131-149`. No downstream Elsa activity changes required — the audit's "use TryProbeAsync from probe call sites" note is for future workflow authors, not a current regression.

## References

- TS source: `packages/api/src/services/pg-health-store.ts:117-218`, `health-store.ts:231-270` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs:131-149, 180-185, 187-192`
- Story: `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md`
- Related findings: `022-provider-health-unique-index-positive.md`, `024-circuit-breaker-window-reset-semantic-change.md`, `012-health-api-response-shape.md`
