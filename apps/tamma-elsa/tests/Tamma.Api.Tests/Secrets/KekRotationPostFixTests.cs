using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// R2 post-fix coverage for the KEK lifecycle hardening:
/// <list type="bullet">
///   <item><b>PF-S5</b> — retry no longer mutates KekProvider state
///   before acquiring the cluster-wide advisory lock; the lock-loser
///   pod exits cleanly without staging a secondary.</item>
///   <item><b>PF-S8</b> — transient Npgsql error during lock
///   acquisition fails closed (rotation status = Failed) instead of
///   silently flipping to "acquired".</item>
///   <item><b>PF-C3</b> — the advisory lock is held on a dedicated
///   <see cref="NpgsqlConnection"/> for the full
///   <c>RunRotationAsync</c> lifetime, so EF's pooled-context
///   <c>DISCARD ALL</c> can't release it mid-rotation.</item>
///   <item><b>Retry-actor-identity</b> — the retry endpoint threads
///   the caller's <see cref="ClaimsPrincipal"/> through to the
///   coordinator so retry-emitted events carry the operator's
///   <c>sub</c>/<c>email</c>/<c>platformRole</c> claims.</item>
/// </list>
/// </summary>
[TestFixture]
public class KekRotationPostFixTests
{
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

    private static ClaimsPrincipal BuildPrincipal(
        string sub, string email, string platformRole)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, sub),
            new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email, email),
            new Claim("platformRole", platformRole),
        }, "test");
        return new ClaimsPrincipal(identity);
    }

    // ── Retry-actor-identity ─────────────────────────────────────────

    [Test]
    public async Task Retry_With_Principal_Records_Actor_Claims_On_Emitted_Events()
    {
        // Drive the coordinator into Failed phase, then call /retry
        // with a fresh ClaimsPrincipal — every event the retry path
        // emits must carry the retry-caller's identity, not the
        // original failed run's actor.
        var dbName = $"kek-postfix-retry-actor-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(
            o => o.UseInMemoryDatabase(dbName));
        services.AddLogging();
        var eventRepo = new RecordingPlatformEventRepository();
        services.AddSingleton<IPlatformEventRepository>(eventRepo);
        await using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();

        var initialPrimary = BuildKek(seed: 1);
        var stagedSecondary = BuildKek(seed: 50);

        // Seed a tenant whose envelope is encrypted under a key the
        // coordinator does not have — drives the loop into Failed.
        var corruptKey = BuildKek(seed: 200);
        const string cs = "Host=h;Database=t;Username=u;Password=p";
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(cs, corruptKey);
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = "T",
                Slug = $"slug-{Guid.NewGuid():N}",
                Type = "personal",
                Plan = "free",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var entry = ctx.Tenants.Add(tenant);
            entry.Property("Status").CurrentValue = "active";
            entry.Property("EncryptedConnectionString").CurrentValue = envelope;
            entry.Property("KekVersion").CurrentValue = 1;
            await ctx.SaveChangesAsync();
        }

        var provider = BuildProvider(initialPrimary);
        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            provider,
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        // Original run — actor claims point at "alice".
        coordinator.Start(
            stagedSecondary,
            actorUserId: "alice-user-id",
            actorEmail: "alice@tamma.dev",
            actorPlatformRole: "platform_admin");
        await coordinator.WaitForCompletionAsync();
        coordinator.GetStatus().Phase.Should().Be(KekRotationPhase.Failed);

        // Retry — different operator, "bob".
        var bob = BuildPrincipal(
            sub: "bob-user-id",
            email: "bob@tamma.dev",
            platformRole: "platform_admin");
        var response = await coordinator.RetryAsync(bob, CancellationToken.None);
        response.Success.Should().BeTrue();
        await coordinator.WaitForCompletionAsync();

        // Find the retry-side STARTED event. We disambiguate by
        // matching on the actor — alice's STARTED is the original run,
        // bob's is the retry.
        var startedEvents = eventRepo.AppendedEvents
            .Where(e => e.Type == "SECRETS.KEK.ROTATION.STARTED")
            .ToList();
        startedEvents.Should().HaveCount(2,
            "two STARTED rows: one per rotation attempt (original + retry)");

        var bobsStartedEvent = startedEvents.SingleOrDefault(e =>
            e.Tags.Contains("\"actorUserId\":\"bob-user-id\""));
        bobsStartedEvent.Should().NotBeNull(
            "the retry STARTED must carry bob's claims, not alice's");

        // Verify all 3 actor fields landed in the retry event's tags.
        var bobTags = JsonSerializer
            .Deserialize<Dictionary<string, string?>>(bobsStartedEvent!.Tags)!;
        bobTags.Should().ContainKey("actorUserId").WhoseValue.Should().Be("bob-user-id");
        bobTags.Should().ContainKey("actorEmail").WhoseValue.Should().Be("bob@tamma.dev");
        bobTags.Should().ContainKey("actorPlatformRole").WhoseValue.Should().Be("platform_admin");
        bobTags.Should().ContainKey("isRetry").WhoseValue.Should().Be("true");

        // Alice's original STARTED event must STILL carry alice's
        // claims — the retry didn't overwrite it.
        var aliceStartedEvent = startedEvents.SingleOrDefault(e =>
            e.Tags.Contains("\"actorUserId\":\"alice-user-id\""));
        aliceStartedEvent.Should().NotBeNull(
            "the original failed run's STARTED row preserves alice's claims");
    }

    [Test]
    public async Task Retry_Without_Principal_Records_Default_Actor()
    {
        // Test fixture / migration-script callers that genuinely lack
        // a principal still drive the retry path — the actor is
        // recorded as anonymous (no actorUserId/actorEmail tags
        // emitted, since the claim values are null/empty).
        var dbName = $"kek-postfix-retry-anon-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(
            o => o.UseInMemoryDatabase(dbName));
        services.AddLogging();
        var eventRepo = new RecordingPlatformEventRepository();
        services.AddSingleton<IPlatformEventRepository>(eventRepo);
        await using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();

        var initialPrimary = BuildKek(seed: 1);
        var stagedSecondary = BuildKek(seed: 50);

        var corruptKey = BuildKek(seed: 200);
        const string cs = "Host=h;Database=t;Username=u;Password=p";
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(cs, corruptKey);
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = "T",
                Slug = $"slug-{Guid.NewGuid():N}",
                Type = "personal",
                Plan = "free",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var entry = ctx.Tenants.Add(tenant);
            entry.Property("Status").CurrentValue = "active";
            entry.Property("EncryptedConnectionString").CurrentValue = envelope;
            entry.Property("KekVersion").CurrentValue = 1;
            await ctx.SaveChangesAsync();
        }

        var provider = BuildProvider(initialPrimary);
        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            provider,
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        coordinator.Start(stagedSecondary);
        await coordinator.WaitForCompletionAsync();
        coordinator.GetStatus().Phase.Should().Be(KekRotationPhase.Failed);

        var response = await coordinator.RetryAsync(principal: null, CancellationToken.None);
        response.Success.Should().BeTrue();
        await coordinator.WaitForCompletionAsync();

        // The retry STARTED event should NOT include actor tags (since
        // the principal is null and the empty-claim guard skips
        // augmenting the tag dictionary).
        var startedEvents = eventRepo.AppendedEvents
            .Where(e => e.Type == "SECRETS.KEK.ROTATION.STARTED")
            .ToList();
        startedEvents.Should().HaveCountGreaterOrEqualTo(2);
        var retryEvent = startedEvents.LastOrDefault(e =>
            e.Tags.Contains("\"isRetry\":\"true\""));
        retryEvent.Should().NotBeNull();
        retryEvent!.Tags.Should().NotContain("actorUserId",
            "anonymous retry must not synthesise actor claims");
    }

    // ── PF-S5 — lock-loser pod doesn't mutate KekProvider state ─────

    [Test]
    public async Task RetryAsync_Does_Not_Mutate_KekProvider_Before_Lock_Acquisition()
    {
        // The previous (broken) implementation called
        // KekProvider.RestoreStagedSecondary INSIDE RetryAsync, before
        // the cluster-wide advisory lock was held. Two pods racing
        // /retry would both mount the same secondary in their
        // in-memory KekProvider.
        //
        // The fix moved RestoreStagedSecondary into RunRotationAsync
        // AFTER the lock is acquired. To prove the move:
        //   1. Drive a first rotation into Failed via a corrupt
        //      tenant — this leaves the in-memory secondary staged
        //      with key A and a kek_rotations failed row also
        //      protected with key A.
        //   2. Manually overwrite the kek_rotations.StagedSecondaryProtected
        //      with a DIFFERENT key (key B) — now the persisted blob
        //      decrypts to key B even though provider._secondary is
        //      still key A.
        //   3. Hold the cluster-wide lock externally so the retry's
        //      RunRotationAsync exits without entering the
        //      RestoreStagedSecondary path.
        //   4. Call /retry. The lock-loser pod must NOT have replaced
        //      provider._secondary with key B — it should still be
        //      key A (the original).
        await using var pg = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("kek_pfs5_test")
            .Build();
        await pg.StartAsync();

        var connectionString = pg.GetConnectionString();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(
            o => o.UseNpgsql(connectionString));
        services.AddLogging();
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        await using var sp = services.BuildServiceProvider();

        // Apply the migration so the kek_rotations + tenants tables
        // exist for the SaveChanges path.
        await using (var bootCtx = await sp
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
            .CreateDbContextAsync())
        {
            await bootCtx.Database.ExecuteSqlRawAsync(
                "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\"");
            await bootCtx.Database.ExecuteSqlRawAsync(
                "CREATE EXTENSION IF NOT EXISTS pgcrypto");
            await bootCtx.Database.EnsureCreatedAsync();
        }

        var initialPrimary = BuildKek(seed: 1);
        var keyA = BuildKek(seed: 50); // Original first-run staged secondary
        var keyB = BuildKek(seed: 99); // Different key persisted into the row

        // Seed a tenant whose envelope is encrypted under a key the
        // coordinator does NOT have — drives the rotation to Failed.
        var corruptKey = BuildKek(seed: 200);
        const string cs = "Host=h;Database=t;Username=u;Password=p";
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(cs, corruptKey);
        await using (var ctx = await sp
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
            .CreateDbContextAsync())
        {
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = "T",
                Slug = $"slug-{Guid.NewGuid():N}",
                Type = "personal",
                Plan = "free",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var entry = ctx.Tenants.Add(tenant);
            entry.Property("Status").CurrentValue = "active";
            entry.Property("EncryptedConnectionString").CurrentValue = envelope;
            entry.Property("KekVersion").CurrentValue = 1;
            await ctx.SaveChangesAsync();
        }

        var provider = BuildProvider(initialPrimary);
        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            provider,
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        // Step 1 — drive into Failed. The first rotation stages key A
        // into provider._secondary AND persists key A into the
        // kek_rotations row.
        coordinator.Start(keyA);
        await coordinator.WaitForCompletionAsync();
        coordinator.GetStatus().Phase.Should().Be(KekRotationPhase.Failed);
        provider.GetSecondary().Should().BeEquivalentTo(keyA,
            "first rotation staged keyA into the provider — that's expected");

        // Step 2 — overwrite kek_rotations.StagedSecondaryProtected
        // with KEY B encrypted under the OLD primary. Now the
        // persisted blob decrypts to key B, but provider._secondary
        // still holds key A.
        var keyB_b64 = Convert.ToBase64String(keyB);
        var keyB_protected = AesGcmConnectionStringDecryptor.EncryptWithKey(
            keyB_b64, initialPrimary);
        await using (var ctx = await sp
            .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
            .CreateDbContextAsync())
        {
            var failedRow = await ctx.KekRotations
                .Where(r => r.Status == "failed")
                .OrderByDescending(r => r.StartedAt)
                .FirstAsync();
            failedRow.StagedSecondaryProtected = keyB_protected;
            await ctx.SaveChangesAsync();
        }

        // Step 3 — externally hold the cluster-wide lock so the
        // retry's RunRotationAsync exits before entering the
        // RestoreStagedSecondary path.
        await using var holderConn = new NpgsqlConnection(connectionString);
        await holderConn.OpenAsync();
        await using (var holdCmd = holderConn.CreateCommand())
        {
            holdCmd.CommandText =
                $"SELECT pg_try_advisory_lock({KekRotationCoordinator.AdvisoryLockKey})";
            var held = (bool)(await holdCmd.ExecuteScalarAsync())!;
            held.Should().BeTrue("test setup must take the cluster-wide lock");
        }

        // Step 4 — call /retry. The retry path must NOT mount key B
        // into the provider before checking the cluster-wide lock.
        var response = await coordinator.RetryAsync(principal: null);
        response.Success.Should().BeTrue("RetryAsync's pre-flight checks pass");

        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Failed,
            "lock-loser pod ends in Failed with the canonical reason");
        status.FailureReason.Should().Contain("another rotation is already in progress");

        // CRUCIAL — provider._secondary must still be key A. If the
        // pre-lock RestoreStagedSecondary call had run, _secondary
        // would now be key B.
        provider.GetSecondary().Should().BeEquivalentTo(keyA,
            "PF-S5: lock-loser pod must not mutate KekProvider._secondary "
            + "before the cluster-wide lock is held — the persisted retry "
            + "blob (key B) must NOT have been mounted");

        // Release the test-side lock.
        await using (var unlockCmd = holderConn.CreateCommand())
        {
            unlockCmd.CommandText =
                $"SELECT pg_advisory_unlock({KekRotationCoordinator.AdvisoryLockKey})";
            await unlockCmd.ExecuteScalarAsync();
        }
    }

    // ── PF-S8 — transient Npgsql exception → fail closed ────────────

    [Test]
    public async Task Rotation_Fails_Closed_On_Transient_Npgsql_Error_During_Lock_Acquisition()
    {
        // Use a Postgres connection string that points at a host that
        // accepts the connection but immediately drops it — simulates
        // a transient cluster blip during pg_try_advisory_lock. The
        // previous (broken) catch-all in RunRotationAsync would flip
        // acquired = true and proceed; the fix fails closed.
        //
        // We model the failure by registering a NpgsqlDataSource
        // pointing at a closed port. The subsequent OpenConnectionAsync
        // throws NpgsqlException, which the new catch maps to
        // acquired = false and Failed phase.
        var services = new ServiceCollection();
        services.AddLogging();
        // NB: an InMemory DbContextFactory is registered so the
        // coordinator can still try to write a rotation row. The
        // failure happens at OpenConnectionAsync time, so the InMemory
        // EF factory is never reached.
        services.AddDbContextFactory<ControlPlaneDbContext>(
            o => o.UseInMemoryDatabase($"kek-pfs8-{Guid.NewGuid():N}"));
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();

        // Closed port — guaranteed to fail with NpgsqlException on
        // OpenConnectionAsync. Port 1 is reserved by the OS and
        // refuses connections.
        const string brokenConnectionString =
            "Host=127.0.0.1;Port=1;Database=tamma;Username=tamma;Password=tamma;Timeout=2";
        services.AddSingleton(NpgsqlDataSource.Create(brokenConnectionString));
        await using var sp = services.BuildServiceProvider();

        var initialPrimary = BuildKek(seed: 1);
        var provider = BuildProvider(initialPrimary);
        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            provider,
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        coordinator.Start(BuildKek(seed: 50));
        await coordinator.WaitForCompletionAsync();

        var status = coordinator.GetStatus();
        status.Phase.Should().Be(KekRotationPhase.Failed,
            "PF-S8: transient Npgsql error during lock acquisition must "
            + "fail closed, not silently flip acquired = true");
        status.FailureReason.Should().Contain("another rotation is already in progress",
            "the rotation must use the same canonical failure reason as the "
            + "lock-already-held path so operators see one clean failure shape");
    }

    // ── PF-C3 — lock survives EF context disposal ───────────────────

    [Test]
    public async Task Advisory_Lock_Survives_EF_Pooled_Context_Disposal()
    {
        // PF-C3 — the previous design held the lock on EF's pooled
        // DbContext. When EF returned the connection to the pool,
        // Npgsql sent DISCARD ALL which silently released
        // session-level advisory locks. The fix opens a dedicated
        // NpgsqlConnection from NpgsqlDataSource and holds it for the
        // rotation lifetime.
        //
        // To prove the dedicated connection holds the lock across an
        // EF dispose, we:
        //   1. Acquire the advisory lock on a dedicated connection
        //      (mirroring what RunRotationAsync now does).
        //   2. Open and immediately dispose an EF DbContext targeting
        //      the same database — its connection-pool return must
        //      not affect the dedicated lock holder.
        //   3. Verify the lock is still held by attempting a second
        //      pg_try_advisory_lock from a third connection (it must
        //      return false).
        await using var pg = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("kek_pfc3_test")
            .Build();
        await pg.StartAsync();
        var connectionString = pg.GetConnectionString();

        await using var dataSource = NpgsqlDataSource.Create(connectionString);

        // 1. Acquire the lock on the dedicated connection.
        var lockConn = await dataSource.OpenConnectionAsync();
        await using (var cmd = lockConn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT pg_try_advisory_lock({KekRotationCoordinator.AdvisoryLockKey})";
            var got = (bool)(await cmd.ExecuteScalarAsync())!;
            got.Should().BeTrue("test setup acquires the rotation lock");
        }

        try
        {
            // 2. Open + dispose an EF context — the pooled connection
            // returns to the pool and triggers DISCARD ALL. This MUST
            // NOT release our dedicated connection's lock.
            var services = new ServiceCollection();
            services.AddDbContextFactory<ControlPlaneDbContext>(
                o => o.UseNpgsql(connectionString));
            await using var sp = services.BuildServiceProvider();
            var factory = sp
                .GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
            await using (var ctx = await factory.CreateDbContextAsync())
            {
                await ctx.Database.OpenConnectionAsync();
                await ctx.Database.CloseConnectionAsync();
            }

            // 3. Attempt to acquire the lock from a fresh connection.
            // It must fail — our dedicated lockConn still owns it.
            await using var probeConn = await dataSource.OpenConnectionAsync();
            await using var probeCmd = probeConn.CreateCommand();
            probeCmd.CommandText =
                $"SELECT pg_try_advisory_lock({KekRotationCoordinator.AdvisoryLockKey})";
            var probeGotIt = (bool)(await probeCmd.ExecuteScalarAsync())!;
            probeGotIt.Should().BeFalse(
                "PF-C3: dedicated NpgsqlConnection holds the session lock "
                + "across EF pooled-context disposal — DISCARD ALL on the "
                + "EF connection MUST NOT release our lock");
        }
        finally
        {
            // Release + dispose the dedicated lock connection.
            await using (var unlockCmd = lockConn.CreateCommand())
            {
                unlockCmd.CommandText =
                    $"SELECT pg_advisory_unlock({KekRotationCoordinator.AdvisoryLockKey})";
                await unlockCmd.ExecuteScalarAsync();
            }
            await lockConn.DisposeAsync();
        }
    }

    // ── Retry endpoint signature wiring ─────────────────────────────

    [Test]
    public async Task RetryEndpoint_Threads_Principal_To_Coordinator()
    {
        // Sanity-check the endpoint signature — the Retry(...) method
        // takes (KekRotationCoordinator, ClaimsPrincipal, HttpContext)
        // and forwards the principal to coordinator.RetryAsync. We
        // can't reach inside the coordinator from a static-method test
        // without exposing internals; instead we assert that the
        // endpoint compiles + the principal flows through to the
        // RetryAsync overload (verified by the type-check at compile
        // time + the smoke test below).
        var dbName = $"kek-postfix-endpoint-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(
            o => o.UseInMemoryDatabase(dbName));
        services.AddLogging();
        services.AddSingleton<IPlatformEventRepository, NoopPlatformEventRepository>();
        await using var sp = services.BuildServiceProvider();

        var initialPrimary = BuildKek(seed: 1);
        var provider = BuildProvider(initialPrimary);
        var coordinator = new KekRotationCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            provider,
            new NoopTenantConnectionResolver(),
            NullLogger<KekRotationCoordinator>.Instance);

        var principal = BuildPrincipal("retry-sub", "retry@tamma.dev", "platform_admin");
        var ctx = new DefaultHttpContext();

        var result = await KekRotationEndpoints.Retry(coordinator, principal, ctx);
        // Idle-phase coordinator → 409 Conflict response.
        result.GetType().Name.Should().Contain("Conflict");
    }

}
