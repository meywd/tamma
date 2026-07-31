using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// 2026-07-30 advisory-lock audit — the KEK rotation gate is the most
/// serious of the four audited sites.
///
/// <para>PF-C3 already moved the cluster-wide rotation lock OFF the EF
/// pooled <c>DbContext</c> and onto "a dedicated
/// <see cref="NpgsqlConnection"/>". But dedicated is not the same as
/// non-pooled: it was opened via <c>NpgsqlDataSource.OpenConnectionAsync</c>,
/// which hands out a POOLED connector. Disposing it in the rotation's
/// <c>finally</c> returned it to the pool with the backend session — and
/// the rotation lock — still alive, because Npgsql defers the
/// <c>DISCARD ALL</c> that runs <c>pg_advisory_unlock_all()</c> until that
/// connector is next USED. The explicit unlock was best-effort and its
/// failure swallowed, on the (false, for a pooled connection) grounds
/// that "the connection drop is the actual guarantee".</para>
///
/// <para><b>Blast radius, and why this ranks first.</b>
/// <see cref="KekRotationCoordinator.AdvisoryLockKey"/> is a CONSTANT — it
/// never rotates the way the per-hour scheduler keys do. So a single
/// swallowed unlock wedges KEK rotation shut for the whole cluster
/// indefinitely, and every subsequent <c>/api/admin/kek/rotate</c> fails
/// with the operator-visible untruth "another rotation is already in
/// progress on this cluster". That is a security operation an operator may
/// need urgently (suspected key compromise), blocked by a lock nobody
/// holds.</para>
///
/// <para>The observer is <see cref="AdvisoryLockProbe"/>, deliberately NOT
/// pooled: a pooled probe can draw the leaking connector and clear the
/// lock via the deferred reset before reading, i.e. repair the very state
/// it was sent to measure.</para>
/// </summary>
[TestFixture]
public class KekRotationLockSessionTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("kek_lock_session")
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

    /// <summary>
    /// Configuration shaped like the host's: the control-plane connection
    /// string under <c>ConnectionStrings:ControlPlane</c>.
    /// </summary>
    private IConfiguration ControlPlaneConfig()
        => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] = _cs,
            }).Build();

    private static byte[] BuildKek(byte seed)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    private static KekProvider BuildProvider(byte[] primary)
    {
        var dict = new Dictionary<string, string?>
        {
            [KekProvider.PrimaryConfigKey] = Convert.ToBase64String(primary),
            [KekProvider.ActiveVersionConfigKey] = "1",
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        return new KekProvider(cfg, NullLogger<KekProvider>.Instance);
    }

    [Test]
    public async Task The_rotation_lock_rides_a_session_that_dies_with_the_rotation()
    {
        // The property that makes every exit path safe: when the rotation
        // ends, the backend that held the cluster-wide gate is GONE — not
        // parked idle in a connection pool, where it would still own the
        // lock on any path that skipped or failed the explicit unlock.
        //
        // The pid is captured from INSIDE the rotation, via the platform
        // event repository: SECRETS.KEK.ROTATION.STARTED is emitted after
        // the advisory lock has been taken, so the probe runs while the
        // gate is genuinely held.
        var services = new ServiceCollection();
        services.AddLogging();
        // Production resolves the control-plane connection string from
        // configuration (ConnectionStringResolver.ResolveControlPlane), and so
        // does the rotation lock — it is the only source that reliably still
        // carries the password. Register it exactly as the host does.
        services.AddSingleton<IConfiguration>(ControlPlaneConfig());
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        var probe = new LockProbingEventRepository(_cs);
        services.AddSingleton<IPlatformEventRepository>(probe);
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        await using var sp = services.BuildServiceProvider();

        await using (var boot = await sp
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
            .CreateDbContextAsync())
        {
            await boot.Database.EnsureCreatedAsync();
        }

        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            BuildProvider(BuildKek(seed: 1)),
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        coordinator.Start(BuildKek(seed: 50));
        await coordinator.WaitForCompletionAsync();

        probe.HolderPid.Should().NotBeNull(
            "the rotation really did hold the cluster-wide gate while it ran — "
            + "otherwise this test proves nothing");

        (await AdvisoryLockProbe.WaitForBackendGoneAsync(
            _cs, probe.HolderPid!.Value, TimeSpan.FromSeconds(10)))
            .Should().BeTrue(
                "the rotation lock's Postgres session must END with the rotation. A "
                + "surviving backend means the lock rode a POOLED connector, and since "
                + "this key is a constant that never rotates, any exit that skipped the "
                + "unlock would wedge cluster-wide KEK rotation shut indefinitely — every "
                + "later /rotate failing with the untrue 'another rotation is already in "
                + "progress on this cluster'");

        (await AdvisoryLockProbe.IsHeldAsync(_cs, KekRotationCoordinator.AdvisoryLockKey))
            .Should().BeFalse("and the gate must be open for the next operator");
    }

    [Test]
    public async Task A_finished_rotation_leaves_the_gate_open_for_the_next_one()
    {
        // The end-to-end consequence: back-to-back rotations must both be
        // admitted. On the pooled implementation this passes only when the
        // pool happens to hand back the leaking connector (whose deferred
        // DISCARD ALL then releases the lock) — i.e. whether the gate is
        // open is a connection-pool draw.
        var services = new ServiceCollection();
        services.AddLogging();
        // Production resolves the control-plane connection string from
        // configuration (ConnectionStringResolver.ResolveControlPlane), and so
        // does the rotation lock — it is the only source that reliably still
        // carries the password. Register it exactly as the host does.
        services.AddSingleton<IConfiguration>(ControlPlaneConfig());
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        await using var sp = services.BuildServiceProvider();

        await using (var boot = await sp
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
            .CreateDbContextAsync())
        {
            await boot.Database.EnsureCreatedAsync();
        }

        var provider = BuildProvider(BuildKek(seed: 1));
        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            provider,
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        coordinator.Start(BuildKek(seed: 60));
        await coordinator.WaitForCompletionAsync();

        // Guard against passing VACUOUSLY. If the coordinator could not take
        // the gate at all — e.g. it was handed a connection string Npgsql had
        // stripped the password from — it aborts with exactly the phase and
        // reason a lock-loser gets, and every "the gate is free afterwards"
        // assertion below would hold for the wrong reason.
        var status = coordinator.GetStatus();
        status.Phase.Should().NotBe(KekRotationPhase.Failed,
            "this test is only meaningful if the rotation actually RAN and actually "
            + "held the cluster-wide gate; a rotation that never acquired it leaves the "
            + "gate free trivially. FailureReason: " + status.FailureReason);

        // Immediately after, a fresh session must be able to take the gate.
        await using var next = await Tamma.Data.Pooling.PostgresAdvisoryLock.TryAcquireAsync(
            _cs,
            Tamma.Data.Pooling.PostgresAdvisoryLockKey.FromInt64(
                KekRotationCoordinator.AdvisoryLockKey));
        next.Should().NotBeNull(
            "a finished rotation must leave the cluster-wide gate open, whichever "
            + "connection the next attempt happens to land on");
    }

    [Test]
    public async Task An_externally_held_gate_still_makes_the_rotation_stand_down()
    {
        // The lock's SEMANTICS must be unchanged by the audit: same key,
        // same exclusion, same canonical failure. Only the session it rides
        // on changed.
        var services = new ServiceCollection();
        services.AddLogging();
        // Production resolves the control-plane connection string from
        // configuration (ConnectionStringResolver.ResolveControlPlane), and so
        // does the rotation lock — it is the only source that reliably still
        // carries the password. Register it exactly as the host does.
        services.AddSingleton<IConfiguration>(ControlPlaneConfig());
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        await using var sp = services.BuildServiceProvider();

        await using (var boot = await sp
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
            .CreateDbContextAsync())
        {
            await boot.Database.EnsureCreatedAsync();
        }

        await using var holder = await Tamma.Data.Pooling.PostgresAdvisoryLock.TryAcquireAsync(
            _cs,
            Tamma.Data.Pooling.PostgresAdvisoryLockKey.FromInt64(
                KekRotationCoordinator.AdvisoryLockKey));
        holder.Should().NotBeNull("test setup holds the cluster-wide rotation gate");

        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            BuildProvider(BuildKek(seed: 1)),
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        coordinator.Start(BuildKek(seed: 70));
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Failed);
        status.FailureReason.Should().Contain("another rotation is already in progress");

        (await AdvisoryLockProbe.IsHeldAsync(_cs, KekRotationCoordinator.AdvisoryLockKey))
            .Should().BeTrue("the lock-loser must not have released the winner's gate");

        // Guard against passing VACUOUSLY: a coordinator that can NEVER take
        // the gate (a broken connection string, say) produces the identical
        // Failed/"already in progress" shape. Release the external holder and
        // show that the very same wiring now DOES rotate — which means the
        // stand-down above was caused by the lock, not by breakage.
        await holder!.DisposeAsync();

        var second = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            BuildProvider(BuildKek(seed: 1)),
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);
        second.Start(BuildKek(seed: 80));
        await second.WaitForCompletionAsync();

        second.GetStatus().Phase.Should().NotBe(KekRotationPhase.Failed,
            "with the gate released the same wiring must rotate — otherwise the "
            + "stand-down above proved nothing about the lock");
    }

    // ───────── the REVERSE hazard: the lock dies, the re-encrypt does not ─────────

    [Test]
    public async Task A_rotation_whose_lock_holding_backend_dies_aborts_instead_of_re_encrypting_on_unguarded()
    {
        // 2026-07-30 follow-up. Everything above guards the direction "the
        // lock outlives the rotation". This is the reverse, and it is the
        // worse one: a pooler recycle, an idle timeout or an administrative
        // pg_terminate_backend ends the lock session WITHOUT touching this
        // process. The re-encrypt runs over entirely different connections, so
        // nothing notices — the cluster-wide gate is now open, a second pod's
        // /rotate is admitted, and two pods re-encrypt the same tenant rows
        // under two different keys. The surviving envelope is then readable
        // only by whichever pod's key gets promoted; the other tenants' rows
        // are unreadable by everyone. That is not recoverable by re-running.
        //
        // The kill happens from a SEPARATE, non-pooled session while the
        // rotation is genuinely parked inside its per-tenant loop, which is
        // the shape of the production incident.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(ControlPlaneConfig());
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        await using var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using (var boot = await factory.CreateDbContextAsync())
        {
            await boot.Database.EnsureCreatedAsync();
        }

        // Two rotatable tenants. One is enough to prove the abort; the second
        // is what proves the rotation did not simply carry on regardless.
        var primary = BuildKek(seed: 1);
        await SeedRotatableTenantsAsync(factory, primary, count: 2);

        // The resolver is called once per tenant INSIDE the guarded loop, with
        // the rotation's own token — so it is the natural place to (a) kill the
        // lock holder and (b) sit there, exactly as a slow re-encrypt would,
        // while the watchdog decides.
        var saboteur = new LockKillingResolver(_cs);

        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            BuildProvider(primary),
            saboteur,
            NullLogger<KekRotationCoordinator>.Instance)
        {
            // Production is 5s (see KekRotationCoordinator.LockHeartbeatInterval);
            // the INTERVAL is a tuning constant, the abort-on-loss is not.
            LockHeartbeatInterval = TimeSpan.FromMilliseconds(150),
        };

        coordinator.Start(BuildKek(seed: 90));
        await coordinator.WaitForCompletionAsync();

        saboteur.Killed.Should().BeTrue(
            "the test must actually have terminated the lock-holding backend — otherwise it "
            + "proves nothing about losing the gate");

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Failed,
            "a rotation that can no longer guarantee it is the only re-encryption running "
            + "must ABORT, not finish. FailureReason: " + status.FailureReason);
        status.FailureReason.Should().Contain("rotation lock was LOST",
            "the operator has to be told WHY it stopped — a bare 'rotation cancelled' sends "
            + "them looking for a shutdown that did not happen");
        status.FailureReason.Should().Contain("retry",
            "…and what to do about the rows that are already on the new key");

        saboteur.Evictions.Should().Be(1,
            "the abort must stop the per-tenant loop where it stood. Reaching the second "
            + "tenant means the re-encrypt kept running with the cluster-wide gate open, "
            + "which is the entire defect");

        // The staged secondary is kept so /retry can finish the job — a
        // lock-loss abort is a partial rotation, not a cancelled one.
        await using var check = await factory.CreateDbContextAsync();
        var row = await check.KekRotations
            .OrderByDescending(r => r.StartedAt).FirstAsync();
        row.Status.Should().Be("failed");
        row.StagedSecondaryProtected.Should().NotBeNull(
            "a rotation aborted part-way through the fleet must stay resumable; persisting it "
            + "as 'cancelled' would zero the staged key and strand the already-rotated rows");
    }

    [Test]
    public async Task A_genuine_cancellation_under_an_armed_watchdog_is_reported_as_cancelled_not_as_loss()
    {
        // The OTHER branch of the same catch, which nothing covered: the
        // watchdog is armed and the token IS cancelled, but the lock was never
        // lost — the operator (or the host) cancelled. The two stories share a
        // token and an exception type and differ only by LockLost, so the
        // branch that reads LockLost has to be pinned from BOTH sides or a
        // future "simplification" can make every cancellation report a lost
        // cluster gate.
        //
        // The persisted state differs too, and that difference matters more
        // than the wording: a lock-loss abort is a PARTIAL rotation and must
        // stay resumable (row 'failed', staged secondary KEPT), while a
        // cancellation is over (row 'cancelled', staged secondary ZEROED).
        // Getting this backwards either strands half-rotated rows or leaves a
        // stale key sitting in the control plane.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(ControlPlaneConfig());
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        await using var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using (var boot = await factory.CreateDbContextAsync())
        {
            await boot.Database.EnsureCreatedAsync();
        }

        var primary = BuildKek(seed: 1);
        await SeedRotatableTenantsAsync(factory, primary, count: 2);

        using var operatorCts = new CancellationTokenSource();
        var canceller = new CancellingResolver(_cs, operatorCts);

        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            BuildProvider(primary),
            canceller,
            NullLogger<KekRotationCoordinator>.Instance)
        {
            LockHeartbeatInterval = TimeSpan.FromMilliseconds(150),
        };

        coordinator.Start(BuildKek(seed: 95), operatorCts.Token);
        await coordinator.WaitForCompletionAsync();

        canceller.GateWasHeld.Should().BeTrue(
            "the rotation must genuinely have held the gate when it was cancelled — "
            + "otherwise no watchdog was armed and this proves nothing about the branch");

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Failed);
        status.FailureReason.Should().Be("rotation cancelled");
        status.FailureReason.Should().NotContain("LOST",
            "the gate was never lost — telling an operator it was sends them hunting a "
            + "pooler/idle-timeout incident that did not happen");

        await using var check = await factory.CreateDbContextAsync();
        var row = await check.KekRotations
            .OrderByDescending(r => r.StartedAt).FirstAsync();
        row.Status.Should().Be("cancelled",
            "a cancelled rotation is over; only a lock-loss abort is persisted as 'failed', "
            + "which is what /retry looks for");
        row.StagedSecondaryProtected.Should().BeNull(
            "…and the staged key is zeroed, because nothing is going to resume this");

        RunningTaskOf(coordinator).IsCompletedSuccessfully.Should().BeFalse(
            "a genuine cancellation RETHROWS — that is the difference from the lock-loss "
            + "branch, which returns so a lost gate is never reported to the host as an "
            + "orderly shutdown");
    }

    [Test]
    public async Task Retry_after_a_lock_loss_abort_finishes_the_rotation_from_where_it_stopped()
    {
        // The lock-loss abort's whole promise to the operator is the last
        // sentence of its status message: "re-run /api/admin/kek/retry to
        // finish the rotation from where it stopped". Nothing covered that
        // end-to-end — only that the row was left in a shape /retry LOOKS for.
        // The gap matters because the resumability depends on three separate
        // decisions agreeing (row persisted 'failed' not 'cancelled', staged
        // secondary kept not zeroed, the primary NOT promoted), and any one of
        // them silently strands the rows already re-encrypted under a key no
        // pod will ever hold.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(ControlPlaneConfig());
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        await using var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using (var boot = await factory.CreateDbContextAsync())
        {
            await boot.Database.EnsureCreatedAsync();
        }

        var primary = BuildKek(seed: 1);
        var tenantIds = await SeedRotatableTenantsAsync(factory, primary, count: 2);

        var saboteur = new LockKillingResolver(_cs);
        var provider = BuildProvider(primary);
        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            provider,
            saboteur,
            NullLogger<KekRotationCoordinator>.Instance)
        {
            LockHeartbeatInterval = TimeSpan.FromMilliseconds(150),
        };

        // ── act 1: lose the gate mid-fleet ──────────────────────────────
        coordinator.Start(BuildKek(seed: 96));
        await coordinator.WaitForCompletionAsync();

        saboteur.Killed.Should().BeTrue("the abort must be caused by a real lost gate");
        coordinator.GetStatus().Phase.Should().Be(KekRotationPhase.Failed);
        provider.GetActiveVersion().Should().Be(1,
            "a partial rotation must NOT promote — the old primary still has rows to read");

        // ── act 2: the operator does what the status told them to ───────
        var retry = await coordinator.RetryAsync(principal: null);
        retry.Success.Should().BeTrue(
            "the lock-loss abort promises /retry will work; if the row had been persisted "
            + "'cancelled' (or its staged key zeroed) there would be nothing to resume. "
            + "Reason: " + retry.Reason);
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Completed,
            "the retry finishes the fleet. FailureReason: " + status.FailureReason);
        provider.GetActiveVersion().Should().Be(2, "…and only NOW is the new key promoted");

        await using var check = await factory.CreateDbContextAsync();
        foreach (var id in tenantIds)
        {
            var tenant = await check.Tenants.IgnoreQueryFilters()
                .FirstAsync(t => t.Id == id);
            check.Entry(tenant).Property<short>("KekVersion").CurrentValue
                .Should().Be(2,
                    "every row must end on the new key — including the one the aborted run "
                    + "had already done, which is the row a fresh /start would have orphaned");
        }

        var row = await check.KekRotations
            .OrderByDescending(r => r.StartedAt).FirstAsync();
        row.Status.Should().Be("completed");
        row.StagedSecondaryProtected.Should().BeNull(
            "a completed rotation zeroes the staged key — it is live now");
    }

    [Test]
    public async Task A_rotation_whose_gate_cannot_be_evaluated_fails_closed_instead_of_wedging()
    {
        // 2026-07-31 review, F5 — fail CLOSED is not the same as fail STUCK.
        //
        // "Host=h;Bogus=1" is refused by HasCredentials (it does not parse),
        // so the resolution seam's trust-auth tier used to hand it straight
        // back, and TryAcquireAsync's Pooling=false rewrite then threw an
        // ArgumentException. Nothing here caught it — the acquisition's catch
        // list was OperationCanceledException + NpgsqlException — so it escaped
        // RunRotationAsync ahead of the try/finally that owns the status. Phase
        // stayed Running FOREVER, _activeRotationId was never cleared, the
        // Task.Run faulted unobserved, and /status reported a rotation that
        // could neither finish nor be retried. A typo in appsettings must not
        // be able to do that.
        var malformed = "Host=nowhere;Database=cp;Username=u;Bogus=1";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:ControlPlane"] = malformed,
                }).Build());
        // EF's view carries no password either, so the seam cannot fall back to
        // it — indistinguishable from the laundered production shape.
        services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseNpgsql(
            new NpgsqlConnectionStringBuilder(_cs) { Password = null }.ConnectionString));
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        services.AddSingleton(NpgsqlDataSource.Create(_cs));
        await using var sp = services.BuildServiceProvider();

        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            BuildProvider(BuildKek(seed: 1)),
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        coordinator.Start(BuildKek(seed: 97));
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().NotBe(KekRotationPhase.Running,
            "a rotation that could not even EVALUATE its gate must reach a terminal phase — "
            + "leaving /status pinned at Running is an operation that can never finish and "
            + "can never be retried");
        status.Phase.Should().Be(KekRotationPhase.Failed);
        status.FailureReason.Should().Contain("gate could not be evaluated",
            "the operator needs to know the GATE is the problem, not the rotation");
        status.FailureReason.Should().NotContain("another rotation is already in progress",
            "that is the exact operator-visible untruth this whole audit exists to remove — "
            + "there is no other rotation, there is a broken connection string");

        RunningTaskOf(coordinator).IsCompletedSuccessfully.Should().BeTrue(
            "and nothing may escape the background task unobserved");
    }

    /// <summary>
    /// The coordinator's in-flight rotation task. Private because nothing in
    /// production needs it; read here because "returned" versus "rethrew" is a
    /// real, load-bearing difference between the lock-loss and cancellation
    /// branches and is otherwise unobservable (WaitForCompletionAsync swallows).
    /// </summary>
    private static Task RunningTaskOf(KekRotationCoordinator coordinator)
    {
        var field = typeof(KekRotationCoordinator).GetField(
            "_runningTask",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.Should().NotBeNull("the coordinator still schedules its rotation on a Task");
        return (Task)field!.GetValue(coordinator)!;
    }

    private static async Task<List<Guid>> SeedRotatableTenantsAsync(
        IDbContextFactory<ControlPlaneDbContext> factory, byte[] primary, int count)
    {
        var ids = new List<Guid>();
        await using var ctx = await factory.CreateDbContextAsync();
        for (var i = 0; i < count; i++)
        {
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = $"T{i}",
                Slug = $"slug-{Guid.NewGuid():N}",
                Type = "personal",
                Plan = "free",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var entry = ctx.Tenants.Add(tenant);
            entry.Property("Status").CurrentValue = "active";
            entry.Property("EncryptedConnectionString").CurrentValue =
                AesGcmConnectionStringDecryptor.EncryptWithKey(
                    "Host=h;Database=t;Username=u;Password=p", primary);
            entry.Property("KekVersion").CurrentValue = (short)1;
            ids.Add(tenant.Id);
        }
        await ctx.SaveChangesAsync();
        return ids;
    }

    /// <summary>
    /// Cancels the operator's token from inside the guarded per-tenant loop —
    /// the genuine-cancellation counterpart of
    /// <see cref="LockKillingResolver"/>, with the gate still healthily held.
    /// Records that the gate WAS held, so the test cannot pass vacuously
    /// against a rotation that never armed a watchdog.
    /// </summary>
    private sealed class CancellingResolver(string connectionString, CancellationTokenSource cts)
        : ITenantConnectionResolver
    {
        private int _evictions;

        public bool GateWasHeld { get; private set; }

        public async ValueTask EvictAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _evictions) != 1) return;

            GateWasHeld = await AdvisoryLockProbe.HolderPidAsync(
                connectionString, KekRotationCoordinator.AdvisoryLockKey) is not null;

            await cts.CancelAsync();
            // Stand in for the rest of a slow re-encrypt; the cancellation
            // reaches the loop through the same token a lost lock would use.
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }

        public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<ITenantConnectionLease> LeaseAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public TenantConnectionPoolStats GetStats() => new(0, 0, 0);
    }

    /// <summary>
    /// Kills the backend holding the rotation gate the first time the
    /// rotation reaches its per-tenant eviction, then blocks on the
    /// rotation's own token — i.e. the guarded work is still in flight when
    /// the lock disappears, which is the whole scenario. If the token is
    /// never cancelled (no watchdog), the wait times out and the rotation
    /// carries on to the next tenant, which is exactly what must not happen.
    /// </summary>
    private sealed class LockKillingResolver(string connectionString) : ITenantConnectionResolver
    {
        private int _evictions;

        public int Evictions => Volatile.Read(ref _evictions);
        public bool Killed { get; private set; }

        public async ValueTask EvictAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _evictions) == 1)
            {
                Killed = await AdvisoryLockProbe.TerminateHolderAsync(
                    connectionString, KekRotationCoordinator.AdvisoryLockKey);

                // Stand in for the rest of a slow re-encrypt. A watchdog-armed
                // rotation cancels this within a heartbeat; an unguarded one
                // sits here for the full delay and then rotates tenant #2.
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }

        public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<ITenantConnectionLease> LeaseAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public TenantConnectionPoolStats GetStats() => new(0, 0, 0);
    }

    /// <summary>
    /// Records the backend pid holding the rotation gate at the moment the
    /// rotation emits its STARTED event — i.e. from inside the critical
    /// section, through a NON-POOLED observer.
    /// </summary>
    private sealed class LockProbingEventRepository(string connectionString) : IPlatformEventRepository
    {
        public int? HolderPid { get; private set; }

        public async Task<PlatformEvent?> AppendAsync(PlatformEvent evt, CancellationToken ct = default)
        {
            if (evt.Id == Guid.Empty) evt.Id = Guid.NewGuid();
            if (evt.CreatedAt == default) evt.CreatedAt = DateTime.UtcNow;

            HolderPid ??= await AdvisoryLockProbe.HolderPidAsync(
                connectionString, KekRotationCoordinator.AdvisoryLockKey);

            return evt;
        }

        public Task<PlatformEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PlatformEvent?>(null);

        public Task<IReadOnlyList<PlatformEvent>> QueryAsync(
            Guid? tenantId = null,
            Guid? userId = null,
            string? typePrefix = null,
            DateTime? since = null,
            bool includePlatformWide = false,
            int limit = 100,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlatformEvent>>(Array.Empty<PlatformEvent>());
    }
}
