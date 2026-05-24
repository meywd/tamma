using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Conventions;

/// <summary>
/// Options for <see cref="ConventionStoreSeeder"/>.
/// </summary>
public sealed class ConventionStoreSeederOptions
{
    /// <summary>
    /// When <c>true</c> (default) the seeder runs in <c>StartAsync</c> during
    /// host bootstrap. Tests that don't need the system-default convention rows
    /// override this to <c>false</c> to skip the per-factory DB round-trip
    /// (mirrors <see cref="Tamma.Api.Services.Alerts.Rules.BuiltInAlertRuleSeederOptions"/>).
    /// The seeder method <see cref="ConventionStoreSeeder.SeedAsync(CancellationToken)"/>
    /// is still callable directly for tests that opt back in.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;
}

/// <summary>
/// Story 27-16 (Wave B) — seeds the <c>conventions</c> system-default rows
/// (<c>tenant_id IS NULL</c>) on app startup, one per <c>(role, action)</c> cell
/// of the frozen taxonomy. Runs as an <see cref="IHostedService"/>.
///
/// <para><b>Single source of truth / anti-drift.</b> The <c>(role, action)</c>
/// keyset comes from <see cref="ConventionSeedSpecs.Build"/>, which iterates
/// <c>RolePhaseMap.EligibleActions</c> — the IDENTICAL iteration the prompt
/// registry (<c>SystemPrompts.BuildRoleActionTemplates</c>) uses. The two seeds
/// share one source, so they cannot drift; an anti-drift test pins the three
/// keysets (prompt registry, convention seed, taxonomy) set-equal.</para>
///
/// <para><b>DbContext / DB targeting.</b> System-default rows are NOT tenant
/// bound (<c>tenant_id IS NULL</c>). In the transitional shared-DB model every
/// tenant rides one physical DB, so the seeder resolves the shared
/// <c>NpgsqlDataSource</c> via <see cref="ITenantConnectionResolver"/> and
/// builds a tenant-less <see cref="TenantDbContext"/> (the same ad-hoc-context
/// path <c>EfTenantDbMigrator</c> uses). The <c>TenantDbContext</c> applies no
/// global query filter (see <c>TammaModelConfiguration.ApplyTenantFilter</c> —
/// a documented no-op in Wave A.5), so the seeder reads/writes
/// <c>tenant_id IS NULL</c> rows with an explicit predicate.</para>
///
/// <para><b>Per-tenant-DB scope (DEFERRED — Epic 28 cutover).</b> When the
/// db-per-tenant split lands, each tenant's physical DB needs its own copy of
/// the system-default rows, seeded at provision time. That is a provisioning
/// concern (and an OPEN design decision documented on the <c>Convention</c>
/// entity / <c>TammaModelConfiguration</c>), NOT a startup seeder. This seeder
/// implements ONLY the startup / shared-DB path; the provisioning path is a
/// follow-up for the Story 27-9 + provisioning flow.</para>
///
/// <para><b>Idempotency contract</b>:</para>
/// <list type="bullet">
///   <item><description>Re-run = no-op when existing rows match the spec.</description></item>
///   <item><description>Body drift (a release changed a transitional default)
///     triggers a surgical update on <c>Body</c> and bumps <c>Version</c>; it
///     does NOT touch <c>Enabled</c> or admin-set audit fields so overrides /
///     toggles survive re-deploy.</description></item>
///   <item><description>New <c>(role, action)</c> cell → insert a fresh
///     system-default row.</description></item>
///   <item><description>Existing system-default row no longer in the taxonomy →
///     left alone (no silent deletion). A future release retiring a cell should
///     delete it explicitly.</description></item>
/// </list>
/// The <c>NULLS NOT DISTINCT</c> unique index on <c>(TenantId, Role, Action)</c>
/// guarantees exactly one system default per cell at the DB level.
/// </summary>
public sealed class ConventionStoreSeeder : IHostedService
{
    private readonly ITenantConnectionResolver _resolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConventionStoreSeeder> _logger;
    private readonly ConventionStoreSeederOptions _options;

    public ConventionStoreSeeder(
        ITenantConnectionResolver resolver,
        TimeProvider timeProvider,
        ILogger<ConventionStoreSeeder> logger)
        : this(resolver, timeProvider, logger, new ConventionStoreSeederOptions())
    {
    }

    public ConventionStoreSeeder(
        ITenantConnectionResolver resolver,
        TimeProvider timeProvider,
        ILogger<ConventionStoreSeeder> logger,
        ConventionStoreSeederOptions options)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _resolver = resolver;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.RunOnStartup)
        {
            _logger.LogDebug(
                "ConventionStoreSeeder gated off (RunOnStartup=false); skipping startup seed.");
            return;
        }

        try
        {
            await SeedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Don't fail app startup on seed drift — the resolution service
            // (Story 27-9) still works with whatever rows are in the DB. Log
            // loud so CI / prod ops see the drift.
            _logger.LogError(ex,
                "ConventionStoreSeeder failed; continuing startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Resolve the shared tenant data source, build a tenant-less
    /// <see cref="TenantDbContext"/>, and run the upsert. The
    /// <see cref="Guid.Empty"/> tenant id is only used to address the shared
    /// data source — the rows written carry <c>TenantId IS NULL</c>.
    /// </summary>
    public async Task<SeedResult> SeedAsync(CancellationToken ct)
    {
        // Guid.Empty addresses the shared data source in the transitional
        // single-DB model (StubTenantConnectionResolver ignores the id).
        var dataSource = await _resolver
            .GetDataSourceAsync(Guid.Empty, ct)
            .ConfigureAwait(false);

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(dataSource, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;

        await using var db = new TenantDbContext(options);
        return await SeedAsync(db, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Core idempotent upsert against a supplied context — the test seam (no
    /// resolver / DI required). Loads existing system-default rows once
    /// (<c>tenant_id IS NULL</c>), inserts missing cells, and surgically
    /// updates drifted bodies while preserving <c>Enabled</c>.
    /// </summary>
    public async Task<SeedResult> SeedAsync(TenantDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Fetch all existing system-default rows (tenant_id IS NULL) in one
        // round-trip, keyed by (role, action). Tenant overrides (tenant_id
        // NOT NULL) are never touched by the seeder.
        var existing = await db.Conventions
            .Where(c => c.TenantId == default(Guid?))
            .ToDictionaryAsync(c => (c.Role, c.Action), ct)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        int inserted = 0, updated = 0, unchanged = 0;

        foreach (var spec in ConventionSeedSpecs.Build())
        {
            if (existing.TryGetValue((spec.Role, spec.Action), out var row))
            {
                if (row.Body != spec.Body)
                {
                    row.Body = spec.Body;
                    row.Version += 1;
                    row.UpdatedAt = now;
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }
            else
            {
                db.Conventions.Add(new Convention
                {
                    // Set Id client-side so EF InMemory (test shim) doesn't
                    // collide on the Guid.Empty default. Production Postgres
                    // applies gen_random_uuid() anyway — strict superset.
                    Id = Guid.NewGuid(),
                    TenantId = null, // system default
                    Role = spec.Role,
                    Action = spec.Action,
                    Body = spec.Body,
                    Version = 1,
                    Enabled = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                inserted++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Convention system defaults seeded: {Inserted} inserted, " +
            "{Updated} updated, {Unchanged} unchanged.",
            inserted, updated, unchanged);

        return new SeedResult(inserted, updated, unchanged);
    }

    public sealed record SeedResult(int Inserted, int Updated, int Unchanged);
}
