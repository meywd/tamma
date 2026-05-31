using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
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
    ///   <item><description><c>ConnectionStrings:TammaAppDb</c> — per-tenant
    ///     connection. Falls back to the admin connection with a warning.
    ///     Replaced by per-tenant resolver in Story 28-4.</description></item>
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
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"));
        });
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>()
                .CreateDbContext());

        // Factory for per-tenant contexts. Uses the app connection when
        // provided, else falls back to the admin connection.
        var tenantConnectionString = string.IsNullOrWhiteSpace(appConnectionString)
            ? adminConnectionString
            : appConnectionString;
        services.AddSingleton<ITenantDbContextFactory>(
            _ => new TenantDbContextFactory(tenantConnectionString));

        // Story 28-3 contract: every consumer of per-tenant connection
        // pooling depends on ITenantConnectionResolver, not directly on
        // a connection string. Wave A.5 post-merge restores the stub
        // resolver so KekRotationCoordinator (Story 28-12) and the
        // LRU pool cache (Story 28-4) have an implementation to wire
        // against until the real per-tenant pool resolver replaces it.
        //
        // TryAddSingleton lets a higher-priority composition (e.g. the
        // pool-cache extension once Story 28-4 lands) register its own
        // resolver first without conflicting with this fallback.
        services.TryAddSingleton<ITenantConnectionResolver>(sp =>
        {
            var dataSource = NpgsqlDataSource.Create(tenantConnectionString);
            return new StubTenantConnectionResolver(dataSource);
        });

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

        return services;
    }

    /// <summary>
    /// Story 28-3 AC3 (2026-05-30 residual follow-up) — release-build
    /// hard-fail when a <b>Production</b> deployment is missing
    /// <c>ConnectionStrings:ControlPlane</c>.
    ///
    /// <para><b>The gap this closes</b>: <c>AddTammaData</c> always
    /// registers <see cref="StubTenantConnectionResolver"/> (which routes
    /// every tenant to the single shared central DB). The real
    /// <c>LruPooledTenantConnectionResolver</c> only replaces it — via
    /// <c>AddTenantConnectionPool</c> — when a CP connection string is
    /// present. A Production deployment that forgets the CP string would
    /// therefore silently keep the stub, putting every tenant on the same
    /// database and defeating tenant isolation, with only an Info log to
    /// show for it. This guard converts that silent fallback into a
    /// fail-fast startup exception.</para>
    ///
    /// <para><b>Why this seam</b>: the guard is a pure function of
    /// <paramref name="isProduction"/> + the CP connection string, so it
    /// is fully unit-testable without standing up a host, and the
    /// composition root (<c>Program.cs</c>) calls it with a single line
    /// next to the existing CP-string gating. It lives here (rather than
    /// inline in <c>Program.cs</c>) so a sibling stream editing
    /// <c>Program.cs</c> concurrently does not collide on the logic.</para>
    ///
    /// <para><b>Fires ONLY when isolation is explicitly required</b>
    /// (2026-05-31 revision): shared-infrastructure mode — every tenant on
    /// the central Postgres, isolated via Phase-3 RLS, with
    /// <c>ConnectionStrings:ControlPlane</c> deliberately unset — is the
    /// <b>documented production default</b> (it is what the Hetzner VPS
    /// deploy runs; see <c>docker-compose.prod.yml</c>). The first cut of
    /// this guard fired on ANY Production host without a CP string and so
    /// crash-looped that supported topology. The guard now fires only when
    /// the operator has set <c>Tamma:RequireTenantIsolation=true</c> —
    /// declaring "per-tenant DBs are mandatory here, a missing CP string is
    /// a misconfiguration." Without the opt-in, shared-DB-in-Production is
    /// allowed and the resolver's existing Info-log fallback stands.</para>
    ///
    /// <para><b>Always a no-op outside Production</b>: Development / Test
    /// deployments run on the stub WITHOUT a CP string by design (the whole
    /// test suite relies on this), regardless of the opt-in.</para>
    /// </summary>
    /// <param name="isProduction"><c>true</c> when the host environment is
    /// Production (e.g. <c>builder.Environment.IsProduction()</c>).</param>
    /// <param name="requireTenantIsolation"><c>true</c> when the operator has
    /// opted into mandatory per-tenant DB isolation via
    /// <c>Tamma:RequireTenantIsolation</c>. When <c>false</c> (the default),
    /// shared-infrastructure mode is permitted in Production.</param>
    /// <param name="controlPlaneConnectionString">The resolved
    /// <c>ConnectionStrings:ControlPlane</c> value (already trimmed of
    /// fallbacks by the caller), or <c>null</c>/empty when unset.</param>
    /// <exception cref="InvalidOperationException">Thrown when
    /// <paramref name="isProduction"/> AND
    /// <paramref name="requireTenantIsolation"/> are both <c>true</c> and
    /// <paramref name="controlPlaneConnectionString"/> is null/whitespace —
    /// the deployment declared per-tenant isolation mandatory but would
    /// otherwise run the stub resolver with isolation disabled.</exception>
    public static void GuardTenantIsolationInProduction(
        bool isProduction,
        bool requireTenantIsolation,
        string? controlPlaneConnectionString)
    {
        // Shared-infrastructure mode (RLS isolation, no CP string) is the
        // documented production default — only enforce per-tenant DB routing
        // when the operator has explicitly opted in.
        if (!isProduction || !requireTenantIsolation)
            return;

        if (string.IsNullOrWhiteSpace(controlPlaneConnectionString))
            throw new InvalidOperationException(
                "Tamma:RequireTenantIsolation=true but "
                + "ConnectionStrings:ControlPlane is not configured in a "
                + "Production environment. The deployment declared per-tenant "
                + "database isolation mandatory, yet without the CP string the "
                + "platform falls back to StubTenantConnectionResolver, which "
                + "routes EVERY tenant to the single shared central database — "
                + "isolation would be disabled. Refusing to start. Either set "
                + "ConnectionStrings:ControlPlane to the dedicated control-plane "
                + "database to enable the per-tenant LRU connection pool "
                + "(Story 28-4), or unset Tamma:RequireTenantIsolation to run in "
                + "shared-infrastructure mode (Phase-3 RLS isolation).");
    }
}
