using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Api.Services.Secrets.Query;
using Tamma.Api.Services.Secrets.Stopgap;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI registration for the platform secret cabinet
/// (Epic 29). The base <see cref="AddTammaSecrets"/> ships the
/// Story 29-1 placeholders; <see cref="AddTammaPostgresSecrets"/>
/// (Story 29-2) layers the real Postgres-backed envelope-encrypted
/// driver on top.
///
/// <list type="bullet">
///   <item><description><see cref="ISecretAccessAuditor"/> backed by
///     the no-op <see cref="NullSecretAccessAuditor"/> by default.
///     A future story replaces this with a Postgres-backed real
///     auditor that writes <c>SECRET.*</c> events to
///     <c>platform_events</c> / <c>domain_events</c>.</description></item>
///   <item><description><see cref="ISecretStoreBackend"/> backed by
///     <see cref="InMemorySecretStoreBackend"/> when only
///     <see cref="AddTammaSecrets"/> is called; backed by
///     <see cref="PostgresSecretStoreBackend"/> when
///     <see cref="AddTammaPostgresSecrets"/> runs (the Postgres
///     extension calls <see cref="ServiceCollectionDescriptorExtensions.RemoveAll"/>
///     before its own AddSingleton so the order of the two calls
///     does not matter).</description></item>
/// </list>
///
/// <para>Intentionally not merged into <c>AddTammaData</c> — Epic 28
/// owns that surface; Story 29-1 / 29-2's hard scope rule keeps the
/// secrets wiring in a separate extension method so the two epics
/// can be reviewed and rolled forward independently.</para>
///
/// <para>Idempotent via TryAdd*; safe to call from tests + production.</para>
/// </summary>
public static class SecretsServiceCollectionExtensions
{
    /// <summary>
    /// Register the Story 29-1 secret-cabinet abstraction. See class
    /// docs for the registration list.
    /// </summary>
    public static IServiceCollection AddTammaSecrets(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Audit pipe — null until a future story wires the real one.
        // Singleton because the auditor is stateless and every
        // backend call emits one event so per-request scope churn
        // would be wasteful.
        services.TryAddSingleton<ISecretAccessAuditor, NullSecretAccessAuditor>();

        // Backend driver — in-memory placeholder. The Postgres backend
        // (Story 29-2) is opted-in via AddTammaPostgresSecrets which
        // calls RemoveAll + AddSingleton so the explicit Postgres
        // wiring wins over this fallback regardless of call order.
        services.TryAddSingleton<ISecretStoreBackend, InMemorySecretStoreBackend>();

        // Shared infrastructure that the query service (registered by
        // AddTammaPostgresSecrets when the DbContext factory is ready)
        // needs. Placed here so callers of bare AddTammaSecrets still
        // see a TimeProvider — the existing reveal service registration
        // also relied on one.
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// Story 29-2: register the Postgres-backed
    /// envelope-encrypted secret store. Wires:
    /// <list type="bullet">
    ///   <item><description><see cref="IKekProvider"/> →
    ///     <see cref="EnvKekProvider"/> sourced from
    ///     <c>TAMMA_SECRET_STORE_KEK_PRIMARY</c> /
    ///     <c>_SECONDARY</c> env vars (singleton).</description></item>
    ///   <item><description><see cref="IDbContextFactory{TContext}"/>
    ///     for <see cref="SecretsDbContext"/> pointing at the
    ///     supplied connection string. Migration history table:
    ///     <c>__SecretStoreMigrationsHistory</c> so the secrets
    ///     schema rolls forward independently of Epic 28's
    ///     <c>__ControlPlaneMigrationsHistory</c> /
    ///     <c>__TenantMigrationsHistory</c>.</description></item>
    ///   <item><description><see cref="ISecretStoreBackend"/> →
    ///     <see cref="PostgresSecretStoreBackend"/> as a singleton
    ///     (overrides the in-memory placeholder from
    ///     <see cref="AddTammaSecrets"/>).</description></item>
    /// </list>
    ///
    /// <para><b>KEK env-var contract</b>: see
    /// <see cref="EnvKekProvider"/>. Format is
    /// <c>kekId:base64(32-byte-key)</c>; the slot id is a single
    /// byte so a future <c>RewrapAllAsync</c> pass can filter by
    /// slot in O(rows) without trial-decrypting every envelope.</para>
    ///
    /// <para>Connection-string resolution:
    /// <list type="number">
    ///   <item><description>Explicit
    ///     <paramref name="connectionString"/> parameter wins.</description></item>
    ///   <item><description>Falls back to
    ///     <c>ConnectionStrings:SecretStore</c> from
    ///     <see cref="IConfiguration"/>.</description></item>
    ///   <item><description>Final fallback to
    ///     <c>ConnectionStrings:ControlPlane</c> so dev / local-laptop
    ///     setups don't need an extra env var (the secrets schema
    ///     coexists with the control-plane schema in the same DB by
    ///     virtue of separate migration history tables).</description></item>
    /// </list></para>
    ///
    /// <para>Per-tenant secrets: this registration covers the
    /// platform-scoped store. Tenant-scoped secrets share the same
    /// schema but live on each tenant's database; a follow-up wiring
    /// step (driven by <see cref="Tamma.Data.Abstractions.ITenantConnectionResolver"/>
    /// from Story 28-4) will register a <em>per-tenant</em>
    /// <see cref="SecretsDbContext"/> factory keyed by tenant id.
    /// Out of scope for the 29-2 PR — the Postgres backend ships
    /// here for the platform half; the per-tenant routing is left
    /// to the rotation-handler stories (29-6, 29-7) that need
    /// it.</para>
    ///
    /// <para>The <c>IKekProvider</c> registration uses
    /// <see cref="EnvKekProvider.FromEnvironment"/> which throws on
    /// startup if <c>TAMMA_SECRET_STORE_KEK_PRIMARY</c> is missing —
    /// fail-fast so a misconfigured host does not silently fall
    /// through to runtime failures on first secret read/write.</para>
    /// </summary>
    public static IServiceCollection AddTammaPostgresSecrets(
        this IServiceCollection services,
        IConfiguration configuration,
        string? connectionString = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Make sure the Story 29-1 placeholders are registered first
        // (TryAdd-based) so the Postgres overrides are deterministic.
        services.AddTammaSecrets();

        // KEK provider: env-var sourced. Singleton because the keys
        // are immutable post-startup and the provider is fully
        // thread-safe. Throws on construction when the primary env
        // var is missing — fail-fast on misconfigured hosts.
        services.TryAddSingleton<IKekProvider>(_ => EnvKekProvider.FromEnvironment());

        // Resolve the connection string per the doc'd order:
        // explicit arg → ConnectionStrings:SecretStore → ConnectionStrings:ControlPlane.
        var resolvedConnectionString = connectionString
            ?? configuration.GetConnectionString("SecretStore")
            ?? configuration.GetConnectionString("ControlPlane")
            ?? throw new InvalidOperationException(
                "AddTammaPostgresSecrets: no connection string supplied and " +
                "neither ConnectionStrings:SecretStore nor " +
                "ConnectionStrings:ControlPlane is configured.");

        // DbContextFactory: singleton lifetime so the backend
        // (singleton) can ask for a fresh short-lived context per
        // call without depending on a request scope. Migration
        // history table is __SecretStoreMigrationsHistory so the
        // schema rolls forward independently of the Epic 28 CP +
        // tenant histories.
        services.AddDbContextFactory<SecretsDbContext>(options =>
        {
            options.UseNpgsql(resolvedConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__SecretStoreMigrationsHistory"));
        });

        // Replace the in-memory backend with the Postgres one.
        // RemoveAll + AddSingleton so we win regardless of the call
        // order relative to AddTammaSecrets.
        services.RemoveAll<ISecretStoreBackend>();
        services.AddSingleton<ISecretStoreBackend, PostgresSecretStoreBackend>();

        // Story 29-4 / 29-5 query + retire surface. Registered here
        // because it depends on the SecretsDbContext factory above;
        // the bare AddTammaSecrets path has no Postgres so it cannot
        // construct this. Scoped to match EF context lifecycles.
        services.TryAddScoped<ISecretQueryService, SecretQueryService>();

        // Story 29-1 concrete ISecretStore facade. Depends on the
        // SecretsDbContext factory (above), the ISecretStoreBackend +
        // ISecretAccessAuditor (from AddTammaSecrets), and TimeProvider.
        // Scoped to match the EF context lifecycle. Only wired on the
        // Postgres path — the bare AddTammaSecrets placeholder has no
        // DbContext factory to back the facade's metadata surface.
        services.TryAddScoped<ISecretStore, SecretStore>();

        return services;
    }

    /// <summary>
    /// Epic 29 (review fix) — production-safe secret-cabinet backend
    /// selection. Extracted from <c>Program.cs</c> so the decision matrix is
    /// unit-testable (the raw <c>GetConnectionString</c> guard it replaces
    /// was a no-op on the VPS, where both <c>SecretStore</c> and
    /// <c>ControlPlane</c> ship as empty strings and the CP DbContext only
    /// works via an admin-connection fallback — so a Production host
    /// silently used the volatile in-memory backend for real secrets).
    ///
    /// <para>Decision matrix (caller passes the ALREADY-RESOLVED connection
    /// from <see cref="Tamma.Api.Infrastructure.ConnectionStringResolver.ResolveSecretStore"/>,
    /// which mirrors the CP DbContext's admin fallback):</para>
    /// <list type="bullet">
    ///   <item><description>KEK present AND a resolvable connection →
    ///     <see cref="AddTammaPostgresSecrets"/> (persistent envelope backend
    ///     + <c>IKekProvider</c> + the <see cref="ISecretStore"/> facade);
    ///     returns <see cref="SecretCabinetBackend.PersistentPostgres"/>.</description></item>
    ///   <item><description>Production AND (KEK absent OR no resolvable
    ///     persistent backend) → <see cref="FailClosedSecretStoreBackend"/>:
    ///     every secret WRITE throws loudly; startup stays up. NEVER falls to
    ///     volatile in-memory for real secrets. Returns
    ///     <see cref="SecretCabinetBackend.FailClosed"/>.</description></item>
    ///   <item><description>Non-Production (dev/test) → the volatile
    ///     in-memory placeholder is fine; returns
    ///     <see cref="SecretCabinetBackend.VolatileInMemory"/>.</description></item>
    /// </list>
    ///
    /// <para>Returns the selected backend so the caller can emit the
    /// appropriate startup log without re-deriving the decision.</para>
    /// </summary>
    public static SecretCabinetBackend AddTammaSecretCabinet(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isProduction,
        bool kekConfigured,
        string? resolvedConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var hasBackendConnection = !string.IsNullOrWhiteSpace(resolvedConnectionString);

        if (kekConfigured && hasBackendConnection)
        {
            // Pass the resolved connection explicitly — do NOT let the
            // extension re-read the raw (possibly empty-string) config keys.
            services.AddTammaPostgresSecrets(configuration, resolvedConnectionString);
            return SecretCabinetBackend.PersistentPostgres;
        }

        // Base placeholders (in-memory backend + auditor + TimeProvider).
        services.AddTammaSecrets();

        if (isProduction)
        {
            // Production without a persistent backend (KEK absent OR no
            // resolvable connection): NEVER silently persist real secrets to
            // volatile memory. Swap the placeholder for the fail-closed guard
            // so every WRITE throws loudly; startup stays up so the operator
            // sees the cause at the first write, not a crash loop.
            services.RemoveAll<ISecretStoreBackend>();
            services.AddSingleton<ISecretStoreBackend, FailClosedSecretStoreBackend>();
            return SecretCabinetBackend.FailClosed;
        }

        // Non-production dev/test — the volatile in-memory backend is fine.
        return SecretCabinetBackend.VolatileInMemory;
    }

    /// <summary>
    /// Story 29-9 / 29-10: register the stopgap migrator +
    /// <see cref="IRuntimeSecretResolver"/>. Requires
    /// <see cref="AddTammaSecrets"/> (and, in production,
    /// <see cref="AddTammaPostgresSecrets"/>) to have wired the
    /// <see cref="ISecretStoreBackend"/> + <see cref="SecretsDbContext"/>
    /// factory.
    ///
    /// <para><paramref name="allowEnvFallback"/> toggles the Story 29-9
    /// coexistence behaviour — <c>true</c> keeps the env-var / config
    /// fallback path alive (default during the grace window);
    /// <c>false</c> flips the resolver to the Story 29-10 fail-fast
    /// mode where a missing cabinet row throws
    /// <see cref="MissingSecretException"/>.</para>
    ///
    /// <para>Idempotent via TryAdd*. Safe to call from tests +
    /// production.</para>
    /// </summary>
    public static IServiceCollection AddTammaSecretStopgapMigrator(
        this IServiceCollection services,
        bool allowEnvFallback = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The migrator + resolver both depend on the secrets DB +
        // backend, so make sure the base registrations are present.
        services.AddTammaSecrets();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IStopgapSecretMigrator, StopgapSecretMigrator>();
        services.TryAddSingleton<IRuntimeSecretResolver>(sp =>
            new RuntimeSecretResolver(
                sp.GetRequiredService<IDbContextFactory<SecretsDbContext>>(),
                sp.GetRequiredService<ISecretStoreBackend>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<
                    Microsoft.Extensions.Logging.ILogger<RuntimeSecretResolver>>(),
                sp.GetRequiredService<TimeProvider>(),
                allowEnvFallback));

        return services;
    }
}

/// <summary>
/// Which secret-store backend <see cref="SecretsServiceCollectionExtensions.AddTammaSecretCabinet"/>
/// wired, so the caller can emit the matching startup log.
/// </summary>
public enum SecretCabinetBackend
{
    /// <summary>Persistent Postgres envelope-encrypted backend (KEK + connection).</summary>
    PersistentPostgres,

    /// <summary>
    /// Production fail-closed guard: writes throw loudly; real secrets are
    /// never silently persisted to volatile memory.
    /// </summary>
    FailClosed,

    /// <summary>Volatile in-memory placeholder — dev/test only.</summary>
    VolatileInMemory,
}
