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

        // Control-plane context (migrations-owning). Scoped because auth
        // handlers, admin endpoints and CP repos depend on it.
        services.AddDbContext<ControlPlaneDbContext>(options =>
        {
            options.UseNpgsql(adminConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"));
        });

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
        services.AddScoped<IInstallationRepository, InstallationRepository>();
        services.AddScoped<IGitHubWebhookDeliveryRepository, GitHubWebhookDeliveryRepository>();

        // Tenant-scoped repositories (use ITenantDbContextFactory internally).
        services.AddScoped<IAgentConfigRepository, AgentConfigRepository>();
        services.AddScoped<IPromptRepository, PromptRepository>();
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
}
