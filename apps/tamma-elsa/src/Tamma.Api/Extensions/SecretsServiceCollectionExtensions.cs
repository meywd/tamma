using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI registration for the platform secret cabinet
/// (Epic 29 / Story 29-1). Story 29-1 is interface-only — this
/// extension registers:
/// <list type="bullet">
///   <item><description><see cref="ISecretAccessAuditor"/> backed by
///     the no-op <see cref="NullSecretAccessAuditor"/>. Story 29-2
///     replaces this with the Postgres-backed real auditor.</description></item>
///   <item><description><see cref="ISecretStoreBackend"/> backed by
///     <see cref="InMemorySecretStoreBackend"/> as a singleton. Story
///     29-2 replaces this with <c>PostgresSecretStoreBackend</c>; the
///     in-memory placeholder keeps subsequent stories' tests
///     buildable until then.</description></item>
/// </list>
///
/// <para>Intentionally not merged into <c>AddTammaData</c> — Epic 28
/// owns that surface; Story 29-1's hard scope rule keeps the secrets
/// wiring in a separate extension method so the two epics can be
/// reviewed and rolled forward independently.</para>
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

        // Audit pipe — null until Story 29-2 wires the real one. Kept
        // as a singleton because the auditor is stateless and every
        // backend call emits one event so per-request scope churn
        // would be wasteful.
        services.TryAddSingleton<ISecretAccessAuditor, NullSecretAccessAuditor>();

        // Backend driver — in-memory placeholder. The Postgres backend
        // (Story 29-2) will register itself with TryAddScoped (it owns
        // a DbContext); the TryAdd* contract here means the first
        // registration wins, so a higher-priority composition can
        // override.
        services.TryAddSingleton<ISecretStoreBackend, InMemorySecretStoreBackend>();

        return services;
    }
}
