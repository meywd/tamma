using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;
using Tamma.Data.Interceptors;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;

namespace Tamma.Data;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the dual-DbContext architecture introduced in Phase-3:
    /// <list type="bullet">
    ///   <item><description><see cref="TammaDbContext"/> — admin connection.
    ///     Uses the superuser-equivalent runtime role, skips RLS, carries the
    ///     permissive (null-tenant = all rows) query filter shape so
    ///     background services (<c>TaskQueueProcessor</c>,
    ///     <c>OutboxSmtpSender</c>, <c>WorkflowSyncService</c>,
    ///     <c>EngineRegistryHeartbeatService</c>,
    ///     <c>ProviderSessionCleanupService</c>) and migrations keep working
    ///     without manual escape hatches at every call site.</description></item>
    ///   <item><description><see cref="TammaAppDbContext"/> — app connection.
    ///     Connects as the <c>tamma_app</c> role (Phase-2 migration) so RLS
    ///     policies are enforced, the <c>TenantContextInterceptor</c> binds
    ///     <c>app.current_tenant_id</c> on connection open, and the EF query
    ///     filter is fail-closed. This is the context per-request endpoint
    ///     handlers should inject.</description></item>
    /// </list>
    ///
    /// <para>Connection strings:</para>
    /// <list type="bullet">
    ///   <item><description><c>ConnectionStrings:TammaDb</c> —
    ///     admin connection. Falls back to
    ///     <c>ConnectionStrings:DefaultConnection</c> for dev/backward
    ///     compat, then to the single <paramref name="adminConnectionString"/>
    ///     arg if neither is configured.</description></item>
    ///   <item><description><c>ConnectionStrings:TammaAppDb</c> — app
    ///     connection. Falls back to the admin connection string with a
    ///     warning (dev-mode only). Production must set this explicitly.
    ///     </description></item>
    /// </list>
    ///
    /// <para>Closes port-gap findings orgs/002 (EF filter permissive on null
    /// tenant) and orgs/004 (<c>withTenantContext</c> SET LOCAL gone).</para>
    ///
    /// <para><b>Epic 28 (DB-per-Tenant)</b> — this method also registers
    /// <see cref="ControlPlaneDbContext"/> when
    /// <paramref name="controlPlaneConnectionString"/> is supplied or
    /// <c>ConnectionStrings:ControlPlane</c> is set. Until Story 28-2's
    /// endpoint cutover lands, no caller injects this context; it is
    /// available for the new auth/admin handlers shipped in subsequent
    /// stories. Falls back to <paramref name="adminConnectionString"/> for
    /// local-dev convenience.</para>
    /// </summary>
    public static IServiceCollection AddTammaData(
        this IServiceCollection services,
        string adminConnectionString,
        string? appConnectionString = null,
        string? controlPlaneConnectionString = null)
    {
        services.AddScoped<ITenantContext, TenantContext>();

        // Interceptor that runs SELECT set_config('app.current_tenant_id', ...)
        // on connection open so the Phase-2 RLS policies evaluate against the
        // current request's tenant. Registered as scoped because it reads
        // ITenantContext (scoped). EF Core's internal service provider
        // resolves DbContextOptions extensions via the application service
        // provider, so the scoped binding is correctly honored per-request.
        services.AddScoped<TenantContextInterceptor>();

        // Admin / platform-root context. Registered first so migrations run
        // against it at startup (see Program.cs). Uses the admin connection
        // (superuser-equivalent) and therefore bypasses RLS. The EF query
        // filter stays permissive (null tenant → all rows) on this context
        // so background services (TaskQueueProcessor, OutboxSmtpSender) and
        // migrations continue to work unchanged.
        //
        // The interceptor is STILL attached so:
        //   (a) raw-SQL queries that touch current_setting() see the right
        //       tenant when a request scope is active;
        //   (b) when individual repositories migrate to the app-role
        //       context, the binding is already plumbed end-to-end and we
        //       only need to flip the injected context type.
        services.AddDbContext<TammaDbContext>((sp, options) =>
        {
            options.UseNpgsql(adminConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"));
            options.AddInterceptors(sp.GetRequiredService<TenantContextInterceptor>());
        });

        // App-role context. If no dedicated connection string is supplied
        // (e.g. dev laptop hitting a single-role local Postgres) we silently
        // fall back to the admin connection. Production must override this
        // via ConnectionStrings:TammaAppDb. The TenantContextInterceptor is
        // installed so the SET app.current_tenant_id plumbing exercises
        // RLS as soon as the connection is tamma_app.
        services.AddDbContext<TammaAppDbContext>((sp, options) =>
        {
            var cs = string.IsNullOrWhiteSpace(appConnectionString)
                ? adminConnectionString
                : appConnectionString;
            options.UseNpgsql(cs, npgsql =>
            {
                // App-role context does NOT run migrations — the admin
                // context owns the __TammaMigrationsHistory table.
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory");
            });
            options.AddInterceptors(sp.GetRequiredService<TenantContextInterceptor>());
        });

        // ── Epic 28: Control-plane context (Story 28-2) ──
        //
        // Registers <see cref="ControlPlaneDbContext"/> alongside the legacy
        // contexts. Until Story 28-2's endpoint migration lands, no handler
        // injects this context — it exists so the new auth, admin, and
        // tenant-lifecycle stories (28-5, 28-6, 28-7, 28-9, 28-11) can
        // inject it as they ship.
        //
        // Uses its own migrations history table (__ControlPlaneMigrationsHistory)
        // so it can coexist with the legacy <see cref="TammaDbContext"/>
        // without clobbering the existing __TammaMigrationsHistory rows
        // during the migration window. In production, the connection string
        // points at the new <c>tamma_control</c> database; in dev it can
        // fall back to the admin connection so local-laptop Postgres setups
        // need no extra configuration.
        var cpConnectionString = string.IsNullOrWhiteSpace(controlPlaneConnectionString)
            ? adminConnectionString
            : controlPlaneConnectionString;
        // The LRU pooled resolver is a singleton and needs short-lived
        // ControlPlaneDbContext instances on cache miss — register the
        // pooled factory pointing at the same connection so the resolver
        // doesn't depend on a request scope. We register the factory
        // FIRST with optionsLifetime: Singleton so DbContextOptions is
        // singleton-scoped (the factory needs that). The subsequent
        // AddDbContext call uses the same singleton DbContextOptions
        // for the scoped context registration the seeder needs.
        services.AddDbContextFactory<ControlPlaneDbContext>(
            options =>
            {
                options.UseNpgsql(cpConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ControlPlaneMigrationsHistory"));
            },
            lifetime: ServiceLifetime.Singleton);
        services.AddDbContext<ControlPlaneDbContext>(
            options =>
            {
                options.UseNpgsql(cpConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ControlPlaneMigrationsHistory"));
            },
            // contextLifetime stays Scoped — handlers and seeders inject
            // ControlPlaneDbContext per-request. optionsLifetime must be
            // Singleton to coexist with the factory above (DbContextOptions
            // is keyed by closed generic type, so two different lifetimes
            // would collide).
            contextLifetime: ServiceLifetime.Scoped,
            optionsLifetime: ServiceLifetime.Singleton);

        // ── Epic 28: Per-tenant context factory + LRU pool resolver (Story 28-4) ──
        //
        // The factory builds a fresh <see cref="TenantDbContext"/> per call,
        // resolving the per-tenant <see cref="Npgsql.NpgsqlDataSource"/>
        // via the <see cref="ITenantConnectionResolver"/>. Story 28-3
        // shipped a stub resolver that routed every tenant to the central
        // admin connection; Story 28-4 replaces that with
        // <see cref="LruPooledTenantConnectionResolver"/> — a process-wide
        // LRU cache of warm Npgsql data sources keyed by tenant id, sized
        // by <c>TenantConnectionPool:MaxEntries</c> (default 500), with
        // per-tenant pool sizing from <c>TenantConnectionPool:MaxPoolSize</c>
        // (default 5).
        //
        // The decryption seam (<see cref="IConnectionStringDecryptor"/>)
        // defaults to <see cref="PassthroughConnectionStringDecryptor"/>
        // so dev/local runs work without any AES-GCM ceremony. The API
        // composition root is expected to override the binding with an
        // adapter over <c>TenantSecretProtector</c> when production-grade
        // KEK encryption is wired (Story 28-12).
        //
        // Singleton lifetime: the resolver owns long-lived data sources
        // and a process-wide pool cache. Metrics is a singleton too —
        // it owns the OTel <c>Meter</c>.
        // Bind TenantConnectionPool options from the host IConfiguration if
        // available, falling back to compiled defaults if the host doesn't
        // register IConfiguration (unit tests). Using AddOptions + Configure
        // pattern so operators can override via env vars / appsettings.json
        // without touching the AddTammaData call site.
        services.AddOptions<TenantConnectionPoolOptions>()
            .Configure<IServiceProvider>((opts, sp) =>
            {
                var cfg = sp.GetService<IConfiguration>();
                cfg?.GetSection(TenantConnectionPoolOptions.SectionName).Bind(opts);
            });

        services.AddSingleton<TenantConnectionPoolMetrics>();
        // Decryptor registered via TryAdd so the API composition root can
        // pre-register the production AES-GCM-backed implementation
        // before AddTammaData runs and have it win.
        services.TryAddSingleton<IConnectionStringDecryptor, PassthroughConnectionStringDecryptor>();
        services.AddSingleton<ITenantConnectionResolver, LruPooledTenantConnectionResolver>();
        services.AddSingleton<ITenantDbContextFactory, TenantDbContextFactory>();

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IPasswordResetRepository, PasswordResetRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddScoped<IInviteRepository, InviteRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IInstallationRepository, InstallationRepository>();
        services.AddScoped<IGitHubWebhookDeliveryRepository, GitHubWebhookDeliveryRepository>();
        services.AddScoped<IAgentConfigRepository, AgentConfigRepository>();
        services.AddScoped<IPromptRepository, PromptRepository>();
        services.AddScoped<IProviderHealthRepository, ProviderHealthRepository>();
        services.AddScoped<IDiagnosticsRepository, DiagnosticsRepository>();
        services.AddScoped<ISanitizationRepository, SanitizationRepository>();
        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IBudgetConfigRepository, BudgetConfigRepository>();

        // ── Epic 28 Story 28-6: control-plane repositories ──
        //
        // Each platform-* repo is a thin wrapper over ControlPlaneDbContext
        // (registered above by Story 28-2). TryAdd* lets adjacent stories
        // (28-4 owns the tenant connection resolver registration) re-enter
        // this method or replace these in tests without conflict — the
        // first registration wins and tests can pre-stage doubles.
        services.TryAddScoped<IPlatformEventRepository, PlatformEventRepository>();
        services.TryAddScoped<IPlatformQueuedTaskRepository, PlatformQueuedTaskRepository>();
        services.TryAddScoped<IPlatformEmailOutboxRepository, PlatformEmailOutboxRepository>();

        return services;
    }
}
