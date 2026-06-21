using Microsoft.Extensions.Configuration;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Services.PromptStore;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// v1 <see cref="IPlatformFallbackPolicy"/> (Story 32-3 AC6.1).
///
/// <list type="bullet">
///   <item><description><b>single-user</b> ⇒ always allowed (the sole user
///     owns everything; falling back to the local platform/env key is the
///     expected behaviour).</description></item>
///   <item><description><b>SaaS</b> ⇒ allowed unless explicitly disabled by
///     config:
///     <list type="bullet">
///       <item><description><c>Providers:PlatformFallbackDisabled = true</c>
///         (global), or</description></item>
///       <item><description><c>Providers:&lt;provider&gt;:PlatformFallbackDisabled = true</c>
///         (per-provider override).</description></item>
///     </list></description></item>
/// </list>
///
/// <para>The plan-level / per-provider gating that Epics 34/35 will drive plugs
/// in here without changing the resolver.</para>
/// </summary>
public sealed class ConfigPlatformFallbackPolicy : IPlatformFallbackPolicy
{
    private readonly IConfiguration _configuration;
    private readonly ITammaModeProvider _mode;

    public ConfigPlatformFallbackPolicy(
        IConfiguration configuration, ITammaModeProvider mode)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(mode);
        _configuration = configuration;
        _mode = mode;
    }

    /// <inheritdoc />
    public bool IsPlatformFallbackAllowed(Guid? tenantId, string providerName)
    {
        // Single-user: the sole user owns everything — always fall back to the
        // platform/local key. (A null tenant id is the single-user signal.)
        if (_mode.Mode == TammaMode.SingleUser || tenantId is null)
        {
            return true;
        }

        // SaaS: allowed by default, disabled only when config says so.
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

    private bool ReadBool(string key)
    {
        var raw = _configuration[key];
        return !string.IsNullOrWhiteSpace(raw)
            && bool.TryParse(raw, out var v) && v;
    }
}
