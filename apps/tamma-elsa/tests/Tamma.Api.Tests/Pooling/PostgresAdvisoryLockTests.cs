using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pooling;

/// <summary>
/// 2026-07-30 advisory-lock audit — contract tests for
/// <see cref="PostgresAdvisoryLock"/>, the shared helper extracted after
/// the defect proven in <c>TenantMigrationSweepRunner</c> (b958adc) turned
/// up in four more places.
///
/// <para><b>The property under test</b> is not "the unlock runs" — every
/// one of those four sites already unlocked on its happy path. It is that
/// the lock rides a session that <b>ends</b> when the critical section
/// ends, so that the exit paths which skip or fail the unlock (a
/// cancelled token, a throw from the unlock itself, a dropped lease, a
/// crash) release it anyway. On a POOLED connection none of that is true:
/// disposal hands the connector back to the pool with the backend session,
/// and the advisory lock, still alive, and Npgsql defers the
/// <c>DISCARD ALL</c> that would release it until that connector is next
/// USED. The gate then stays shut for the pool's idle lifetime, or forever
/// on a <c>MinPoolSize</c> connector.</para>
/// </summary>
[TestFixture]
public class PostgresAdvisoryLockTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("advisory_lock_helper")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [Test]
    public void ToUnpooledConnectionString_turns_pooling_off()
    {
        // Pooling=false is load-bearing, not a tuning choice: it is the
        // single flag that makes "closing the connection releases the
        // lock" true. If this ever flips back, every caller silently
        // regains the ability to park its gate shut.
        var rewritten = PostgresAdvisoryLock.ToUnpooledConnectionString(_cs);
        new NpgsqlConnectionStringBuilder(rewritten).Pooling.Should().BeFalse();

        // …and nothing else about the connection string is disturbed.
        var original = new NpgsqlConnectionStringBuilder(_cs);
        var updated = new NpgsqlConnectionStringBuilder(rewritten);
        updated.Host.Should().Be(original.Host);
        updated.Port.Should().Be(original.Port);
        updated.Database.Should().Be(original.Database);
        updated.Username.Should().Be(original.Username);
    }

    [Test]
    public async Task Disposing_a_lease_releases_the_lock_AND_ends_its_backend_session()
    {
        const long key = 0x7A11_0001L;

        var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(key));
        lease.Should().NotBeNull();

        var pid = await AdvisoryLockProbe.HolderPidAsync(_cs, key);
        pid.Should().NotBeNull("the lease really does hold the key — otherwise this test proves nothing");

        await lease!.DisposeAsync();

        (await AdvisoryLockProbe.IsHeldAsync(_cs, key)).Should().BeFalse(
            "the explicit unlock releases the key on the orderly path");
        (await AdvisoryLockProbe.WaitForBackendGoneAsync(_cs, pid!.Value, TimeSpan.FromSeconds(10)))
            .Should().BeTrue(
                "the lock-holding backend must be GONE, not parked idle in a connection "
                + "pool. A surviving backend is a session that could still be holding the "
                + "lock on any path where the explicit unlock did not run");
    }

    [Test]
    public async Task A_lease_whose_unlock_never_runs_still_releases_the_lock()
    {
        // This is the case every one of the audited sites got wrong: the
        // unlock is best-effort and its failure is swallowed on the
        // grounds that "closing the connection releases it anyway". Here
        // the unlock is made impossible (the session is closed out from
        // under the lease, as a dropped connection or a cancelled command
        // would do) and the lock must STILL be gone afterwards.
        const long key = 0x7A11_0002L;

        var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(key));
        lease.Should().NotBeNull();
        var pid = await AdvisoryLockProbe.HolderPidAsync(_cs, key);
        pid.Should().NotBeNull();

        // No unlock can possibly be issued after this line.
        await lease!.Session!.CloseAsync();
        await lease.DisposeAsync();

        (await AdvisoryLockProbe.IsHeldAsync(_cs, key)).Should().BeFalse(
            "closing a NON-POOLED session ends the backend, which drops its advisory "
            + "locks. On a pooled connection the connector would go back to the pool "
            + "with this key still held and the gate would stay shut");
        (await AdvisoryLockProbe.WaitForBackendGoneAsync(_cs, pid!.Value, TimeSpan.FromSeconds(10)))
            .Should().BeTrue();
    }

    [Test]
    public async Task Disposing_twice_is_safe()
    {
        const long key = 0x7A11_0003L;
        var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(key));
        await lease!.DisposeAsync();
        await lease.DisposeAsync(); // must not double-dispose the connection
        (await AdvisoryLockProbe.IsHeldAsync(_cs, key)).Should().BeFalse();
    }

    [Test]
    public async Task A_refused_acquisition_returns_null_and_leaves_no_session_behind()
    {
        const long key = 0x7A11_0004L;

        var holder = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(key));
        holder.Should().NotBeNull();

        var refused = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(key));
        refused.Should().BeNull("pg_try_advisory_lock does not block — it refuses");

        // Exactly one holder: the refused attempt must not have left a
        // half-open session lying around.
        (await AdvisoryLockProbe.HolderPidAsync(_cs, key)).Should().NotBeNull();

        await holder!.DisposeAsync();
        (await AdvisoryLockProbe.IsHeldAsync(_cs, key)).Should().BeFalse();
    }

    [Test]
    public async Task FromHashTextExtended_keys_on_the_databases_own_hash_not_a_client_side_one()
    {
        // TenantMoveService keys its per-tenant move gate on
        // pg_try_advisory_lock(hashtextextended(tenantId, 0)). Moving that
        // call behind this helper must not change WHICH number is locked —
        // a re-derived key is a different lock that excludes nobody.
        var tenantId = Guid.NewGuid().ToString("D");
        var expected = await AdvisoryLockProbe.HashTextExtendedAsync(_cs, tenantId);

        var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromHashTextExtended(tenantId));
        lease.Should().NotBeNull();

        (await AdvisoryLockProbe.HolderPidAsync(_cs, expected)).Should().NotBeNull(
            "the key the helper locked must be exactly hashtextextended(tenantId, 0)");

        await lease!.DisposeAsync();
        (await AdvisoryLockProbe.IsHeldAsync(_cs, expected)).Should().BeFalse();
    }

    // ───────── liveness watchdog: the REVERSE hazard ─────────
    //
    // Everything above tests "the lock outlived the work". These test the
    // other direction: the WORK outlives the lock. A pooler recycle, an idle
    // timeout or an administrative pg_terminate_backend ends the lease session
    // without touching the process, the gate silently opens, and the guarded
    // work carries on believing it is alone — which for a tenant move or a KEK
    // re-encrypt means two of them running at once.

    [Test]
    public async Task A_watchdog_cancels_its_token_when_the_lock_holding_backend_is_killed()
    {
        const long key = 0x7A11_0006L;

        // Declaration order IS the disposal contract: the lease is declared
        // first so it is disposed LAST, i.e. after the watchdog that shares
        // its session. Reversing them races a probe against the release.
        await using var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(key));
        lease.Should().NotBeNull();

        await using var watchdog = lease!.WatchLiveness(
            TimeSpan.FromMilliseconds(100), CancellationToken.None, site: "test");
        watchdog.Token.IsCancellationRequested.Should().BeFalse(
            "a watchdog over a genuinely held lock must not fire on its own");

        (await AdvisoryLockProbe.TerminateHolderAsync(_cs, key)).Should().BeTrue(
            "the test must actually kill the lock-holding backend — otherwise it proves nothing");

        await WaitForCancellationAsync(watchdog.Token, TimeSpan.FromSeconds(10));

        watchdog.Token.IsCancellationRequested.Should().BeTrue(
            "the guarded work must be aborted, not left running unguarded: the gate is now "
            + "open and a second holder can start at any moment");
        watchdog.LockLost.Should().BeTrue(
            "and the caller must be able to tell 'we lost exclusivity' from 'the host asked "
            + "us to stop' — they are different stories for an operator, and different "
            + "recovery states");
    }

    [Test]
    public async Task A_watchdog_does_not_cry_wolf_while_the_lock_is_genuinely_held()
    {
        // The other side of the guarantee: a heartbeat that mistook normal
        // operation for loss would abort every long move and every rotation.
        const long key = 0x7A11_0007L;

        await using var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(key));
        lease.Should().NotBeNull();

        await using var watchdog = lease!.WatchLiveness(
            TimeSpan.FromMilliseconds(50), CancellationToken.None, site: "test");

        await Task.Delay(1000); // ~20 heartbeats

        watchdog.Token.IsCancellationRequested.Should().BeFalse();
        watchdog.LockLost.Should().BeFalse();
    }

    [Test]
    public async Task A_watchdog_over_a_hashtextextended_key_watches_that_exact_key()
    {
        // The move gate keys on hashtextextended, which is frequently NEGATIVE
        // — the pg_locks re-assembly of (classid, objid) has to round-trip
        // that. A watchdog that silently watched the wrong key would either
        // never fire (guard gone) or fire immediately (every move aborts).
        var tenantId = Guid.NewGuid().ToString("D");
        var key = await AdvisoryLockProbe.HashTextExtendedAsync(_cs, tenantId);

        await using var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromHashTextExtended(tenantId));
        lease.Should().NotBeNull();

        await using var watchdog = lease!.WatchLiveness(
            TimeSpan.FromMilliseconds(100), CancellationToken.None, site: "test");
        await Task.Delay(400);
        watchdog.LockLost.Should().BeFalse("the key really is held — the probe must see it");

        (await AdvisoryLockProbe.TerminateHolderAsync(_cs, key)).Should().BeTrue();
        await WaitForCancellationAsync(watchdog.Token, TimeSpan.FromSeconds(10));
        watchdog.LockLost.Should().BeTrue();
    }

    [Test]
    public async Task A_watchdogs_token_is_cancelled_by_the_callers_token_too()
    {
        // Wrapping the caller's token must not DISCARD it: host shutdown has
        // to keep reaching the guarded work.
        const long key = 0x7A11_0008L;
        using var caller = new CancellationTokenSource();

        await using var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(key));
        await using var watchdog = lease!.WatchLiveness(
            TimeSpan.FromMilliseconds(100), caller.Token, site: "test");

        await caller.CancelAsync();

        watchdog.Token.IsCancellationRequested.Should().BeTrue();
        watchdog.LockLost.Should().BeFalse(
            "an ordinary cancellation is not lock loss — reporting it as such would send an "
            + "operator hunting a database problem that did not happen");
    }

    [Test]
    public async Task Disposing_a_watchdog_does_not_cancel_the_work_it_was_watching()
    {
        // Callers dispose the watchdog in a finally, AFTER the guarded work
        // completed successfully. If that disposal cancelled the token, every
        // successful run would end by cancelling itself — and any cleanup
        // still riding that token would fail.
        const long key = 0x7A11_0009L;

        await using var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(key));
        var watchdog = lease!.WatchLiveness(
            TimeSpan.FromMilliseconds(50), CancellationToken.None, site: "test");
        var token = watchdog.Token;

        await watchdog.DisposeAsync();

        token.IsCancellationRequested.Should().BeFalse();
        watchdog.LockLost.Should().BeFalse();
        await watchdog.DisposeAsync(); // idempotent
    }

    [Test]
    public async Task A_synchronous_host_that_stops_the_heartbeat_first_is_not_reported_as_lock_loss()
    {
        // 2026-07-31 review, F2 — the ORDERING CONTRACT, and what breaking it
        // costs. The watchdog and the lease ride the SAME single session, so a
        // host that ends the session while the heartbeat is still running hands
        // the probe a dead connection — which the probe reports as LOCK LOST,
        // correctly in every other context. For a pod that was simply asked to
        // stop, that is a false and alarming story: the operator is told the
        // cluster-wide lock was lost to "a pooler/proxy drop, an idle timeout,
        // or a terminated backend" and goes looking for a database incident
        // that never happened.
        //
        // An IDisposable host (TenantMigrationSweepRunner.Dispose) cannot await
        // DisposeAsync, and before StopHeartbeat existed it had no way to
        // honour the contract at all. Both halves are pinned here: the
        // violation produces the false loss, the contract order does not.
        const long violated = 0x7A11_000AL;
        const long honoured = 0x7A11_000BL;

        // ── the violation: end the session with the heartbeat still running ──
        var doomed = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(violated));
        var unstopped = doomed!.WatchLiveness(
            TimeSpan.FromMilliseconds(50), CancellationToken.None, site: "test");
        doomed.DisposeSession();
        await WaitForCancellationAsync(unstopped.Token, TimeSpan.FromSeconds(10));
        unstopped.LockLost.Should().BeTrue(
            "this is WHY the order matters — a heartbeat left running over an ended session "
            + "reports loss, which is exactly what an orderly shutdown must not produce");
        await unstopped.DisposeAsync();

        // ── the contract: stop the heartbeat first, then end the session ──
        var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            _cs, PostgresAdvisoryLockKey.FromInt64(honoured));
        var watchdog = lease!.WatchLiveness(
            TimeSpan.FromMilliseconds(50), CancellationToken.None, site: "test");
        await Task.Delay(200); // several genuine probes while the lock is held
        watchdog.LockLost.Should().BeFalse("the lock is genuinely held at this point");

        watchdog.StopHeartbeat(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "the heartbeat must actually be confirmed stopped — a timed-out stop means the "
            + "ordering could not be honoured and the caller is back in the case above");
        lease.DisposeSession();
        await Task.Delay(200); // several intervals' worth of "would have probed"

        watchdog.LockLost.Should().BeFalse(
            "an orderly synchronous shutdown is a shutdown, not a lost lock");
        watchdog.Token.IsCancellationRequested.Should().BeFalse(
            "…and StopHeartbeat must not cancel the work token either — it is the "
            // (DisposeAsync has the same guarantee; StopHeartbeat is its sync half.)
            + "counterpart of DisposeAsync, which pins the same property");

        (await AdvisoryLockProbe.IsHeldAsync(_cs, honoured)).Should().BeFalse(
            "and the lock is still released — ending the non-pooled session is what does "
            + "that, and stopping the heartbeat first changes none of it");

        await watchdog.DisposeAsync();
        await lease.DisposeAsync();
    }

    private static async Task WaitForCancellationAsync(CancellationToken token, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            await Task.Delay(25);
    }

    [Test]
    public async Task A_failed_connect_leaves_nothing_holding_anything()
    {
        // Port 1 refuses connections. The helper must surface that rather
        // than pretend it acquired, and must not leak the half-built
        // connection.
        const string broken =
            "Host=127.0.0.1;Port=1;Database=tamma;Username=tamma;Password=tamma;Timeout=2";

        var act = async () => await PostgresAdvisoryLock.TryAcquireAsync(
            broken, PostgresAdvisoryLockKey.FromInt64(0x7A11_0005L));

        await act.Should().ThrowAsync<NpgsqlException>(
            "a lock that could not be attempted must never read as acquired");
    }
}
