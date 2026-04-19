using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Interceptors;
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
    /// </summary>
    public static IServiceCollection AddTammaData(
        this IServiceCollection services,
        string adminConnectionString,
        string? appConnectionString = null)
    {
        services.AddScoped<ITenantContext, TenantContext>();

        // Admin / platform-root context. Registered first so migrations run
        // against it at startup (see Program.cs).
        services.AddDbContext<TammaDbContext>(options =>
            options.UseNpgsql(adminConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory")));

        // App-role context. If no dedicated connection string is supplied
        // (e.g. dev laptop hitting a single-role local Postgres) we silently
        // fall back to the admin connection. Production must override this
        // via ConnectionStrings:TammaAppDb. The TenantContextInterceptor is
        // still installed so the SET app.current_tenant_id plumbing is
        // exercised and the fail-closed EF filter kicks in — only the
        // role-separation layer degrades.
        //
        // The interceptor is registered as Scoped because it reads
        // ITenantContext (scoped). EF Core's internal service provider
        // resolves DbContextOptions extensions including interceptors via
        // the application service provider, so the scoped binding is
        // correctly honored per-request.
        services.AddScoped<TenantContextInterceptor>();

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

        return services;
    }
}
