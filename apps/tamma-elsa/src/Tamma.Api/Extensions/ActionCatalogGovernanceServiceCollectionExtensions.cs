using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Tamma.Api.Services.Actions;
using Tamma.Api.Services.Agents;
using Tamma.Core.Actions;
using Tamma.Data;
using Tamma.Data.Repositories;

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
        // ── Story 43-5 — the governed-action storage + resolution stack ─────

        // CP repositories only when a control-plane DB factory is wired (the
        // AddProviderModelCatalogAndSettings conditional-wiring pattern);
        // hosts without one get a snapshot store whose reads all answer
        // "no rows" — behaviour byte-identical to the shipped defaults.
        if (services.Any(d => d.ServiceType == typeof(IDbContextFactory<ControlPlaneDbContext>)))
        {
            services.TryAddSingleton<IActionAssignmentRepository, EfActionAssignmentRepository>();
            services.TryAddSingleton<IActionAuthorizationLedger, EfActionAuthorizationLedger>();
        }

        // The singleton snapshot store (the hardened ProviderSettingsStore
        // patterns: volatile whole-snapshot, 60 s TTL, version-gated installs,
        // invalidate-on-write) + its cold-start priming hosted service.
        services.TryAddSingleton<IGovernancePolicySnapshotProvider>(sp =>
            new GovernancePolicySnapshotStore(
                sp.GetService<IActionAssignmentRepository>(),
                sp.GetRequiredService<Services.PromptStore.ITammaModeProvider>(),
                sp.GetRequiredService<ILogger<GovernancePolicySnapshotStore>>(),
                sp.GetService<TimeProvider>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, GovernancePolicySnapshotPrimingService>());

        // Principal resolution (43-5 AC7/D9). SoleUserProvider caches success
        // only, so it is singleton-safe; the resolver reads the scoped
        // ITenantContext and is therefore scoped.
        services.TryAddSingleton<ISoleUserProvider>(sp => new SoleUserProvider(
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            sp.GetService<IDbContextFactory<ControlPlaneDbContext>>()));
        services.TryAddScoped<IGovernancePrincipalResolver, GovernancePrincipalResolver>();

        // ── Story 43-5 F11 — the BREAK-GLASS override ───────────────────────
        // Config-sourced and singleton BY DESIGN, not by convenience: the state
        // is read once at construction, so engaging it requires a configuration
        // change AND a restart. There is deliberately no endpoint and no writer
        // — an API that can switch off a governance posture is itself a
        // governance surface, and a compromised admin session would reach it.
        services.TryAddSingleton<IGovernanceBreakGlass>(sp =>
            new ConfigurationGovernanceBreakGlass(
                sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
                sp.GetService<ILogger<ConfigurationGovernanceBreakGlass>>(),
                sp.GetService<TimeProvider>()));

        // The audit event family (43-5 AC13) + the DB-backed IAutonomyGate
        // (scoped: IEventRepository / IAcceptanceRulesResolver are scoped).
        services.TryAddScoped<ActionGateEventsService>();
        services.TryAddScoped<IAutonomyGate, AutonomyGateService>();

        // ── Seam B — the tool-loop gate ─────────────────────────────────────
        // Story 43-5 data-source seam: the 43-4 gate class, now fed by the
        // 43-5 assignment ladder (SCOPED — it reads the scoped ITenantContext;
        // its only consumer, InlineToolLoopRunner, is scoped). With zero
        // assignment rows the ladder returns the shipped catalog defaults, so
        // day-one behaviour is byte-identical to the 43-4 registration.
        services.TryAddScoped<IToolLoopAutonomyGate>(sp => new CatalogDefaultToolLoopAutonomyGate(
            sp.GetRequiredService<IGovernancePolicySnapshotProvider>(),
            sp.GetRequiredService<ITenantContext>(),
            sp.GetService<ILogger<CatalogDefaultToolLoopAutonomyGate>>(),
            sp.GetService<IGovernanceBreakGlass>()));

        // ── Story 43-8 AC9 — enforcementSites ───────────────────────────────
        // Computes, per ActionKey, the concrete bound sites (routes carrying
        // IActionGateMetadata + TammaApiClient methods carrying [PerformsEffect])
        // so the admin API can serialise them and the 43-7 UI can render "not
        // enforced anywhere yet" for an EMPTY array. Singleton with a lazy first
        // computation: endpoint building happens on the first request, so eager
        // resolution here would capture an empty data source.
        services.TryAddSingleton<IActionEnforcementSites>(sp =>
            new ActionEnforcementSites(() => ActionEnforcementSites.DiscoverEndpoints(sp)));

        // The fail-loud startup validator (Tamma.Api host only — see class doc).
        // TryAddEnumerable keeps a repeated call from running the checks twice.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ActionCatalogStartupValidator>());

        return services;
    }
}
