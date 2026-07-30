# Finding: four more session-scoped advisory locks were held on POOLED connections — the gate can park itself SHUT

**Date**: 2026-07-30
**Context**: follow-up audit ordered by commit `b958adc` ("Fix the sweep gate parking itself
shut: an advisory lock on a pooled connection"), whose commit message recorded four other sites
that take a session-scoped `pg_try_advisory_lock` the same way and asked for them to be audited.
**Verdict**: all four were exposed. All four fixed, via a shared helper
(`Tamma.Data.Pooling.PostgresAdvisoryLock`) extracted from the sweep runner's now-correct
pattern. Two reverse-direction hazards (lock dies while the guarded work continues) are recorded
here as open, not fixed.

## The mechanism

Every advisory-lock holder in this codebase documented the same invariant, in almost the same
words: *the lock is session-scoped, so closing the connection releases it, so there is no failure
mode that leaves the gate stuck shut.*

**That invariant is false on a pooled connection.** Disposing a pooled `NpgsqlConnection` —
whether it came from EF's `DbContext`, from an `NpgsqlDataSource`, or from
`new NpgsqlConnection(cs)` against an ordinary connection string — hands the connector back to
the pool with the backend session, and therefore the advisory lock, **still alive**. Npgsql
defers its `DISCARD ALL` reset (which is what runs `pg_advisory_unlock_all()`) until that
connector is next USED. So any exit path that drops the connection without a *successful*
explicit `pg_advisory_unlock` parks the lock on an idle connector — for up to
`Connection Idle Lifetime` (300s by default), or **forever** for a `MinPoolSize` connector that
is never pruned. Whether the gate is open then depends on which connector the pool happens to
hand out next.

Two consequences that are easy to miss:

- **`Pooling=false` is what makes the documented invariant true.** It is load-bearing, not a
  tuning choice. The cost is one extra TCP connect per critical section.
- **A pooled *probe* hides the bug.** A test that reads `pg_locks` over a pooled connection can
  be handed the very connector that leaked the lock; Npgsql prepends the deferred `DISCARD ALL`
  to the probe's own query, so the probe silently repairs the state it was sent to measure and
  reports "released". This is why the original defect survived a green local suite and only
  showed up as a one-test CI flake. Every probe in the new tests is deliberately non-pooled.

## The four sites

All line numbers are pre-fix.

### 1. `KekRotationCoordinator` (`Tamma.Api/Services/Secrets/KekRotationCoordinator.cs:937`) — WORST

- **Connection**: `NpgsqlDataSource.OpenConnectionAsync(ct)` → **POOLED**. Story PF-C3 had
  already moved this lock *off* the EF pooled context onto "a dedicated `NpgsqlConnection`", but
  dedicated is not the same as non-pooled — the data source has its own pool, so the mirror-image
  failure applied.
- **Exit paths**: happy path, exception, and cancellation all reach the `finally`, and the unlock
  correctly used `CancellationToken.None`, so the release was attempted on every path. The hole
  was the `catch (Exception ex) { LogDebug(...) }` around it: a throwing unlock (a cluster blip, a
  half-dead connection) fell through to `DisposeAsync()`, whose comment said "the connection drop
  is the actual guarantee" — false for a pooled connector. Process death is safe (the OS closes
  the socket, the backend exits).
- **Blast radius**: `AdvisoryLockKey` is a **constant** — unlike the per-hour scheduler keys it
  never rotates. One swallowed unlock wedges KEK rotation shut for the whole cluster
  indefinitely, and every later `POST /api/admin/kek/rotate` fails with the operator-visible
  untruth *"another rotation is already in progress on this cluster"*. A security operation an
  operator may need urgently (suspected key compromise), blocked by a lock nobody holds. **Rank
  1.**
- **Changed**: acquisition goes through `PostgresAdvisoryLock.TryAcquireAsync(
  dataSource.ConnectionString, PostgresAdvisoryLockKey.FromInt64(AdvisoryLockKey), …)`. Same key,
  same `pg_try_advisory_lock(bigint)` call, same fail-closed `NpgsqlException` handling; only the
  session changed. The now-dead private `TryAcquireAdvisoryLockAsync` / `ReleaseAdvisoryLockAsync`
  helpers were deleted and the PF-C3 class-doc bullet corrected.

### 2. `TenantMoveService` (`Tamma.Api/Services/Provisioning/TenantMoveService.cs:875`)

- **Connection**: `ControlPlaneDbContext` from `IDbContextFactory`, force-opened via
  `db.Database.OpenConnectionAsync(ct)` → **POOLED**.
- **Exit paths**: `await using var moveLock` in `MoveAsync` disposes on normal completion,
  exception and cancellation alike, and `MoveAdvisoryLock.DisposeAsync` issued the unlock with no
  cancellation token (so cancellation did not defeat it). The hole was again the `catch` — which
  logged *"the lock dies with the session regardless"*, the exact false invariant — followed by
  disposing the pooled context.
- **Blast radius**: the key is per tenant and never rotates, so a single swallowed unlock parks
  **that tenant's** move gate shut indefinitely; every later move for it aborts with *"a move for
  tenant X is already in progress"*, which is untrue and gives an operator nothing to act on.
  Scoped to one tenant and to an admin-initiated, rare operation, so below the KEK gate. **Rank
  2.**
- **Changed**: `AcquireMoveLockAsync` now reads the connection string off the CP context, disposes
  the context immediately, and takes the lock through
  `PostgresAdvisoryLockKey.FromHashTextExtended(tenantId.ToString("D"))` — `hashtextextended` is
  still evaluated *by Postgres* over the same `"D"`-formatted id, so the key is bit-identical. The
  bespoke `MoveAdvisoryLock` class was deleted. Also stops the lease pinning a pooled EF connector
  for the whole duration of a dump/restore move.

### 3. `AuditChainCheckpointScheduler` (`Tamma.Api/Services/Audit/AuditChainCheckpointScheduler.cs:139`)

- **Connection**: the tick scope's `ControlPlaneDbContext` connection → **POOLED**.
- **Exit paths**: this had the only *guaranteed* miss path of the four. The unlock in the
  `finally` was passed the tick's **own `CancellationToken`**:

  ```csharp
  await unlock.ExecuteScalarAsync(ct).ConfigureAwait(false);
  }
  catch { /* closing the connection releases it anyway */ }
  ```

  On host shutdown `ct` is already cancelled when the `finally` runs, so the unlock throws before
  reaching the server, the bare `catch` swallows it, and `conn.CloseAsync()` returns the connector
  to the pool with the hour's lock still held. Not a rare race — it is what happens *every time* a
  pod is stopped mid-checkpoint.
- **Blast radius**: the key is per `(year, day_of_year, hour)`, so the damage self-heals at the
  top of the next hour. Within that hour every pod reads "another pod is the leader" and skips, so
  **no audit-chain checkpoints are written for that hour by anyone** — a gap in the tamper-evidence
  anchors of a compliance surface. Bounded, but the likeliest to actually occur. **Rank 3.**
- **Changed**: the lock is taken on its own non-pooled session via the helper (same key
  derivation, unchanged), the `opened`/`CloseAsync` dance is gone, and the guarded work runs
  inside `await using (lease)`. The lease's unlock always uses `CancellationToken.None`.

### 4. `PostgresAdvisoryLeaderLock` (`Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs:309`)

- **Connection**: `new NpgsqlConnection(cs)` against the raw `DefaultConnection` string →
  **POOLED** (Npgsql pools by default).
- **Note**: this class has **two** consumers, not one — `HourlyAnalyticsRollupScheduler` and
  `TenantScheduledTriggerService` both default to it. Fixing it fixes both.
- **Exit paths**: `await using var lease` in `TickAsync` covers completion, dispatch failure and
  cancellation, and `AdvisoryLockLease.DisposeAsync` used `ExecuteScalarAsync()` with no token, so
  cancellation did not defeat the unlock. Holes: (a) the `catch { }` around the unlock, whose
  comment was verbatim the false invariant — *"closing the connection releases the lock either
  way"*; and (b) a narrow window in `TryAcquireAsync`'s own `catch { await conn.DisposeAsync();
  throw; }`, where the backend may have granted the lock but the client never saw the reply.
- **Blast radius**: per-hour key, self-heals next hour. Within the hour every pod stands down and
  the rollup is dispatched by nobody; because the workflow infers its target hour from the clock,
  a skipped hour is **not** backfilled — one hour of `platform_analytics_hourly` is simply
  missing. Lowest severity of the four. **Rank 4.**
- **Changed**: the body is now a two-line delegation to `PostgresAdvisoryLock.TryAcquireAsync`;
  the nested `AdvisoryLockLease` is gone. `NoOpLease` (no connection string → single-pod → this
  pod is the leader) is unchanged, as is the null-means-refused / throw-means-fail-closed
  contract.

## The shared helper

Extracted `Tamma.Data.Pooling.PostgresAdvisoryLock` (+ `PostgresAdvisoryLockKey`,
`PostgresAdvisoryLockLease`) and migrated all four sites to it. The reason for extracting rather
than patching four `catch` blocks: four places had independently re-derived the same pattern and
all four got the same detail wrong, so the fix that stops the *next* author repeating it is one
type whose contract says why the session must not be pooled. The type doc spells out the deferred
`DISCARD ALL` mechanism and states explicitly that `Pooling=false` must not be "optimised" away.

Key handling deliberately keeps each site's exact SQL: `PostgresAdvisoryLockKey.FromInt64` emits
`pg_try_advisory_lock(@k)`, `FromHashTextExtended` emits
`pg_try_advisory_lock(hashtextextended(@t, 0))`. Re-deriving the move key client-side would have
produced a different number, i.e. a different lock that excludes nobody.

**`TenantMigrationSweepRunner` was deliberately NOT migrated.** Its lease additionally carries an
`Interlocked` ownership handoff between `ReleaseAsync` and `Dispose`, and a watchdog that
re-verifies the lock mid-sweep. Those are sweep-specific and the runner is already correct and
verified; reworking it to fit the helper would have risked the one site that is known good. The
helper's doc points at it. If the two ever need to converge, the helper already exposes
`Lease.Session` for liveness re-verification.

## A second trap found while fixing this: the password-stripped connection string

Moving a lock onto its own session means **re-opening a connection from a connection
string** — and the obvious sources for that string silently lose the password. Npgsql defaults
`PersistSecurityInfo` to false, so:

- **`NpgsqlDataSource.ConnectionString` never carries the password.** Verified directly:
  `NpgsqlDataSource.Create("…;Password=SUPERSECRET").ConnectionString` returns
  `Host=…;Port=…;Database=…;Username=…` — no password.
- **EF's `Database.GetConnectionString()` carries it in most shapes, but NOT reliably.** In a
  container where an `NpgsqlDataSource` is registered in DI *and* the context's connection has
  been materialised, it comes back stripped. (A five-shape probe with no data source in play
  returned the full string every time, which is exactly what makes this trap so easy to miss:
  the naive test passes.)

The consequence is severe and silent. A stripped string produces an `NpgsqlException` on open;
`KekRotationCoordinator` — correctly — treats a failed lock attempt as fail-closed; so *every*
rotation aborts with the operator-visible untruth "another rotation is already in progress on
this cluster". The first cut of the KEK fix did exactly this, and the **full unfiltered
`Tamma.Api.Tests` run is what caught it** (it broke a pre-existing test,
`RetryAsync_Does_Not_Mutate_KekProvider_Before_Lock_Acquisition`, which needs a rotation to
actually run). A filtered run would have hidden it. This trap is already recorded in the repo for
a different subsystem — see the Story 44-1 note on the migrate-all sweep in `Program.cs`.

**How each site sources its string, and why that is safe:**

| Site | Source | Safe because |
|---|---|---|
| `KekRotationCoordinator` | `ConnectionStringResolver.ResolveControlPlane(IConfiguration)` → `ResolveAdmin` → EF, each candidate **rejected unless it still has a password** (`HasCredentials`); otherwise fail closed | Configuration is the string the host itself resolved and the one the singleton data source was built from, so same database, credentials intact |
| `PostgresAdvisoryLeaderLock` | `IConfiguration.GetConnectionString("DefaultConnection")` | Raw configuration, never laundered |
| `AuditChainCheckpointScheduler`, `TenantMoveService` | EF `Database.GetConnectionString()` | Verified working against a real cluster by their pinning tests, and by the full suite |

**Open risk (not fixed, needs a decision):** `AuditChainCheckpointScheduler`, `TenantMoveService`
**and the already-shipped `TenantMigrationSweepRunner` (`b958adc`)** all take the EF route. Their
tests pass, but none of those test containers reproduces the exact production shape (a registered
`NpgsqlDataSource` **plus** a materialised control-plane connection) under which EF was observed
to hand back a stripped string. If production does hit that shape, those three locks would throw
on open rather than park — i.e. they fail closed and loudly (a sweep/move/checkpoint that refuses
to start), not silently. That is the safe direction, but it is still a latent outage. The
durable fix is to give all four sites the same
configuration-first-with-credential-check resolution the KEK site now uses, ideally hoisted into
`PostgresAdvisoryLock` itself so no caller can get it wrong. Deliberately not done in this pass:
it would mean touching `TenantMigrationSweepRunner`, the one site already verified green, on the
strength of a hazard not yet observed in production.

## Open / not fixed

1. **The reverse hazard is unaddressed at two sites.** `b958adc`'s Finding 1.1 fixed the *other*
   direction for the sweep runner: a connection can die without the pod dying (pooler drop, idle
   timeout, `pg_terminate_backend`), which releases the lock while the guarded work carries on
   believing it is alone. Neither `TenantMoveService` (a move holds the gate across `pg_dump` /
   `pg_restore`, minutes of wall clock with the lock session idle) nor `KekRotationCoordinator`
   (re-encrypts every tenant row under the lock) re-verifies that it still holds the lock. Adding
   a watchdog changes what the guard *guarantees*, not just how it is held, so it was left out of
   this change. `PostgresAdvisoryLockLease.Session` is exposed so the check can be added without
   further surgery. The two schedulers do not need it (their critical sections are short).
2. **Site 4's pinning test is structural, not behavioural.** `PostgresAdvisoryLeaderLock` is
   `internal` to `Tamma.ElsaServer`, visible only to `Tamma.Activities.Tests`, which has no
   Testcontainers infrastructure; `Tamma.Api.Tests`, which does, has no project reference to
   `Tamma.ElsaServer`. Adding a Postgres container to `Tamma.Activities.Tests` would make every
   run of that suite require Docker and change the repo's test topology; adding an ElsaServer
   project reference to `Tamma.Api.Tests` would pull the Elsa host into a 5,400-test
   `WebApplicationFactory` suite. Neither was worth it for one site. Instead the behavioural proof
   comes from `PostgresAdvisoryLockTests` (real cluster, exercising the exact helper call the
   leader lock now makes), and `PostgresAdvisoryLeaderLockSessionTests` pins by reflection that
   the class holds no `DbConnection` of its own and declares no lease but `NoOpLease` — which is
   precisely what regressed. Both reflection assertions fail against the pre-fix code, naming
   `AdvisoryLockLease._conn`.
3. **No audit of non-advisory session state.** This pass looked only at
   `pg_try_advisory_lock`/`pg_advisory_unlock`. Anything else that sets session-scoped state on a
   pooled connector (`SET LOCAL` is fine; plain `SET`, `LISTEN`, temp tables, prepared statements,
   advisory locks taken inside raw SQL activities) has the same deferred-reset property and was
   not surveyed.

## Tests added

Behavioural, all observing `pg_locks` / `pg_stat_activity` through a deliberately non-pooled
`AdvisoryLockProbe`:

- `PostgresAdvisoryLockTests` (7) — the helper's contract, including
  `A_lease_whose_unlock_never_runs_still_releases_the_lock` and
  `FromHashTextExtended_keys_on_the_databases_own_hash_not_a_client_side_one`.
- `AuditChainCheckpointSchedulerLockTests.Host_shutdown_mid_tick_does_not_park_the_hours_leader_lock_shut`
  — reproduces the cancelled-token miss path directly.
- `AuditChainCheckpointSchedulerLockTests.The_leader_lock_rides_a_session_that_dies_with_the_tick`
- `KekRotationLockSessionTests.The_rotation_lock_rides_a_session_that_dies_with_the_rotation`
- `TenantMoveServiceConcurrencyTests.Move_lock_rides_a_session_that_dies_with_the_move_not_a_pooled_connector`

The recurring shape — *capture the lock-holder's backend pid while the lock is held, then assert
that backend is GONE once the critical section ends* — is the general test for this class of bug.
A surviving backend is a session that can still be holding the lock on any path where the unlock
did not run, which is exactly what a pooled connector gives you.

Structural (no database, `Tamma.Activities.Tests`):

- `PostgresAdvisoryLeaderLockSessionTests` (4) — see "Open / not fixed" item 2 for why this site
  is pinned structurally rather than behaviourally.

All discriminating tests were confirmed to fail against the pre-fix code by running them in a
`git worktree` at `b958adc` with only the new test files and the helper copied in: the four
behavioural ones failed on their own assertions (the audit one reproducing the defect exactly —
"after host shutdown the hour's lock is *still held*"), and the two reflection pins failed
naming `AdvisoryLockLease._conn`.

## Note on running these tests

Multi-fixture *filtered* runs of `Tamma.Api.Tests` (e.g. `--filter …~Secrets`) start several
Testcontainers Postgres instances concurrently and produce spurious failures — a container took
14.6s to become ready in one observed run. Those same tests pass individually and in the full
suite. **Trust the full unfiltered run**; a filtered one is not a reliable signal here, in either
direction. It was the full run that caught the password-stripping regression above.
