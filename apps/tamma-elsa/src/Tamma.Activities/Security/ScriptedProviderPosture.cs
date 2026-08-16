using Microsoft.Extensions.Configuration;

namespace Tamma.Activities.Security;

/// <summary>
/// Epic 31 P5 follow-up (2026-08-13) — the SHARED posture for the "scripted"
/// LLM provider: a deterministic, in-process test provider that unblocks the
/// engine-driven autonomous E2E (no fake/echo provider existed before it; see
/// tests/Tamma.Platforms.IntegrationTests/GiteaFullStackE2ETests.cs).
///
/// <para><b>Opt-in only, structurally.</b> The provider key is NOT in
/// <see cref="ProviderAllowlist"/>'s defaults and its catalogue classification
/// is <c>Allowlisted=false</c> (the defensive non-selectable convention), so a
/// deployment that never sets <see cref="FlagKey"/> cannot select it — not via
/// a provider chain, not via an agent config, not via a request override
/// (the credential resolver's allowlist check refuses it first).</para>
///
/// <para><b>Impossible to enable in production, structurally.</b>
/// <see cref="AssertAllowed"/> THROWS at host startup when the flag is set on
/// a deployment carrying any SaaS/production signal (explicit
/// <c>Tamma:Mode=saas</c>, a <c>Tamma:TenantSharedSecret</c>, or a
/// <c>ConnectionStrings:ControlPlane</c> — the exact CLAUDE.md mode-detection
/// signals mirrored by <c>TammaModeProvider.Resolve</c>). Defense in depth:
/// even if a host skipped the assert, the SaaS provider gate fails closed on
/// an unknown provider (<c>SaaSProviderGate</c> denies non-catalogued keys)
/// and <c>HttpProviderClient</c> refuses the non-HTTP transport.</para>
///
/// <para>This type lives in Tamma.Activities so BOTH hosts (Tamma.Api, which
/// serves the responses, and Tamma.ElsaServer, which only allow-lists the key
/// for chain resolution) share one flag and one guard.</para>
/// </summary>
public static class ScriptedProviderPosture
{
    /// <summary>The single opt-in flag, shared by both hosts.</summary>
    public const string FlagKey = "Llm:EnableScriptedProvider";

    /// <summary>The canonical provider key.</summary>
    public const string ProviderKey = "scripted";

    /// <summary>Whether the scripted provider is opted in on this host.</summary>
    public static bool IsEnabled(IConfiguration configuration)
        => string.Equals(configuration[FlagKey], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this configuration carries any SaaS/production deployment signal
    /// (mirrors <c>TammaModeProvider.Resolve</c>'s inference exactly).
    /// </summary>
    public static bool HasProductionSignal(IConfiguration configuration)
    {
        var explicitMode = configuration["Tamma:Mode"];
        if (!string.IsNullOrWhiteSpace(explicitMode)
            && explicitMode.Trim().Equals("saas", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(configuration["Tamma:TenantSharedSecret"])
            || !string.IsNullOrWhiteSpace(configuration.GetConnectionString("ControlPlane"));
    }

    /// <summary>
    /// Fail-loud structural guard: enabled + production signal ⇒ the host
    /// REFUSES to start. Returns true when the provider is enabled and allowed.
    /// </summary>
    public static bool AssertAllowed(IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
        {
            return false;
        }

        if (HasProductionSignal(configuration))
        {
            throw new InvalidOperationException(
                $"{FlagKey}=true is refused on this deployment: a SaaS/production signal is present " +
                "(Tamma:Mode=saas, Tamma:TenantSharedSecret, or ConnectionStrings:ControlPlane). " +
                "The scripted LLM provider is a deterministic TEST provider and must never be " +
                "registrable on a production-shaped host. Remove the flag or the SaaS config.");
        }

        return true;
    }
}
