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
