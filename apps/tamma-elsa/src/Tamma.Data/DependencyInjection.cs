using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Data.Abstractions;
using Tamma.Data.Repositories;

namespace Tamma.Data;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Epic 28 isolation model:
    /// <list type="bullet">
    ///   <item><description><see cref="ControlPlaneDbContext"/> — owns the
    ///     CP tables (users, tenants, memberships, invites, API keys,
    ///     GitHub installations, auth tokens, mentorship schema).
    ///     Registered scoped; migrations run against this context.
    ///     </description></item>
    ///   <item><description><see cref="ITenantDbContextFactory"/> — creates
    ///     a <see cref="TenantDbContext"/> per tenant per call. Every
    ///     tenant-scoped repository depends on this factory and passes the
    ///     tenant id explicitly (or resolves it from
    ///     <see cref="ITenantContext"/> for per-request scopes).
    ///     </description></item>
    /// </list>
    ///
    /// <para>Connection strings:</para>
    /// <list type="bullet">
    ///   <item><description><c>ConnectionStrings:TammaDb</c> — admin / CP
    ///     connection. Falls back to <c>DefaultConnection</c> for dev.
    ///     </description></item>
    ///   <item><description><c>ConnectionStrings:TammaAppDb</c> — central
    ///     app connection. Falls back to the admin connection with a warning.
    ///     Unified-tenancy Phase 3: used ONLY by the system store
    ///     (<see cref="ISystemStoreDbContextFactory"/>); tenant data always
    ///     goes through <see cref="ITenantConnectionResolver"/>.</description></item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddTammaData(
        this IServiceCollection services,
        string adminConnectionString,
        string? appConnectionString = null,
        string? controlPlaneConnectionString = null)
    {
        services.AddScoped<ITenantContext, TenantContext>();

        // Control-plane context (migrations-owning). The factory is the
        // canonical seam — every scoped <see cref="ControlPlaneDbContext"/>
        // is created from <see cref="IDbContextFactory{TContext}"/>.
        //
        // Two callers can wire this:
        //
        // <list type="bullet">
        //   <item><description>The default (here) registers a plain
        //     non-pooled <c>AddDbContextFactory&lt;ControlPlaneDbContext&gt;</c>.
        //     Used in tests, dev, and any composition that hasn't opted
        //     into the per-tenant connection pool.</description></item>
        //   <item><description>Production wires
        //     <c>AddTenantConnectionPool</c> after this method, which
        //     calls <c>RemoveAll</c> on the factory + options
        //     registrations and replaces them with the pooled factory
        //     from <c>AddPooledDbContextFactory</c>.</description></item>
        // </list>
        //
        // The scoped <see cref="ControlPlaneDbContext"/> registration
        // resolves a context from the factory on demand. Consumers
        // (auth handlers, admin endpoints, CP repos) keep the same DI
        // shape — they still take a scoped CP context — but the
        // underlying instance comes from the pool when one is wired.
        // (Round-2 review H10: removed the parallel
        // <c>AddDbContext&lt;ControlPlaneDbContext&gt;</c> registration
        // that conflicted with the pooled factory.)
        services.AddDbContextFactory<ControlPlaneDbContext>(options =>
        {
            options.UseNpgsql(adminConnectionString, npgsql =>
                // Must match ControlPlaneDesignTimeDbContextFactory — one history table
                // for design-time and runtime (unified-tenancy Phase 0 reconciliation).
                npgsql.MigrationsHistoryTable("__ControlPlaneMigrationsHistory"));
            // Story 35-1 follow-up — suppress the required-navigation/query-filter
            // advisory at the options-builder seam (pooling-safe; see
            // ControlPlaneDbContext.ConfigureControlPlaneWarnings).
            ControlPlaneDbContext.ConfigureControlPlaneWarnings(options);
        });
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
                .CreateDbContext());

        // Central connection string (app ?? admin) — retained ONLY for the
        // system store below. Tenant data no longer rides it.
        var tenantConnectionString = string.IsNullOrWhiteSpace(appConnectionString)
            ? adminConnectionString
            : appConnectionString;

        // Unified-tenancy Phase 3 — tenant contexts are resolver-only. The
        // factory asks ITenantConnectionResolver (production:
        // LruPooledTenantConnectionResolver, wired by
        // AddTenantConnectionPool) for the tenant's per-tenant
        // NpgsqlDataSource built from the stored encrypted connection
        // string. There is no shared-connection fallback any more.
        services.AddSingleton<ITenantDbContextFactory>(sp =>
            new TenantDbContextFactory(
                sp.GetRequiredService<ITenantConnectionResolver>()));

        // Unified-tenancy Phase 3 — the SYSTEM STORE seam. Platform-level
        // system-default rows (TenantId IS NULL) live in the CENTRAL database's
        // public-schema tenant tables; services reach them through this factory
        // instead of riding a tenant connection. Deliberately bound to the same
        // central connection string the shared TenantDbContextFactory uses
        // (app ?? admin) — the system store IS the central DB.
        services.AddSingleton<ISystemStoreDbContextFactory>(
            _ => new SystemStoreDbContextFactory(tenantConnectionString));

        // Unified-tenancy Phase 3 — NO fallback ITenantConnectionResolver is
        // registered here. The composition root (Program.cs) wires the
        // LruPooledTenantConnectionResolver unconditionally via
        // AddTenantConnectionPool; test fixtures register their own resolver
        // double. The transitional StubTenantConnectionResolver (every tenant
        // on the shared central DB) was deleted in this phase.

        // Control-plane repositories.
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IPasswordResetRepository, PasswordResetRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddScoped<IInviteRepository, InviteRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        // Story 28-7 deferred-item: CP-scoped routing index for prefix-based
        // API-key auth lookups. Scoped because ControlPlaneDbContext is.
        services.AddScoped<IPlatformApiKeyIndexRepository, PlatformApiKeyIndexRepository>();
        services.AddScoped<IInstallationRepository, InstallationRepository>();
        services.AddScoped<IGitHubWebhookDeliveryRepository, GitHubWebhookDeliveryRepository>();
        // Story 31-2: per-(tenant, platform_kind) installation registry. Scoped
        // because ControlPlaneDbContext is. The installation row carries a
        // SecretRef pointing at Epic 29's secret store; the resolver
        // (registered in Tamma.Api) reads plaintext through ISecretStore +
        // ISecretStoreBackend at resolve time.
        services.AddScoped<
            ITenantPlatformInstallationRepository,
            TenantPlatformInstallationRepository>();
        // PF-S9 — single-row sentinel that pins the bootstrap superadmin.
        // Scoped because it leans on ControlPlaneDbContext.
        services.AddScoped<IPlatformBootstrapRepository, PlatformBootstrapRepository>();

        // Story 32-1 — CP-resident agent identity + versioning repository.
        // Resolves against ControlPlaneDbContext (definitions are CP-resident);
        // distinct from the tenant-scoped IAgentConfigRepository below.
        services.AddScoped<IAgentRepository, AgentRepository>();

        // Story 32-2 — role→agent selections. Dual-scoped (CP for single-user;
        // tenant schema for SaaS), routed internally by the ambient principal.
        services.AddScoped<IAgentSelectionRepository, AgentSelectionRepository>();

        // Tenant-scoped repositories (use ITenantDbContextFactory internally).
        services.AddScoped<IAgentConfigRepository, AgentConfigRepository>();
        services.AddScoped<IPromptRepository, PromptRepository>();
        services.AddScoped<IConventionRepository, ConventionRepository>();
        services.AddScoped<IProviderHealthRepository, ProviderHealthRepository>();
        services.AddScoped<IDiagnosticsRepository, DiagnosticsRepository>();
        services.AddScoped<ISanitizationRepository, SanitizationRepository>();
        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IBudgetConfigRepository, BudgetConfigRepository>();
        services.AddScoped<IQueuedTaskRepository, QueuedTaskRepository>();
        services.AddScoped<IEmailOutboxRepository, EmailOutboxRepository>();

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
        // Story 38-3 — control-plane Slack notification outbox repository.
        services.TryAddScoped<ISlackOutboxRepository, SlackOutboxRepository>();

        return services;
    }
}
