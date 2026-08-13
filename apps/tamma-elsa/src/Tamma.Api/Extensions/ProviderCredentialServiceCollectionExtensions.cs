using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Data.Repositories;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 32-3 — DI registration for the BYOK→platform provider-credential
/// resolver, its cache invalidator, and the BYOK read seam. Only wires the
/// cabinet-backed BYOK reader when the Story 29-2 <see cref="SecretsDbContext"/>
/// factory is present (production / secrets-enabled tests); otherwise a Null
/// reader is registered so the resolver degrades to the platform path without
/// a DI-validation failure on hosts with no secret store.
///
/// <para>The <see cref="IProviderCredentialResolver"/> is a singleton so its
/// in-process BYOK cache survives across requests (TTL + explicit invalidate
/// keep it coherent). It depends on the singleton
/// <see cref="Tamma.Api.Services.Secrets.Stopgap.IRuntimeSecretResolver"/>
/// (platform key path) — resolved as optional so single-user / no-secrets
/// hosts still build.</para>
/// </summary>
public static class ProviderCredentialServiceCollectionExtensions
{
    public static IServiceCollection AddProviderCredentialResolution(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Provider allowlist (shared with the activity's fail-closed guards).
        services.TryAddSingleton<ProviderAllowlist>();

        // Fallback policy (mode + config driven).
        services.TryAddSingleton<IPlatformFallbackPolicy, ConfigPlatformFallbackPolicy>();

        // BYOK read seam. Cabinet-backed when the secrets DbContext factory is
        // wired; a Null reader otherwise so the resolver degrades cleanly.
        if (services.Any(d => d.ServiceType
                == typeof(IDbContextFactory<SecretsDbContext>)))
        {
            services.TryAddSingleton<ITenantProviderKeyReader, CabinetTenantProviderKeyReader>();
        }
        else
        {
            services.TryAddSingleton<ITenantProviderKeyReader, NullTenantProviderKeyReader>();
        }

        // The resolver — singleton so the BYOK cache is process-wide. Factory
        // shape so IRuntimeSecretResolver (the platform-key leg) is OPTIONAL:
        // single-user / no-secrets hosts may not have it registered, and the
        // resolver tolerates null (degrading the platform leg to "unset").
        //
        // 2026-08-13 (engine-driven E2E): IEventRepository is SCOPED, and this
        // singleton's factory resolved it from the root provider — a captive
        // dependency. Production silently promoted it (a single DbContext-backed
        // repository living forever inside the singleton); a Development host
        // (ValidateScopes) throws "Cannot resolve scoped service ... from root
        // provider" on FIRST llm/call, 500ing the endpoint. The resolver keeps
        // its singleton lifetime (the BYOK cache contract) and now audits
        // through a scope-per-append adapter.
        services.TryAddSingleton<IProviderCredentialResolver>(sp =>
            new DefaultProviderCredentialResolver(
                sp.GetRequiredService<ITenantProviderKeyReader>(),
                sp.GetService<IRuntimeSecretResolver>(),
                sp.GetRequiredService<IPlatformFallbackPolicy>(),
                new ScopePerCallEventRepository(sp.GetRequiredService<IServiceScopeFactory>()),
                sp.GetRequiredService<ITammaModeProvider>(),
                sp.GetRequiredService<ProviderAllowlist>(),
                sp.GetRequiredService<ILogger<DefaultProviderCredentialResolver>>(),
                sp.GetService<TimeProvider>()));

        // Cache invalidator (SECRET.ROTATE.ACTIVATED handler + mutation hook).
        services.TryAddSingleton<ProviderCredentialCacheInvalidator>();

        return services;
    }

    /// <summary>
    /// 2026-08-13 — scope-per-call <see cref="IEventRepository"/> adapter for the
    /// SINGLETON credential resolver (which only appends audit events). Each call
    /// creates a DI scope, resolves the real scoped repository, and delegates —
    /// the same pattern <c>TenantScheduledTriggerService</c> uses for its scoped
    /// dependencies. Default-interface members (agent trail / time-travel reads)
    /// are NOT forwarded: the resolver never calls them, and their defaults throw
    /// loudly if that ever changes.
    /// </summary>
    private sealed class ScopePerCallEventRepository(IServiceScopeFactory scopes) : IEventRepository
    {
        private async Task<T> RunAsync<T>(Func<IEventRepository, Task<T>> call)
        {
            using var scope = scopes.CreateScope();
            return await call(scope.ServiceProvider.GetRequiredService<IEventRepository>())
                .ConfigureAwait(false);
        }

        public Task<Tamma.Data.Entities.DomainEvent> AppendAsync(Tamma.Data.Entities.DomainEvent evt)
            => RunAsync(r => r.AppendAsync(evt));

        public Task<Tamma.Data.Entities.DomainEvent?> GetByIdAsync(Guid id)
            => RunAsync(r => r.GetByIdAsync(id));

        public Task<List<Tamma.Data.Entities.DomainEvent>> QueryAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit)
            => RunAsync(r => r.QueryAsync(tenantId, type, issueNumber, limit));

        public Task<Tamma.Data.Entities.DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
            => RunAsync(r => r.GetLastByTypeAsync(tenantId, type));

        public async Task ClearAsync(Guid tenantId)
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IEventRepository>()
                .ClearAsync(tenantId).ConfigureAwait(false);
        }

        public Task<(IReadOnlyList<Tamma.Data.Entities.DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => RunAsync(r => r.QueryWithPaginationAsync(tenantId, type, issueNumber, limit, offset));

        public Task<(IReadOnlyList<Tamma.Data.Entities.DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => RunAsync(r => r.ListByTenantAsync(tenantId, typePrefix, limit, offset));
    }
}
