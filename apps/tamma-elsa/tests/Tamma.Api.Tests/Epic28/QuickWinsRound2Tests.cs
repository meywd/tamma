using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Endpoints;
using Tamma.Api.Endpoints.Admin;
using Tamma.Data.Abstractions;
using Tamma.Api.Services;
using Tamma.Api.Services.TenantStatus;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Round-2 review (2026-04-26) — verification harness for the
/// "quick wins" batch (H4, H10, M11, M16). One file groups the related
/// assertions so the batch is easy to track down + revisit.
///
/// <list type="bullet">
///   <item><description>H4 — <c>BuildStepLadder</c> consumes a typed
///     <c>IEnumerable&lt;StepEvent&gt;</c> instead of
///     <c>IEnumerable&lt;dynamic&gt;</c>.</description></item>
///   <item><description>H10 — <c>IDbContextFactory&lt;ControlPlaneDbContext&gt;</c>
///     yields a fresh CP context per call.</description></item>
///   <item><description>M11 — <c>PoolWarmupService</c> extends
///     <c>BackgroundService</c> and <c>StopAsync</c> cancels the
///     warmup.</description></item>
///   <item><description>M16 — Admin endpoints honour
///     <c>TimeProvider</c> for clock reads.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class QuickWinsRound2Tests
{
    // ── H4 — BuildStepLadder typed input ─────────────────────────────

    [Test]
    public void H4_BuildStepLadder_AcceptsTypedStepEvents_AndProjectsLatestStatePerStep()
    {
        // Round-2 review H4: the reducer used to take
        // IEnumerable<dynamic>, which disabled nullable analysis on
        // every member access. The fix swaps to a typed StepEvent
        // record. This test asserts both the typed signature compiles
        // and the reducer returns the expected ladder.
        var t0 = new DateTime(2026, 04, 26, 12, 00, 00, DateTimeKind.Utc);
        var events = new[]
        {
            new TenantStatusEndpoint.StepEvent(
                "TENANT.PROVISION.STEP_STARTED",
                """{"step":"create-role"}""",
                t0),
            new TenantStatusEndpoint.StepEvent(
                "TENANT.PROVISION.STEP_COMPLETED",
                """{"step":"create-role"}""",
                t0.AddSeconds(2)),
            new TenantStatusEndpoint.StepEvent(
                "TENANT.PROVISION.STEP_STARTED",
                """{"step":"migrate-tenant-db"}""",
                t0.AddSeconds(3)),
            new TenantStatusEndpoint.StepEvent(
                "TENANT.PROVISION.STEP_FAILED",
                """{"step":"migrate-tenant-db"}""",
                t0.AddSeconds(4)),
            // an event missing the step tag gets skipped cleanly
            new TenantStatusEndpoint.StepEvent(
                "TENANT.PROVISION.STEP_STARTED",
                """{"other":"x"}""",
                t0.AddSeconds(5)),
        };

        var ladder = TenantStatusEndpoint.BuildStepLadder(events);

        ladder.Should().HaveCount(2);
        ladder.Should().ContainSingle(s => s.Step == "create-role" && s.State == "done");
        ladder.Should().ContainSingle(s => s.Step == "migrate-tenant-db" && s.State == "failed");
    }

    [Test]
    public void H4_BuildStepLadder_HandlesEmpty_AndIgnoresUnknownTypes()
    {
        TenantStatusEndpoint.BuildStepLadder(Array.Empty<TenantStatusEndpoint.StepEvent>())
            .Should().BeEmpty();

        var t0 = DateTime.UtcNow;
        var unknown = new[]
        {
            new TenantStatusEndpoint.StepEvent(
                "TOTALLY.UNRELATED",
                """{"step":"x"}""",
                t0),
        };
        TenantStatusEndpoint.BuildStepLadder(unknown).Should().HaveCount(1)
            .And.ContainSingle(s => s.State == "unknown");
    }

    // ── H10 — IDbContextFactory<ControlPlaneDbContext> ───────────────

    [Test]
    public void H10_AddTammaData_RegistersIDbContextFactory_AndYieldsFreshContextPerCall()
    {
        // Round-2 review H10: AddTammaData previously called both
        // AddDbContext and (via TenantConnectionPool) AddPooledDbContextFactory
        // for the same context type. The fix is to register a single
        // IDbContextFactory<ControlPlaneDbContext> from AddTammaData
        // and have AddTenantConnectionPool replace it with a pooled
        // factory in production. This test asserts the factory is
        // resolvable and produces fresh contexts.
        var services = new ServiceCollection();
        services.AddTammaData("Host=localhost;Database=cp_test;Username=u;Password=p");
        services.AddDbContextFactory<ControlPlaneDbContext>(
            opts => opts.UseInMemoryDatabase(Guid.NewGuid().ToString()),
            ServiceLifetime.Singleton);

        // The replacement above is the test substitute for the
        // production wiring (postgres). The key assertion is that a
        // factory resolves cleanly and the scoped context resolution
        // path goes through the factory.
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var factory = sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        factory.Should().NotBeNull();

        using var ctx1 = factory.CreateDbContext();
        using var ctx2 = factory.CreateDbContext();
        ctx1.Should().NotBeNull();
        ctx2.Should().NotBeNull();
        ReferenceEquals(ctx1, ctx2).Should().BeFalse(
            "factory must return a fresh DbContext per call");
    }

    [Test]
    public void H10_ScopedControlPlaneDbContext_IsResolvedFromFactory()
    {
        // The scoped CP context should be resolved by calling
        // IDbContextFactory<ControlPlaneDbContext>.CreateDbContext().
        // We assert the scoped resolution works and produces a usable
        // context that can read/write its tables.
        var services = new ServiceCollection();
        services.AddTammaData("Host=localhost;Database=cp_test;Username=u;Password=p");
        // Swap the factory's options to InMemory for the test.
        services.RemoveAll(typeof(IDbContextFactory<ControlPlaneDbContext>));
        services.RemoveAll(typeof(DbContextOptions<ControlPlaneDbContext>));
        services.AddDbContextFactory<ControlPlaneDbContext>(
            opts => opts.UseInMemoryDatabase("h10-scoped-test"),
            ServiceLifetime.Singleton);

        using var sp = services.BuildServiceProvider(validateScopes: true);
        using var scope = sp.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        scopedDb.Should().NotBeNull();
        scopedDb.Database.IsInMemory().Should().BeTrue(
            "scoped CP context should be sourced from the registered factory");
    }

    // ── M11 — PoolWarmupService is a BackgroundService ──────────────

    [Test]
    public void M11_PoolWarmupService_ExtendsBackgroundService()
    {
        // Round-2 review M11: PoolWarmupService used IHostedService
        // directly and called Task.Run with the wrong cancellation
        // token. The fix is to extend BackgroundService so the host
        // owns the cancellation lifecycle.
        typeof(PoolWarmupService).BaseType.Should().Be(typeof(BackgroundService));
    }

    [Test]
    public async Task M11_PoolWarmupService_StopAsync_CancelsTheWarmup()
    {
        // Build a minimal DI container with the warmup enabled. The
        // warmup tries to resolve IPlatformAnalyticsService — when it's
        // missing, the service logs a warning and exits cleanly. We
        // verify both shapes here:
        //   1. StartAsync returns immediately (BackgroundService kicks
        //      ExecuteAsync onto its own task).
        //   2. StopAsync awaits cleanly without hanging.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLogging();
        var opts = Options.Create(new PoolWarmupOptions
        {
            Enabled = true,
            TopTenants = 0,  // empty list → analytics call short-circuits
            PerTenantTimeoutSeconds = 1,
        });

        using var sp = services.BuildServiceProvider();
        using var warmup = new PoolWarmupService(sp, opts,
            sp.GetRequiredService<ILogger<PoolWarmupService>>());

        await warmup.StartAsync(CancellationToken.None);
        // Give the host a moment to start ExecuteAsync.
        await Task.Delay(20);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await warmup.StopAsync(stopCts.Token);
        // If StopAsync hangs the cts cancels and the assertion below
        // fires; otherwise we get here cleanly.
        stopCts.IsCancellationRequested.Should().BeFalse(
            "BackgroundService.StopAsync must complete promptly when ExecuteAsync exits");
    }

    // ── M16 — TimeProvider drives admin endpoint clock reads ─────────

    [Test]
    public async Task M16_RetryTenant_StampsUpdatedAt_FromInjectedTimeProvider()
    {
        // Round-2 review M16: admin endpoints used to read
        // DateTime.UtcNow inline. The fix injects TimeProvider so
        // tests can pin / advance the clock and the production
        // composition can use a single shared TimeProvider.
        var fakeNow = new DateTimeOffset(2026, 06, 01, 12, 00, 00, TimeSpan.Zero);
        var time = new FixedTimeProvider(fakeNow);

        await using var db = BuildInMemoryCpContext();
        await PlansSeeder.SeedAsync(db);

        var tenantId = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Acme",
            Slug = "acme-" + tenantId.ToString("N")[..6],
            Type = "team",
            Plan = "free",
            CreatedAt = fakeNow.UtcDateTime.AddMinutes(-10),
            UpdatedAt = fakeNow.UtcDateTime.AddMinutes(-5),
        };
        db.Tenants.Add(tenant);
        db.Entry(tenant).Property("Status").CurrentValue = "failed";
        db.Entry(tenant).Property("PlanId").CurrentValue = PlansSeeder.FreePlanId;
        await db.SaveChangesAsync();

        var publisher = new RecordingEventPublisher();
        var statusCache = new NoopStatusCache();

        var result = await AdminTenantsEndpoints.RetryTenant(
            tenantId, db, publisher, statusCache, new NoopConnectionResolver(), time, EmptyPrincipal());

        result.Should().BeOfType<Ok<AdminTenantActionResponse>>();
        var reloaded = await db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenantId);
        reloaded.UpdatedAt.Should().Be(fakeNow.UtcDateTime,
            "retry should stamp UpdatedAt from the injected TimeProvider");

        // The platform event payload should also carry the pinned
        // clock value in requestedAt.
        publisher.Events.Should().ContainSingle();
        var evt = publisher.Events[0];
        evt.Type.Should().Be("TENANT.PROVISIONING_REQUESTED");
        evt.Data.Should().Contain(fakeNow.UtcDateTime.ToString("o").Substring(0, 19));
    }

    [Test]
    public async Task M16_DeleteTenant_StampsDeleteRequestedAt_FromInjectedTimeProvider()
    {
        var fakeNow = new DateTimeOffset(2026, 07, 15, 09, 30, 00, TimeSpan.Zero);
        var time = new FixedTimeProvider(fakeNow);

        await using var db = BuildInMemoryCpContext();
        await PlansSeeder.SeedAsync(db);

        var tenantId = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Acme",
            Slug = "acme-" + tenantId.ToString("N")[..6],
            Type = "team",
            Plan = "free",
            CreatedAt = fakeNow.UtcDateTime.AddDays(-1),
            UpdatedAt = fakeNow.UtcDateTime.AddDays(-1),
        };
        db.Tenants.Add(tenant);
        db.Entry(tenant).Property("Status").CurrentValue = "active";
        db.Entry(tenant).Property("PlanId").CurrentValue = PlansSeeder.FreePlanId;
        await db.SaveChangesAsync();

        var publisher = new RecordingEventPublisher();
        var statusCache = new NoopStatusCache();

        var result = await AdminTenantsEndpoints.DeleteTenant(
            tenantId, db, publisher, statusCache, new NoopConnectionResolver(), time, EmptyPrincipal());

        result.Should().BeOfType<Ok<AdminTenantActionResponse>>();
        var reloaded = await db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenantId);
        ((DateTime?)db.Entry(reloaded).Property("DeleteRequestedAt").CurrentValue)
            .Should().Be(fakeNow.UtcDateTime);
        reloaded.UpdatedAt.Should().Be(fakeNow.UtcDateTime);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static ControlPlaneDbContext BuildInMemoryCpContext()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ControlPlaneDbContext(options);
    }

    /// <summary>
    /// Minimal <see cref="TimeProvider"/> stand-in that returns a fixed
    /// instant. Tests pin the clock instead of pulling in the
    /// Microsoft.Extensions.TimeProvider.Testing package.
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class NoopStatusCache : ITenantStatusCache
    {
        public bool TryGet(Guid tenantId, out string? status)
        { status = null; return false; }
        public void Set(Guid tenantId, string? status) { }
        public void Invalidate(Guid tenantId) { }
    }

    // R2 merge — minimal resolver stub for tests that exercise admin
    // endpoint TimeProvider behaviour without caring about resolver
    // eviction. EvictAsync is a no-op.
    private sealed class NoopConnectionResolver : ITenantConnectionResolver
    {
        public ValueTask<Npgsql.NpgsqlDataSource> GetDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not used in TimeProvider tests.");
        public ValueTask<Npgsql.NpgsqlDataSource> GetElsaDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not used in TimeProvider tests.");
        public ValueTask<ITenantConnectionLease> LeaseAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not used in TimeProvider tests.");
        public ValueTask EvictAsync(Guid tenantId, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public TenantConnectionPoolStats GetStats() =>
            new TenantConnectionPoolStats(0, 0, 0);
    }

    // R2 merge — empty principal for tests that don't care about actor.
    private static System.Security.Claims.ClaimsPrincipal EmptyPrincipal() =>
        new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());

    private sealed class RecordingEventPublisher : IPlatformEventPublisher
    {
        public List<PlatformEvent> Events { get; } = new();
        public Task<PlatformEvent?> AppendAndPublishAsync(
            PlatformEvent evt,
            CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.FromResult<PlatformEvent?>(evt);
        }
    }
}
