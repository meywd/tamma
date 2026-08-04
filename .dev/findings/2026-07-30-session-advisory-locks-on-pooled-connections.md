# Finding: four more session-scoped advisory locks were held on POOLED connections — the gate can park itself SHUT

**Date**: 2026-07-30
**Context**: follow-up audit ordered by commit `b958adc` ("Fix the sweep gate parking itself
shut: an advisory lock on a pooled connection"), whose commit message recorded four other sites
that take a session-scoped `pg_try_advisory_lock` the same way and asked for them to be audited.
**Verdict**: all four were exposed. All four fixed, via a shared helper
(`Tamma.Data.Pooling.PostgresAdvisoryLock`) extracted from the sweep runner's now-correct
pattern. Two reverse-direction hazards (lock dies while the guarded work continues) are recorded
here as open, not fixed.

> **UPDATE 2026-07-30 (later the same day) — both open items are now CLOSED.** The reverse hazard
> has a shared, opt-in liveness watchdog on the helper, enabled at the two long-held gates
> (`TenantMoveService`, `KekRotationCoordinator`); `TenantMigrationSweepRunner` was migrated onto
> it and its bespoke copy deleted. All four sites now resolve their lock-session connection string
> through one seam on `PostgresAdvisoryLock`. See **"Closing the two open items"** at the end of
> this document, which also records **why the EF password-stripping shape could not be reproduced
> in a container** — the reproducible cause is a container that registers an `NpgsqlDataSource`
> alongside the context. *(This line originally claimed a process-wide EF Core cache property; that
> stronger claim is now marked unreproduced — see F4 in "Adversarial review of the watchdog pass".)*

> **UPDATE 2026-07-31 — an adversarial review of the watchdog commit (`3b738cc`) found seven
> issues, two of them corrections to claims in THIS document.** Nothing unsafe, but the move's
> lock-loss diagnosis was unreachable in production and the test that "proved" it was measuring a
> fiction. See **"Adversarial review of the watchdog pass"** at the end.

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

**~~Open risk (not fixed, needs a decision)~~ CLOSED — see "Closing the two open items":**
`AuditChainCheckpointScheduler`, `TenantMoveService`
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

*(Items 1 and 2 below were the state at the end of the first pass. Both are now closed — see
"Closing the two open items". Item 3 remains open. The original text is kept because it is the
statement of the problem the follow-up solved.)*

1. **~~The reverse hazard is unaddressed at two sites.~~ CLOSED.** `b958adc`'s Finding 1.1 fixed the *other*
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

---

# Closing the two open items (2026-07-30, follow-up pass)

## Item 1 — the reverse hazard: a lock can die while the work it guards continues

### What shipped

`PostgresAdvisoryLockLease.WatchLiveness(interval, callerToken, logger, site)` returns a
`PostgresAdvisoryLockWatchdog` that:

- re-reads `pg_locks` **from the lease session itself**, pinned to `pid = pg_backend_pid()`,
  qualified by database, `objsubid = 1` and `mode = 'ExclusiveLock'`, with the 64-bit key
  reassembled using `|`;
  > **Corrected 2026-07-31 (F3).** This bullet originally said "`|` (not `+`) so `hashtextextended`
  > keys — frequently negative — round-trip". **That rationale is false.** `|` and `+` are exactly
  > equivalent here: the high term is always a multiple of 2^32 and the low term always in
  > `[0, 2^32)`, so the bits never overlap and int8 never overflows. Verified on `postgres:17-alpine`
  > over 500k random `(classid, objid)` pairs plus every boundary combination — 0 differences,
  > negatives included (the reviewer's independent run over 2,000,000 pairs plus `-1`,
  > `long.MinValue`, `long.MaxValue`, `0xFFFFFFFF80000000`, `0x0000000080000000`,
  > `0x8000000000000001` agreed). The choice is **stylistic**. It is now `|` at all four sites
  > (`PostgresAdvisoryLockKey.HeldByThisBackendSql`, `TenantMigrationSweepRunner`'s cross-pod probe,
  > `AdvisoryLockProbe`, and the sweep tests' own probes) purely so they read the same — the tree
  > used to be split `|`/`+`, which invites a future reader to "fix" a working site or hunt a bug
  > that is not there. `mode = 'ExclusiveLock'` was added in the same pass (F6).
- treats **any** failed probe as loss, because the dominant reason a command on the lease
  connection throws is that the backend is gone;
- exposes `Token` (a `CancellationTokenSource` linked to the caller's) which it cancels on loss,
  and `LockLost` so a caller can tell "we lost exclusivity" from "the host stopped us" — two very
  different stories to put in front of an operator, and two different recovery states;
- on `DisposeAsync` stops the heartbeat and does **not** cancel `Token`, so a critical section
  that finished normally cannot be retro-cancelled by its own cleanup.

Ordering contract, documented on the method and honoured at every site: **dispose the watchdog
before the lease.** Both ride the same single session; a probe racing the release would either
fault on a concurrently-executing command or report a spurious loss. `await using` in declaration
order (lease first, watchdog second) gets this right.

### Enabled where, and the interval, per site

| Site | Watchdog | Interval | Why |
|---|---|---|---|
| `KekRotationCoordinator` | **yes** | **5s** | Holds the gate across a full fleet re-encrypt. Its unit of work is a single row — milliseconds — so a long interval buys a second pod a great many rows. And two concurrent re-encrypts are not "duplicate work": pod A rewrites a row under key A and bumps its version, pod B rewrites it under key B, and the surviving envelope is readable only by whichever key is eventually promoted. Unreadable tenant secrets, not recoverable by re-running. Tightest interval of the three because the consequence is the worst and the work unit the smallest. A half-hour rotation pays 360 trivial reads. |
| `TenantMoveService` | **yes** | **10s** | Holds the gate across `pg_dump` + `pg_restore` — minutes with the lock session completely idle, which is exactly the profile an idle timeout / pooler recycle / admin `pg_terminate_backend` kills. The bound that matters is how long the move can keep issuing destructive steps (DROP SCHEMA CASCADE on target, the re-point commit, the source drop) after a second mover is admitted; those are seconds apart, so 10s keeps the unguarded window to about one of them. A half-hour move pays 180 reads. |
| `TenantMigrationSweepRunner` | **yes** (migrated) | **15s**, unchanged | Existing judgement kept verbatim: one tenant's migration is seconds to minutes, so 15s keeps the unguarded window under roughly one tenant's worth of fleet DDL. Only the implementation moved. |
| `AuditChainCheckpointScheduler` | **no** | — | A tick writes one signed checkpoint per active scope and is over in seconds, with the connection actively in use throughout — the window is too small for the hazard. And the cost if it happened is one duplicate append-only checkpoint row for an hour, not data lost. Documented on the class so the omission reads as a decision. |
| `PostgresAdvisoryLeaderLock` (analytics rollup + scheduled triggers) | **no** | — | Same shape: elect a leader, dispatch a workflow, release. Short bounded critical section, per-hour self-healing key, and a rare double-dispatch is duplicated work rather than corruption. |

### The sweep runner WAS migrated onto the shared watchdog

The first pass deliberately left it alone: it was the one site already known good, and its lease
carried extra machinery (an `Interlocked` ownership handoff between `ReleaseAsync` and `Dispose`,
plus the watchdog). Migrating it now, because:

- once the helper grew a watchdog, the runner's copy was **a literal duplicate of shared code**,
  and "two implementations of one idea" is the mechanism that put this bug in four places to begin
  with. Leaving it means the next fix has to be found and applied twice.
- the "extra machinery" turned out to already exist in the helper or to be one line: the ownership
  handoff is `PostgresAdvisoryLockLease.DisposeAsync`'s own `Interlocked.Exchange`, and the
  synchronous `Dispose` path needed only `DisposeSession()` — a named method rather than an
  `IDisposable` implementation, deliberately, so a `using` written where `await using` was meant
  cannot silently pick the weaker path.
- the risk is bounded by the best-covered fixture in this area (19 tests, including the
  kill-the-backend abort pin and the pooled-connector pin), plus the full unfiltered suite.

What stayed in the runner: the process-local slot, partial-result reporting, and the **cross-pod**
`pg_locks` probe behind `IsSweepRunningAsync` — that one asks "does ANY pod hold the key", which
is a different question from the lease's "do *I* still hold it" and correctly lives elsewhere.

### Behavioural consequences at each site

- **KEK**: on loss the rotation aborts, the status reads *"the cluster-wide rotation lock was LOST
  mid-rotation … re-run /api/admin/kek/retry"*, and the `kek_rotations` row is persisted as
  **`failed` with the staged secondary KEPT** (not `cancelled`, which zeroes it) — because a
  lock-loss abort is a *partial* rotation and must stay resumable. The coordinator returns instead
  of rethrowing the `OperationCanceledException`, so a lost lock is never reported as an orderly
  shutdown.
- **Move**: `MoveAsync` is now a thin wrapper (lock → watchdog → `MoveCoreAsync`) that translates
  a watchdog cancellation into an `InvalidOperationException` naming the lost gate, what it means,
  and that a re-run resumes idempotently. A bare `OperationCanceledException` would have sent an
  operator looking for a shutdown that did not happen.
- **Sweep**: unchanged wire behaviour — `Failed` + partial result + the "lock was lost" error.

## Item 2 — one seam for the lock session's connection string

All four sites now call `PostgresAdvisoryLock.TryResolveSessionConnectionString(configuration,
efContext, logger, site)` (or `ResolveSessionConnectionString`, which throws instead of returning
null, for the three whose fail-closed path is an exception). `KekRotationCoordinator`'s private
copy was deleted and now delegates. The policy, in one place:

1. **configuration, if it still carries a password** — `ControlPlane` → `TammaDb` →
   `DefaultConnection`, the same order and the same keys `Tamma.Api`'s own `ConnectionStringResolver`
   uses, pinned by a test that asserts both against each other. Raw configuration is the only
   source Npgsql never launders.
2. **EF's `GetConnectionString()`, if it still carries a password** — correct in most shapes, and
   the only thing a fixture with no configuration has.
3. **configuration verbatim, even without a password** — a trust-auth / integrated-security
   deployment legitimately has none, and configuration is *raw*, so a missing password THERE is
   the deployment's own, not a stripped one. This tier is a deliberate widening of the KEK site's
   original credentials-only rule: without it, extending that rule to the audit scheduler would
   have cost trust-auth deployments their checkpoints outright.
4. otherwise **null / throw** — a password-less string that only EF produced is indistinguishable
   from a laundered one, and it is exactly the shape observed in production. The exception names
   the laundering mechanism and the config key to set, because "connection string missing" sends
   an operator to the wrong place.

Wiring: `TenantMigrationSweepRunner` and `TenantMoveService` gained an **optional trailing
`IConfiguration?` constructor parameter** (DI fills it in the host; fixtures that construct them
directly keep the EF route and are unaffected). `AuditChainCheckpointScheduler` resolves
`IConfiguration` from its per-tick scope. `KekRotationCoordinator` already did.

### Why no container reproduced the stripping — the mechanism, stated at the strength it is proven

This was the loose end from the first pass ("no test container reproduces the production shape").

**What reproduces, deterministically:**

> **A `ControlPlaneDbContext` resolved from a DI container that also has an `NpgsqlDataSource`
> registered comes back from `Database.GetConnectionString()` with the password STRIPPED.** Npgsql's
> EF provider mints the context's `DbConnection` from the data source, and
> `NpgsqlDataSource.ConnectionString` never carries the password (`PersistSecurityInfo` defaults to
> false). This is the production shape (`Program.cs` registers a singleton CP data source next to
> the EF factory), it is pinned by `PostgresAdvisoryLockConnectionStringTests`, and it is the whole
> reason the resolution seam prefers raw configuration.

**What is NOT proven — read this before citing the strong form.** An earlier version of this
section claimed something much larger: that the behaviour is decided by EF Core's *process-wide*
internal service-provider cache, first-context-wins, so that *"build a data-source-bearing context
first and every later context loses its password — including ones whose own container has no data
source"*, and that `AuditChainCheckpointSchedulerLockTests` therefore flip-flops depending on what
ran before it.

> **Marked UNREPRODUCED as of 2026-07-31 (F4).** A reviewer probing in one process observed: bare
> context before → `HasCredentials=True`; DI context with a data source → `False` (deterministic,
> as above); bare context **after** → **`True`**. The poisoning did **not** propagate to a later
> context whose own container has no data source. And `AuditChainCheckpointSchedulerLockTests`
> passed 5/5 run alone — the very fixture named above as flip-flopping.
>
> That is **one ordering, not a proof of absence**: the strong form is not disproven, and EF Core
> genuinely does cache internal service providers per options shape, so a shape-collision route to
> it remains plausible. It is downgraded from "established mechanism" to "hypothesis that has not
> reproduced", and nothing should be justified by it alone.

Consequences, restated against what actually holds:

- **The audit's inability to reproduce it was not a wiring mistake.** The deterministic form needs a
  data source registered *in the same container* as the context — which the first pass's fixtures
  did not have. That is sufficient to explain the miss; the process-wide story is not needed.
- **No test may assert on `HasCredentials(ef.GetConnectionString())`** — but for the narrow reason:
  it is a property of *how the fixture's container is wired*, easy to get subtly wrong, and it
  couples a lock test to Npgsql-provider internals that are not the thing under test. (If the strong
  form is ever confirmed, add "and it depends on test ordering" here.) The pins therefore *simulate*
  the laundering deterministically: a context bound to an explicitly password-less connection string
  is indistinguishable, at the seam, from one Npgsql stripped. That is what every pinning test does,
  and it is the right shape either way.
- **Production can still change under you.** A deployment that today gets a credentialed EF string
  starts getting a stripped one the moment an `NpgsqlDataSource` is registered alongside the context
  — an unrelated feature's wiring change. That is why the durable fix is "prefer configuration", not
  "the EF route works for us". This consequence survives the correction intact; it never needed the
  process-wide claim.

## Tests added in this pass

Behavioural, all observing through the non-pooled `AdvisoryLockProbe` (which gained
`TerminateHolderAsync` — the reverse hazard's probe: it kills the lock holder from a separate
session while the guarded work keeps running):

- `PostgresAdvisoryLockTests` (+5): `A_watchdog_cancels_its_token_when_the_lock_holding_backend_is_killed`,
  `A_watchdog_does_not_cry_wolf_while_the_lock_is_genuinely_held`,
  `A_watchdog_over_a_hashtextextended_key_watches_that_exact_key`,
  `A_watchdogs_token_is_cancelled_by_the_callers_token_too`,
  `Disposing_a_watchdog_does_not_cancel_the_work_it_was_watching`.
- `KekRotationLockSessionTests.A_rotation_whose_lock_holding_backend_dies_aborts_instead_of_re_encrypting_on_unguarded`
  — kills the gate's backend from inside the per-tenant loop and requires the rotation to stop
  *where it stood* (one eviction, not two), with the lock-lost reason and a resumable `failed` row.
- `TenantMoveServiceConcurrencyTests.A_move_whose_lock_holding_backend_dies_mid_dump_aborts_instead_of_restoring_on_unguarded`
  — kills the gate while the move is parked in `pg_dump`; requires a prompt named abort and that
  `pg_restore` never ran.
- `PostgresAdvisoryLockConnectionStringTests` (9, new file) — the seam: the data-source string is
  always password-less; configuration wins over a laundered EF string; the resolved string opens a
  real authenticated session *in the right database*; EF is used when there is no configuration;
  a password-less EF-only string is refused with a message naming the trap; a trust-auth
  deployment keeps its lock; and the configuration key order is pinned against
  `ConnectionStringResolver`.
- Per-site sourcing pins: `TenantMigrationSweepRunnerTests` (+2),
  `AuditChainCheckpointSchedulerLockTests` (+2), `TenantMoveServiceConcurrencyTests` (+2) — each
  one "the gate still opens when EF's string has no password" plus "no usable connection string
  fails closed and names the trap".

`AuditChainCheckpointScheduler` gained an `internal TickForTestAsync` seam, because
`ExecuteAsync`'s loop deliberately swallows a failing tick and the fail-closed path is otherwise
unobservable.

### Confirmed discriminating against the pre-fix code

Run in a `git worktree` at `6df34fc` with adapted copies (the new production APIs do not exist
there, so the assertions were reduced to the observable behaviour):

| Pre-fix behaviour observed | Fixed-code expectation |
|---|---|
| KEK: `phase=Completed`, `evictions=2` after the gate's backend was killed — the rotation re-encrypted the *next* tenant with the cluster gate open, and took the full 31s unguarded delay to do it | aborts at eviction 1 with "rotation lock was LOST" |
| Move: sat in `pg_dump` for the full 60s after the gate died, then `TimeoutException` | prompt `InvalidOperationException` "… LOST mid-move …" *(the pin that produced this row was later found to be measuring a fiction — see F1 in the 2026-07-31 section)* |
| Sweep, password-less EF string: `NpgsqlException: No password has been provided but the backend requires one` out of `StartAsync` | start accepted (configuration route) / `InvalidOperationException` naming the trap |
| Audit, password-less EF string: `leaderLockHeld=False checkpointCalls=0` — the hour gets no checkpoints from anyone | leader elected, checkpoints written |
| Move, password-less EF string + externally-held gate: `NpgsqlException: No password has been provided …` | stands down with "already in progress" (i.e. the real gate was evaluated) |

## Still open

Item 3 of the original list is unchanged and still open: **no audit of non-advisory session
state.** This pass and the previous one looked only at `pg_try_advisory_lock` /
`pg_advisory_unlock`. Anything else that sets session-scoped state on a pooled connector (`SET
LOCAL` is fine; plain `SET`, `LISTEN`, temp tables, prepared statements, advisory locks taken
inside raw SQL activities) has the same deferred-reset property and has not been surveyed.

Two smaller things worth naming rather than leaving implicit:

- **`PostgresAdvisoryLeaderLock`'s pin is still structural**, for the reasons in item 2 of the
  original list (it is `internal` to `Tamma.ElsaServer`, visible only to a suite with no
  Testcontainers). Unchanged by this pass; it takes no watchdog and its connection string comes
  from raw `IConfiguration`, so neither closed item applies to it.
- **The watchdog is a heartbeat, not a fence.** It bounds how long work continues after losing
  exclusivity; it cannot make the window zero. A gate whose critical section genuinely must never
  overlap needs a fenced token (a control-plane lease row with a monotonically increasing epoch
  checked at every write), which is a much larger change and is not warranted by anything observed
  here.

---

# Adversarial review of the watchdog pass (2026-07-31)

A review of `3b738cc` found seven things wrong with the *follow-up* pass above — none of them a
safety hole (every gate still fails closed, every abort still aborts before the destructive step),
all of them either a broken DIAGNOSIS, a test that measured a fiction, or a claim in this document
stated more strongly than the evidence supports. Two of the seven are corrections to things this
document asserted; those are corrected in place above as well as recorded here.

## F1 (MEDIUM) — the move's lock-loss diagnosis was unreachable in production

`MoveAsync` filtered on `catch (OperationCanceledException) when (moveGuard?.LockLost == true)`.
But the minutes-long idle window a lost gate has to be detected in is spent inside
`RunPgToolAsync` → `IProcessRunner.RunAsync(…, ct)`, and production's `DefaultProcessRunner`
**catches that `OperationCanceledException`**, kills the process tree, and returns
`ProcessRunResult(ExitCode: -1, TimedOut: !ct.IsCancellationRequested)` — and under a watchdog
abort `ct` *is* cancelled, so `TimedOut` is **false**. `RunPgToolAsync` therefore threw
`InvalidOperationException("pg_dump failed (exit -1) …")`, which the filter could never see. The
operator got exactly the bare, misleading error the wrapper existed to eliminate. Restore had the
same shape.

**The test that "proved" it passed only because the double was unrealistic.** `GatedRunner` let the
`OperationCanceledException` escape — a shape `DefaultProcessRunner` cannot produce. A pin that
only passes against an unrealistic double is worse than no pin: it converts an untested path into a
tested-looking one.

Fixed on both sides:

- `RunPgToolAsync` calls `ct.ThrowIfCancellationRequested()` **before interpreting the result** —
  the runner swallows cancellation, so the caller re-raises it. One line covers dump and restore
  alike (restore never reaches `EnsureRestoreSucceeded` with a bogus result).
- `MoveAsync`'s filter is now `catch (Exception ex) when (moveGuard?.LockLost == true)`, keeping the
  original as `InnerException`. Filtering on the exception TYPE made the diagnosis depend on which
  seam happened to observe the cancellation; filtering on `LockLost` — the fact that decides the
  story — does not.
- `GatedRunner` now mirrors `DefaultProcessRunner`: it swallows the OCE and returns
  `ExitCode: -1, TimedOut: false` (and `TimedOut: true` for its own hard timeout).

**Confirmed discriminating by execution**: with the double fixed and the production fix reverted,
`A_move_whose_lock_holding_backend_dies_mid_dump_aborts_instead_of_restoring_on_unguarded` fails
with `InvalidOperationException :: pg_dump failed (exit -1) during tenant move step 'dump'`. The
test also now asserts the message does **not** contain `exit -1`.

> Deliberately NOT changed: `DefaultProcessRunner` itself. Its swallow-and-report contract is
> depended on by `LocalExecutor` and every other caller, and it is outside this lane. The fix
> belongs at the seam that has a cancellation policy — the move.

## F2 (LOW-MEDIUM) — a clean pod shutdown was reported as a lost cluster lock

`TenantMigrationSweepRunner.Dispose()` violated the ordering contract documented on
`WatchLiveness`: it ended the lease session (`lease?.TakeLock()?.DisposeSession()`) while the run's
watchdog was still probing that same session, with the watchdog disposed later on `ExecuteAsync`'s
thread. The in-flight probe then found a closed session and reported LOSS — the right reading
everywhere else — so `ExecuteAsync`'s catch told the operator *"The cluster-wide sweep lock was
lost mid-run (a pooler/proxy drop, an idle timeout, or a terminated backend)"* about a pod that was
simply asked to stop, and marked the result partial. `_shutdown.Cancel()` mitigated it but left a
window between the loop's cancellation check and the probe's own read of the session.

The existing pin forbade only the strings `"ObjectDisposed"`/`"disposed"`, so it passed straight
through this.

Fixed:

- `PostgresAdvisoryLockWatchdog.StopHeartbeat(timeout)` — the synchronous half of the ordering
  contract, counterpart to `PostgresAdvisoryLockLease.DisposeSession()`. Before it existed an
  `IDisposable` host had **no way** to honour the contract at all. It cancels the loop, waits
  (bounded) for the heartbeat to exit, cancels nothing else, and disposes nothing, so a later
  `DisposeAsync` is unaffected.
- `RunLease` gained a watchdog slot published under the same lock as the lease slot
  (`TryPublishWatchdog` / `TakeWatchdog`), so `Dispose` can reach it; `Dispose` stops the heartbeat
  before ending the session.
- `PostgresAdvisoryLockWatchdog.RunAsync` re-checks `token.IsCancellationRequested` **after** the
  probe as well as before it. That closes the residual window generically: a stopping host cancels
  first and ends the session second, so a "no" that arrives with the token already cancelled is a
  shutdown, not a loss.

Pins: `PostgresAdvisoryLockTests
.A_synchronous_host_that_stops_the_heartbeat_first_is_not_reported_as_lock_loss` pins **both**
halves deterministically — the violating order (end the session, leave the heartbeat running) sets
`LockLost`, the contract order does not — and
`TenantMigrationSweepRunnerTests.Disposing_mid_run_is_not_reported_to_the_operator_as_a_lost_cluster_lock`
pins the operator-visible consequence at the site. The site-level one is honest about being
probabilistic against the old code (the race is narrow); the deterministic proof is the
primitive-level one. The other two Dispose paths honoured the contract already and were not
touched.

## F3 (LOW) — the `|`-not-`+` rationale was false, and repeated in the commit message

See the corrected bullet under "What shipped" above. `|` and `+` are exactly equivalent; the tree
was split between them; it is now uniformly `|` with the rationale rewritten to say what is true
(stylistic) at all four sites plus this document. Nothing about behaviour changed.

## F4 (LOW) — the EF-cache claim was stated far past its evidence

See "Why no container reproduced the stripping" above, rewritten. The deterministic narrow form
(*a context resolved from a container that also registers an `NpgsqlDataSource` loses its
password*) is kept and is what everything relies on; the process-wide first-context-wins form is
marked **unreproduced**, not false — one contrary ordering is not a proof of absence. The practical
guidance ("no test may assert on EF's string") is kept, re-attached to the real reason.

## F5 (LOW) — a malformed connection string escaped the seam; at the KEK site that was fail-STUCK

`HasCredentials("Host=h;Bogus=1")` answered false because the string does not PARSE, so the
resolution seam's third tier (*configuration verbatim, for trust-auth deployments*) handed the
malformed string back verbatim; `ToUnpooledConnectionString` then threw `ArgumentException` from a
different stack frame. `KekRotationCoordinator`'s acquisition caught only
`OperationCanceledException` and `NpgsqlException`, so it escaped `RunRotationAsync` **ahead of the
try/finally that owns the status**: phase pinned at `Running` forever, `_activeRotationId` never
cleared, the `Task.Run` faulted unobserved, `/status` reporting a rotation that could neither
finish nor be retried. Fail closed and fail stuck are not the same thing.

Fixed:

- `HasCredentials` is now a **total** predicate (`ArgumentException`, `FormatException`,
  `OverflowException`, `InvalidCastException`, `NotSupportedException` — `Port=abc` and
  `Port=99999999999` used to escape it).
- Tier 3 refuses a string Npgsql cannot parse, logging at ERROR, and
  `ResolveSessionConnectionString` throws the new
  **`AdvisoryLockConnectionStringException`** with a distinct MALFORMED message. It derives from
  `InvalidOperationException`, so every existing catch and every existing pin still matches.
- The KEK acquisition catch is total (`catch (Exception)`, still after the OCE catch). Every way of
  failing to take the gate now ends as `Failed` **with a reason that says the gate could not be
  EVALUATED** — the old code reported a configuration fault as *"another rotation is already in
  progress on this cluster"*, which is the exact operator-visible untruth this whole audit exists to
  remove. `_activeRotationId` is cleared on that path.
- `ResolveLockConnectionString` now passes the coordinator's logger to the seam (it passed `null`),
  which is where the malformed-vs-laundered distinction is actually made.

## F6 / F7 (INFO)

- **`mode`**: `HeldByThisBackendSql` did not filter `pg_locks.mode`, so a same-backend SHARE-mode
  advisory lock would satisfy a predicate whose job is "am I still excluding people?". Added
  `AND mode = 'ExclusiveLock'` there, in the sweep runner's cross-pod probe, and in the test probes.
  The `pg_advisory_xact_lock` axis named in the review **cannot** be filtered: `pg_locks` has no
  column distinguishing a session-scoped advisory lock from a transaction-scoped one (verified on
  `postgres:17` — both are `advisory` / `ExclusiveLock` / `objsubid=1`). The doc comment now says
  "qualified on every axis `pg_locks` exposes" and names that one as out of reach, instead of
  claiming to be fully qualified.
- **The move's pre-fix symptom**: `TenantMoveService`'s comment claimed the password-stripped string
  made *"every move abort with the untrue 'already in progress'"*. It did not — a failed OPEN
  propagates, so the real symptom was `NpgsqlException("No password has been provided but the
  backend requires one …")`, which the table in this document already recorded correctly. Comment
  fixed.

## Coverage added

- `KekRotationLockSessionTests.A_genuine_cancellation_under_an_armed_watchdog_is_reported_as_cancelled_not_as_loss`
  — the other side of the branch that reads `LockLost`. Only the loss side was pinned, so nothing
  stopped a "simplification" making every cancellation report a lost cluster gate. Asserts the
  reason is exactly `"rotation cancelled"`, the row is `cancelled` with the staged key **zeroed**
  (vs `failed` + kept), and that the rotation **rethrew** rather than returning.
- `KekRotationLockSessionTests.Retry_after_a_lock_loss_abort_finishes_the_rotation_from_where_it_stopped`
  — the abort's promise to the operator ("re-run `/api/admin/kek/retry`"), end to end: gate killed
  mid-fleet, no promotion, `/retry`, fleet completes, every tenant on the new key, row `completed`
  with the staged key zeroed. The resumability depends on three decisions agreeing and none of them
  was covered together.
- `KekRotationLockSessionTests.A_rotation_whose_gate_cannot_be_evaluated_fails_closed_instead_of_wedging`
  — F5's fail-STUCK pin.
- `PostgresAdvisoryLockConnectionStringTests` (+3) — `HasCredentials` totality, the malformed
  refusal and its named type.

## Decided, not fixed: the audit scheduler's unresolvable-connection-string path

`AuditChainCheckpointScheduler`'s loop is WARN-and-continue for every failing tick. An unresolvable
(or malformed) lock connection string is not like the other failures: it fails **identically every
tick, forever**, and the consequence is that the deployment writes no tamper-evidence anchors at
all while looking healthy.

**Chosen: escalate the level, keep continuing.** The loop now catches
`AdvisoryLockConnectionStringException` separately and logs at **ERROR** with a message that says
what is *not happening* ("NO audit-chain checkpoints are being written by this deployment … this
will not fix itself") rather than what threw. It does **not** crash the host: the checkpoint loop is
opt-in (`RunOnStartup`) and taking a whole API down over a background-scheduler misconfiguration
trades a compliance gap for an outage. The escalation is the alert; the fix is a config change and a
restart. Branching on the exception TYPE rather than sniffing the message is why
`AdvisoryLockConnectionStringException` exists.

Rejected alternatives, for the record: a health-check contribution (the scheduler has no health
seam today, and a degraded/unhealthy API over an opt-in background loop is the outage trade again);
and dedupe-to-once-per-hour (the tick retries every 30s from `FireAtMinute` on, so this is ~90
errors/hour, which is loud — but a compliance surface silently producing nothing is exactly the case
where loud is correct).
