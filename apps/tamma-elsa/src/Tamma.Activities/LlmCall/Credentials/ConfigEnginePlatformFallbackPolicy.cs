using Microsoft.Extensions.Configuration;

namespace Tamma.Activities.LlmCall.Credentials;

/// <summary>
/// Story 32-3 — <see cref="IPlatformFallbackPolicy"/> for the standalone Elsa
/// workflow host (<c>Tamma.ElsaServer</c>), which cannot reference the
/// <c>Tamma.Api</c> <c>ITammaModeProvider</c>. Mirrors
/// <c>ConfigPlatformFallbackPolicy</c>'s rules but derives the operating mode
/// from configuration directly (same detection as
/// <c>TammaModeProvider.Resolve</c>):
///
/// <list type="bullet">
///   <item><description><b>single-user</b> (or any <c>tenantId == null</c>) ⇒
///     always allowed — the local platform/config key is the expected
///     source.</description></item>
///   <item><description><b>SaaS</b> ⇒ allowed unless explicitly disabled via
///     <c>Providers:&lt;provider&gt;:PlatformFallbackDisabled</c> or
///     <c>Providers:PlatformFallbackDisabled</c>.</description></item>
/// </list>
/// </summary>
public sealed class ConfigEnginePlatformFallbackPolicy : IPlatformFallbackPolicy
{
    private readonly IConfiguration _configuration;
    private readonly bool _isSaaS;

    public ConfigEnginePlatformFallbackPolicy(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _isSaaS = ResolveIsSaaS(configuration);
    }

    /// <inheritdoc />
    public bool IsPlatformFallbackAllowed(Guid? tenantId, string providerName)
    {
        // Single-user / platform scope: always fall back to the local key.
        if (!_isSaaS || tenantId is null)
        {
            return true;
        }

        var provider = providerName.Trim().ToLowerInvariant();
        if (ReadBool($"Providers:{provider}:PlatformFallbackDisabled"))
        {
            return false;
        }

        if (ReadBool("Providers:PlatformFallbackDisabled"))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// SaaS-mode detection — identical contract to
    /// <c>Tamma.Api.Services.PromptStore.TammaModeProvider.Resolve</c>:
    /// explicit <c>Tamma:Mode</c> wins, else presence of
    /// <c>Tamma:TenantSharedSecret</c> or the <c>ControlPlane</c> connection
    /// string signals SaaS; default single-user.
    /// </summary>
    private static bool ResolveIsSaaS(IConfiguration configuration)
    {
        var explicitMode = configuration["Tamma:Mode"];
        if (!string.IsNullOrWhiteSpace(explicitMode))
        {
            return explicitMode.Trim().ToLowerInvariant() switch
            {
                "saas" => true,
                "single-user" or "singleuser" or "single_user" => false,
                _ => false,
            };
        }

        var hasSharedSecret = !string.IsNullOrWhiteSpace(
            configuration["Tamma:TenantSharedSecret"]);
        var hasControlPlane = !string.IsNullOrWhiteSpace(
            configuration.GetConnectionString("ControlPlane"));
        return hasSharedSecret || hasControlPlane;
    }

    private bool ReadBool(string key)
    {
        var raw = _configuration[key];
        return !string.IsNullOrWhiteSpace(raw)
            && bool.TryParse(raw, out var v) && v;
    }
}
