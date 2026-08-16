using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;
using Tamma.Api.Services.Agents.Scripted;

namespace Tamma.Api.Extensions;

/// <summary>
/// 2026-08-13 (Epic 31 P5 follow-up) — registration for the opt-in "scripted"
/// LLM provider, the deterministic in-process test provider that unblocks the
/// engine-driven autonomous E2E.
///
/// <para><b>Structural posture</b> (see <see cref="ScriptedProviderPosture"/>):
/// <list type="bullet">
///   <item>Flag off (the default) ⇒ this method is a NO-OP: no responder, no
///   allowlist widening, no credential decorator — the host is byte-identical
///   to before this feature existed.</item>
///   <item>Flag on + any SaaS/production signal ⇒ the host REFUSES TO START
///   (<see cref="ScriptedProviderPosture.AssertAllowed"/> throws).</item>
///   <item>Flag on, non-production ⇒ registers the responder, adds "scripted"
///   to the DI allowlist options (the credential resolver's fail-closed
///   allowlist check), and wraps the credential resolver so "scripted"
///   resolves a placeholder key (the responder is in-process — no key exists
///   or is ever sent anywhere).</item>
/// </list></para>
///
/// <para>Call AFTER <see cref="ProviderCredentialServiceCollectionExtensions.AddProviderCredentialResolution"/>
/// — the credential decorator wraps whatever resolver registration exists at
/// call time (asserted, fail-loud).</para>
/// </summary>
public static class ScriptedLlmProviderServiceCollectionExtensions
{
    public static IServiceCollection AddScriptedLlmProvider(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Off (default) ⇒ no-op. On + production signal ⇒ THROWS (refuse to start).
        if (!ScriptedProviderPosture.AssertAllowed(configuration))
        {
            return services;
        }

        // Optional per-test script overrides — fail-loud on a bad path.
        var scriptPath = configuration[$"{ScriptedLlmProviderOptions.SectionName}:ScriptPath"];
        var overrides = string.IsNullOrWhiteSpace(scriptPath)
            ? null
            : ScriptedLlmResponder.LoadOverrides(scriptPath);

        services.AddSingleton<IScriptedLlmResponder>(sp => new ScriptedLlmResponder(
            overrides, sp.GetService<ILogger<ScriptedLlmResponder>>()));

        // Allow-list the key on THIS host only (DI options — the shipped
        // ProviderAllowlist.DefaultProviders set is never touched).
        services.PostConfigure<ProviderAllowlistOptions>(o =>
        {
            if (!o.AdditionalProviders.Contains(
                    ScriptedProviderPosture.ProviderKey, StringComparer.OrdinalIgnoreCase))
            {
                o.AdditionalProviders.Add(ScriptedProviderPosture.ProviderKey);
            }
        });

        // Wrap the already-registered credential resolver: "scripted" answers a
        // placeholder credential; every other provider delegates untouched.
        var inner = services.LastOrDefault(d =>
            d.ServiceType == typeof(IProviderCredentialResolver));
        if (inner is null)
        {
            throw new InvalidOperationException(
                "AddScriptedLlmProvider must be called AFTER AddProviderCredentialResolution — " +
                "no IProviderCredentialResolver registration found to decorate.");
        }

        services.Remove(inner);
        services.Add(ServiceDescriptor.Describe(
            typeof(IProviderCredentialResolver),
            sp => new ScriptedProviderCredentialResolver(
                (IProviderCredentialResolver)CreateInner(sp, inner)),
            inner.Lifetime));

        return services;
    }

    private static object CreateInner(IServiceProvider sp, ServiceDescriptor inner)
    {
        if (inner.ImplementationInstance is not null)
        {
            return inner.ImplementationInstance;
        }

        if (inner.ImplementationFactory is not null)
        {
            return inner.ImplementationFactory(sp);
        }

        if (inner.ImplementationType is not null)
        {
            return ActivatorUtilities.CreateInstance(sp, inner.ImplementationType);
        }

        throw new InvalidOperationException(
            "IProviderCredentialResolver registration has no implementation to decorate.");
    }
}
