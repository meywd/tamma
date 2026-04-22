using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Interceptors;
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
    /// <para>The obsolete <c>TammaDbContext</c> and <c>TammaAppDbContext</c>
    /// types are registered transitionally so unmigrated consumers keep
    /// compiling; they are deleted at the end of the Wave A.5 cleanup.</para>
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
        string? appConnectionString = null)
    {
        services.AddScoped<ITenantContext, TenantContext>();

        // Control-plane context (migrations-owning). Scoped because auth
        // handlers, admin endpoints and CP repos depend on it. Uses the
        // admin connection so CP writes are unconstrained.
        services.AddDbContext<ControlPlaneDbContext>(options =>
        {
            options.UseNpgsql(adminConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"));
        });

        // Factory for per-tenant contexts. Uses the app connection when
        // provided (RLS plane on the shared DB during transition), else
        // falls back to the admin connection.
        var tenantConnectionString = string.IsNullOrWhiteSpace(appConnectionString)
            ? adminConnectionString
            : appConnectionString;
        services.AddSingleton<ITenantDbContextFactory>(
            _ => new TenantDbContextFactory(tenantConnectionString));

        // ──── Obsolete contexts — registered transitionally ────
        // Kept for unmigrated consumers. Wave A.5 deletes these and the
        // consumers in the same commit window.
#pragma warning disable CS0618 // Type or member is obsolete
        services.AddScoped<TenantContextInterceptor>();

        services.AddDbContext<TammaDbContext>((sp, options) =>
        {
            options.UseNpgsql(adminConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"));
            options.AddInterceptors(sp.GetRequiredService<TenantContextInterceptor>());
        });

        services.AddDbContext<TammaAppDbContext>((sp, options) =>
        {
            options.UseNpgsql(tenantConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"));
            options.AddInterceptors(sp.GetRequiredService<TenantContextInterceptor>());
        });
#pragma warning restore CS0618

        // Control-plane repositories (Epic 28 isolation model).
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IPasswordResetRepository, PasswordResetRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddScoped<IInviteRepository, InviteRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IInstallationRepository, InstallationRepository>();
        services.AddScoped<IGitHubWebhookDeliveryRepository, GitHubWebhookDeliveryRepository>();

        // Tenant-scoped repositories (Epic 28 — use ITenantDbContextFactory).
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

        return services;
    }
}
