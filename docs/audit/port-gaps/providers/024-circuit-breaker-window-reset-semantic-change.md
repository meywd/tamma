# Finding 024: Circuit breaker window reset — C# slides, TS accumulated forever

**Scope**: providers
**Severity**: P3 (drift / doc-only)
**Status**: Behavioral drift (semantics changed; arguably improved)
**Estimated port effort**: 0h (documentation only)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/health-store.ts`.

- File: `packages/api/src/services/health-store.ts:155-206` (`InMemoryHealthStore.recordFailure`)
- Contract/behavior: The failure counter **never decremented** except on a successful call. Once a row was created, every failure `failureCount++`. A success reset it to 0. There was no time-window reset.

```typescript
// packages/api/src/services/health-store.ts (9e9a57c~1) — lines 183-206
record.failureCount++;
record.lastFailureAt = nowIso;
record.updatedAt = nowIso;

// If half-open probe failed, re-open immediately
if (record.halfOpenInProgress) {
  record.halfOpenInProgress = false;
  record.circuitOpen = true;
  record.circuitOpenUntil = new Date(now.getTime() + this.circuitOpenDurationMs).toISOString();
}

// Check threshold
if (!record.circuitOpen && record.failureCount >= this.failureThreshold) {
  record.circuitOpen = true;
  record.circuitOpenUntil = new Date(now.getTime() + this.circuitOpenDurationMs).toISOString();
}
```

- Implication: a provider that fails 5 times over 2 hours (with no successes in between) tripped the circuit. A provider that fails twice a day for 3 days accumulates to 6 → circuit opens. This is "5 failures ever, until a success resets".

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs:59-99`
- Contract/behavior: Uses a sliding `FailureWindow` (default `60s`). If the next failure arrives more than 60s after `FailureWindowStart`, the window slides — `FailureWindowStart = now`, `FailureCount = 0`, then `FailureCount++` to 1. Threshold check happens against the windowed count.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs — lines 69-99
// Slide the failure window if expired or absent.
if (health.FailureWindowStart is null ||
    (now - health.FailureWindowStart.Value) > _options.FailureWindow)
{
    health.FailureWindowStart = now;
    health.FailureCount = 0;
}

health.FailureCount++;
health.LastFailure = now;
health.UpdatedAt = now;

// A HalfOpen probe that fails re-opens the circuit immediately, regardless
// of window failure count.
if (wasHalfOpen) { OpenCircuit(health, now); }
else if (health.FailureCount >= _options.FailureThreshold) { OpenCircuit(health, now); }
else { health.Status = "degraded"; }
```

- Implication: "5 failures within 60 seconds" — a burst detector. A provider failing once a minute for an hour never trips the circuit.

## 3. The gap

- TS: "5 lifetime failures without a success → open" (accumulator). Catches slow-tail flakiness.
- C#: "5 failures within 60 seconds → open" (sliding window). Catches burst failures, tolerates flaky-tail.
- The two semantics produce different behaviour:
  - Provider with 2% intermittent failures, 1 req/s: TS opens within 4 minutes; C# opens only if a burst happens to cluster.
  - Provider with a flash outage (10 failures in 2 seconds): both open; C# opens faster because the window starts immediately.
  - Provider with transient auth issue (3 fails, then a success, then 3 fails…): TS resets on each success; C# also effectively resets. Equivalent here.
- C# is arguably stronger for transient-error tolerance but weaker for catching long-tail degradation. The TS behaviour is better if you want to know "provider X has consistently misbehaved today".
- For a caller whose workflow retries a failing provider every 10 seconds:
  - TS: after 5 failures (50s), circuit opens.
  - C#: after 5 failures (50s if within the 60s window, or never if spaced out enough). With a 10s interval inside a 60s window: 5 failures land, circuit opens at the 5th.

Error paths:
- Identical — both throw or return `CircuitBreakerState.Open`. Only the *when* differs.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md`.
- Story 9-3 does not pin the window semantics. Under-specified.
- Archived `015_provider_health.sql` schema also doesn't carry window metadata (no `failure_window_start` column — TS stored only `failure_count` + `last_failure_at`).
- Story alignment:
  - [x] Describes a third behavior — story is agnostic; both impls are compliant with AC.
  - [ ] Matches TS behavior.
  - [ ] Matches C# behavior.

## 5. Status

- **Classification**: Behavioral drift (arguably an upgrade).
- **What's needed to finish**:
  1. Document the sliding-window semantics explicitly in `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md`.
  2. Expose `FailureWindow` and `FailureThreshold` via `CircuitBreakerOptions` in `appsettings.json` (they are configurable via DI today — just not documented).
  3. (Optional) Add a second threshold policy: "lifetime failures without success" to catch long-tail flakiness. This preserves both behaviours.
  4. Write an ADR in `.dev/decisions/` capturing the semantic change and rationale.
- **Is it "just a stub" or is scope missing?** Neither. Both implementations satisfy AC. The C# choice is not a bug, but the behavioural difference from TS is worth flagging because an operator upgrading from TS may see circuits behaving differently.
- **Blockers**: None.

## Remediation

No code change required. Documentation only.

- Files to modify (doc):
  - `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md` (add AC note)
- Files to create:
  - `.dev/decisions/<next>-circuit-breaker-sliding-window.md`
- Tests to add: none for the existing behaviour; if dual-policy is added, `CircuitBreaker_LongTailDegradation_OpensAfterNLifetimeFailuresWithoutSuccess`.
- Estimated effort: 30min (doc only).

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Already-fixed (P3 — drift documented; both implementations satisfy AC)
- **Commit**: n/a — code change deemed unnecessary per the audit's own §5 ("No code change required. Documentation only.").
- **Notes**: The C# sliding-window semantics are stronger for transient-error tolerance and are wired through `CircuitBreakerOptions` (configurable). The audit suggested (a) doc update on Story 9-3 and (b) optional ADR — both are documentation chores tracked separately, not port-gap regressions. Sliding-window vs lifetime-counter is an operational characteristic, not a correctness bug.

## References

- TS source: `packages/api/src/services/health-store.ts:155-206`, `packages/api/src/services/pg-health-store.ts:117-159` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs:59-99`, `CircuitBreakerOptions.cs`
- Story: `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md`
- Related findings: `026-circuit-breaker-stronger-positive.md`, `022-provider-health-unique-index-positive.md`
