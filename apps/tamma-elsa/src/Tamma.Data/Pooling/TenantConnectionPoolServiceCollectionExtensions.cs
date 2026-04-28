using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// Story 28-4 — DI extension that wires the production
/// <see cref="LruPooledTenantConnectionResolver"/> over the
/// <see cref="StubTenantConnectionResolver"/> registered by
/// <c>AddTammaData</c>. Call this AFTER
/// <c>builder.Services.AddTammaData(...)</c> so the explicit
/// <see cref="ServiceLifetime.Singleton"/> registration here wins over
/// the stub's <c>TryAddSingleton</c>.
///
/// <para><b>What this does</b>:
/// <list type="number">
///   <item><description>Binds <see cref="TenantConnectionPoolOptions"/>
///     from the <c>TenantConnectionPool</c> config section.</description></item>
///   <item><description>Registers the pool metrics singleton (used by
///     OpenTelemetry exporters + the admin diagnostics endpoint).</description></item>
///   <item><description>Adds an <see cref="IDbContextFactory{TContext}"/>
///     for <c>ControlPlaneDbContext</c> so the resolver (a singleton)
///     can read CP rows on cold-miss without depending on a scoped
///     DbContext.</description></item>
///   <item><description>Replaces the
///     <see cref="ITenantConnectionResolver"/> registration with the
///     production LRU resolver. The resolver implements
///     <see cref="IAdminPoolDiagnostics"/> too — same instance is
///     forwarded to that service id so admin endpoints can resolve
///     either interface.</description></item>
/// </list>
/// </para>
///
/// <para><b>Why a separate extension</b>: <c>AddTammaData</c> runs in
/// every test fixture (including ones that don't want a real Postgres
/// pool). Keeping this opt-in via a separate call lets unit-test
/// fixtures stick with the stub while the production composition root
/// (<c>Program.cs</c>) opts into the LRU resolver.</para>
/// </summary>
public static class TenantConnectionPoolServiceCollectionExtensions
{
    /// <summary>
    /// Register the production tenant-connection LRU resolver +
    /// supporting infrastructure. Idempotent — calling twice in the
    /// same DI container is safe (the second call replaces the first
    /// resolver registration cleanly).
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="configuration">Root configuration. Reads
    /// <c>TenantConnectionPool</c> section.</param>
    /// <param name="controlPlaneConnectionString">Connection string
    /// for the control-plane database. Used to wire the
    /// <c>ControlPlaneDbContext</c> factory.</param>
    public static IServiceCollection AddTenantConnectionPool(
        this IServiceCollection services,
        IConfiguration configuration,
        string controlPlaneConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(controlPlaneConnectionString))
            throw new ArgumentException(
                "AddTenantConnectionPool requires a non-empty control-plane " +
                "connection string. The resolver reads tenant rows from CP " +
                "to look up encrypted connection strings on cold-miss.",
                nameof(controlPlaneConnectionString));

        // Bind options from the TenantConnectionPool section. Defaults
        // (MaxEntries=500, MaxPoolSize=5) come from the options class —
        // the section can be absent in dev without crashing. Uses
        // Microsoft.Extensions.Configuration.Binder (referenced in
        // Tamma.Data.csproj) instead of pulling in the heavier
        // Options.ConfigurationExtensions dependency.
        services.AddOptions<TenantConnectionPoolOptions>()
            .Configure(opts =>
                configuration
                    .GetSection(TenantConnectionPoolOptions.SectionName)
                    .Bind(opts));

        services.TryAddSingleton<TenantConnectionPoolMetrics>();

        // The resolver needs an IDbContextFactory<ControlPlaneDbContext>
        // because it's a singleton and must not capture the scoped
        // DbContext registered by AddTammaData. Pooled factory caches
        // recent contexts so cold-miss CP lookups don't pay
        // construction cost on every request.
        //
        // Round-2 review H10: AddTammaData registers a plain
        // <c>AddDbContextFactory&lt;ControlPlaneDbContext&gt;</c> as the
        // default. Strip the factory + options registrations before
        // <c>AddPooledDbContextFactory</c> wires the pooled variant so
        // there's exactly one factory + options pipeline registered for
        // <c>ControlPlaneDbContext</c>. Without the cleanup the second
        // <c>AddPooledDbContextFactory</c> call layers another
        // <c>IDbContextFactory&lt;ControlPlaneDbContext&gt;</c>
        // descriptor on top of the existing one and both options
        // pipelines run on construction.
        services.RemoveAll<IDbContextFactory<ControlPlaneDbContext>>();
        services.RemoveAll<DbContextOptions<ControlPlaneDbContext>>();
        services.AddPooledDbContextFactory<ControlPlaneDbContext>(opts =>
        {
            opts.UseNpgsql(controlPlaneConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"));
        });

        // Replace the stub. Use Replace+Singleton (not TryAdd) so this
        // call deterministically wins over AddTammaData's
        // TryAddSingleton fallback regardless of registration order.
        services.RemoveAll<ITenantConnectionResolver>();
        services.AddSingleton<LruPooledTenantConnectionResolver>();
        services.AddSingleton<ITenantConnectionResolver>(
            sp => sp.GetRequiredService<LruPooledTenantConnectionResolver>());
        services.AddSingleton<IAdminPoolDiagnostics>(
            sp => sp.GetRequiredService<LruPooledTenantConnectionResolver>());

        // Round-2 follow-up — register the cluster-wide tenant-status
        // invalidation bus over the CP <c>NpgsqlDataSource</c>. The bus
        // publishes pg_notify on every Invalidate call from the admin
        // endpoints; the matching listener (registered via
        // <c>AddTenantStatusInvalidation</c>) opens a long-lived LISTEN
        // connection from the same data source and dispatches into the
        // local cache + resolver.
        services.AddTenantStatusInvalidation(controlPlaneConnectionString);

        return services;
    }

    /// <summary>
    /// Round-2 follow-up — register the Postgres-backed tenant-status
    /// invalidation bus and a singleton CP <see cref="NpgsqlDataSource"/>
    /// for it to share with the listener
    /// (<c>TenantStatusInvalidationListener</c>, registered separately
    /// in <c>Tamma.Api</c>'s composition root).
    ///
    /// <para>Idempotent — calling twice in the same DI container is
    /// safe; <c>TryAddSingleton</c> for the bus + data source guards
    /// against duplicate registrations. When the connection string is
    /// missing/empty, the <see cref="NullTenantStatusInvalidationBus"/>
    /// wins (no-op publish, no listener wired).</para>
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="controlPlaneConnectionString">CP connection string
    /// used for both publish and listen connections. Pass <c>null</c>
    /// or empty to fall through to the Null seam.</param>
    public static IServiceCollection AddTenantStatusInvalidation(
        this IServiceCollection services,
        string? controlPlaneConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(controlPlaneConnectionString))
        {
            // No CP connection string: tests / single-pod dev. Register
            // the no-op bus (idempotent — TryAdd lets a previous
            // explicit registration win).
            services.TryAddSingleton<ITenantStatusInvalidationBus, NullTenantStatusInvalidationBus>();
            return services;
        }

        // Build a singleton NpgsqlDataSource scoped to the bus + listener.
        // We could share an existing CP data source if one were
        // registered globally, but Tamma's CP plumbing today is
        // EF-DbContext-only (no DI-resolvable NpgsqlDataSource). Keep
        // this localised: the bus + listener share one source built
        // from the same connection string the EF factory uses.
        services.TryAddSingleton(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(controlPlaneConnectionString);
            if (loggerFactory is not null)
                dataSourceBuilder.UseLoggerFactory(loggerFactory);
            return dataSourceBuilder.Build();
        });

        services.RemoveAll<ITenantStatusInvalidationBus>();
        services.AddSingleton<ITenantStatusInvalidationBus, PostgresTenantStatusInvalidationBus>();

        return services;
    }
}
