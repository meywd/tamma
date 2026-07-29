using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Tamma.Api.Services.Actions;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 43-4 — DI registration for the action-catalog tool-vocabulary
/// governance pieces: the Seam B tool-loop autonomy gate (a REQUIRED
/// constructor dependency of <see cref="InlineToolLoopRunner"/>) and the
/// fail-loud <see cref="ActionCatalogStartupValidator"/>.
///
/// <para>Registered idempotently (TryAdd*) and invoked from
/// <c>AddAgentResolverServices</c> so the Tamma.Api host is wired without a
/// <c>Program.cs</c> edit; <c>Program.cs</c> may also call this directly — a
/// second call is a no-op. <b>Tamma.ElsaServer must NOT call this</b>: the
/// engine registers no tool executors (Story 32-5 AC9) and the validator would
/// throw on every engine boot. The engine keeps only the eager
/// <c>ActionCatalog.Validate()</c> composition call (43-2 AC13).</para>
/// </summary>
public static class ActionCatalogGovernanceServiceCollectionExtensions
{
    /// <summary>
    /// Register the Seam B autonomy gate (behaviour-preserving v1 default:
    /// catalog-shipped thresholds, shipped dial default) and the tool-vocabulary
    /// startup validator.
    /// </summary>
    public static IServiceCollection AddActionCatalogGovernance(this IServiceCollection services)
    {
        // The v1 gate — catalog defaults, v1 dial semantics (automated iff
        // dial >= MinAutonomy; AlwaysHuman blocks). Story 43-5 swaps the
        // implementation for the resolver-backed gate behind the same interface.
        services.TryAddSingleton<IToolLoopAutonomyGate, CatalogDefaultToolLoopAutonomyGate>();

        // The fail-loud startup validator (Tamma.Api host only — see class doc).
        // TryAddEnumerable keeps a repeated call from running the checks twice.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ActionCatalogStartupValidator>());

        return services;
    }
}
